using WorkPilot.Application.Common;
using WorkPilot.Contracts.Audit;

namespace WorkPilot.Application.Modules.Audit;

/// <summary>
/// The one read path over the audit log (spec 0011, AC-8): the <c>/activity</c> page and the
/// dashboard both page through it.
/// </summary>
public interface IActivityQuery
{
    /// <summary>The page size when none is asked for (AC-1).</summary>
    const int DefaultTake = 50;

    /// <summary>The largest page size allowed.</summary>
    const int MaxTake = 100;

    /// <summary>
    /// One page of entries, newest first, optionally one category (case insensitive), strictly
    /// older than the (<paramref name="before"/>, <paramref name="beforeId"/>) cursor. Invalid for an
    /// unknown category, a cursor missing one of its halves, or <paramref name="take"/> outside 1 to
    /// <see cref="MaxTake"/>.
    /// </summary>
    Task<Result<ActivityPageDto>> GetPageAsync(string? category, DateTimeOffset? before, Guid? beforeId, int take, CancellationToken cancellationToken);
}
