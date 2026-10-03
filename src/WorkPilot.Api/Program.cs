using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WorkPilot.AI.Providers;
using WorkPilot.Api.Endpoints;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Applications;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Modules.Identity;
using WorkPilot.Infrastructure.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Agent;
using WorkPilot.Workers.Approvals;
using WorkPilot.Workers.Audit;
using WorkPilot.Workers.Common;
using WorkPilot.Workers.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Aspire cross-cutting concerns: OpenTelemetry, health checks, service
// discovery, resilient HttpClient defaults.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// One error shape for every endpoint (spec 0018, section 4): expected failures come back from use
// cases as Result<T> and are mapped by ResultHttpExtensions.ToHttp; anything unexpected becomes a
// logged 500 ProblemDetails through UseExceptionHandler below.
builder.Services.AddProblemDetails();

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
// The outbox interceptor asks for an event dispatch right after a save that
// published one (spec 0018, section 3).
builder.AddNpgsqlDbContext<WorkPilotDbContext>(
    "workpilotdb",
    configureDbContextOptions: options => options.AddInterceptors(OutboxSaveChangesInterceptor.Instance));

// Resolves/creates the Profile row for a GoTrue user id. The Api process is
// the sole owner of the DbContext (see above), so the Web host calls this
// internal endpoint over the Aspire service discovery network rather than
// touching EF Core itself (docs/specs/0004-auth-app-shell.md).
builder.Services.AddScoped<IProfileProvisioningService, ProfileProvisioningService>();

// Each AI purpose (the Planner, for now) gets the provider and model
// Ai:Purposes maps it to, validated at startup (docs/specs/0006-ai-provider-abstraction).
builder.Services.AddWorkPilotAi(builder.Configuration);

// === Modules (alphabetical; one Add and one Map line each, spec 0018) ===
builder.Services.AddAgentModule(builder.Configuration);
builder.Services.AddApplicationsModule(builder.Configuration);
builder.Services.AddApprovalsModule(builder.Configuration);
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddJobsModule(builder.Configuration);
builder.Services.AddProfileModule(builder.Configuration);
// === End modules ===

// Hangfire, storage in the same Postgres database as EF Core (per spec:
// "Background jobs / workflows | Hangfire, storage in the same Postgres
// database").
var connectionString = builder.Configuration.GetConnectionString("workpilotdb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // Fail fast at startup (AGENTS.md): Aspire supplies this in dev and the
    // tests set it; without it Hangfire would only fail on first use.
    throw new InvalidOperationException("ConnectionStrings:workpilotdb is not configured.");
}

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        options => options.UseNpgsqlConnection(connectionString),
        // A job fetched by a process that then dies stays invisible until
        // InvisibilityTimeout passes (Hangfire.PostgreSql default: 30 min),
        // so a run killed mid-planning/mid-step would sit stuck that long
        // before resuming (spec 0005, AC-9). Sliding keeps extending the
        // lease while a live worker runs the job, so a short timeout is safe
        // for long jobs too, and a crashed worker's job is re-fetched fast.
        new PostgreSqlStorageOptions
        {
            UseSlidingInvisibilityTimeout = true,
            InvisibilityTimeout = TimeSpan.FromMinutes(1),
        }));
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

// Unexpected exceptions become a logged 500 ProblemDetails, and any error
// response an endpoint returns without a body (a bare Results.NotFound())
// gets a ProblemDetails body too, so every Api failure has one shape (spec 0018, section 4).
app.UseExceptionHandler();
app.UseStatusCodePages();

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
    var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
    await db.Database.MigrateAsync();

    // Job deduplication (docs/specs/0017-job-deduplication, AC-9): when any job's
    // match key is stale (a rule change, or rows from before this feature), the
    // reconcile job recomputes the keys and merges the duplicates, once.
    if (await db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.DedupRuleVersion < JobDedupKey.CurrentRuleVersion))
    {
        scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<ReconcileJobsJob>(j => j.RunAsync());
    }

    // Job matching (docs/specs/0019-job-matching-engine): one sweep on start extracts what is
    // missing or stale and rescores profiles after a scoring or config change.
    scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<MatchSweepJob>(j => j.RunAsync());
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
app.ApplyRecurringJobs(); // every module's recurring jobs (spec 0018, section 5)

app.MapGet("/health/db", async (WorkPilotDbContext db) =>
{
    var count = await db.Profiles.CountAsync();
    return Results.Ok(new { status = "ok", profiles = count });
});

// On demand AI provider check (docs/specs/0006-ai-provider-abstraction, AC-8):
// one tiny prompt through the Default purpose. Deliberately NOT a registered
// health check, so /health and /alive never spend tokens or go unhealthy
// because a provider is down. Internal only, same network boundary as
// /internal/* below.
app.MapGet("/health/ai", async (
    [FromKeyedServices(AiPurposes.Default)] IChatClient client,
    [FromKeyedServices(AiPurposes.Default)] ResolvedAiPurpose target,
    CancellationToken cancellationToken) =>
{
    var result = await AiHealthProbe.RunAsync(client, target, cancellationToken);
    var body = new AiHealthResponse(
        result.Healthy ? "ok" : "error",
        result.Target.Purpose,
        result.Target.Provider,
        result.Target.Model,
        result.LatencyMs,
        result.Error);
    return Results.Json(body, statusCode: result.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
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

// === Modules (alphabetical; one Add and one Map line each, spec 0018) ===
app.MapAgentEndpoints();
app.MapApprovalsEndpoints();
app.MapJobsEndpoints();
app.MapProfileEndpoints();
// === End modules ===

app.Run();

/// <summary>Request body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileRequest(Guid AuthUserId, string Email);

/// <summary>Response body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileResponse(Guid ProfileId);

internal sealed record AiHealthResponse(string Status, string Purpose, string Provider, string? Model, long LatencyMs, string? Error);
