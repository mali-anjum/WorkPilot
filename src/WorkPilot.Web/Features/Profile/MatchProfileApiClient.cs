using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Features.Profile;

/// <summary>The outcome of a match profile save: the saved version, or a message, flagged stale on a 412.</summary>
public sealed record MatchProfileSaveResult(VersionedMatchProfile? Saved, string? Error, bool Stale)
{
    public bool Succeeded => Saved is not null;
}

/// <summary>The Web host's typed client for the match profile endpoints (spec 0019, AC-11).</summary>
public interface IMatchProfileApiClient
{
    /// <summary>The profile's match profile with its ETag.</summary>
    Task<ApiResult<VersionedMatchProfile>> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Saves the whole match profile with <paramref name="etag"/> as <c>If-Match</c>.</summary>
    Task<MatchProfileSaveResult> SaveAsync(Guid profileId, string etag, MatchProfileDocument document, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMatchProfileApiClient" />
public sealed class MatchProfileApiClient(IHttpClientFactory httpClientFactory) : IMatchProfileApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Api => httpClientFactory.CreateClient("api");

    /// <inheritdoc />
    public async Task<ApiResult<VersionedMatchProfile>> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/profile/{profileId}/match-profile", cancellationToken);
        var result = await ApiResultReader.ReadAsync<MatchProfileDocument>(response, "Your profile could not be found; sign in again.", cancellationToken);
        return result.Succeeded
            ? ApiResult<VersionedMatchProfile>.Ok(new VersionedMatchProfile(result.Value!, ETagOf(response)))
            : ApiResult<VersionedMatchProfile>.Fail(result.Error!);
    }

    /// <inheritdoc />
    public async Task<MatchProfileSaveResult> SaveAsync(Guid profileId, string etag, MatchProfileDocument document, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/profile/{profileId}/match-profile")
        {
            Content = JsonContent.Create(document, options: Json),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await Api.SendAsync(request, cancellationToken);
        var result = await ApiResultReader.ReadAsync<MatchProfileDocument>(response, "Your profile could not be found; sign in again.", cancellationToken);
        return result.Succeeded
            ? new MatchProfileSaveResult(new VersionedMatchProfile(result.Value!, ETagOf(response)), null, false)
            : new MatchProfileSaveResult(null, result.Error, response.StatusCode == HttpStatusCode.PreconditionFailed);
    }

    private static string ETagOf(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? string.Empty;
}
