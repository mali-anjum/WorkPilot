using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Features.Jobs;

/// <summary>
/// The Web host's typed client for the internal jobs and matching endpoints (specs 0019, 0021).
/// Pages call this rather than the Api directly, since only the Web server can reach the internal
/// Api (spec 0004); the profile id always comes from the signed in session, never from the browser.
/// </summary>
public interface IJobsApiClient
{
    /// <summary>One page of jobs, filtered and sorted (spec 0021, AC-1, AC-2).</summary>
    Task<ApiResult<MatchListDto>> SearchAsync(Guid profileId, JobListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The filter choices (spec 0021, AC-2).</summary>
    Task<ApiResult<JobFacetsDto>> GetFacetsAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>One job's details and links, soft deleted ones included (spec 0021, AC-5).</summary>
    Task<ApiResult<JobDetailView>> GetJobAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>The match panel of one job (spec 0019, AC-10).</summary>
    Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Asks for a fresh extraction and rescore (spec 0019, AC-10).</summary>
    Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Hides a job from the list (spec 0021, AC-4).</summary>
    Task<ApiResult<bool>> DismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Shows a dismissed job again (spec 0021, AC-4).</summary>
    Task<ApiResult<bool>> UndoDismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Every job source with its job count and latest run (spec 0021, AC-6).</summary>
    Task<ApiResult<IReadOnlyList<JobSourceSummaryDto>>> ListSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers a board and queues its first ingestion; field errors come back per field (spec 0021, AC-6).</summary>
    Task<ApiResult<TriggerJobIngestionResponse>> AddSourceAsync(TriggerJobIngestionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queues one more ingestion of a stored source (spec 0021, AC-6).</summary>
    Task<ApiResult<TriggerJobIngestionResponse>> RunSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IJobsApiClient" />
public sealed class JobsApiClient(IHttpClientFactory httpClientFactory) : IJobsApiClient
{
    private const string NoProfile = "Your profile could not be found; sign in again.";
    private const string NoJob = "That job no longer exists.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Api => httpClientFactory.CreateClient("api");

    /// <inheritdoc />
    public async Task<ApiResult<MatchListDto>> SearchAsync(Guid profileId, JobListQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(SearchUrl(profileId, query), cancellationToken);
        return await ApiResultReader.ReadAsync<MatchListDto>(response, NoProfile, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<JobFacetsDto>> GetFacetsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/jobs/facets?profileId={profileId}", cancellationToken);
        return await ApiResultReader.ReadAsync<JobFacetsDto>(response, NoProfile, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<JobDetailView>> GetJobAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/jobs/{jobId}?profileId={profileId}", cancellationToken);
        return await ApiResultReader.ReadAsync<JobDetailView>(response, NoJob, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/jobs/{jobId}/match?profileId={profileId}", cancellationToken);
        return await ApiResultReader.ReadAsync<JobMatchDetailDto>(response, "This job has no match for your profile yet.", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.PostAsJsonAsync($"/internal/jobs/{jobId}/match/rescore", new RescoreMatchRequest(profileId), Json, cancellationToken);
        return await ApiResultReader.ReadAsync<RescoreMatchResponse>(response, NoJob, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<bool>> DismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.PutAsJsonAsync($"/internal/jobs/{jobId}/dismissal", new DismissJobRequest(profileId), Json, cancellationToken);
        return await ReadNoContentAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<bool>> UndoDismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.DeleteAsync($"/internal/jobs/{jobId}/dismissal?profileId={profileId}", cancellationToken);
        return await ReadNoContentAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<JobSourceSummaryDto>>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync("/internal/jobs/sources", cancellationToken);
        return await ApiResultReader.ReadAsync<IReadOnlyList<JobSourceSummaryDto>>(response, "Job sources could not be found.", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<TriggerJobIngestionResponse>> AddSourceAsync(TriggerJobIngestionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await Api.PostAsJsonAsync("/internal/jobs/ingestions", request, Json, cancellationToken);
        return await ApiResultReader.ReadAsync<TriggerJobIngestionResponse>(response, "Job sources could not be found.", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<TriggerJobIngestionResponse>> RunSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.PostAsync($"/internal/jobs/sources/{sourceId}/ingestions", null, cancellationToken);
        return await ApiResultReader.ReadAsync<TriggerJobIngestionResponse>(response, "That job source no longer exists.", cancellationToken);
    }

    /// <summary>The <c>GET /internal/matches</c> URL for <paramref name="query"/>: only the set filters, invariant culture.</summary>
    public static string SearchUrl(Guid profileId, JobListQuery query)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["profileId"] = profileId.ToString(),
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture),
            ["q"] = query.Q,
            ["company"] = query.Company,
            ["location"] = query.Location,
            ["remoteType"] = query.RemoteType,
            ["sourceId"] = query.SourceId?.ToString(),
            ["minScore"] = query.MinScore?.ToString(CultureInfo.InvariantCulture),
            ["postedWithinDays"] = query.PostedWithinDays?.ToString(CultureInfo.InvariantCulture),
            ["salaryMin"] = query.SalaryMin?.ToString(CultureInfo.InvariantCulture),
            ["hideBlocked"] = query.HideBlocked ? "true" : null,
            ["includeDismissed"] = query.IncludeDismissed ? "true" : null,
            ["sort"] = query.Sort,
        };
        return QueryHelpers.AddQueryString("/internal/matches", parameters.Where(p => !string.IsNullOrWhiteSpace(p.Value)));
    }

    // A 204 is success with no body; any failure is read as ProblemDetails.
    private static async Task<ApiResult<bool>> ReadNoContentAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        response.IsSuccessStatusCode
            ? ApiResult<bool>.Ok(true)
            : await ApiResultReader.ReadAsync<bool>(response, NoJob, cancellationToken);
}

/// <summary>Web host wiring for the Jobs module: the jobs list, detail and sources (specs 0019, 0021).</summary>
public static class JobsWebExtensions
{
    /// <summary>Registers the typed jobs Api client the pages use.</summary>
    public static IServiceCollection AddJobsWeb(this IServiceCollection services) =>
        services.AddScoped<IJobsApiClient, JobsApiClient>();
}
