using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>Persistence port for job ingestion (spec 0008). Adds are staged until <see cref="SaveChangesAsync"/>.</summary>
public interface IJobIngestionRepository
{
    /// <summary>Loads a job source by id, or <c>null</c>.</summary>
    Task<JobSource?> GetSourceAsync(Guid jobSourceId, CancellationToken cancellationToken);

    /// <summary>Finds a job source by its unique <c>(Type, Name)</c>, or <c>null</c>.</summary>
    Task<JobSource?> FindSourceAsync(string type, string name, CancellationToken cancellationToken);

    /// <summary>Stages a new job source.</summary>
    void AddSource(JobSource source);

    /// <summary>Loads the source's existing jobs (with their snapshots) whose external id is in <paramref name="externalIds"/>, keyed by external id.</summary>
    Task<IReadOnlyDictionary<string, Job>> GetJobsByExternalIdAsync(
        Guid jobSourceId,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken);

    /// <summary>Stages a new job (and the snapshots it carries).</summary>
    void AddJob(Job job);

    /// <summary>Stages a new snapshot of an already stored job.</summary>
    void AddSnapshot(JobSnapshot snapshot);

    /// <summary>Commits everything staged, atomically.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
