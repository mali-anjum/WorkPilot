using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Contracts.Notifications;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Features.Notifications;

/// <summary>
/// The Web host's typed client for <c>/internal/notifications</c> (spec 0020). The bell and the
/// <c>/notifications</c> page call this, always with the signed in profile's id.
/// </summary>
public interface INotificationsApiClient
{
    /// <summary>The unread count the bell shows (AC-4).</summary>
    Task<ApiResult<UnreadCountDto>> GetUnreadCountAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>One page, newest first (AC-4, AC-5).</summary>
    Task<ApiResult<NotificationPageDto>> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read or unread.</summary>
    Task<ApiResult<NotificationDto>> SetReadAsync(Guid profileId, Guid notificationId, bool read, CancellationToken cancellationToken = default);

    /// <summary>Marks every unread notification read.</summary>
    Task<ApiResult<MarkAllNotificationsReadResponse>> MarkAllReadAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>Dismisses one notification; the value is true on success.</summary>
    Task<ApiResult<bool>> DismissAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INotificationsApiClient" />
public sealed class NotificationsApiClient(IHttpClientFactory httpClientFactory) : INotificationsApiClient
{
    private const string NotFound = "The notification was not found.";

    /// <inheritdoc />
    public async Task<ApiResult<UnreadCountDto>> GetUnreadCountAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api().GetAsync($"/internal/notifications/unread-count?profileId={profileId}", cancellationToken);
        return await ApiResultReader.ReadAsync<UnreadCountDto>(response, NotFound, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<NotificationPageDto>> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var url = $"/internal/notifications?profileId={profileId}&unreadOnly={(unreadOnly ? "true" : "false")}&page={page}&pageSize={pageSize}";
        using var response = await Api().GetAsync(url, cancellationToken);
        return await ApiResultReader.ReadAsync<NotificationPageDto>(response, NotFound, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<NotificationDto>> SetReadAsync(Guid profileId, Guid notificationId, bool read, CancellationToken cancellationToken = default)
    {
        using var response = await Api().PostAsJsonAsync($"/internal/notifications/{notificationId}/read", new MarkNotificationReadRequest(profileId, read), cancellationToken);
        return await ApiResultReader.ReadAsync<NotificationDto>(response, NotFound, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<MarkAllNotificationsReadResponse>> MarkAllReadAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await Api().PostAsJsonAsync("/internal/notifications/read-all", new MarkAllNotificationsReadRequest(profileId), cancellationToken);
        return await ApiResultReader.ReadAsync<MarkAllNotificationsReadResponse>(response, NotFound, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<bool>> DismissAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        using var response = await Api().DeleteAsync($"/internal/notifications/{notificationId}?profileId={profileId}", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ApiResult<bool>.Ok(true);
        }

        var failure = await ApiResultReader.ReadAsync<object>(response, NotFound, cancellationToken);
        return ApiResult<bool>.Fail(failure.Error ?? NotFound);
    }

    private HttpClient Api() => httpClientFactory.CreateClient("api");
}

/// <summary>Web host wiring for the Notifications module (spec 0020).</summary>
public static class NotificationsWebExtensions
{
    /// <summary>Registers the notifications Api client and the per circuit browser time zone.</summary>
    public static IServiceCollection AddNotificationsWeb(this IServiceCollection services)
    {
        services.AddScoped<INotificationsApiClient, NotificationsApiClient>();
        services.TryAddScoped<BrowserTimeZone>();
        return services;
    }
}
