using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Calendar;

public class CalendarEvent : Entity
{
    public required Guid ProfileId { get; init; }
    public required string ExternalCalendarId { get; init; }
    public required string Title { get; set; }
    public required DateTimeOffset StartsAt { get; set; }
    public required DateTimeOffset EndsAt { get; set; }
}
