using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Infrastructure.Persistence;
using ProfileEntity = WorkPilot.Domain.Modules.Profile.Profile;

namespace WorkPilot.Infrastructure.Modules.Jobs.Matching;

/// <inheritdoc cref="IMatchRepository" />
public sealed class MatchRepository(WorkPilotDbContext db) : IMatchRepository
{
    /// <summary>A Pending row older than this is treated as lost and extracted again.</summary>
    public static readonly TimeSpan LostPendingAfter = TimeSpan.FromMinutes(30);

    /// <summary>A Failed row is retried once this long after it last went Pending.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
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

    /// <inheritdoc />
    public async Task<JobContentState?> GetJobContentAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new
            {
                j.Id,
                j.Description,
                Hash = j.Snapshots
                    .Where(s => j.Links.Any(l => l.Id == j.PrimaryLinkId && l.JobSourceId == s.JobSourceId && l.ExternalId == s.ExternalId))
                    .OrderByDescending(s => s.Provenance.RetrievedAt)
                    .Select(s => s.ContentHash)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        return job is null ? null : new JobContentState(job.Id, job.Description, job.Hash ?? string.Empty);
    }

    /// <inheritdoc />
    public Task<JobRequirement?> GetRequirementAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.JobRequirements.FirstOrDefaultAsync(r => r.JobId == jobId, cancellationToken);

