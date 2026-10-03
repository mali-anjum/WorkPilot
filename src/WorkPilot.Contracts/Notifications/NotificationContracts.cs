namespace WorkPilot.Contracts.Notifications;

/// <summary>One notification as the bell and <c>/notifications</c> show it (spec 0020).</summary>
/// <param name="Id">The notification.</param>
/// <param name="Type">What it is about, e.g. <c>approvals.requested</c>.</param>
/// <param name="Priority"><c>Info</c>, <c>Success</c>, <c>Warning</c>, <c>ActionRequired</c> or <c>Error</c>.</param>
/// <param name="Title">The headline.</param>
/// <param name="Body">More detail, or null.</param>
/// <param name="Link">An app relative path to open, or null.</param>
/// <param name="Payload">Extra data as JSON, or null.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ReadAt">When it was read (UTC), or null while unread.</param>
public sealed record NotificationDto(
    Guid Id,
    string Type,
    string Priority,
    string Title,
    string? Body,
    string? Link,
    string? Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>Response body for <c>GET /internal/notifications</c>: one page, newest first (AC-5).</summary>
public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, int Total, int Page, int PageSize);

/// <summary>Response body for <c>GET /internal/notifications/unread-count</c> (AC-4).</summary>
public sealed record UnreadCountDto(int Count);

/// <summary>Request body for <c>POST /internal/notifications/{id}/read</c>: <paramref name="Read"/> false marks it unread.</summary>
public sealed record MarkNotificationReadRequest(Guid ProfileId, bool Read);

/// <summary>Request body for <c>POST /internal/notifications/read-all</c>.</summary>
public sealed record MarkAllNotificationsReadRequest(Guid ProfileId);

/// <summary>Response body for <c>POST /internal/notifications/read-all</c>: how many were marked read.</summary>
public sealed record MarkAllNotificationsReadResponse(int Updated);
