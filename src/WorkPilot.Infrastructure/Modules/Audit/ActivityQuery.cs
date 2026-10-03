using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Audit;
using WorkPilot.Contracts.Audit;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Audit;

/// <inheritdoc cref="IActivityQuery" />
public sealed class ActivityQuery(WorkPilotDbContext db) : IActivityQuery
{
    /// <inheritdoc />
    public async Task<Result<ActivityPageDto>> GetPageAsync(string? category, DateTimeOffset? before, Guid? beforeId, int take, CancellationToken cancellationToken)
    {
        ActivityCategory? filter = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!AuditCategories.TryParse(category.Trim(), out var parsed))
            {
                return Result<ActivityPageDto>.Invalid("category", $"Unknown category \"{category}\". Use one of: {string.Join(", ", Enum.GetNames<ActivityCategory>())}.");
            }

            filter = parsed;
        }

        if (before.HasValue != beforeId.HasValue)
        {
            return Result<ActivityPageDto>.Invalid("beforeId", "before and beforeId must be sent together.");
        }

        if (take is < 1 or > IActivityQuery.MaxTake)
        {
            return Result<ActivityPageDto>.Invalid("take", $"take must be 1 to {IActivityQuery.MaxTake}.");
        }

        var rows = db.AuditLogs.AsNoTracking();
        if (filter is { } only)
        {
            rows = rows.Where(a => a.Category == only);
        }

        if (before is { } cursorAt && beforeId is { } cursorId)
        {
            // Keyset paging (spec 0011): a row value comparison on the feed index, so rows inserted
            // at the top between two pages never shift what the next page returns.
            var at = cursorAt.ToUniversalTime();
            rows = rows.Where(a => EF.Functions.LessThan(ValueTuple.Create(a.OccurredAt, a.Id), ValueTuple.Create(at, cursorId)));
        }

        // One extra row tells whether a next page exists.
        var page = await rows
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Take(take + 1)
            .Select(a => new ActivityEntryDto(a.Id, a.OccurredAt, a.Actor, a.Action, a.Category.ToString(), a.TargetType, a.TargetId, a.Payload))
            .ToListAsync(cancellationToken);

        if (page.Count <= take)
        {
            return Result<ActivityPageDto>.Ok(new ActivityPageDto(page, null, null));
        }

        page.RemoveAt(page.Count - 1);
        var last = page[^1];
        return Result<ActivityPageDto>.Ok(new ActivityPageDto(page, last.OccurredAt, last.Id));
    }
}
