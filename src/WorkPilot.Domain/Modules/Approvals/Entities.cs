using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Agent;

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
/// real foreign key for the ability to approve any entity type. Spec 0007
/// adds the frozen evidence snapshot the Approval center shows, the time it
/// was requested, and whether an explicit confirmation tier approval was
/// actually confirmed.
/// </summary>
public class Approval : Entity
{
    public required string TargetType { get; init; }
    public required Guid TargetId { get; init; }
    public required string RiskTier { get; init; }
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public DateTimeOffset? DecidedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }

    /// <summary>Immutable evidence snapshot (a serialized <see cref="ApprovalEvidence"/>) captured when the approval was requested, so the approver sees exactly what will run (spec 0007, AC-3). Null when the tool describes no evidence.</summary>
    public string? EvidenceJson { get; init; }

    /// <summary>When the run suspended and asked for this decision.</summary>
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True only when an <see cref="ToolRiskTier.ExplicitConfirmation"/> approval was approved with the typed confirmation (spec 0007, AC-8).</summary>
    public bool ExplicitlyConfirmed { get; private set; }

    /// <summary><see cref="RiskTier"/> as the enum; an unknown stored value is treated as the strictest tier.</summary>
    public ToolRiskTier Tier => ApprovalPolicy.ParseTier(RiskTier);

    /// <summary>
    /// Records a decision. Throws if this approval was already decided, or if
    /// an explicit confirmation tier approval is approved without being
    /// confirmed. The persistence layer's concurrency guard on
    /// <see cref="Status"/> (spec 0007, AC-9) is the real defense against two
    /// concurrent decisions; this is the in process invariant.
    /// </summary>
    public void Decide(ApprovalStatus decision, Guid decidedBy, DateTimeOffset decidedAt, bool explicitlyConfirmed = false)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException($"Approval {Id} was already decided ({Status}).");
        }

        if (decision == ApprovalStatus.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "A decision must be Approved or Rejected.");
        }

        if (decision == ApprovalStatus.Approved && ApprovalPolicy.RequiresExplicitConfirmation(Tier) && !explicitlyConfirmed)
        {
            throw new InvalidOperationException($"Approval {Id} needs an explicit confirmation before it can be approved.");
        }

        Status = decision;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
        ExplicitlyConfirmed = decision == ApprovalStatus.Approved && explicitlyConfirmed;
    }
}
