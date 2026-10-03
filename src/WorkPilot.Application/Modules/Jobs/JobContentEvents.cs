using WorkPilot.Application.Common;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>
/// Publishes <see cref="JobContentChanged"/> for the jobs a unit of work created or whose primary
/// content changed (spec 0019, AC-1). A job's content is its primary link plus that link's latest
/// snapshot hash: the caller stamps the jobs it loaded before changing them, then publishes after,
/// inside the same <c>InTransactionAsync</c> unit and before its save, so a rerun of the unit
/// stamps and publishes again on fresh data.
/// </summary>
public sealed class JobContentEvents(IEventPublisher events)
{
    private readonly Dictionary<Guid, string> _before = [];

    /// <summary>Records the content of <paramref name="jobs"/> before the unit changes them (the first stamp of a job wins).</summary>
    public void Stamp(IEnumerable<Job> jobs)
    {
        foreach (var job in jobs)
        {
            _before.TryAdd(job.Id, ContentOf(job));
        }
    }

    /// <summary>
    /// Publishes one event per job in <paramref name="jobs"/> that is new (never stamped) or whose
    /// content differs from its stamp. A job left with no links was merged away and is skipped.
    /// Returns how many were published.
    /// </summary>
    public int PublishChanges(IEnumerable<Job> jobs)
    {
        var published = 0;
        foreach (var job in jobs.DistinctBy(j => j.Id))
        {
            if (job.Links.Count == 0)
            {
                continue;
            }

            var now = ContentOf(job);
            if (_before.TryGetValue(job.Id, out var before) && before == now)
            {
                continue;
            }

            events.Publish(new JobContentChanged(job.Id, HashOf(job) ?? string.Empty));
            _before[job.Id] = now;
            published++;
        }

        return published;
    }

    private static string ContentOf(Job job) => $"{job.PrimaryLinkId}:{HashOf(job)}";

    private static string? HashOf(Job job) =>
        job.PrimaryLink is { } primary ? job.LatestSnapshotOf(primary)?.ContentHash : null;
}
