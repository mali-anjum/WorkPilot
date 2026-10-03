using Hangfire;
using Microsoft.Extensions.Logging;
using WorkPilot.Application.Modules.Jobs.Matching;

namespace WorkPilot.Workers.Jobs;

/// <summary>
/// Reads one job's requirements, then scores it for every profile (spec 0019, AC-1, AC-8). Three
/// runs at most: a failed attempt is counted and committed, then rethrown so Hangfire retries;
/// the third failure marks the row Failed and the job still scores from its own columns. A
/// missing or soft deleted job is a quiet no op.
/// </summary>
public sealed class ExtractJobRequirementsJob(JobMatchingService matching, ILogger<ExtractJobRequirementsJob> logger)
{
    /// <summary>Extracts (unless current and not <paramref name="force"/>d), then scores.</summary>
    [DisableConcurrentExecution("jobs.extract-requirements:{0}", 120)]
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid jobId, bool force)
    {
        var started = TimeProvider.System.GetTimestamp();
        var outcome = await matching.ExtractAsync(jobId, force, CancellationToken.None);
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        if (outcome == ExtractionOutcome.NoJob)
        {
            logger.LogInformation("Job {JobId} is gone or deleted; no requirements to extract.", jobId);
            return;
        }

        // Never the description text: only ids, outcome and timing (spec 0019, Observability).
        logger.LogInformation("Requirements extraction for job {JobId}: {Outcome} in {ElapsedMs} ms.", jobId, outcome, (long)elapsed.TotalMilliseconds);
        if (outcome == ExtractionOutcome.Failed)
        {
            logger.LogWarning("Requirements extraction for job {JobId} failed for the last time; scoring from the job's own columns.", jobId);
        }

        if (outcome == ExtractionOutcome.RetryLater)
        {
            throw new InvalidOperationException($"Requirements extraction for job {jobId} failed; Hangfire retries it.");
        }

        var summary = await matching.ScoreJobAsync(jobId, CancellationToken.None);
        logger.LogInformation(
            "Scored job {JobId}: {Written} written, {Skipped} unchanged, {Matched} strong matches.",
            jobId, summary.Written, summary.Skipped, summary.Matched);
    }
}

/// <summary>Scores every job for one profile, after a match profile change (spec 0019, AC-7).</summary>
public sealed class RescoreProfileJob(JobMatchingService matching, ILogger<RescoreProfileJob> logger)
{
    /// <summary>Rescores <paramref name="profileId"/>'s matches in batches.</summary>
    [DisableConcurrentExecution("jobs.rescore-profile:{0}", 600)]
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid profileId)
    {
        var summary = await matching.RescoreProfileAsync(profileId, CancellationToken.None);
        logger.LogInformation(
            "Rescored profile {ProfileId}: {Written} written, {Skipped} unchanged, {Matched} strong matches.",
            profileId, summary.Written, summary.Skipped, summary.Matched);
    }
}

/// <summary>
/// Keeps matches fresh (spec 0019): queues extraction for jobs with no, stale, lost or due failed
/// requirements, and a rescore for profiles with old or missing matches. Hourly, and once on Api start.
/// </summary>
public sealed class MatchSweepJob(JobMatchingService matching, ILogger<MatchSweepJob> logger)
{
    /// <summary>Recurring job id.</summary>
    public const string RecurringJobId = "jobs.match-sweep";

    /// <summary>Runs one sweep.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync()
    {
        var work = await matching.SweepAsync(CancellationToken.None);
        logger.LogInformation(
            "Match sweep queued {Jobs} extractions and {Profiles} profile rescores.",
            work.JobsToExtract.Count, work.ProfilesToRescore.Count);
    }
}

/// <inheritdoc cref="IMatchJobScheduler" />
public sealed class HangfireMatchJobScheduler(IBackgroundJobClient jobs) : IMatchJobScheduler
{
    /// <inheritdoc />
    public void EnqueueExtraction(Guid jobId, bool force) =>
        jobs.Enqueue<ExtractJobRequirementsJob>(j => j.RunAsync(jobId, force));

    /// <inheritdoc />
    public void EnqueueProfileRescore(Guid profileId) =>
        jobs.Enqueue<RescoreProfileJob>(j => j.RunAsync(profileId));
}
