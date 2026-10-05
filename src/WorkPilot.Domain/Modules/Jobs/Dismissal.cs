namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>
/// One profile ruled one job out (spec 0021, AC-4): the job is hidden from that profile's list by
/// default. At most one per (profile, job); undo deletes it, since it is a preference, not history.
/// Dismissing never changes the shared job.
/// </summary>
public class JobDismissal
{
    public Guid ProfileId { get; private init; }
    public Guid JobId { get; private init; }
    public DateTimeOffset DismissedAt { get; private init; }

    /// <summary>A dismissal of <paramref name="jobId"/> by <paramref name="profileId"/> at <paramref name="at"/>.</summary>
    public static JobDismissal Create(Guid profileId, Guid jobId, DateTimeOffset at)
    {
        if (profileId == Guid.Empty || jobId == Guid.Empty)
        {
            throw new ArgumentException("A dismissal needs a profile and a job.");
        }

        return new JobDismissal { ProfileId = profileId, JobId = jobId, DismissedAt = at };
    }
}
