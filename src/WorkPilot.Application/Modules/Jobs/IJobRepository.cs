using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>
/// Persistence port for the Jobs module (specs 0008, 0017). Adds and removes
/// are staged until <see cref="SaveChangesAsync"/>. Every job it returns is
/// loaded with its links and snapshots, soft deleted or not.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction and commits
    /// it, or nothing. On a transient failure the whole unit may run again on
    /// a clean change tracker, so <paramref name="work"/> must load everything
    /// it touches itself.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a transaction scoped Postgres advisory lock on each match key, in
    /// sorted order so two runs never deadlock (spec 0017, AC-10). Must be
    /// called inside <see cref="InTransactionAsync{T}"/>.
    /// </summary>
    Task LockDedupKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken);

    /// <summary>Loads a job source by id, or <c>null</c>.</summary>
    Task<JobSource?> GetSourceAsync(Guid jobSourceId, CancellationToken cancellationToken);

    /// <summary>Loads the job sources with these ids, keyed by id.</summary>
    Task<IReadOnlyDictionary<Guid, JobSource>> GetSourcesAsync(IEnumerable<Guid> jobSourceIds, CancellationToken cancellationToken);

    /// <summary>Finds a job source by its unique <c>(Type, Name)</c>, or <c>null</c>.</summary>
    Task<JobSource?> FindSourceAsync(string type, string name, CancellationToken cancellationToken);

    /// <summary>Stages a new job source.</summary>
    void AddSource(JobSource source);

    /// <summary>Loads a job by id, or <c>null</c>.</summary>
    Task<Job?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Loads the jobs with these ids.</summary>
    Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken);

    /// <summary>Loads the jobs holding a link from <paramref name="jobSourceId"/> for any of <paramref name="externalIds"/>, keyed by external id.</summary>
    Task<IReadOnlyDictionary<string, Job>> GetJobsByLinkAsync(
        Guid jobSourceId,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken);

    /// <summary>Loads every job whose stored <see cref="Job.DedupKey"/> is <paramref name="dedupKey"/>.</summary>
    Task<IReadOnlyList<Job>> GetJobsByKeyAsync(string dedupKey, CancellationToken cancellationToken);

    /// <summary>Loads every job holding at least one link from <paramref name="jobSourceId"/>.</summary>
    Task<IReadOnlyList<Job>> GetJobsLinkedToSourceAsync(Guid jobSourceId, CancellationToken cancellationToken);

    /// <summary>Loads up to <paramref name="take"/> stale jobs (<see cref="Job.IsStale"/>), oldest first.</summary>
    Task<IReadOnlyList<Job>> GetStaleJobsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Stages a new job (and the links and snapshots it carries).</summary>
    void AddJob(Job job);

    /// <summary>Stages a new link of an already stored job.</summary>
    void AddLink(JobSourceLink link);

    /// <summary>Stages a new snapshot of an already stored job.</summary>
    void AddSnapshot(JobSnapshot snapshot);

    /// <summary>Stages a hard delete of a job that was merged into another.</summary>
    void RemoveJob(Job job);

    /// <summary>
    /// Moves <paramref name="fromJobId"/>'s match rows to <paramref name="toJobId"/>,
    /// dropping each one whose profile already has a match there (matches can
    /// be recomputed). Runs immediately, inside the current transaction.
    /// </summary>
    Task ReassignMatchesAsync(Guid fromJobId, Guid toJobId, CancellationToken cancellationToken);

    /// <summary>Commits everything staged (inside a transaction, only on its commit).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
