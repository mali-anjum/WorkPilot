using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Jobs;

/// <inheritdoc cref="IJobCatalogQueries" />
public sealed class JobCatalogQueries(WorkPilotDbContext db) : IJobCatalogQueries
{
    /// <inheritdoc />
    public async Task<JobFacetsDto> GetFacetsAsync(CancellationToken cancellationToken)
    {
        var companies = await db.Jobs.AsNoTracking()
            .Select(j => j.Company)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
        var remoteTypes = await db.Jobs.AsNoTracking()
            .Where(j => j.RemoteType != null)
            .Select(j => j.RemoteType!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(cancellationToken);
        var sources = await db.JobSources.AsNoTracking()
            .Where(s => db.JobSourceLinks.Any(l => l.JobSourceId == s.Id && db.Jobs.Any(j => j.Id == l.JobId)))
            .OrderBy(s => s.Type)
            .ThenBy(s => s.Name)
            .Select(s => new JobSourceFacetDto(s.Id, s.Type, s.Name))
            .ToListAsync(cancellationToken);
        return new JobFacetsDto(companies, remoteTypes, sources);
    }

    /// <inheritdoc />
    public Task<JobDetailView?> GetJobAsync(Guid jobId, Guid? profileId, CancellationToken cancellationToken) =>
        db.Jobs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(j => j.Id == jobId)
            .Select(j => new JobDetailView(
                j.Id,
                j.Title,
                j.Company,
                j.Location,
                j.RemoteType,
                j.Description,
                j.PostedAt,
                j.Provenance.SourceUrl,
                j.Provenance.RetrievedAt,
                j.Provenance.VerifiedAt,
                j.Provenance.Confidence,
                j.Snapshots.Count,
                j.Links
                    .Join(db.JobSources, l => l.JobSourceId, s => s.Id, (l, s) => new { Link = l, SourceType = s.Type })
                    .OrderBy(x => x.Link.FirstSeenAt)
                    .ThenBy(x => x.Link.Id)
                    .Select(x => new JobLinkView(
                        x.Link.Id,
                        x.Link.JobSourceId,
                        x.SourceType,
                        x.Link.ExternalId,
                        x.Link.SourceUrl,
                        x.Link.FirstSeenAt,
                        x.Link.LastSeenAt,
                        x.Link.Confidence,
                        x.Link.Id == j.PrimaryLinkId,
                        x.Link.SplitAt))
                    .ToList(),
                j.SalaryRangeMin,
                j.SalaryRangeMax,
                j.IsDeleted,
                profileId != null && db.JobDismissals.Any(d => d.ProfileId == profileId && d.JobId == j.Id)))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSourceSummaryDto>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        // The audit log is the Audit module's table; reading it is allowed (spec 0018).
        var rows = await db.JobSources.AsNoTracking()
            .OrderBy(s => s.Type)
            .ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Type,
                s.Name,
                s.CompanyName,
                JobCount = db.JobSourceLinks.Count(l => l.JobSourceId == s.Id && db.Jobs.Any(j => j.Id == l.JobId)),
                LastRun = db.AuditLogs
                    .Where(a => a.Action == JobIngestionService.AuditAction
                        && a.TargetType == JobIngestionService.AuditTargetType
                        && a.TargetId == s.Id)
                    .OrderByDescending(a => a.OccurredAt)
                    .Select(a => new { a.OccurredAt, a.Payload })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r =>
            {
                var counts = r.LastRun is null ? null : ReadCounts(r.LastRun.Payload);
                return new JobSourceSummaryDto(
                    r.Id,
                    r.Type,
                    r.Name,
                    r.CompanyName,
                    r.JobCount,
                    r.LastRun?.OccurredAt,
                    counts?.Created,
                    counts?.Updated);
            })
            .ToList();
    }

    // The created and updated counts of a JobsIngested payload (a JobIngestionSummary).
    private static (int? Created, int? Updated)? ReadCounts(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return (Int(doc.RootElement, "created"), Int(doc.RootElement, "updated"));
        }
        catch (JsonException)
        {
            return null;
        }

        static int? Int(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
    }
}

/// <inheritdoc cref="IJobDismissalRepository" />
public sealed class JobDismissalRepository(WorkPilotDbContext db) : IJobDismissalRepository
{
    /// <inheritdoc />
    public Task<bool> JobExistsAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == jobId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ProfileExistsAsync(Guid profileId, CancellationToken cancellationToken) =>
        db.Profiles.AnyAsync(p => p.Id == profileId, cancellationToken);

    /// <inheritdoc />
    public Task AddAsync(JobDismissal dismissal, CancellationToken cancellationToken) =>
        // ON CONFLICT keeps the first dismissal, so two racing requests both succeed (AC-4).
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO app.job_dismissals ("ProfileId", "JobId", "DismissedAt")
            VALUES ({dismissal.ProfileId}, {dismissal.JobId}, {dismissal.DismissedAt})
            ON CONFLICT ("ProfileId", "JobId") DO NOTHING
            """,
            cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(Guid profileId, Guid jobId, CancellationToken cancellationToken) =>
        db.JobDismissals.Where(d => d.ProfileId == profileId && d.JobId == jobId).ExecuteDeleteAsync(cancellationToken);
}
