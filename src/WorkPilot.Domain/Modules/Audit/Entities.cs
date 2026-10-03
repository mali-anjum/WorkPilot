using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Audit;

/// <summary>Append only. Soft deleted like every audit relevant entity, but never truly removed (spec 0002).</summary>
public class AuditLog : SoftDeletableEntity
{
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required Guid TargetId { get; init; }
    public string? Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The activity feed's domain, set once from <see cref="AuditCategories.For"/> when the entry is recorded (spec 0011).</summary>
    public ActivityCategory Category { get; init; } = ActivityCategory.System;
}
