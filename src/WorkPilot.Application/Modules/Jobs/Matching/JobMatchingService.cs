using WorkPilot.Application.Common;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;

namespace WorkPilot.Application.Modules.Jobs.Matching;

/// <summary>How one extraction run ended.</summary>
public enum ExtractionOutcome
{
    /// <summary>The job is missing or soft deleted; nothing was done.</summary>
    NoJob,

    /// <summary>The row already held this content by the current extractor; no AI call.</summary>
    AlreadyCurrent,

    Extracted,

    /// <summary>The attempt failed and another one should follow (the caller rethrows).</summary>
    RetryLater,

    /// <summary>The third attempt failed: the row is Failed (AC-8).</summary>
    Failed,
}

/// <summary>Counts from one scoring pass.</summary>
public sealed record ScoringSummary(int Written, int Skipped, int Matched);

/// <summary>
/// The matching use cases (spec 0019): read a job's requirements once per content, score it for
/// every profile, rescore a profile, the upkeep sweep, and the manual Rescore. The rules live in
/// the Domain (<see cref="MatchScorer"/>, <see cref="JobRequirement"/>); this only orchestrates.
/// </summary>
public sealed class JobMatchingService(
    IMatchRepository repository,
    IJobRequirementExtractor extractor,
    IEventPublisher events,
    IMatchJobScheduler scheduler,
    MatchingSettings settings,
    TimeProvider time)
{
    /// <summary>How many jobs one rescore transaction scores.</summary>
    public const int RescoreBatchSize = 200;

    /// <summary>
    /// Reads <paramref name="jobId"/>'s requirements for its current content, unless they are
    /// already current and <paramref name="force"/> is false. A failed attempt is counted and
    /// committed; the third one marks the row Failed (AC-1, AC-8, AC-12).
    /// </summary>
    public async Task<ExtractionOutcome> ExtractAsync(Guid jobId, bool force, CancellationToken cancellationToken)
    {
        var content = await repository.GetJobContentAsync(jobId, cancellationToken);
        if (content is null)
        {
            return ExtractionOutcome.NoJob;
        }

        var row = await repository.GetRequirementAsync(jobId, cancellationToken);
        if (row is not null && !force && row.IsCurrentFor(content.ContentHash))
        {
            return ExtractionOutcome.AlreadyCurrent;
        }

        var now = time.GetUtcNow();
        if (row is null)
        {
            row = JobRequirement.Start(jobId, content.ContentHash, now);
            repository.AddRequirement(row);
        }
        else
        {
            row.MarkPending(content.ContentHash, now);
        }

        await repository.SaveChangesAsync(cancellationToken);

        try
        {
            var extraction = await extractor.ExtractAsync(settings.Truncate(content.Description), cancellationToken);
            row.MarkExtracted(MatchingJson.Serialize(extraction.Requirements), extraction.Model, time.GetUtcNow());
            await repository.SaveChangesAsync(cancellationToken);
            return ExtractionOutcome.Extracted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var final = row.RecordFailure($"{ex.GetType().Name}: {ex.Message}");
            await repository.SaveChangesAsync(cancellationToken);
            return final ? ExtractionOutcome.Failed : ExtractionOutcome.RetryLater;
        }
    }

    /// <summary>Scores one job for every profile, in one transaction (AC-1, AC-7, AC-17).</summary>
    public async Task<ScoringSummary> ScoreJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var profileIds = await repository.GetProfileIdsAsync(cancellationToken);
        return await repository.InTransactionAsync(
            async ct =>
            {
                var jobs = await repository.GetScoringJobsAsync([jobId], ct);
                var total = new ScoringSummary(0, 0, 0);
                if (jobs.Count == 0)
                {
                    return total;
                }

                foreach (var profileId in profileIds)
                {
                    var profile = await repository.GetMatchProfileAsync(profileId, ct);
                    if (profile is not null)
                    {
                        total = Add(total, await ScoreAsync(profile, jobs, ct));
                    }
                }

                await repository.SaveChangesAsync(ct);
                return total;
            },
            cancellationToken);
    }

    /// <summary>Scores every non deleted job for one profile, <see cref="RescoreBatchSize"/> jobs per transaction (AC-7).</summary>
    public async Task<ScoringSummary> RescoreProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var total = new ScoringSummary(0, 0, 0);
        Guid? after = null;
        while (true)
        {
            var ids = await repository.GetJobIdsPageAsync(after, RescoreBatchSize, cancellationToken);
            if (ids.Count == 0)
            {
                return total;
            }

            after = ids[^1];
            var batch = await repository.InTransactionAsync(
                async ct =>
                {
                    var profile = await repository.GetMatchProfileAsync(profileId, ct);
                    if (profile is null)
                    {
                        return (ScoringSummary?)null;
                    }

                    var summary = await ScoreAsync(profile, await repository.GetScoringJobsAsync(ids, ct), ct);
                    await repository.SaveChangesAsync(ct);
                    return summary;
                },
                cancellationToken);
            if (batch is null)
            {
                return total;
            }

            total = Add(total, batch);
        }
    }

    /// <summary>
    /// Queues every extraction and profile rescore the sweep finds (spec 0019, <c>MatchSweepJob</c>).
    /// With <paramref name="rescoreEveryProfile"/> (the run on Api start) every profile is rescored:
    /// a changed <c>Matching</c> config is only visible to the fingerprint check, which then rewrites
    /// just the matches it changes (AC-7).
    /// </summary>
    public async Task<MatchSweepWork> SweepAsync(bool rescoreEveryProfile, CancellationToken cancellationToken)
    {
        var work = await repository.FindSweepWorkAsync(time.GetUtcNow(), cancellationToken);
        if (rescoreEveryProfile)
        {
            work = work with { ProfilesToRescore = await repository.GetProfileIdsAsync(cancellationToken) };
        }

        foreach (var jobId in work.JobsToExtract)
        {
            scheduler.EnqueueExtraction(jobId, force: false);
        }

        foreach (var profileId in work.ProfilesToRescore)
        {
            scheduler.EnqueueProfileRescore(profileId);
        }

        return work;
    }

    /// <summary>
    /// The Rescore button (AC-10): moves the job's requirements to Pending and queues a forced
    /// extraction, which rescores every profile. Queues nothing when extraction is already Pending.
    /// </summary>
    public async Task<Result<RescoreMatchResponse>> RequestRescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        var content = await repository.GetJobContentAsync(jobId, cancellationToken);
        if (content is null || await repository.GetMatchProfileAsync(profileId, cancellationToken) is null)
        {
            return Result<RescoreMatchResponse>.NotFound("That job or profile does not exist.");
        }

        var row = await repository.GetRequirementAsync(jobId, cancellationToken);
        if (row is { Status: RequirementsStatus.Pending })
        {
            return Result<RescoreMatchResponse>.Ok(new RescoreMatchResponse(false));
        }

        if (row is null)
        {
            repository.AddRequirement(JobRequirement.Start(jobId, content.ContentHash, time.GetUtcNow()));
        }
        else
        {
            row.MarkPending(content.ContentHash, time.GetUtcNow());
        }

        await repository.SaveChangesAsync(cancellationToken);
        scheduler.EnqueueExtraction(jobId, force: true);
        return Result<RescoreMatchResponse>.Ok(new RescoreMatchResponse(true));
    }

    // Scores these jobs for one profile inside the caller's transaction: skips a match whose
    // fingerprint is unchanged, upserts the rest, and publishes JobMatched on a threshold crossing.
    private async Task<ScoringSummary> ScoreAsync(MatchProfileInput profile, IReadOnlyList<ScoringJobRow> jobs, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return new ScoringSummary(0, 0, 0);
        }

        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var profileFingerprint = profile.Fingerprint();
        var stored = await repository.GetStoredMatchesAsync(profile.ProfileId, jobs.Select(j => j.JobId).ToList(), cancellationToken);
        int written = 0, skipped = 0, matched = 0;
        foreach (var job in jobs)
        {
            var input = ToInput(job);
            var fingerprint = MatchFingerprint.Compute(
                new RequirementsStamp(job.ContentHash, job.ExtractorVersion, job.Status),
                input,
                profileFingerprint,
                settings,
                profile.HasOpenEndedExperience,
                today);
            var previous = stored.GetValueOrDefault(job.JobId);
            if (previous?.InputsFingerprint == fingerprint)
            {
                skipped++;
                continue;
            }

            var result = MatchScorer.Score(input, profile, settings, today);
            var wrote = await repository.UpsertMatchAsync(
                new MatchWrite(
                    job.JobId,
                    profile.ProfileId,
                    result.Score,
                    result.Confidence,
                    result.HasBlocker,
                    MatchingJson.Serialize(result.Explanation),
                    fingerprint,
                    MatchScorer.ScoringVersion,
                    now),
                cancellationToken);
            if (!wrote)
            {
                skipped++;
                continue;
            }

            written++;
            if (CrossesThreshold(previous, result, profile.StrongMatchThreshold))
            {
                events.Publish(new JobMatched(job.JobId, profile.ProfileId, result.Score!.Value));
                matched++;
            }
        }

        return new ScoringSummary(written, skipped, matched);
    }

    /// <summary>
    /// AC-17: the new score is at or above the threshold without a blocker, and the previous one
    /// was absent, null, below the threshold, or blocked.
    /// </summary>
    public static bool CrossesThreshold(StoredMatch? previous, MatchResult result, int threshold)
    {
        if (result.Score is not { } score || score < threshold || result.HasBlocker)
        {
            return false;
        }

        return previous is null || previous.Score is null || previous.Score < threshold || previous.HasBlocker;
    }

    private JobMatchInput ToInput(ScoringJobRow job)
    {
        var failed = job.Status == RequirementsStatus.Failed;
        var requirements = !failed && job.RequirementsJson is { } json ? MatchingJson.Deserialize<JobRequirementsV1>(json) : null;
        return new JobMatchInput(
            job.JobId,
            job.Title,
            job.Location,
            job.RemoteType,
            settings.Truncate(job.Description),
            requirements,
            failed || requirements is null);
    }

    private static ScoringSummary Add(ScoringSummary a, ScoringSummary b) =>
        new(a.Written + b.Written, a.Skipped + b.Skipped, a.Matched + b.Matched);
}

/// <summary>Queues requirements extraction when a job's content changed (spec 0019, AC-1).</summary>
public sealed class EnqueueRequirementExtraction(IMatchJobScheduler scheduler) : IEventHandler<JobContentChanged>
{
    public static string HandlerKey => "jobs.enqueue-requirement-extraction";

    public Task HandleAsync(JobContentChanged domainEvent, CancellationToken cancellationToken)
    {
        scheduler.EnqueueExtraction(domainEvent.JobId, force: false);
        return Task.CompletedTask;
    }
}

/// <summary>Queues a rescore of every job when a match profile changed (spec 0019, AC-7).</summary>
public sealed class EnqueueProfileRescore(IMatchJobScheduler scheduler) : IEventHandler<Domain.Modules.Profile.MatchProfileChanged>
{
    public static string HandlerKey => "jobs.enqueue-profile-rescore";

    public Task HandleAsync(Domain.Modules.Profile.MatchProfileChanged domainEvent, CancellationToken cancellationToken)
    {
        scheduler.EnqueueProfileRescore(domainEvent.ProfileId);
        return Task.CompletedTask;
    }
}