    /// <inheritdoc />
    public void AddRequirement(JobRequirement requirement) => db.JobRequirements.Add(requirement);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetProfileIdsAsync(CancellationToken cancellationToken) =>
        await db.Profiles.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<MatchProfileInput?> GetMatchProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await db.Profiles.AsNoTracking()
            .Include(p => p.Skills)
            .Include(p => p.Experiences)
            .Include(p => p.Education)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        return profile is null ? null : ToInput(profile);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetJobIdsPageAsync(Guid? afterJobId, int take, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking();
        if (afterJobId is { } after)
        {
            query = query.Where(j => j.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(j => j.Id).Select(j => j.Id).Take(take).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScoringJobRow>> GetScoringJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0)
        {
            return [];
        }

        return await (
                from j in db.Jobs.AsNoTracking()
                join r in db.JobRequirements.AsNoTracking() on j.Id equals r.JobId
                where jobIds.Contains(j.Id) && r.Status != RequirementsStatus.Pending
                orderby j.Id
                select new ScoringJobRow(j.Id, j.Title, j.Location, j.RemoteType, j.Description, r.ContentHash, r.ExtractorVersion, r.Status, r.Requirements))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, StoredMatch>> GetStoredMatchesAsync(Guid profileId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken) =>
        await db.JobMatches.AsNoTracking()
            .Where(m => m.ProfileId == profileId && jobIds.Contains(m.JobId))
            .ToDictionaryAsync(m => m.JobId, m => new StoredMatch(m.Score, m.HasBlocker, m.InputsFingerprint), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> UpsertMatchAsync(MatchWrite write, CancellationToken cancellationToken)
    {
        // The unique (JobId, ProfileId) index serializes concurrent writers, so two runs end with one
        // correct row; the WHERE leaves an unchanged fingerprint untouched (AC-7); Id never changes.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO app.job_matches
                ("Id", "JobId", "ProfileId", "Score", "Confidence", "HasBlocker", "Explanation", "InputsFingerprint", "ScoringVersion", "RankedAt")
            VALUES (@id, @jobId, @profileId, @score, @confidence, @hasBlocker, @explanation, @fingerprint, @scoringVersion, @rankedAt)
            ON CONFLICT ("JobId", "ProfileId") DO UPDATE SET
                "Score" = excluded."Score",
                "Confidence" = excluded."Confidence",
                "HasBlocker" = excluded."HasBlocker",
                "Explanation" = excluded."Explanation",
                "InputsFingerprint" = excluded."InputsFingerprint",
                "ScoringVersion" = excluded."ScoringVersion",
                "RankedAt" = excluded."RankedAt"
            WHERE app.job_matches."InputsFingerprint" IS DISTINCT FROM excluded."InputsFingerprint"
            """,
            [
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.CreateVersion7() },
                new NpgsqlParameter("jobId", NpgsqlDbType.Uuid) { Value = write.JobId },
                new NpgsqlParameter("profileId", NpgsqlDbType.Uuid) { Value = write.ProfileId },
                new NpgsqlParameter("score", NpgsqlDbType.Integer) { Value = (object?)write.Score ?? DBNull.Value },
                new NpgsqlParameter("confidence", NpgsqlDbType.Varchar) { Value = write.Confidence.ToString() },
                new NpgsqlParameter("hasBlocker", NpgsqlDbType.Boolean) { Value = write.HasBlocker },
                new NpgsqlParameter("explanation", NpgsqlDbType.Jsonb) { Value = write.ExplanationJson },
                new NpgsqlParameter("fingerprint", NpgsqlDbType.Varchar) { Value = write.InputsFingerprint },
                new NpgsqlParameter("scoringVersion", NpgsqlDbType.Integer) { Value = write.ScoringVersion },
                new NpgsqlParameter("rankedAt", NpgsqlDbType.TimestampTz) { Value = write.RankedAt.UtcDateTime },
            ],
            cancellationToken);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<MatchSweepWork> FindSweepWorkAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pendingCutoff = (now - LostPendingAfter).UtcDateTime;
        var failedCutoff = (now - RetryFailedAfter).UtcDateTime;
        var extractorVersion = JobRequirement.CurrentExtractorVersion;
        var scoringVersion = MatchScorer.ScoringVersion;

        // A job needs extraction when it has no row, its row is for other content or an older
        // extractor, a Pending row was lost, or a Failed row is due for its daily retry.
        var jobs = await db.Database.SqlQuery<Guid>($"""
            WITH current_content AS (
                SELECT j."Id" AS job_id,
                    (SELECT s."ContentHash"
                     FROM app.job_snapshots s
                     JOIN app.job_source_links l
                       ON l."Id" = j."PrimaryLinkId" AND s."JobSourceId" = l."JobSourceId" AND s."ExternalId" = l."ExternalId"
                     ORDER BY s."Provenance_RetrievedAt" DESC
                     LIMIT 1) AS hash
                FROM app.jobs j
                WHERE NOT j."IsDeleted")
            SELECT c.job_id AS "Value"
            FROM current_content c
            LEFT JOIN app.job_requirements r ON r."JobId" = c.job_id
            WHERE r."Id" IS NULL
               OR (r."Status" <> 'Pending' AND (r."ContentHash" IS DISTINCT FROM COALESCE(c.hash, '') OR r."ExtractorVersion" < {extractorVersion}))
               OR (r."Status" = 'Pending' AND r."PendingSince" < {pendingCutoff})
               OR (r."Status" = 'Failed' AND r."PendingSince" < {failedCutoff})
            ORDER BY c.job_id
            """).ToListAsync(cancellationToken);

        // A profile needs a rescore when one of its matches is from an older scoring version, or it
        // lacks a match for a job whose requirements were read (or failed).
        var profiles = await db.Database.SqlQuery<Guid>($"""
            SELECT p."Id" AS "Value"
            FROM app.profiles p
            WHERE NOT p."IsDeleted"
              AND (EXISTS (SELECT 1 FROM app.job_matches m WHERE m."ProfileId" = p."Id" AND m."ScoringVersion" < {scoringVersion})
                OR EXISTS (
                    SELECT 1
                    FROM app.job_requirements r
                    JOIN app.jobs j ON j."Id" = r."JobId" AND NOT j."IsDeleted"
                    WHERE r."Status" <> 'Pending'
                      AND NOT EXISTS (SELECT 1 FROM app.job_matches m WHERE m."JobId" = r."JobId" AND m."ProfileId" = p."Id")))
            ORDER BY p."Id"
            """).ToListAsync(cancellationToken);

        return new MatchSweepWork(jobs, profiles);
    }

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    private static MatchProfileInput ToInput(ProfileEntity profile) => new(
        profile.Id,
        profile.Skills.Select(s => new ProfileSkillInput(s.Id, s.Name)).ToList(),
        profile.TargetRoles,
        profile.Experiences.Select(e => new ExperienceInput(e.Id, e.Title, e.Company, e.StartDate, e.EndDate)).ToList(),
        profile.Education.Select(e => new EducationInput(e.Id, e.Degree, e.Field, e.DegreeLevel)).ToList(),
        profile.RemotePreference,
        profile.PreferredLocations ?? [],
        profile.JobTypes,
        profile.MinSalary,
        profile.SalaryCurrency,
        profile.AuthorizedCountries,
        profile.NeedsSponsorshipElsewhere,
        profile.StrongMatchThreshold);
}
