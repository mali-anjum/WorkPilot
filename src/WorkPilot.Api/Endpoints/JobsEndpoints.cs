using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Jobs;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// Internal job ingestion and deduplication endpoints
/// (docs/specs/0008-job-source-ingestion, docs/specs/0017-job-deduplication).
/// Internal only, same network boundary as the other <c>/internal/*</c> routes.
/// </summary>
internal static class JobsEndpoints
{
    /// <summary>Default and maximum page size for <c>GET /internal/jobs</c> (spec 0008, AC-7).</summary>
    internal const int DefaultTake = 50;

    /// <inheritdoc cref="DefaultTake" />
    internal const int MaxTake = 200;

    /// <summary>Maps the ingestion trigger, the list and detail reads, and the split.</summary>
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/jobs/ingestions", TriggerIngestionAsync);
        app.MapGet("/internal/jobs", ListJobsAsync);
        app.MapGet("/internal/jobs/{id:guid}", GetJobAsync);
        app.MapPost("/internal/jobs/{id:guid}/links/{linkId:guid}/split", SplitLinkAsync);
        return app;
    }

    /// <summary>
    /// Registers (find or create) the board as a job source and enqueues one
    /// ingestion background job; returns immediately (spec 0008, AC-1). An
    /// optional <c>companyName</c> (1 to 200 chars) is applied by that job,
    /// renaming and rematching the source's jobs when it changed (spec 0017, AC-7).
    /// </summary>
    private static async Task<IResult> TriggerIngestionAsync(
        TriggerJobIngestionRequest request,
        JobIngestionService ingestion,
        IBackgroundJobClient jobs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.BoardToken))
        {
            return Results.BadRequest();
        }

        var companyName = request.CompanyName?.Trim();
        if (companyName is { Length: 0 or > JobIngestionService.MaxCompanyNameLength })
        {
            return Results.BadRequest();
        }

        var source = await ingestion.RegisterSourceAsync(request.Source, request.BoardToken, cancellationToken);
        if (source is null)
        {
            return Results.BadRequest();
        }

        var keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        var backgroundJobId = jobs.Enqueue<IngestJobsJob>(j => j.RunAsync(source.Id, keywords, companyName));

        return Results.Accepted(value: new TriggerJobIngestionResponse(source.Id, backgroundJobId));
    }

    /// <summary>Lists the canonical jobs with a link from a source, with provenance and every source they were seen on, newest first (spec 0017, AC-13).</summary>
    private static async Task<IResult> ListJobsAsync(
        Guid jobSourceId,
        int? take,
        WorkPilotDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.JobSources.AnyAsync(s => s.Id == jobSourceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var jobs = await db.Jobs
            .AsNoTracking()
            .Where(j => j.Links.Any(l => l.JobSourceId == jobSourceId))
            .OrderByDescending(j => j.PostedAt ?? j.Provenance.RetrievedAt)
            .ThenBy(j => j.Id)
            .Take(limit)
            .Select(j => new JobView(
                j.Id,
                j.Title,
                j.Company,
                j.Location,
                j.RemoteType,
                j.PostedAt,
                j.Provenance.SourceUrl,
                j.Provenance.RetrievedAt,
                j.Provenance.VerifiedAt,
                j.Provenance.Confidence,
                j.Snapshots.Count,
                j.Links
                    .OrderBy(l => l.FirstSeenAt)
                    .ThenBy(l => l.Id)
                    .Select(l => new JobSourceView(l.JobSourceId, l.ExternalId, l.SourceUrl, l.LastSeenAt))
                    .ToList()))
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs);
    }

    /// <summary>One job with every link and its snapshot count; 404 when unknown or soft deleted (spec 0017, AC-13).</summary>
    private static async Task<IResult> GetJobAsync(Guid id, WorkPilotDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs
            .AsNoTracking()
            .Where(j => j.Id == id)
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
                    .OrderBy(l => l.FirstSeenAt)
                    .ThenBy(l => l.Id)
                    .Select(l => new JobLinkView(
                        l.Id,
                        l.JobSourceId,
                        db.JobSources.Where(s => s.Id == l.JobSourceId).Select(s => s.Type).First(),
                        l.ExternalId,
                        l.SourceUrl,
                        l.FirstSeenAt,
                        l.LastSeenAt,
                        l.Confidence,
                        l.Id == j.PrimaryLinkId,
                        l.SplitAt))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    /// <summary>
    /// Moves one link into a new job of its own (spec 0017, AC-8): 200 with
    /// both job ids, 400 for a job's only link, 404 for an unknown or soft
    /// deleted job or a link not on it.
    /// </summary>
    private static async Task<IResult> SplitLinkAsync(Guid id, Guid linkId, JobDedupService dedup, CancellationToken cancellationToken)
    {
        var result = await dedup.SplitAsync(id, linkId, cancellationToken);
        return result.Outcome switch
        {
            SplitOutcome.Split => Results.Ok(new SplitJobLinkResponse(result.JobId, result.NewJobId!.Value)),
            SplitOutcome.LastLink => Results.BadRequest(),
            _ => Results.NotFound(),
        };
    }
}

/// <summary>Request body for <c>POST /internal/jobs/ingestions</c>.</summary>
/// <param name="Source">Source type, <c>greenhouse</c> or <c>lever</c>.</param>
/// <param name="BoardToken">The board's identifier at that source: a Greenhouse board token or a Lever site name.</param>
/// <param name="Keywords">Optional title keywords; any one matching keeps a posting.</param>
/// <param name="CompanyName">Optional company every posting from this source belongs to (1 to 200 chars).</param>
internal sealed record TriggerJobIngestionRequest(string? Source, string? BoardToken, string? Keywords, string? CompanyName);

/// <summary>Response body for <c>POST /internal/jobs/ingestions</c>.</summary>
internal sealed record TriggerJobIngestionResponse(Guid JobSourceId, string BackgroundJobId);

/// <summary>One place a job was seen, in <c>GET /internal/jobs</c>.</summary>
internal sealed record JobSourceView(Guid JobSourceId, string ExternalId, string SourceUrl, DateTimeOffset LastSeenAt);

/// <summary>One canonical job as returned by <c>GET /internal/jobs</c>.</summary>
internal sealed record JobView(
    Guid Id,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    DateTimeOffset? PostedAt,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? VerifiedAt,
    decimal? Confidence,
    int SnapshotCount,
    IReadOnlyList<JobSourceView> Sources);

/// <summary>One link of a job, in <c>GET /internal/jobs/{id}</c>.</summary>
internal sealed record JobLinkView(
    Guid Id,
    Guid JobSourceId,
    string SourceType,
    string ExternalId,
    string SourceUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    decimal Confidence,
    bool IsPrimary,
    DateTimeOffset? SplitAt);

/// <summary>Response body for <c>GET /internal/jobs/{id}</c>.</summary>
internal sealed record JobDetailView(
    Guid Id,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    string? Description,
    DateTimeOffset? PostedAt,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? VerifiedAt,
    decimal? Confidence,
    int SnapshotCount,
    IReadOnlyList<JobLinkView> Links);

/// <summary>Response body for <c>POST /internal/jobs/{id}/links/{linkId}/split</c>.</summary>
internal sealed record SplitJobLinkResponse(Guid JobId, Guid NewJobId);
