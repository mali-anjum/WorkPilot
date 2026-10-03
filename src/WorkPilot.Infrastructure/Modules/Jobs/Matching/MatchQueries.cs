using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Jobs.Matching;

/// <inheritdoc cref="IMatchQueries" />
public sealed class MatchQueries(WorkPilotDbContext db, TimeProvider time) : IMatchQueries
{
    /// <inheritdoc />
    public async Task<Result<MatchListDto>> ListAsync(Guid profileId, JobListQuery query, CancellationToken cancellationToken)
    {
        var errors = JobSearchValidation.Validate(query);
        if (errors.Count > 0)
        {
            return Result<MatchListDto>.Invalid(errors);
        }

        if (!await db.Profiles.AnyAsync(p => p.Id == profileId, cancellationToken))
        {
            return Result<MatchListDto>.NotFound("That profile does not exist.");
        }

        var rows = Filter(Search(profileId), query);
        var total = await rows.CountAsync(cancellationToken);
        var page = await Sort(rows, query.Sort)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(r => new
            {
                r.Job.Id,
                r.Job.Title,
                r.Job.Company,
                r.Job.Location,
                r.Job.RemoteType,
                r.Job.PostedAt,
                FirstSeenAt = r.Job.Provenance.RetrievedAt,
                SourceTypes = r.Job.Links
                    .Join(db.JobSources, l => l.JobSourceId, s => s.Id, (l, s) => s.Type)
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList(),
                HasMatch = r.Match != null,
                Score = r.Match == null ? null : r.Match.Score,
                Confidence = r.Match == null ? null : r.Match.Confidence,
                HasBlocker = r.Match != null && r.Match.HasBlocker,
                RankedAt = r.Match == null ? (DateTimeOffset?)null : r.Match.RankedAt,
                r.Dismissed,
            })
            .ToListAsync(cancellationToken);

        var hasSkills = await db.Set<ProfileSkill>().AnyAsync(ps => ps.ProfileId == profileId, cancellationToken);
        var hasExperience = await db.Experiences.AnyAsync(e => e.ProfileId == profileId, cancellationToken);
        var incomplete = !hasSkills || !hasExperience;

        var items = page
            .Select(r => new JobListItemDto(
                r.Id,
                r.Title,
                r.Company,
                r.Location,
                r.RemoteType,
                r.PostedAt,
                r.FirstSeenAt,
                r.SourceTypes,
                r.Score,
                r.Confidence,
                r.HasBlocker,
                r.RankedAt,
                StatusOf(r.HasMatch, r.Score, incomplete),
                r.Dismissed))
            .ToList();
        return Result<MatchListDto>.Ok(new MatchListDto(items, total, query.Page, query.PageSize, incomplete));
    }

    /// <summary>
    /// A row's match status (spec 0021, Value sourcing): Scored with a score, NotEnoughInfo for a
    /// row with a null score, and with no row ProfileIncomplete or Pending.
    /// </summary>
    public static string StatusOf(bool hasMatch, int? score, bool profileIncomplete) => (hasMatch, score) switch
    {
        (true, not null) => JobMatchStatuses.Scored,
        (true, null) => JobMatchStatuses.NotEnoughInfo,
        _ => profileIncomplete ? JobMatchStatuses.ProfileIncomplete : JobMatchStatuses.Pending,
    };

    // Non deleted jobs (the global filter) with the profile's match, if any, and whether it dismissed them.
    private IQueryable<SearchRow> Search(Guid profileId) =>
        from j in db.Jobs.AsNoTracking()
        join m in db.JobMatches.AsNoTracking().Where(m => m.ProfileId == profileId) on j.Id equals m.JobId into matches
        from m in matches.DefaultIfEmpty()
        select new SearchRow
        {
            Job = j,
            Match = m,
            Dismissed = db.JobDismissals.Any(d => d.ProfileId == profileId && d.JobId == j.Id),
        };

