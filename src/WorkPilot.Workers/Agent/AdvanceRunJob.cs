using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Workers.Agent;

/// <summary>
/// The Execution Engine: one idempotent job type that drives a whole
/// <see cref="AgentRun"/>, re-enqueuing itself after each step (spec 0005's
/// chosen design). <see cref="AgentStep.Status"/> in the database, not
/// Hangfire's own job bookkeeping, is the resumption cursor: a re-delivered
/// or re-run invocation of this job is always safe to call again (AC-9).
/// </summary>
public sealed class AdvanceRunJob(
    WorkPilotDbContext db,
    IToolRegistry registry,
    IVerificationEngine verification,
    IAuditService audit,
    IBackgroundJobClient jobs)
{
    // Hangfire's own automatic retry would re-run this job at least once more
    // on failure, which is exactly the double-execution risk this job's own
    // AgentStep.Status-driven logic exists to prevent; disabling it keeps
    // exactly one recovery mechanism, not two disagreeing ones.
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid agentRunId)
    {
        var run = await db.AgentRuns
            .Include(r => r.Steps.OrderBy(s => s.Ordinal))
            .FirstOrDefaultAsync(r => r.Id == agentRunId);

        if (run is null || run.Status is AgentRunStatus.Completed or AgentRunStatus.Failed)
        {
            return; // nothing to do; a re-delivered job for an already terminal (or gone) run is a no-op
        }

        var next = run.Steps.FirstOrDefault(s => s.Status is AgentStepStatus.Pending or AgentStepStatus.AwaitingApproval or AgentStepStatus.Running);
        if (next is null)
        {
            run.TransitionTo(AgentRunStatus.Completed);
            await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
            await db.SaveChangesAsync();
            return;
        }

        if (!registry.TryGet(next.ToolName, out var tool))
        {
            // The plan was policy checked before any step ran; a tool
            // disappearing mid-run means the registry changed under a live
            // run (a deploy), not a normal path.
            await FailStepAndRunAsync(run, next);
            return;
        }

        switch (next.Status)
        {
            case AgentStepStatus.Pending when tool.RiskTier != ToolRiskTier.AutoAllowed:
                await SuspendForApprovalAsync(run, next, tool);
                return;

            case AgentStepStatus.Pending:
                next.TransitionTo(AgentStepStatus.Running);
                // Commit "Running" before executing: a crash mid tool call is then visible on re-entry (AC-9).
                await db.SaveChangesAsync();
                break;

            case AgentStepStatus.AwaitingApproval:
                var approved = await db.Approvals.AnyAsync(a =>
                    a.TargetType == ApprovalTargets.AgentStep && a.TargetId == next.Id && a.Status == ApprovalStatus.Approved);
                if (!approved)
                {
                    return; // still waiting on a decision; the decide endpoint re-enqueues this run
                }

                next.TransitionTo(AgentStepStatus.Running);
                // Same commit-before-execute as the Pending path (AC-9).
                await db.SaveChangesAsync();
                break;

            case AgentStepStatus.Running when !tool.IsIdempotent:
                // A prior invocation of this job died mid-execution. Re-running
                // a non-idempotent tool risks a double side effect, so this
                // fails the step instead of silently retrying it (AC-9).
                await FailStepAndRunAsync(run, next);
                return;

            case AgentStepStatus.Running:
                break; // idempotent: safe to execute again
        }

        var arguments = string.IsNullOrEmpty(next.ArgumentsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(next.ArgumentsJson) ?? [];

        var result = await ExecuteWithRetryAsync(tool, new ToolExecutionContext(run.ProfileId, arguments));
        var verified = verification.Verify(tool, result);

        db.ToolCalls.Add(new ToolCall
        {
            AgentStepId = next.Id,
            ToolName = tool.Name,
            Success = verified,
        });
        // AuditLog.Payload is jsonb: OutputJson is already valid JSON (or
        // null, fine for jsonb), but Error is plain human-readable text, so
        // it has to be wrapped as JSON before it can land in that column.
        var payload = result.OutputJson ?? (result.Error is null ? null : JsonSerializer.Serialize(new { error = result.Error }));
        audit.Record("Agent", tool.Name, tool.TargetType ?? ApprovalTargets.AgentStep, result.TargetId ?? next.Id, payload);

        if (verified)
        {
            next.TransitionTo(AgentStepStatus.Succeeded);
            await db.SaveChangesAsync();
            jobs.Enqueue<AdvanceRunJob>(j => j.RunAsync(run.Id));
        }
        else
        {
            next.TransitionTo(AgentStepStatus.Failed);
            run.TransitionTo(AgentRunStatus.Failed);
            await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
            await db.SaveChangesAsync();
        }
    }

    private async Task SuspendForApprovalAsync(AgentRun run, AgentStep step, ITool tool)
    {
        step.TransitionTo(AgentStepStatus.AwaitingApproval);
        db.Approvals.Add(new Approval
        {
            TargetType = ApprovalTargets.AgentStep,
            TargetId = step.Id,
            RiskTier = tool.RiskTier.ToString(),
        });
        run.TransitionTo(AgentRunStatus.AwaitingApproval);
        audit.Record("Agent", "ApprovalRequested", ApprovalTargets.AgentStep, step.Id, null);
        await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
        await db.SaveChangesAsync();
        // No job re-enqueued: POST /internal/agent/approvals/{id}/decide resumes this run.
    }

    private async Task FailStepAndRunAsync(AgentRun run, AgentStep step)
    {
        step.TransitionTo(AgentStepStatus.Failed);
        run.TransitionTo(AgentRunStatus.Failed);
        await WorkflowMirror.SyncAsync(db, run, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private static async Task<ToolExecutionResult> ExecuteWithRetryAsync(ITool tool, ToolExecutionContext context)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var timeout = new CancellationTokenSource(tool.Timeout);
            ToolExecutionResult result;
            try
            {
                result = await tool.ExecuteAsync(context, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                result = ToolExecutionResult.Fail($"Tool \"{tool.Name}\" timed out after {tool.Timeout}.");
            }
            catch (Exception ex)
            {
                result = ToolExecutionResult.Fail(ex.Message);
            }

            if (result.Success || attempt > tool.MaxRetries)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)));
        }
    }
}
