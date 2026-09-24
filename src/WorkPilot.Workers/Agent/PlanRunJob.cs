using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Workers.Agent;

/// <summary>
/// Runs the Planner and Policy Engine for one <see cref="AgentRun"/>, off the
/// request thread so a process restart mid-planning is safe (spec 0005,
/// AC-1, AC-9). Not retried by Hangfire itself: the injected <see cref="IPlanner"/>
/// already retries once internally (AC-7); a second Hangfire-level retry
/// would only duplicate that.
/// </summary>
public sealed class PlanRunJob(WorkPilotDbContext db, IPlanner planner, IToolRegistry registry, IPolicyEngine policy, IBackgroundJobClient jobs)
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
        catch (Exception ex) when (ex is PlanParseException or PlannerUnavailableException)
        {
            // A provider failure fails the run rather than stranding it at
            // Planning; the chat client's logging middleware already logged
            // the cause (spec 0006, AC-6).
            run.TransitionTo(AgentRunStatus.Failed);
            await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
            await db.SaveChangesAsync();
            return;
        }

        run.TransitionTo(AgentRunStatus.PolicyCheck);
        var violations = policy.Validate(plan, registry);
        if (violations.Count > 0)
        {
            run.TransitionTo(AgentRunStatus.Failed);
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
}
