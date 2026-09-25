using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Jobs;

/// <inheritdoc cref="IJobIngestionRepository" />
public sealed class JobIngestionRepository(WorkPilotDbContext db) : IJobIngestionRepository
{
    /// <inheritdoc />
    public Task<JobSource?> GetSourceAsync(Guid jobSourceId, CancellationToken cancellationToken) =>
        db.JobSources.FirstOrDefaultAsync(s => s.Id == jobSourceId, cancellationToken);

    /// <inheritdoc />
    public Task<JobSource?> FindSourceAsync(string type, string name, CancellationToken cancellationToken) =>
        db.JobSources.FirstOrDefaultAsync(s => s.Type == type && s.Name == name, cancellationToken);

    /// <inheritdoc />
    public void AddSource(JobSource source) => db.JobSources.Add(source);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Job>> GetJobsByExternalIdAsync(
        Guid jobSourceId,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        if (externalIds.Count == 0)
        {
            return new Dictionary<string, Job>();
        }

        // IgnoreQueryFilters: a soft deleted job still owns its (source,
        // external id) slot under the unique index, so it must be found and
        // refreshed, never re-inserted as a duplicate.
        var jobs = await db.Jobs
            .IgnoreQueryFilters()
            .Include(j => j.Snapshots)
            .Where(j => j.JobSourceId == jobSourceId && externalIds.Contains(j.ExternalId))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return jobs.ToDictionary(j => j.ExternalId, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public void AddJob(Job job) => db.Jobs.Add(job);

    // Added explicitly: a new child with a preset Guid key discovered only
    // through a tracked parent's collection would be treated as existing
    // (Unchanged) by EF Core, and never inserted.
    /// <inheritdoc />
    public void AddSnapshot(JobSnapshot snapshot) => db.JobSnapshots.Add(snapshot);

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
