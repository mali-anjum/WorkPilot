using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Application.Modules.Approvals;

/// <summary>An approval with the step it gates and that step's run, loaded for a decision (tracked, so changes persist on save).</summary>
public sealed record ApprovalDecisionContext(Approval Approval, AgentStep Step, AgentRun Run);

/// <summary>How persisting a decision went.</summary>
public enum SaveDecisionOutcome
{
    /// <summary>Everything committed together.</summary>
    Saved,

    /// <summary>The approval was no longer <c>Pending</c> when the update ran; nothing committed.</summary>
    AlreadyDecided,

    /// <summary>The run changed underneath the decision (its concurrency token moved); nothing committed.</summary>
    ConcurrentChange,
}

/// <summary>One pending approval as read for the Approval center, before mapping to its DTO.</summary>
public sealed record PendingApprovalRow(
    Guid ApprovalId,
    string RiskTier,
    DateTimeOffset RequestedAt,
    Guid AgentRunId,
    string Goal,
    int StepOrdinal,
    string ToolName,
    string? ArgumentsJson,
    string? EvidenceJson);

/// <summary>One decided approval as read for the Approval center.</summary>
public sealed record DecidedApprovalRow(
    Guid ApprovalId,
    string ToolName,
    string Goal,
    string RiskTier,
    ApprovalStatus Status,
    DateTimeOffset? DecidedAt,
    Guid? DecidedBy,
    bool ExplicitlyConfirmed);

/// <summary>Persistence for approvals gating agent steps (spec 0007). Implemented in Infrastructure.</summary>
public interface IApprovalRepository
{
    /// <summary>Loads an approval that targets an <c>AgentStep</c>, with its step and run, or null when there is none.</summary>
    Task<ApprovalDecisionContext?> LoadForDecisionAsync(Guid approvalId, CancellationToken cancellationToken);

    /// <summary>
    /// Commits every pending change (the decision, the step/run transitions,
    /// the audit row) in one transaction, guarded by the approval's status
    /// and the run's concurrency token (AC-9).
    /// </summary>
    Task<SaveDecisionOutcome> SaveDecisionAsync(ApprovalDecisionContext context, CancellationToken cancellationToken);

    /// <summary>Every pending approval on runs owned by <paramref name="profileId"/>, oldest first.</summary>
    Task<IReadOnlyList<PendingApprovalRow>> ListPendingAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> decided approvals on runs owned by <paramref name="profileId"/>, newest first.</summary>
    Task<IReadOnlyList<DecidedApprovalRow>> ListRecentlyDecidedAsync(Guid profileId, int limit, CancellationToken cancellationToken);
}
