using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Application.Modules.Audit;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Workers.Agent;

/// <summary>
/// Runs the Planner and Policy Engine for one <see cref="AgentRun"/>, off the
/// request thread so a process restart mid-planning is safe (spec 0005,
/// AC-1, AC-9). Not retried by Hangfire itself: the injected <see cref="IPlanner"/>
/// already retries once internally (AC-7); a second Hangfire-level retry
/// would only duplicate that. A provider failure (after the AI client's own
/// retries) or an unparseable plan fails the run and is audited as
/// <c>PlanningFailed</c> (spec 0006, AC-6), never left stuck at Planning.
/// </summary>
public sealed class PlanRunJob(WorkPilotDbContext db, IPlanner planner, IToolRegistry registry, IPolicyEngine policy, IBackgroundJobClient jobs, IAuditService audit, IEventPublisher events)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid agentRunId)
    {
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == agentRunId);

        // A re-delivered job for a run that already progressed (or was never created) is a no-op.
        if (run is null || run.Status != AgentRunStatus.Planning)
        {
            return;
        }

        var descriptors = registry.All.Select(t => new ToolDescriptor(t.Name, t.Description, t.RequiredArguments)).ToList();

        AgentPlan plan;
        try
        {
            plan = await planner.PlanAsync(run.Goal, descriptors, CancellationToken.None);
        }
        catch (AiProviderException ex)
        {
            await FailPlanningAsync(run, AgentRunFailureReasons.ProviderError, ex.Purpose, ex.Provider, ex.Model, ex.Message);
            return;
        }
        catch (PlanParseException ex)
        {
            await FailPlanningAsync(run, AgentRunFailureReasons.UnparseablePlan, ex.Purpose, ex.Provider, ex.Model, ex.Message);
            return;
        }

        run.TransitionTo(AgentRunStatus.PolicyCheck);
        var violations = policy.Validate(plan, registry);
        if (violations.Count > 0)
        {
            run.TransitionTo(AgentRunStatus.Failed);
            events.Publish(new AgentRunFailed(run.Id, AgentRunFailureReasons.PolicyViolation));
            await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
            await db.SaveChangesAsync();
            return;
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            db.AgentSteps.Add(new AgentStep
            {
                AgentRunId = run.Id,
                Ordinal = i,
                ToolName = step.Tool,
                ArgumentsJson = JsonSerializer.Serialize(step.Arguments, JsonSerializerOptions.Web),
            });
        }

        run.TransitionTo(AgentRunStatus.Executing);
        await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
        await db.SaveChangesAsync();

        jobs.Enqueue<AdvanceRunJob>(j => j.RunAsync(run.Id));
    }

    private async Task FailPlanningAsync(AgentRun run, string reason, string? purpose, string? provider, string? model, string error)
    {
        run.TransitionTo(AgentRunStatus.Failed);
        await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);

        // No key and no prompt text: the exception messages are built to
        // carry neither (spec 0006, AC-6).
        var payload = JsonSerializer.Serialize(new { reason, purpose, provider, model, error }, JsonSerializerOptions.Web);
        audit.Record("Agent", "PlanningFailed", nameof(AgentRun), run.Id, payload);
        events.Publish(new AgentRunFailed(run.Id, reason));

        await db.SaveChangesAsync();
    }
}
