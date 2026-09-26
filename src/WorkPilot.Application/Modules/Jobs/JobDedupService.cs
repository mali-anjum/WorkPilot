using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>What <see cref="JobDedupService.SplitAsync"/> did.</summary>
public enum SplitOutcome
{
    Split,

    /// <summary>The job is unknown or soft deleted, or the link isn't on it (404).</summary>
    NotFound,

    /// <summary>The link is the job's only link (400).</summary>
    LastLink,
}

/// <summary>The result of a split: the original job, and the new one when it happened.</summary>
public sealed record SplitResult(SplitOutcome Outcome, Guid JobId, Guid? NewJobId);

/// <summary>
/// The deduplication use cases that run outside ingestion (spec 0017): the
/// reconcile run that recomputes stale keys and merges the jobs that now
/// collide (AC-9), and the split that undoes a wrong merge (AC-8).
/// </summary>
public sealed class JobDedupService(
    IEnumerable<IJobSource> sources,
    IJobRepository repository,
    JobMerger merger,
    TimeProvider time)
{
    /// <summary>How many stale jobs one reconcile pass reads at a time.</summary>
    public const int ReconcileBatchSize = 200;

    /// <summary>
    /// Processes every stale job: recomputes its key and merges each group of
    /// jobs sharing it into the oldest, one transaction per key group, so a
    /// crash leaves the rest stale for the next run. Returns how many jobs
    /// were merged away. Running it again with nothing stale changes nothing.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var merged = 0;
        while (true)
        {
            // Read outside a transaction only to find the groups; each group reads again under its lock.
            var stale = await repository.GetStaleJobsAsync(ReconcileBatchSize, cancellationToken);
            if (stale.Count == 0)
            {
                return merged;
            }

            var groups = stale
                .GroupBy(j => JobDedupKey.For(j.Company, j.Title, j.Location))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Key: g.Key, Ids: g.Select(j => j.Id).ToList()))
                .ToList();
            foreach (var (key, ids) in groups)
            {
                merged += await repository.InTransactionAsync(ct => ReconcileKeyAsync(key, ids, ct), cancellationToken);
            }
        }
    }

    private async Task<int> ReconcileKeyAsync(string key, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await repository.LockDedupKeysAsync([key], cancellationToken);

        // The group's stale jobs, read again under the lock (a job that changed
        // meanwhile is left for the next pass); MarkCurrent gives them the new key.
        var stale = (await repository.GetJobsAsync(ids, cancellationToken))
            .Where(j => j.IsStale && JobDedupKey.For(j.Company, j.Title, j.Location) == key)
            .ToList();
        foreach (var job in stale)
        {
            job.MarkCurrent();
        }

        var merged = await merger.MergeGroupAsync(key, stale, JobMerger.Reasons.Reconcile, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return merged;
    }

    /// <summary>
    /// Moves link <paramref name="linkId"/> off job <paramref name="jobId"/>
    /// into a new job of its own, rebuilt from that link's latest snapshot,
    /// and audits it (AC-8, AC-12). Applications and matches stay on the
    /// original job.
    /// </summary>
    public Task<SplitResult> SplitAsync(Guid jobId, Guid linkId, CancellationToken cancellationToken) =>
        repository.InTransactionAsync(async ct =>
        {
            var job = await repository.GetJobAsync(jobId, ct);
            if (job is null || job.IsDeleted || job.Links.All(l => l.Id != linkId))
            {
                return new SplitResult(SplitOutcome.NotFound, jobId, null);
            }

            if (job.Links.Count == 1)
            {
                return new SplitResult(SplitOutcome.LastLink, jobId, null);
            }

            // Both halves may take this key, so a concurrent ingestion must wait for the split.
            await repository.LockDedupKeysAsync([job.DedupKey ?? JobDedupKey.For(job.Company, job.Title, job.Location)], ct);

            var postings = await PostingResolver.CreateAsync(repository, sources, [job], ct);
            var created = job.SplitLink(linkId, time.GetUtcNow(), postings.Of);
            repository.AddJob(created);
            merger.RecordSplit(job.Id, created.Id, linkId);
            await repository.SaveChangesAsync(ct);
            return new SplitResult(SplitOutcome.Split, job.Id, created.Id);
        }, cancellationToken);
}
