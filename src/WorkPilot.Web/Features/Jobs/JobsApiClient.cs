using System.Net.Http.Json;
using System.Text.Json;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Features.Jobs;

/// <summary>
/// The Web host's typed client for the internal matching endpoints (spec 0019). Pages call this
/// rather than the Api directly, since only the Web server can reach the internal Api (spec 0004);
/// the profile id always comes from the signed in session, never from the browser.
/// </summary>
public interface IJobsApiClient
{
    /// <summary>One page of jobs, best match first (AC-9).</summary>
    Task<ApiResult<MatchListDto>> ListMatchesAsync(Guid profileId, int page, CancellationToken cancellationToken = default);

    /// <summary>The match panel of one job (AC-10).</summary>
    Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Asks for a fresh extraction and rescore (AC-10).</summary>
    Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IJobsApiClient" />
public sealed class JobsApiClient(IHttpClientFactory httpClientFactory) : IJobsApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Api => httpClientFactory.CreateClient("api");

    /// <inheritdoc />
    public async Task<ApiResult<MatchListDto>> ListMatchesAsync(Guid profileId, int page, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/matches?profileId={profileId}&page={page}", cancellationToken);
        return await ApiResultReader.ReadAsync<MatchListDto>(response, "Your profile could not be found; sign in again.", cancellationToken);
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
        return await ApiResultReader.ReadAsync<RescoreMatchResponse>(response, "That job no longer exists.", cancellationToken);
    }
}

/// <summary>Web host wiring for the Jobs module: the jobs list and match panel (spec 0019).</summary>
public static class JobsWebExtensions
{
    /// <summary>Registers the typed matching Api client the pages use.</summary>
    public static IServiceCollection AddJobsWeb(this IServiceCollection services) =>
        services.AddScoped<IJobsApiClient, JobsApiClient>();
}
