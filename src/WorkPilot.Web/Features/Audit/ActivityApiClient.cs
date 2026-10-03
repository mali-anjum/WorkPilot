using WorkPilot.Contracts.Audit;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Features.Audit;

/// <summary>
/// The Web host's typed client for the activity feed endpoint (spec 0011). Pages call this rather
/// than the Api directly, since only the Web server can reach the internal Api (spec 0004).
/// </summary>
public interface IActivityApiClient
{
    /// <summary>
    /// One page of the feed, newest first, optionally one category, older than the cursor
    /// (<paramref name="before"/>, <paramref name="beforeId"/>) when given (AC-1, AC-2, AC-8).
    /// </summary>
    Task<ApiResult<ActivityPageDto>> GetPageAsync(string? category, DateTimeOffset? before, Guid? beforeId, int take = 50, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IActivityApiClient" />
public sealed class ActivityApiClient(IHttpClientFactory httpClientFactory) : IActivityApiClient
{
    /// <inheritdoc />
    public async Task<ApiResult<ActivityPageDto>> GetPageAsync(string? category, DateTimeOffset? before, Guid? beforeId, int take = 50, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }

        if (before is { } at && beforeId is { } id)
        {
            query.Add($"before={Uri.EscapeDataString(at.ToString("O"))}");
            query.Add($"beforeId={id}");
        }

        using var response = await httpClientFactory.CreateClient("api").GetAsync($"/internal/audit/activity?{string.Join('&', query)}", cancellationToken);
        return await ApiResultReader.ReadAsync<ActivityPageDto>(response, "The activity feed could not be found.", cancellationToken);
    }
}

/// <summary>Web host wiring for the Audit module: the activity feed (spec 0011).</summary>
public static class AuditWebExtensions
{
    /// <summary>Registers the activity feed client and the per circuit browser time zone.</summary>
    public static IServiceCollection AddAuditWeb(this IServiceCollection services) =>
        services
            .AddScoped<IActivityApiClient, ActivityApiClient>()
            .AddScoped<BrowserTimeZone>();
}
