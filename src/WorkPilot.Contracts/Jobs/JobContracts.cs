namespace WorkPilot.Contracts.Jobs;

/// <summary>Why a row has the score it has (spec 0021, AC-3), as sent in <see cref="JobListItemDto.MatchStatus"/>.</summary>
public static class JobMatchStatuses
{
    /// <summary>The profile's match row has a score.</summary>
    public const string Scored = "Scored";

    /// <summary>The match row exists, but its score is null: too little was known.</summary>
    public const string NotEnoughInfo = "NotEnoughInfo";

    /// <summary>No match row, and the profile has no skills or no experience.</summary>
    public const string ProfileIncomplete = "ProfileIncomplete";

    /// <summary>No match row yet; scoring has not reached this job.</summary>
    public const string Pending = "Pending";
}

/// <summary>The sort orders of the jobs list (spec 0021, AC-1), as sent in <c>sort</c>.</summary>
public static class JobSorts
{
    /// <summary>Scored before unscored, unblocked before blocked, best score first, then newest.</summary>
    public const string Score = "score";

    /// <summary>Posted date (or first seen) descending.</summary>
    public const string Newest = "newest";
}

/// <summary>
/// The filters, sort and page of <c>GET /internal/matches</c> (spec 0021, AC-2). Every field is
/// optional; null means not filtered.
/// </summary>
public sealed record JobListQuery
{
    /// <summary>The 1 based page.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>Case insensitive contains on title or company.</summary>
    public string? Q { get; init; }

    /// <summary>Exact company.</summary>
    public string? Company { get; init; }

    /// <summary>Case insensitive contains on location.</summary>
    public string? Location { get; init; }

    /// <summary>Exact remote type.</summary>
    public string? RemoteType { get; init; }

    /// <summary>Jobs with a link from this source.</summary>
    public Guid? SourceId { get; init; }

    /// <summary>0 to 100; unscored jobs are left out when set.</summary>
    public int? MinScore { get; init; }

    /// <summary>1, 7 or 30.</summary>
    public int? PostedWithinDays { get; init; }

    /// <summary>Jobs whose top of range is at least this; jobs with no salary are left out when set.</summary>
    public decimal? SalaryMin { get; init; }

    /// <summary>Leaves out jobs whose match has a blocker.</summary>
    public bool HideBlocked { get; init; }

    /// <summary>Shows dismissed jobs too, marked as dismissed.</summary>
    public bool IncludeDismissed { get; init; }

    /// <summary><see cref="JobSorts.Score"/> (the default) or <see cref="JobSorts.Newest"/>.</summary>
    public string? Sort { get; init; }
}

/// <summary>One row of the jobs list (spec 0021, AC-3).</summary>
/// <param name="PostedAt">When the source says it was posted, if it says.</param>
/// <param name="FirstSeenAt">The earliest time any source showed it; the posted date when <paramref name="PostedAt"/> is null.</param>
/// <param name="SourceTypes">One entry per source type the job has a link from.</param>
/// <param name="Confidence">High, Medium or Low, with a match row.</param>
/// <param name="MatchStatus">One of <see cref="JobMatchStatuses"/>.</param>
public sealed record JobListItemDto(
    Guid JobId,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    DateTimeOffset? PostedAt,
    DateTimeOffset FirstSeenAt,
    IReadOnlyList<string> SourceTypes,
    int? Score,
    string? Confidence,
    bool HasBlocker,
    DateTimeOffset? RankedAt,
    string MatchStatus,
    bool Dismissed);

/// <summary>One page of <c>GET /internal/matches</c> (spec 0019, AC-9; spec 0021).</summary>
/// <param name="ProfileIncomplete">True when the profile has no skills or no experience rows.</param>
public sealed record MatchListDto(IReadOnlyList<JobListItemDto> Items, int Total, int Page, int PageSize, bool ProfileIncomplete);

/// <summary>A source in the filter list.</summary>
public sealed record JobSourceFacetDto(Guid Id, string Type, string Name);

/// <summary>The filter choices of <c>GET /internal/jobs/facets</c>: the values present on listed jobs (spec 0021, AC-2).</summary>
public sealed record JobFacetsDto(IReadOnlyList<string> Companies, IReadOnlyList<string> RemoteTypes, IReadOnlyList<JobSourceFacetDto> Sources);

/// <summary>One link of a job, in <c>GET /internal/jobs/{id}</c> (spec 0017, AC-13).</summary>
public sealed record JobLinkView(
    Guid Id,
    Guid JobSourceId,
    string SourceType,
    string ExternalId,
    string SourceUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    decimal Confidence,
    bool IsPrimary,
    DateTimeOffset? SplitAt);

/// <summary>
/// Response body for <c>GET /internal/jobs/{id}</c> (spec 0017, AC-13; spec 0021, AC-5). A soft
/// deleted job is returned with <paramref name="IsDeleted"/> set.
/// </summary>
/// <param name="Dismissed">Whether the <c>profileId</c> asked about dismissed it; false when none was given.</param>
public sealed record JobDetailView(
    Guid Id,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    string? Description,
    DateTimeOffset? PostedAt,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? VerifiedAt,
    decimal? Confidence,
    int SnapshotCount,
    IReadOnlyList<JobLinkView> Links,
    decimal? SalaryRangeMin,
    decimal? SalaryRangeMax,
    bool IsDeleted,
    bool Dismissed);

/// <summary>Request body for <c>PUT /internal/jobs/{id}/dismissal</c>.</summary>
public sealed record DismissJobRequest(Guid ProfileId);

/// <summary>One job source in the Job sources drawer (spec 0021, AC-6).</summary>
/// <param name="JobCount">Links from this source to listed (non deleted) jobs.</param>
/// <param name="LastRunAt">When the latest ingestion run finished, or null when it never ran.</param>
public sealed record JobSourceSummaryDto(
    Guid Id,
    string Type,
    string Name,
    string? CompanyName,
    int JobCount,
    DateTimeOffset? LastRunAt,
    int? LastRunCreated,
    int? LastRunUpdated);

/// <summary>Request body for <c>POST /internal/jobs/ingestions</c> (spec 0008).</summary>
/// <param name="Source">Source type, <c>greenhouse</c> or <c>lever</c>.</param>
/// <param name="BoardToken">The board's identifier at that source: a Greenhouse board token or a Lever site name.</param>
/// <param name="Keywords">Optional title keywords; any one matching keeps a posting.</param>
/// <param name="CompanyName">Optional company every posting from this source belongs to (1 to 200 chars).</param>
public sealed record TriggerJobIngestionRequest(string? Source, string? BoardToken, string? Keywords = null, string? CompanyName = null);

/// <summary>Response body for <c>POST /internal/jobs/ingestions</c> and <c>POST /internal/jobs/sources/{id}/ingestions</c>.</summary>
public sealed record TriggerJobIngestionResponse(Guid JobSourceId, string BackgroundJobId);
