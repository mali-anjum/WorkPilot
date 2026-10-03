using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>
/// A job was created, or its primary posting's content changed (a new primary link, or a new
/// snapshot hash on it), so its requirements must be read again (spec 0019, AC-1). Published by the
/// Application use cases that change jobs, since the <see cref="Job"/> entity cannot publish.
/// </summary>
/// <param name="ContentHash">Informational: the handler's job re-reads the current state.</param>
public sealed record JobContentChanged(Guid JobId, string ContentHash) : IDomainEvent
{
    public static string EventName => "jobs.job-content-changed.v1";
}

/// <summary>
/// A job newly reached a profile's strong match threshold without a blocker (spec 0019, AC-17).
/// Notifications (spec 0020) turn it into an alert.
/// </summary>
public sealed record JobMatched(Guid JobId, Guid ProfileId, int Score) : IDomainEvent
{
    public static string EventName => "jobs.job-matched.v1";
}
