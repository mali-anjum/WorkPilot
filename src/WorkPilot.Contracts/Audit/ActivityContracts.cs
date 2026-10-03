namespace WorkPilot.Contracts.Audit;

/// <summary>Response body for <c>GET /internal/audit/activity</c>: one page of the activity feed, newest first (spec 0011, AC-1).</summary>
/// <param name="Items">The entries, <c>OccurredAt</c> then <c>Id</c> descending.</param>
/// <param name="NextBefore">The cursor for the next page (the last item's <c>OccurredAt</c>), or null when there is no next page.</param>
/// <param name="NextBeforeId">The last item's <c>Id</c>, sent with <paramref name="NextBefore"/>.</param>
public sealed record ActivityPageDto(IReadOnlyList<ActivityEntryDto> Items, DateTimeOffset? NextBefore, Guid? NextBeforeId);

/// <summary>One audit entry as the feed shows it.</summary>
/// <param name="Id">The audit row.</param>
/// <param name="OccurredAt">When it happened (UTC).</param>
/// <param name="Actor">A profile id, <c>Agent</c>, or another system actor.</param>
/// <param name="Action">What happened, as the writing module named it.</param>
/// <param name="Category"><c>Agent</c>, <c>Jobs</c>, <c>Email</c>, <c>Calendar</c>, <c>System</c> or <c>Errors</c>.</param>
/// <param name="TargetType">The kind of thing it touched.</param>
/// <param name="TargetId">The thing it touched.</param>
/// <param name="Payload">The evidence, as JSON, or null when none was recorded.</param>
public sealed record ActivityEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string Category,
    string TargetType,
    Guid TargetId,
    string? Payload);
