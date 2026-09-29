using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Approvals;

/// <summary>An approval was created and now waits for a decision (spec 0018 event catalog).</summary>
public sealed record ApprovalRequested(Guid ApprovalId, string TargetType, Guid TargetId) : IDomainEvent
{
    public static string EventName => "approvals.approval-requested.v1";
}

/// <summary>An approval was approved or rejected; <see cref="Status"/> is the <see cref="ApprovalStatus"/> name.</summary>
public sealed record ApprovalDecided(Guid ApprovalId, string Status) : IDomainEvent
{
    public static string EventName => "approvals.approval-decided.v1";
}
