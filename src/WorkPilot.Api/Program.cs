using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Infrastructure.Modules.Identity;
using WorkPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// A short, explicit host shutdown timeout (the ASP.NET Core default is
// 30s) so a graceful stop (container stop, test host teardown) doesn't
// drag out waiting on Hangfire's background dispatchers.
builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

// Aspire cross-cutting concerns: OpenTelemetry, health checks, service
// discovery, resilient HttpClient defaults.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Encrypts OAuthConnection access/refresh tokens before EF Core writes them
// (docs/specs/0002-data-model/index.md, AC-6). Default key storage (the
// local user profile / registry on Windows, ~/.aspnet/DataProtection-Keys on
// Linux) is fine for this single instance VPS deployment; revisit if the key
// ring needs to survive a container image swap without a mounted volume.
builder.Services.AddDataProtection();

// EF Core, pointed at the "workpilotdb" connection supplied by Aspire
// (the AppHost wires this to the same Postgres instance/database Supabase's
// own Postgres container exposes; see AppHost.cs and docker-compose.yml).
// EF Core owns only the product schema here, never `auth.*`/`storage.*`.
builder.AddNpgsqlDbContext<WorkPilotDbContext>("workpilotdb");

// Resolves/creates the Profile row for a GoTrue user id. The Api process is
// the sole owner of the DbContext (see above), so the Web host calls this
// internal endpoint over the Aspire service discovery network rather than
// touching EF Core itself (docs/specs/0004-auth-app-shell.md).
builder.Services.AddScoped<IProfileProvisioningService, ProfileProvisioningService>();

// Hangfire, storage in the same Postgres database as EF Core (per spec:
// "Background jobs / workflows | Hangfire, storage in the same Postgres
// database").
var connectionString = builder.Configuration.GetConnectionString("workpilotdb");
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
// The background worker server (polling threads, watchdog, heartbeat) has
// nothing to do with serving requests and isn't needed by WebApplicationFactory
// integration tests, whose host is torn down immediately after each test;
// running it there only adds shutdown latency. Hangfire's dashboard/client
// API (AddHangfire above) still works without it.
if (!builder.Configuration.GetValue<bool>("Hangfire:DisableServer"))
{
    builder.Services.AddHangfireServer(options =>
    {
        // Hangfire's own default shutdown wait is long enough to make graceful
        // host shutdown (container stop) drag out well past what's reasonable;
        // a short, explicit timeout is Hangfire's own documented recommendation.
        options.ShutdownTimeout = TimeSpan.FromSeconds(5);
    });
}

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Hangfire:DisableServer"))
{
    // Loud on purpose: this flag means no background job ever actually
    // runs (jobs still enqueue and the dashboard still works, but nothing
    // dequeues them), which is silent and easy to leave on by accident
    // outside the test host that sets it.
    app.Logger.LogWarning("Hangfire background worker server is DISABLED (Hangfire:DisableServer=true). Enqueued jobs will not run.");
}

// Apply EF Core migrations once at startup, not per-request (see /health/db
// below) - DDL has no business running on every unauthenticated hit to a
// health endpoint.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>().Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints(); // Aspire health/liveness endpoints from ServiceDefaults

// Hangfire dashboard (AC-6: dashboard must be reachable). No auth filter
// yet — this is a local scaffold; add authorization before any non-local
// deployment.
app.UseHangfireDashboard("/hangfire");

app.MapGet("/health/db", async (WorkPilotDbContext db) =>
{
    var count = await db.Profiles.CountAsync();
    return Results.Ok(new { status = "ok", profiles = count });
});

// Internal only: the Api project is never externally exposed (see AppHost.cs,
// only "web" carries WithExternalHttpEndpoints), so this needs no separate
// auth beyond that network boundary. Called once per sign in by the Web
// host to resolve the founder's ProfileId (docs/specs/0004-auth-app-shell.md).
app.MapPost("/internal/identity/profile", async (
    ResolveProfileRequest request,
    IProfileProvisioningService profiles,
    CancellationToken cancellationToken) =>
{
    var profileId = await profiles.GetOrCreateProfileIdAsync(request.AuthUserId, request.Email, cancellationToken);
    return Results.Ok(new ResolveProfileResponse(profileId));
});

app.Run();

/// <summary>Request body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileRequest(Guid AuthUserId, string Email);

/// <summary>Response body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileResponse(Guid ProfileId);
