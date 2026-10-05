using WorkPilot.Application.Common;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>The read side of the shared job catalog for the jobs pages (spec 0021): filter choices, one job, and the sources.</summary>
public interface IJobCatalogQueries
{
    /// <summary>The companies, remote types and sources present on non deleted jobs (AC-2).</summary>
    Task<JobFacetsDto> GetFacetsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One job with every link, soft deleted ones included (marked), or null when unknown (AC-5).
    /// <paramref name="profileId"/>, when given, fills <see cref="JobDetailView.Dismissed"/>.
    /// </summary>
    Task<JobDetailView?> GetJobAsync(Guid jobId, Guid? profileId, CancellationToken cancellationToken);

    /// <summary>Every source with its job count and its latest ingestion run, read from the audit log (AC-6).</summary>
    Task<IReadOnlyList<JobSourceSummaryDto>> ListSourcesAsync(CancellationToken cancellationToken);
}

/// <summary>Persistence port for <c>job_dismissals</c> (spec 0021); only the Jobs module writes it.</summary>
public interface IJobDismissalRepository
{
    /// <summary>Whether the job exists, soft deleted or not.</summary>
    Task<bool> JobExistsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Whether the profile exists.</summary>
    Task<bool> ProfileExistsAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the dismissal unless that (profile, job) already has one; runs immediately.
    /// False when the job was removed (a merge) after the caller checked it exists.
    /// </summary>
    Task<bool> AddAsync(JobDismissal dismissal, CancellationToken cancellationToken);

    /// <summary>Deletes the (profile, job) dismissal, if there is one; runs immediately.</summary>
    Task RemoveAsync(Guid profileId, Guid jobId, CancellationToken cancellationToken);
}

/// <summary>Dismiss and undo (spec 0021, AC-4): both are safe to repeat, and never change the shared job.</summary>
public sealed class JobDismissalService(IJobDismissalRepository repository, TimeProvider time)
{
    /// <summary>Hides the job from the profile's list; dismissing twice keeps the first one. 404 for an unknown job or profile.</summary>
    public async Task<Result<bool>> DismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        if (await MissingAsync(jobId, profileId, cancellationToken) is { } missing)
        {
            return missing;
        }

        return await repository.AddAsync(JobDismissal.Create(profileId, jobId, time.GetUtcNow()), cancellationToken)
            ? Result<bool>.Ok(true)
            : Result<bool>.NotFound("That job does not exist.");
    }

    /// <summary>Shows the job again; fine when it was not dismissed. 404 for an unknown job or profile.</summary>
    public async Task<Result<bool>> UndoAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        if (await MissingAsync(jobId, profileId, cancellationToken) is { } missing)
        {
            return missing;
        }

        await repository.RemoveAsync(profileId, jobId, cancellationToken);
        return Result<bool>.Ok(true);
    }

    private async Task<Result<bool>?> MissingAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        if (!await repository.JobExistsAsync(jobId, cancellationToken))
        {
            return Result<bool>.NotFound("That job does not exist.");
        }

        return await repository.ProfileExistsAsync(profileId, cancellationToken)
            ? null
            : Result<bool>.NotFound("That profile does not exist.");
    }
}
