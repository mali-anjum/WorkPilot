using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Notifications;

public class Notification : SoftDeletableEntity
{
    public required Guid ProfileId { get; init; }
    public required string Type { get; init; }
    public string? Payload { get; init; }
    public DateTimeOffset? ReadAt { get; set; }
}
