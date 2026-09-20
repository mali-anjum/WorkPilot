using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
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

// EF Core, pointed at the "workpilotdb" connection supplied by Aspire
// (the AppHost wires this to the same Postgres instance/database Supabase's
// own Postgres container exposes; see AppHost.cs and docker-compose.yml).
// EF Core owns only the product schema here, never `auth.*`/`storage.*`.
builder.AddNpgsqlDbContext<WorkPilotDbContext>("workpilotdb");

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
    var count = await db.ScaffoldPings.CountAsync();
    return Results.Ok(new { status = "ok", scaffoldPings = count });
});

app.Run();
