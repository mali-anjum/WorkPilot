using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Approvals;

/// <summary>Well known <see cref="Approval.TargetType"/> values, so callers never hand-type the string.</summary>
public static class ApprovalTargets
{
    /// <summary>An approval gating one planned <c>AgentStep</c> (spec 0005; a pending approval has no <c>ToolCall</c> row yet, so it targets the step, not the call).</summary>
    public const string AgentStep = "AgentStep";
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// A pending or decided approval on some other entity (a submission, an
/// outreach send). <see cref="TargetType"/>/<see cref="TargetId"/> is a
/// deliberate polymorphic reference (spec 0002 Consequences): it trades a
/// real foreign key for the ability to approve any entity type.
/// </summary>
public class Approval : Entity
{
    public required string TargetType { get; init; }
    public required Guid TargetId { get; init; }
    public required string RiskTier { get; init; }
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public DateTimeOffset? DecidedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }

    /// <summary>Records a decision. Throws if this approval was already decided (the API layer's own atomic guard, spec 0005, is the real concurrency defense; this is the in-process invariant).</summary>
    public void Decide(ApprovalStatus decision, Guid decidedBy, DateTimeOffset decidedAt)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException($"Approval {Id} was already decided ({Status}).");
        }

        if (decision == ApprovalStatus.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "A decision must be Approved or Rejected.");
        }

        Status = decision;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
    }
}
