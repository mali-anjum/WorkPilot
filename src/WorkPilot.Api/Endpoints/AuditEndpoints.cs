using WorkPilot.Api.Common;
using WorkPilot.Application.Modules.Audit;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// The activity feed's internal endpoint (spec 0011). Internal only, same network boundary as the
/// other <c>/internal/*</c> routes; the product has one user, so the feed shows every audit row.
/// </summary>
internal static class AuditEndpoints
{
    /// <summary>Maps <c>GET /internal/audit/activity</c>.</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/audit/activity", GetActivityAsync);
        return app;
    }

    /// <summary>
    /// One page of the activity feed, newest first, 50 by default (AC-1), optionally one category
    /// (AC-2): 400 ProblemDetails for an unknown category, a cursor missing a half, or a bad take.
    /// </summary>
    private static async Task<IResult> GetActivityAsync(
        string? category,
        DateTimeOffset? before,
        Guid? beforeId,
        int? take,
        IActivityQuery activity,
        CancellationToken cancellationToken) =>
        (await activity.GetPageAsync(category, before, beforeId, take ?? IActivityQuery.DefaultTake, cancellationToken)).ToHttp(Results.Ok);
}
