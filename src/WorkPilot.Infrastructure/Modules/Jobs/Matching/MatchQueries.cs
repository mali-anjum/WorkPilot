using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Jobs.Matching;

/// <inheritdoc cref="IMatchQueries" />
public sealed class MatchQueries(WorkPilotDbContext db) : IMatchQueries
{
    /// <summary>The page size when none is asked for (AC-9).</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest page size allowed.</summary>
    public const int MaxPageSize = 100;

    /// <inheritdoc />
    public async Task<Result<MatchListDto>> ListAsync(Guid profileId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            return Result<MatchListDto>.Invalid("page", "page must be 1 or more.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return Result<MatchListDto>.Invalid("pageSize", $"pageSize must be 1 to {MaxPageSize}.");
        }

        if (!await db.Profiles.AnyAsync(p => p.Id == profileId, cancellationToken))
        {
            return Result<MatchListDto>.NotFound("That profile does not exist.");
        }

        var total = await db.Jobs.CountAsync(cancellationToken);

        // Scored first; among those unblocked before blocked, best score first; ties by job id.
        // Jobs with no match row, or a null score, come last.
        var items = await (
                from j in db.Jobs.AsNoTracking()
                join m in db.JobMatches.AsNoTracking().Where(m => m.ProfileId == profileId) on j.Id equals m.JobId into matches
                from m in matches.DefaultIfEmpty()
                let scored = m != null && m.Score != null
                let blocked = m != null && m.HasBlocker
                orderby scored descending, blocked, m!.Score descending, j.Id
                select new MatchListItemDto(
                    j.Id,
                    j.Title,
                    j.Company,
                    j.Location,
                    m == null ? null : m.Score,
                    m == null ? null : m.Confidence,
                    blocked,
                    m == null ? null : m.RankedAt))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var hasSkills = await db.Set<ProfileSkill>().AnyAsync(ps => ps.ProfileId == profileId, cancellationToken);
        var hasExperience = await db.Experiences.AnyAsync(e => e.ProfileId == profileId, cancellationToken);
        return Result<MatchListDto>.Ok(new MatchListDto(items, total, page, pageSize, !hasSkills || !hasExperience));
    }

    /// <inheritdoc />
    public async Task<Result<JobMatchDetailDto>> GetAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken)
    {
        // Scoped to the given profile (AC-15): another profile's match is never returned.
        var row = await (
                from m in db.JobMatches.AsNoTracking()
                join j in db.Jobs.AsNoTracking() on m.JobId equals j.Id
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
