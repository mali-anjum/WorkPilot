using Hangfire;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Api.Common;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Jobs;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// Internal job ingestion, deduplication and matching endpoints
/// (docs/specs/0008-job-source-ingestion, docs/specs/0017-job-deduplication, docs/specs/0019-job-matching-engine,
/// docs/specs/0021-jobs-list-detail).
/// Internal only, same network boundary as the other <c>/internal/*</c> routes.
/// </summary>
internal static class JobsEndpoints
{
    /// <summary>Default and maximum page size for <c>GET /internal/jobs</c> (spec 0008, AC-7).</summary>
    internal const int DefaultTake = 50;

    /// <inheritdoc cref="DefaultTake" />
    internal const int MaxTake = 200;

    /// <summary>
    /// Maps the ingestion trigger, the list and detail reads, the split, the match list, panel and
    /// rescore, and the jobs list's facets, dismissals and sources (spec 0021).
    /// </summary>
    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/jobs/ingestions", TriggerIngestionAsync);
        app.MapGet("/internal/jobs", ListJobsAsync);
        app.MapGet("/internal/jobs/facets", GetFacetsAsync);
        app.MapGet("/internal/jobs/sources", ListSourcesAsync);
        app.MapPost("/internal/jobs/sources/{id:guid}/ingestions", RunSourceAsync);
        app.MapGet("/internal/jobs/{id:guid}", GetJobAsync);
        app.MapPut("/internal/jobs/{id:guid}/dismissal", DismissAsync);
        app.MapDelete("/internal/jobs/{id:guid}/dismissal", UndoDismissAsync);
        app.MapPost("/internal/jobs/{id:guid}/links/{linkId:guid}/split", SplitLinkAsync);
        app.MapGet("/internal/matches", ListMatchesAsync);
        app.MapGet("/internal/jobs/{jobId:guid}/match", GetMatchAsync);
        app.MapPost("/internal/jobs/{jobId:guid}/match/rescore", RescoreMatchAsync);
        return app;
    }

    /// <summary>
    /// Registers (find or create) the board as a job source and enqueues one
    /// ingestion background job; returns immediately (spec 0008, AC-1). An
    /// optional <c>companyName</c> (1 to 200 chars) is applied by that job,
    /// renaming and rematching the source's jobs when it changed (spec 0017, AC-7).
    /// Bad input is a 400 ProblemDetails naming <c>source</c>, <c>boardToken</c> or
    /// <c>companyName</c> (spec 0021, AC-6).
    /// </summary>
    private static async Task<IResult> TriggerIngestionAsync(
        TriggerJobIngestionRequest request,
        JobIngestionService ingestion,
        IBackgroundJobClient jobs,
        CancellationToken cancellationToken)
    {
        var companyName = request.CompanyName?.Trim();
        if (companyName is { Length: 0 or > JobIngestionService.MaxCompanyNameLength })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["companyName"] = [$"The company name must be 1 to {JobIngestionService.MaxCompanyNameLength} characters."],
            });
        }

        var registered = await ingestion.RegisterSourceAsync(request.Source, request.BoardToken, cancellationToken);
        if (!registered.IsOk)
        {
            return registered.ToHttp(_ => Results.Ok());
        }

        var source = registered.Value!;
        var keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        var backgroundJobId = jobs.Enqueue<IngestJobsJob>(j => j.RunAsync(source.Id, keywords, companyName));

        return Results.Accepted(value: new TriggerJobIngestionResponse(source.Id, backgroundJobId));
    }

    /// <summary>
    /// Queues one more ingestion of a stored source, with its stored board and company name
    /// (spec 0021, AC-6): 202, or 404 for an unknown source.
    /// </summary>
    private static async Task<IResult> RunSourceAsync(Guid id, WorkPilotDbContext db, IBackgroundJobClient jobs, CancellationToken cancellationToken)
    {
        if (!await db.JobSources.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return Results.Problem(detail: "That job source does not exist.", statusCode: StatusCodes.Status404NotFound);
        }

        // No company name: the run keeps the stored one rather than renaming.
        var backgroundJobId = jobs.Enqueue<IngestJobsJob>(j => j.RunAsync(id, null, null));
        return Results.Accepted(value: new TriggerJobIngestionResponse(id, backgroundJobId));
    }

    /// <summary>Every job source with its job count and latest run (spec 0021, AC-6).</summary>
    private static async Task<IResult> ListSourcesAsync(IJobCatalogQueries catalog, CancellationToken cancellationToken) =>
        Results.Ok(await catalog.ListSourcesAsync(cancellationToken));

    /// <summary>The companies, remote types and sources the filters offer (spec 0021, AC-2); 400 without a <c>profileId</c>.</summary>
    private static async Task<IResult> GetFacetsAsync(Guid? profileId, IJobCatalogQueries catalog, CancellationToken cancellationToken)
    {
        if (profileId is not { } id || id == Guid.Empty)
        {
            return ProfileIdRequired();
        }

        return Results.Ok(await catalog.GetFacetsAsync(cancellationToken));
    }

    /// <summary>Hides a job from the profile's list (spec 0021, AC-4): 204, also when it was already dismissed; 404 for an unknown job or profile.</summary>
    private static async Task<IResult> DismissAsync(Guid id, DismissJobRequest request, JobDismissalService dismissals, CancellationToken cancellationToken) =>
        (await dismissals.DismissAsync(id, request.ProfileId, cancellationToken)).ToHttp(_ => Results.NoContent());

    /// <summary>Shows a dismissed job again (spec 0021, AC-4): 204, also when it was not dismissed; 404 for an unknown job or profile.</summary>
    private static async Task<IResult> UndoDismissAsync(Guid id, Guid? profileId, JobDismissalService dismissals, CancellationToken cancellationToken)
    {
        if (profileId is not { } profile || profile == Guid.Empty)
        {
            return ProfileIdRequired();
        }

        return (await dismissals.UndoAsync(id, profile, cancellationToken)).ToHttp(_ => Results.NoContent());
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

    /// <summary>
    /// One job with every link and its snapshot count (spec 0017, AC-13), soft deleted ones
    /// included with <c>isDeleted</c> set (spec 0021, AC-5); an optional <c>profileId</c> fills
    /// <c>dismissed</c>. 404 when unknown.
    /// </summary>
    private static async Task<IResult> GetJobAsync(Guid id, Guid? profileId, IJobCatalogQueries catalog, CancellationToken cancellationToken)
    {
        var job = await catalog.GetJobAsync(id, profileId, cancellationToken);
        return job is null
            ? Results.Problem(detail: "That job does not exist.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(job);
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

    /// <summary>
    /// The profile's jobs, 25 per page by default, best match first or newest first, narrowed by
    /// the filters (spec 0019, AC-9; spec 0021, AC-1, AC-2): 400 ProblemDetails naming each bad
    /// parameter or a missing <c>profileId</c>, 404 for an unknown profile.
    /// </summary>
    private static async Task<IResult> ListMatchesAsync(
        Guid? profileId,
        int? page,
        int? pageSize,
        string? q,
        string? company,
        string? location,
        string? remoteType,
        Guid? sourceId,
        int? minScore,
        int? postedWithinDays,
        decimal? salaryMin,
        bool? hideBlocked,
        bool? includeDismissed,
        string? sort,
        IMatchQueries matches,
        CancellationToken cancellationToken)
    {
        if (profileId is not { } id || id == Guid.Empty)
        {
            return ProfileIdRequired();
        }

        var query = new JobListQuery
        {
            Page = page ?? 1,
            PageSize = pageSize ?? JobSearchValidation.DefaultPageSize,
            Q = q,
            Company = company,
            Location = location,
            RemoteType = remoteType,
            SourceId = sourceId,
            MinScore = minScore,
            PostedWithinDays = postedWithinDays,
            SalaryMin = salaryMin,
            HideBlocked = hideBlocked ?? false,
            IncludeDismissed = includeDismissed ?? false,
            Sort = string.IsNullOrWhiteSpace(sort) ? null : sort,
        };
        var result = await matches.ListAsync(id, query, cancellationToken);
        return result.ToHttp(Results.Ok);
    }

    private static IResult ProfileIdRequired() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["profileId"] = ["profileId is required."] });

    /// <summary>The match panel for one job and profile (AC-10); 404 when there is none for this profile (AC-15).</summary>
    private static async Task<IResult> GetMatchAsync(Guid jobId, Guid profileId, IMatchQueries matches, CancellationToken cancellationToken) =>
        (await matches.GetAsync(jobId, profileId, cancellationToken)).ToHttp(Results.Ok);

    /// <summary>
    /// Forces a fresh extraction and rescore (AC-10): 202 with <c>queued</c> false when extraction is
    /// already Pending; 404 for an unknown job or profile.
    /// </summary>
    private static async Task<IResult> RescoreMatchAsync(Guid jobId, RescoreMatchRequest request, JobMatchingService matching, CancellationToken cancellationToken) =>
        (await matching.RequestRescoreAsync(jobId, request.ProfileId, cancellationToken))
            .ToHttp(response => Results.Accepted(value: response));
}

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

/// <summary>Response body for <c>POST /internal/jobs/{id}/links/{linkId}/split</c>.</summary>
internal sealed record SplitJobLinkResponse(Guid JobId, Guid NewJobId);
