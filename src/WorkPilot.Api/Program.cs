using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using WorkPilot.AI.Agent;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent.Tools;
using WorkPilot.Infrastructure.Modules.Identity;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Agent;

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

// Agent orchestrator core (docs/specs/0005-agent-orchestrator-core.md): Planner ->
// Policy Engine -> Tool Registry -> Execution Engine -> Verification Engine ->
// Approval Engine -> Audit. The Planner's IChatClient is whichever provider
// AI:ActiveProvider names, validated at startup (docs/specs/0006-ai-provider-abstraction).
builder.Services.AddWorkPilotChatClient(builder.Configuration);
builder.Services.AddScoped<IPlanner, ChatClientPlanner>();
builder.Services.AddScoped<IPolicyEngine, PolicyEngine>();
builder.Services.AddScoped<IVerificationEngine, VerificationEngine>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IToolRegistry, ToolRegistry>();
builder.Services.AddScoped<ITool, ListMyProfileTool>();
builder.Services.AddScoped<ITool, ApprovalRequiredDemoTool>();
builder.Services.AddScoped<PlanRunJob>();
builder.Services.AddScoped<AdvanceRunJob>();

// Hangfire, storage in the same Postgres database as EF Core (per spec:
// "Background jobs / workflows | Hangfire, storage in the same Postgres
// database").
var connectionString = builder.Configuration.GetConnectionString("workpilotdb");
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

// Internal only (same network boundary as /internal/identity/profile above).
// Triggers a run and returns immediately: planning and execution both happen
// in background jobs, never inline in the request, so a restart never leaves
// a run stuck mid-request (docs/specs/0005-agent-orchestrator-core.md, AC-1, AC-9).
app.MapPost("/internal/agent/runs", async (
    TriggerAgentRunRequest request,
    WorkPilotDbContext db,
    IBackgroundJobClient jobs,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Goal))
    {
        return Results.BadRequest();
    }

    var profileExists = await db.Profiles.AnyAsync(p => p.Id == request.ProfileId, cancellationToken);
    if (!profileExists)
    {
        return Results.NotFound();
    }

    var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.Planning.ToString() };
    db.WorkflowInstances.Add(workflow);

    var run = new AgentRun
    {
        WorkflowInstanceId = workflow.Id,
        ProfileId = request.ProfileId,
        Goal = request.Goal,
    };
    db.AgentRuns.Add(run);
    await db.SaveChangesAsync(cancellationToken);

    jobs.Enqueue<PlanRunJob>(j => j.RunAsync(run.Id));

    return Results.Accepted(value: new TriggerAgentRunResponse(run.Id, run.Status.ToString()));
});

app.MapGet("/internal/agent/runs/{id:guid}", async (Guid id, WorkPilotDbContext db, CancellationToken cancellationToken) =>
{
    var run = await db.AgentRuns
        .Include(r => r.Steps.OrderBy(s => s.Ordinal))
        .ThenInclude(s => s.ToolCalls)
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    if (run is null)
    {
        return Results.NotFound();
    }

    var steps = run.Steps
        .Select(s => new AgentRunStepView(s.Ordinal, s.ToolName, s.Status.ToString(), s.ToolCalls.Count > 0 ? s.ToolCalls[^1].Success : null))
        .ToList();

    return Results.Ok(new AgentRunView(run.Id, run.Status.ToString(), steps));
});

// Internal only. The minimal decision hook the real Approval Center (scope
// item 8) will build on top of: an atomic UPDATE guards against a double
// decision (409) rather than a load-then-save race (AC-10).
app.MapPost("/internal/agent/approvals/{id:guid}/decide", async (
    Guid id,
    DecideApprovalRequest request,
    WorkPilotDbContext db,
    IBackgroundJobClient jobs,
    IAuditService audit,
    CancellationToken cancellationToken) =>
{
    if (request.Decision is not ("Approve" or "Reject"))
    {
        return Results.BadRequest();
    }

    var newStatus = request.Decision == "Approve" ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
    var decidedAt = DateTimeOffset.UtcNow;

    var updated = await db.Approvals
        .Where(a => a.Id == id && a.Status == ApprovalStatus.Pending)
        .ExecuteUpdateAsync(
            setters => setters
                .SetProperty(a => a.Status, newStatus)
                .SetProperty(a => a.DecidedBy, request.DecidedBy)
                .SetProperty(a => a.DecidedAt, decidedAt),
            cancellationToken);

    if (updated == 0)
    {
        var exists = await db.Approvals.AnyAsync(a => a.Id == id, cancellationToken);
        return exists ? Results.Conflict() : Results.NotFound();
    }

    var approval = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken);
    var step = await db.AgentSteps.FirstAsync(s => s.Id == approval.TargetId, cancellationToken);
    var run = await db.AgentRuns.FirstAsync(r => r.Id == step.AgentRunId, cancellationToken);

    // Spec 0005's data mapping: an approval's creation is audited as "Agent",
    // but its decision is audited as the deciding profile.
    audit.Record(request.DecidedBy.ToString(), $"Approval{newStatus}", ApprovalTargets.AgentStep, step.Id, null);

    var resumed = newStatus == ApprovalStatus.Approved;
    if (resumed)
    {
        // The step stays AwaitingApproval here: AdvanceRunJob moves it to
        // Running right before executing, so "Running" keeps meaning "the
        // tool may have started" and a non-idempotent approved tool isn't
        // mistaken for one that crashed mid-execution (AC-9).
        run.TransitionTo(AgentRunStatus.Executing);
    }
    else
    {
        step.TransitionTo(AgentStepStatus.Skipped);
        run.TransitionTo(AgentRunStatus.Failed);
    }

    await WorkflowMirror.SyncAsync(db, run, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);

    if (resumed)
    {
        jobs.Enqueue<AdvanceRunJob>(j => j.RunAsync(run.Id));
    }

    return Results.Ok(new DecideApprovalResponse(approval.Id, newStatus.ToString(), resumed));
});

app.Run();

/// <summary>Request body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileRequest(Guid AuthUserId, string Email);

/// <summary>Response body for <c>POST /internal/identity/profile</c>.</summary>
internal sealed record ResolveProfileResponse(Guid ProfileId);

/// <summary>Request body for <c>POST /internal/agent/runs</c>.</summary>
internal sealed record TriggerAgentRunRequest(string Goal, Guid ProfileId);

/// <summary>Response body for <c>POST /internal/agent/runs</c>.</summary>
internal sealed record TriggerAgentRunResponse(Guid AgentRunId, string Status);

/// <summary>One step as reported by <c>GET /internal/agent/runs/{id}</c>.</summary>
internal sealed record AgentRunStepView(int Ordinal, string ToolName, string Status, bool? Success);

/// <summary>Response body for <c>GET /internal/agent/runs/{id}</c>.</summary>
internal sealed record AgentRunView(Guid AgentRunId, string Status, IReadOnlyList<AgentRunStepView> Steps);

/// <summary>Request body for <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
internal sealed record DecideApprovalRequest(string Decision, Guid DecidedBy);

/// <summary>Response body for <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
internal sealed record DecideApprovalResponse(Guid ApprovalId, string Status, bool Resumed);
