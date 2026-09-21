using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Approvals;

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
    public required string Status { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}