    // Every filter is a SQL predicate (spec 0021, AC-2).
    private IQueryable<SearchRow> Filter(IQueryable<SearchRow> rows, JobListQuery query)
    {
        if (!query.IncludeDismissed)
        {
            rows = rows.Where(r => !r.Dismissed);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = JobSearchValidation.ContainsPattern(query.Q.Trim());
            rows = rows.Where(r => EF.Functions.ILike(r.Job.Title, pattern, @"\") || EF.Functions.ILike(r.Job.Company, pattern, @"\"));
        }

        if (!string.IsNullOrWhiteSpace(query.Company))
        {
            var company = query.Company;
            rows = rows.Where(r => r.Job.Company == company);
        }

        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            var pattern = JobSearchValidation.ContainsPattern(query.Location.Trim());
            rows = rows.Where(r => r.Job.Location != null && EF.Functions.ILike(r.Job.Location, pattern, @"\"));
        }

        if (!string.IsNullOrWhiteSpace(query.RemoteType))
        {
            var remoteType = query.RemoteType;
            rows = rows.Where(r => r.Job.RemoteType == remoteType);
        }

        if (query.SourceId is { } sourceId)
        {
            rows = rows.Where(r => r.Job.Links.Any(l => l.JobSourceId == sourceId));
        }

        if (query.MinScore is { } minScore)
        {
            rows = rows.Where(r => r.Match != null && r.Match.Score != null && r.Match.Score >= minScore);
        }

        if (query.PostedWithinDays is { } days)
        {
            var since = time.GetUtcNow().AddDays(-days);
            rows = rows.Where(r => (r.Job.PostedAt ?? r.Job.Provenance.RetrievedAt) >= since);
        }

        if (query.SalaryMin is { } salaryMin)
        {
            // The top of the range; a range with only a minimum tops out at it.
            rows = rows.Where(r => (r.Job.SalaryRangeMax ?? r.Job.SalaryRangeMin) >= salaryMin);
        }

        if (query.HideBlocked)
        {
            rows = rows.Where(r => r.Match == null || !r.Match.HasBlocker);
        }

        return rows;
    }

    // Best match: scored before unscored, unblocked before blocked, best score first (spec 0019,
    // AC-9); then for both sorts the posted date (first seen when the source gives none), then id.
    private static IOrderedQueryable<SearchRow> Sort(IQueryable<SearchRow> rows, string? sort)
    {
        var sorted = sort == JobSorts.Newest
            ? rows.OrderByDescending(r => r.Job.PostedAt ?? r.Job.Provenance.RetrievedAt)
            : rows
                .OrderByDescending(r => r.Match != null && r.Match.Score != null)
                .ThenBy(r => r.Match != null && r.Match.HasBlocker)
                .ThenByDescending(r => r.Match!.Score)
                .ThenByDescending(r => r.Job.PostedAt ?? r.Job.Provenance.RetrievedAt);
        return sorted.ThenBy(r => r.Job.Id);
    }

    private sealed class SearchRow
    {
        public required Job Job { get; init; }
        public JobMatch? Match { get; init; }
        public bool Dismissed { get; init; }
    }

    /// <inheritdoc />
    public async Task<Result<JobMatchDetailDto>> GetAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        // Scoped to the given profile (AC-15): another profile's match is never returned.
        var row = await (
                from m in db.JobMatches.AsNoTracking()
                join j in db.Jobs.AsNoTracking().IgnoreQueryFilters() on m.JobId equals j.Id
                join p in db.Profiles.AsNoTracking() on m.ProfileId equals p.Id
                where m.JobId == jobId && m.ProfileId == profileId
                select new
                {
                    j.Id,
                    j.Title,
                    j.Company,
                    j.Location,
                    j.Provenance.SourceUrl,
                    m.Score,
                    m.Confidence,
                    m.HasBlocker,
                    m.Explanation,
                    m.RankedAt,
                    Requirements = db.JobRequirements.Where(r => r.JobId == j.Id).Select(r => new { r.Status, r.FailureReason }).FirstOrDefault(),
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Result<JobMatchDetailDto>.NotFound("This job has no match for your profile yet.");
        }

        var explanation = MatchingJson.Deserialize<MatchExplanationDto>(row.Explanation)
            ?? throw new InvalidOperationException($"Match of job {jobId} has an empty explanation.");
        return Result<JobMatchDetailDto>.Ok(new JobMatchDetailDto(
            row.Id,
            row.Title,
            row.Company,
            row.Location,
            row.SourceUrl,
            row.Score,
            row.Confidence,
            row.HasBlocker,
            explanation,
            row.Requirements?.Status.ToString(),
            row.Requirements?.FailureReason,
            row.RankedAt));
    }
}
