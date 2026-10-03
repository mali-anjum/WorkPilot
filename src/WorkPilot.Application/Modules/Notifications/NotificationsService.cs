using WorkPilot.Application.Common;
using WorkPilot.Contracts.Notifications;
using WorkPilot.Domain.Modules.Notifications;

namespace WorkPilot.Application.Modules.Notifications;

/// <summary>
/// The notification use cases behind <c>/internal/notifications</c> (spec 0020). Every one is
/// scoped to a profile: another profile's notification behaves exactly like a missing one (AC-8).
/// </summary>
public sealed class NotificationsService(INotificationRepository notifications, TimeProvider time)
{
    /// <summary>The page size when none is asked for (AC-5).</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest page size allowed.</summary>
    public const int MaxPageSize = 50;

    /// <summary>One page, newest first; Invalid for a page below 1 or a page size outside 1 to <see cref="MaxPageSize"/>.</summary>
    public async Task<Result<NotificationPageDto>> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            return Result<NotificationPageDto>.Invalid("page", "page must be 1 or more.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return Result<NotificationPageDto>.Invalid("pageSize", $"pageSize must be 1 to {MaxPageSize}.");
        }

        return Result<NotificationPageDto>.Ok(await notifications.GetPageAsync(profileId, unreadOnly, page, pageSize, cancellationToken));
    }

    /// <summary>The unread count the bell shows (AC-4).</summary>
    public async Task<UnreadCountDto> CountUnreadAsync(Guid profileId, CancellationToken cancellationToken) =>
        new(await notifications.CountUnreadAsync(profileId, cancellationToken));

    /// <summary>Marks one notification read or unread; NotFound when it is missing, dismissed or someone else's.</summary>
    public async Task<Result<NotificationDto>> SetReadAsync(Guid profileId, Guid notificationId, bool read, CancellationToken cancellationToken)
    {
        if (await notifications.FindAsync(profileId, notificationId, cancellationToken) is not { } notification)
        {
            return Result<NotificationDto>.NotFound("The notification was not found.");
        }

        if (read)
        {
            notification.MarkRead(time.GetUtcNow());
        }
        else
        {
            notification.MarkUnread();
        }

        await notifications.SaveChangesAsync(cancellationToken);
        return Result<NotificationDto>.Ok(ToDto(notification));
    }

    /// <summary>Marks every unread notification read (AC-5).</summary>
    public async Task<MarkAllNotificationsReadResponse> MarkAllReadAsync(Guid profileId, CancellationToken cancellationToken) =>
        new(await notifications.MarkAllReadAsync(profileId, time.GetUtcNow(), cancellationToken));

    /// <summary>Dismisses one notification for good; NotFound when it is missing, dismissed or someone else's.</summary>
    public async Task<Result<bool>> DismissAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken)
    {
        if (await notifications.FindAsync(profileId, notificationId, cancellationToken) is not { } notification)
        {
            return Result<bool>.NotFound("The notification was not found.");
        }

        notification.Dismiss(time.GetUtcNow());
        await notifications.SaveChangesAsync(cancellationToken);
        return Result<bool>.Ok(true);
    }

    /// <summary>The DTO of a notification.</summary>
    public static NotificationDto ToDto(Notification n) =>
        new(n.Id, n.Type, n.Priority.ToString(), n.Title, n.Body, n.Link, n.Payload, n.CreatedAt, n.ReadAt);
}

/// <summary>Notification settings (<c>Notifications</c> configuration section, spec 0020).</summary>
public sealed class NotificationsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Notifications";

    /// <summary>How many days a read notification is kept before the cleanup dismisses it (AC-7). 1 to 3650, default 90.</summary>
    public int ReadRetentionDays { get; set; } = 90;
}

/// <summary>
/// The daily cleanup (spec 0020, AC-7): dismisses read notifications read more than
/// <see cref="NotificationsOptions.ReadRetentionDays"/> ago. Unread ones are never touched.
/// </summary>
public sealed class NotificationsCleanup(INotificationRepository notifications, TimeProvider time)
{
    /// <summary>Runs one cleanup; returns how many were dismissed.</summary>
    public Task<int> RunAsync(int readRetentionDays, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return notifications.DismissReadBeforeAsync(now.AddDays(-readRetentionDays), now, cancellationToken);
    }
}
