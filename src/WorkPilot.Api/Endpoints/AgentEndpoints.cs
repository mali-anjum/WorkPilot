using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Agent;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// The Agent module's internal endpoints (spec 0005). Internal only, like every Api route: the
/// Api is never externally exposed (spec 0004).
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Maps <c>POST /internal/agent/runs</c> and <c>GET /internal/agent/runs/{id}</c>.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/agent/runs");

        // Triggers a run and returns immediately: planning and execution both happen in background
        // jobs, never inline in the request, so a restart never leaves a run stuck mid request
        // (spec 0005, AC-1, AC-9).
        group.MapPost("/", async (
            TriggerAgentRunRequest request,
            WorkPilotDbContext db,
            IBackgroundJobClient jobs,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Goal))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["goal"] = ["A goal is required."] });
            }

            var profileExists = await db.Profiles.AnyAsync(p => p.Id == request.ProfileId, cancellationToken);
            if (!profileExists)
            {
                return Results.Problem(detail: "That profile does not exist.", statusCode: StatusCodes.Status404NotFound);
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

        group.MapGet("/{id:guid}", async (Guid id, WorkPilotDbContext db, CancellationToken cancellationToken) =>
        {
            var run = await db.AgentRuns
                .Include(r => r.Steps.OrderBy(s => s.Ordinal))
                .ThenInclude(s => s.ToolCalls)
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (run is null)
            {
                return Results.Problem(detail: "That agent run does not exist.", statusCode: StatusCodes.Status404NotFound);
            }

            var steps = run.Steps
                .Select(s => new AgentRunStepView(s.Ordinal, s.ToolName, s.Status.ToString(), s.ToolCalls.Count > 0 ? s.ToolCalls[^1].Success : null))
                .ToList();

            return Results.Ok(new AgentRunView(run.Id, run.Status.ToString(), steps));
        });

        return app;
    }
}

/// <summary>Request body for <c>POST /internal/agent/runs</c>.</summary>
public sealed record TriggerAgentRunRequest(string Goal, Guid ProfileId);

/// <summary>Response body for <c>POST /internal/agent/runs</c>.</summary>
public sealed record TriggerAgentRunResponse(Guid AgentRunId, string Status);

/// <summary>One step as reported by <c>GET /internal/agent/runs/{id}</c>.</summary>
public sealed record AgentRunStepView(int Ordinal, string ToolName, string Status, bool? Success);

/// <summary>Response body for <c>GET /internal/agent/runs/{id}</c>.</summary>
public sealed record AgentRunView(Guid AgentRunId, string Status, IReadOnlyList<AgentRunStepView> Steps);
