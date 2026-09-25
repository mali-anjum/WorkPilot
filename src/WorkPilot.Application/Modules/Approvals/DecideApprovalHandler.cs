using System.Text.Json;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Application.Modules.Approvals;

/// <summary>A decision on one approval (spec 0007).</summary>
/// <param name="ApprovalId">The approval to decide.</param>
/// <param name="Decision"><c>Approve</c> or <c>Reject</c>.</param>
/// <param name="DecidedBy">The deciding profile, resolved by the caller from an authenticated session, never from browser input.</param>
/// <param name="Confirmation">The typed confirmation phrase, needed only to approve an explicit confirmation tier action.</param>
public sealed record DecideApprovalCommand(Guid ApprovalId, string Decision, Guid DecidedBy, string? Confirmation);

/// <summary>Why a decision did or didn't take effect; each maps to one HTTP status at the endpoint.</summary>
public enum DecideApprovalOutcome
{
    Decided,
    InvalidDecision,
    NotFound,
    NotOwner,
    ConfirmationRequired,
    AlreadyDecided,
    ConcurrentChange,
}

/// <summary>The outcome of a <see cref="DecideApprovalCommand"/>.</summary>
public sealed record DecideApprovalResult(DecideApprovalOutcome Outcome, Guid ApprovalId, ApprovalStatus? Status = null, bool Resumed = false);

/// <summary>
/// The decide use case (spec 0007, building on spec 0005's decide path):
/// checks the decision is well formed, that the decider owns the run (AC-7),
/// that an explicit confirmation tier Approve carries the typed phrase (AC-8),
/// then applies the domain transitions and the audit row and commits them all
/// at once under the repository's concurrency guard (AC-9, AC-10). On Approve
/// it schedules the run to advance; <c>AdvanceRunJob</c> re-checks the
/// policy before it executes anything (AC-2).
/// </summary>
public sealed class DecideApprovalHandler(
    IApprovalRepository approvals,
    IAuditService audit,
    IAgentRunScheduler scheduler,
    TimeProvider time)
{
    public const string Approve = "Approve";
    public const string Reject = "Reject";

    /// <summary>Decides one approval. Never throws for an expected refusal; the outcome says why.</summary>
    public async Task<DecideApprovalResult> HandleAsync(DecideApprovalCommand command, CancellationToken cancellationToken)
    {
        if (command.Decision is not (Approve or Reject))
        {
            return new DecideApprovalResult(DecideApprovalOutcome.InvalidDecision, command.ApprovalId);
        }

        var context = await approvals.LoadForDecisionAsync(command.ApprovalId, cancellationToken);
        if (context is null)
        {
            return new DecideApprovalResult(DecideApprovalOutcome.NotFound, command.ApprovalId);
        }

        var (approval, step, run) = context;

        // Nobody decides on someone else's behalf (AC-7).
        if (run.ProfileId != command.DecidedBy)
        {
            return new DecideApprovalResult(DecideApprovalOutcome.NotOwner, approval.Id);
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return new DecideApprovalResult(DecideApprovalOutcome.AlreadyDecided, approval.Id, approval.Status);
        }

        var approve = command.Decision == Approve;
        var explicitlyConfirmed = false;
        if (approve && ApprovalPolicy.RequiresExplicitConfirmation(approval.Tier))
        {
            if (!ApprovalPolicy.IsConfirmed(step.ToolName, command.Confirmation))
            {
                return new DecideApprovalResult(DecideApprovalOutcome.ConfirmationRequired, approval.Id, approval.Status);
            }

            explicitlyConfirmed = true;
        }

        var status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        approval.Decide(status, command.DecidedBy, time.GetUtcNow(), explicitlyConfirmed);

        if (approve)
        {
            // The step stays AwaitingApproval: AdvanceRunJob moves it to
            // Running itself, right after re-checking the policy (spec 0005
            // review fix, spec 0007 AC-2).
            run.TransitionTo(AgentRunStatus.Executing);
        }
        else
        {
            step.TransitionTo(AgentStepStatus.Skipped);
            run.TransitionTo(AgentRunStatus.Failed);
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                approvalId = approval.Id,
                agentRunId = run.Id,
                toolName = step.ToolName,
                riskTier = approval.RiskTier,
                decision = command.Decision,
                explicitlyConfirmed,
            },
            JsonSerializerOptions.Web);
        audit.Record(command.DecidedBy.ToString(), $"Approval{status}", ApprovalTargets.AgentStep, step.Id, payload);

        var saved = await approvals.SaveDecisionAsync(context, cancellationToken);
        switch (saved)
        {
            case SaveDecisionOutcome.AlreadyDecided:
                return new DecideApprovalResult(DecideApprovalOutcome.AlreadyDecided, approval.Id);
            case SaveDecisionOutcome.ConcurrentChange:
                return new DecideApprovalResult(DecideApprovalOutcome.ConcurrentChange, approval.Id);
        }

        if (approve)
        {
            scheduler.EnqueueAdvance(run.Id);
        }

        return new DecideApprovalResult(DecideApprovalOutcome.Decided, approval.Id, status, Resumed: approve);
    }
}
