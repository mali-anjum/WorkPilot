using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Jobs;

/// <inheritdoc cref="IJobRepository" />
public sealed class JobRepository(WorkPilotDbContext db) : IJobRepository
{
    /// <summary>
    /// The first key of every dedup advisory lock (<c>pg_advisory_xact_lock(int, int)</c>),
    /// so these locks never collide with another feature's (17 for spec 0017).
    /// </summary>
    public const int DedupLockClass = 17;

    /// <summary>How many times a unit runs when another writer changed one of its jobs first.</summary>
    public const int MaxConcurrencyAttempts = 3;

    /// <inheritdoc />
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        // Through the execution strategy, so a retrying one (Aspire's default)
        // allows the transaction; each attempt starts from a clean tracker.
        var strategy = db.Database.CreateExecutionStrategy();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await strategy.ExecuteAsync(
                    work,
                    async (_, unit, ct) =>
                    {
                        db.ChangeTracker.Clear();
                        await using var transaction = await db.Database.BeginTransactionAsync(ct);
                        var result = await unit(ct);
                        await transaction.CommitAsync(ct);
                        return result;
                    },
                    verifySucceeded: null,
                    cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // A job's xmin changed under this unit (a split, an ingestion or a
                // reconcile committed first): nothing was saved, so run it again on
                // fresh data rather than overwrite the other writer's change.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetDedupKeyAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.DedupKey, j.Company, j.Title, j.Location })
            .FirstOrDefaultAsync(cancellationToken);
        return job is null ? null : job.DedupKey ?? JobDedupKey.For(job.Company, job.Title, job.Location);
    }

    /// <inheritdoc />
    public async Task LockDedupKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        // Sorted lock ids, so two runs locking overlapping keys never deadlock.
        var lockIds = keys.Select(LockIdOf).Distinct().Order().ToList();
        foreach (var lockId in lockIds)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DedupLockClass}, {lockId})", cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<JobSource?> GetSourceAsync(Guid jobSourceId, CancellationToken cancellationToken) =>
        db.JobSources.FirstOrDefaultAsync(s => s.Id == jobSourceId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, JobSource>> GetSourcesAsync(IEnumerable<Guid> jobSourceIds, CancellationToken cancellationToken)
    {
        var ids = jobSourceIds.Distinct().ToList();
        return await db.JobSources.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobSource?> FindSourceAsync(string type, string name, CancellationToken cancellationToken) =>
        db.JobSources.FirstOrDefaultAsync(s => s.Type == type && s.Name == name, cancellationToken);

    /// <inheritdoc />
    public void AddSource(JobSource source) => db.JobSources.Add(source);

    /// <inheritdoc />
    public Task<Job?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Jobs().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken) =>
        jobIds.Count == 0 ? [] : await Jobs().Where(j => jobIds.Contains(j.Id)).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Job>> GetJobsByLinkAsync(
        Guid jobSourceId,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        if (externalIds.Count == 0)
        {
            return new Dictionary<string, Job>();
        }

        var jobs = await Jobs()
            .Where(j => j.Links.Any(l => l.JobSourceId == jobSourceId && externalIds.Contains(l.ExternalId)))
            .ToListAsync(cancellationToken);

        return jobs
            .SelectMany(j => j.Links.Where(l => l.JobSourceId == jobSourceId).Select(l => (l.ExternalId, Job: j)))
            .ToDictionary(x => x.ExternalId, x => x.Job, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> GetJobsByKeyAsync(string dedupKey, CancellationToken cancellationToken) =>
        await Jobs().Where(j => j.DedupKey == dedupKey).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> GetJobsLinkedToSourceAsync(Guid jobSourceId, CancellationToken cancellationToken) =>
        await Jobs().Where(j => j.Links.Any(l => l.JobSourceId == jobSourceId)).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> GetStaleJobsAsync(int take, CancellationToken cancellationToken) =>
        await Jobs()
            .Where(j => j.DedupRuleVersion < JobDedupKey.CurrentRuleVersion)
            .OrderBy(j => j.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public void AddJob(Job job) => db.Jobs.Add(job);

    // Added explicitly: a new child with a preset Guid key discovered only
    // through a tracked parent's collection would be treated as existing
    // (Unchanged) by EF Core, and never inserted.
    /// <inheritdoc />
    public void AddLink(JobSourceLink link) => db.JobSourceLinks.Add(link);

    /// <inheritdoc />
    public void AddSnapshot(JobSnapshot snapshot) => db.JobSnapshots.Add(snapshot);

    /// <inheritdoc />
    public void RemoveJob(Job job) => db.Jobs.Remove(job);

    /// <inheritdoc />
    public async Task ReassignMatchesAsync(Guid fromJobId, Guid toJobId, CancellationToken cancellationToken)
    {
        await db.JobMatches
            .Where(m => m.JobId == fromJobId && db.JobMatches.Any(t => t.JobId == toJobId && t.ProfileId == m.ProfileId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.JobMatches
            .Where(m => m.JobId == fromJobId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JobId, toJobId), cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    // A soft deleted job still owns its links, so every lookup sees it (and can revive it).
    private IQueryable<Job> Jobs() =>
        db.Jobs.IgnoreQueryFilters().Include(j => j.Links).Include(j => j.Snapshots).AsSplitQuery();

    // The first 4 bytes of the key's SHA-256 hex, as the second advisory lock key.
    private static int LockIdOf(string key) =>
        BinaryPrimitives.ReadInt32BigEndian(Convert.FromHexString(key.AsSpan(0, 8)));
}
