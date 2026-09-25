using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Jobs;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// Internal job source ingestion endpoints (docs/specs/0008-job-source-ingestion).
/// Internal only, same network boundary as the other <c>/internal/*</c> routes.
/// </summary>
internal static class JobsEndpoints
{
    /// <summary>Default and maximum page size for <c>GET /internal/jobs</c> (AC-7).</summary>
    internal const int DefaultTake = 50;

    /// <inheritdoc cref="DefaultTake" />
    internal const int MaxTake = 200;

    /// <summary>Maps <c>POST /internal/jobs/ingestions</c> and <c>GET /internal/jobs</c>.</summary>
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/jobs/ingestions", TriggerIngestionAsync);
        app.MapGet("/internal/jobs", ListJobsAsync);
        return app;
    }

    /// <summary>
    /// Registers (find or create) the board as a job source and enqueues one
    /// ingestion background job; returns immediately (AC-1).
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

        var source = await ingestion.RegisterSourceAsync(request.Source, request.BoardToken, cancellationToken);
        if (source is null)
        {
            return Results.BadRequest();
        }

        var keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        var backgroundJobId = jobs.Enqueue<IngestJobsJob>(j => j.RunAsync(source.Id, keywords));

        return Results.Accepted(value: new TriggerJobIngestionResponse(source.Id, backgroundJobId));
    }

    /// <summary>Lists a source's canonical jobs with provenance, newest first (AC-7).</summary>
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
            .Where(j => j.JobSourceId == jobSourceId)
            .OrderByDescending(j => j.PostedAt ?? j.Provenance.RetrievedAt)
            .ThenBy(j => j.Id)
            .Take(limit)
            .Select(j => new JobView(
                j.Id,
                j.ExternalId,
                j.Title,
                j.Company,
                j.Location,
                j.RemoteType,
                j.PostedAt,
                j.Provenance.SourceUrl,
                j.Provenance.RetrievedAt,
                j.Provenance.VerifiedAt,
                j.Provenance.Confidence,
                j.Snapshots.Count))
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs);
    }
}

/// <summary>Request body for <c>POST /internal/jobs/ingestions</c>.</summary>
/// <param name="Source">Source type, e.g. <c>greenhouse</c>.</param>
/// <param name="BoardToken">The board's identifier at that source, e.g. a Greenhouse board token.</param>
/// <param name="Keywords">Optional title keywords; any one matching keeps a posting.</param>
internal sealed record TriggerJobIngestionRequest(string? Source, string? BoardToken, string? Keywords);

/// <summary>Response body for <c>POST /internal/jobs/ingestions</c>.</summary>
internal sealed record TriggerJobIngestionResponse(Guid JobSourceId, string BackgroundJobId);

/// <summary>One canonical job as returned by <c>GET /internal/jobs</c>.</summary>
internal sealed record JobView(
    Guid Id,
    string ExternalId,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    DateTimeOffset? PostedAt,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? VerifiedAt,
    decimal? Confidence,
    int SnapshotCount);
