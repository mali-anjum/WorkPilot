using Microsoft.AspNetCore.Mvc;
using WorkPilot.Api.Common;
using WorkPilot.Application.Modules.Notifications;
using WorkPilot.Contracts.Notifications;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// The Notifications module's endpoints (spec 0020). Internal only, like every Api endpoint
/// (spec 0004): the Web host passes the signed in <c>profileId</c> and every call is scoped to it,
/// so another profile's notification answers 404 like a missing one (AC-8).
/// </summary>
internal static class NotificationsEndpoints
{
    /// <summary>Maps the <c>/internal/notifications</c> group.</summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/notifications");
        group.MapGet("/", GetPageAsync);
        group.MapGet("/unread-count", GetUnreadCountAsync);
        group.MapPost("/{id:guid}/read", SetReadAsync);
        group.MapPost("/read-all", MarkAllReadAsync);
        group.MapDelete("/{id:guid}", DismissAsync);
        return app;
    }

    /// <summary>One page of the profile's notifications, newest first (AC-5); 400 for bad paging.</summary>
    private static async Task<IResult> GetPageAsync(
        Guid? profileId,
        bool? unreadOnly,
        int? page,
        int? pageSize,
        NotificationsService notifications,
        CancellationToken cancellationToken)
    {
        if (MissingProfile(profileId) is { } problem)
        {
            return problem;
        }

        var result = await notifications.GetPageAsync(
            profileId!.Value,
            unreadOnly ?? false,
            page ?? 1,
            pageSize ?? NotificationsService.DefaultPageSize,
            cancellationToken);
        return result.ToHttp(Results.Ok);
    }

    /// <summary>The bell's unread count (AC-4).</summary>
    private static async Task<IResult> GetUnreadCountAsync(Guid? profileId, NotificationsService notifications, CancellationToken cancellationToken) =>
        MissingProfile(profileId) ?? Results.Ok(await notifications.CountUnreadAsync(profileId!.Value, cancellationToken));

    /// <summary>Marks one notification read or unread (AC-5); 404 when it is missing or not the profile's (AC-8).</summary>
    private static async Task<IResult> SetReadAsync(Guid id, [FromBody] MarkNotificationReadRequest body, NotificationsService notifications, CancellationToken cancellationToken) =>
        MissingProfile(body.ProfileId) ?? (await notifications.SetReadAsync(body.ProfileId, id, body.Read, cancellationToken)).ToHttp(Results.Ok);

    /// <summary>Marks every unread notification of the profile read (AC-4, AC-5).</summary>
    private static async Task<IResult> MarkAllReadAsync([FromBody] MarkAllNotificationsReadRequest body, NotificationsService notifications, CancellationToken cancellationToken) =>
        MissingProfile(body.ProfileId) ?? Results.Ok(await notifications.MarkAllReadAsync(body.ProfileId, cancellationToken));

    /// <summary>Dismisses one notification (AC-5); 404 when it is missing or not the profile's (AC-8).</summary>
    private static async Task<IResult> DismissAsync(Guid id, Guid? profileId, NotificationsService notifications, CancellationToken cancellationToken) =>
        MissingProfile(profileId) ?? (await notifications.DismissAsync(profileId!.Value, id, cancellationToken)).ToHttp(_ => Results.NoContent());

    private static IResult? MissingProfile(Guid? profileId) =>
        profileId is { } id && id != Guid.Empty
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["profileId"] = ["profileId is required."] });
}
