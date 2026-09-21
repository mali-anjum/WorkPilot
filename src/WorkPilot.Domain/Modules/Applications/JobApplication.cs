using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Applications;

public enum ApplicationStatus
{
    Discovered,
    Matched,
    Preparing,
    PendingApproval,
    Submitted,
    Interviewing,
    Offered,
    Rejected,
    Withdrawn,
}

/// <summary>
/// One job application and its pipeline state. Transitions are enforced here,
/// not by a raw setter, so an invalid jump (e.g. Discovered straight to
/// Submitted) can never reach the database (spec 0002, AC-4).
/// </summary>
public class JobApplication : SoftDeletableEntity
{
    private static readonly Dictionary<ApplicationStatus, ApplicationStatus[]> ValidTransitions = new()
    {
        [ApplicationStatus.Discovered] = [ApplicationStatus.Matched, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Matched] = [ApplicationStatus.Preparing, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Preparing] = [ApplicationStatus.PendingApproval, ApplicationStatus.Withdrawn],
        [ApplicationStatus.PendingApproval] = [ApplicationStatus.Submitted, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Submitted] = [ApplicationStatus.Interviewing, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Interviewing] = [ApplicationStatus.Offered, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Offered] = [],
        [ApplicationStatus.Rejected] = [],
        [ApplicationStatus.Withdrawn] = [],
    };

    public required Guid JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required Guid ResumeVersionId { get; set; }
    public Guid? CoverLetterVersionId { get; set; }
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Discovered;

    public List<ApplicationAnswer> Answers { get; init; } = [];
    public List<ApplicationEvent> Events { get; init; } = [];

    /// <summary>Moves to <paramref name="next"/>, or throws if that transition is not allowed from the current status.</summary>
    public void TransitionTo(ApplicationStatus next)
    {
        if (!ValidTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidOperationException($"Cannot transition a job application from {Status} to {next}.");
        }

        Status = next;
    }
}

public class ApplicationAnswer : Entity
{
    public required Guid JobApplicationId { get; init; }
    public required string Question { get; set; }
    public required string Answer { get; set; }
}

/// <summary>An append only entry in an application's history. Never deleted, even when the application itself is soft deleted.</summary>
public class ApplicationEvent : Entity
{
    public required Guid JobApplicationId { get; init; }
    public required string EventType { get; init; }
    public string? Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
