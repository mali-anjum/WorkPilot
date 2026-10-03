namespace WorkPilot.Application.Modules.Jobs.Matching;

/// <summary>One row of <c>GET /internal/matches</c> (spec 0019, AC-9).</summary>
public sealed record MatchListItemDto(
    Guid JobId,
    string Title,
    string Company,
    string? Location,
    int? Score,
    string? Confidence,
    bool HasBlocker,
    DateTimeOffset? RankedAt);

/// <summary>One page of <c>GET /internal/matches</c>.</summary>
/// <param name="ProfileIncomplete">True when the profile has no skills or no experience rows.</param>
public sealed record MatchListDto(IReadOnlyList<MatchListItemDto> Items, int Total, int Page, int PageSize, bool ProfileIncomplete);

/// <summary>The match panel of <c>GET /internal/jobs/{jobId}/match</c> (spec 0019, AC-10).</summary>
public sealed record JobMatchDetailDto(
    Guid JobId,
    string Title,
    string Company,
    string? Location,
    string JobUrl,
    int? Score,
    string Confidence,
    bool HasBlocker,
    MatchExplanationDto Explanation,
    string? RequirementsStatus,
    string? FailureReason,
    DateTimeOffset RankedAt);

/// <summary>The rescore request body.</summary>
public sealed record RescoreMatchRequest(Guid ProfileId);

/// <summary>The rescore answer: false when extraction was already Pending, so nothing new was queued.</summary>
public sealed record RescoreMatchResponse(bool Queued);

// The explanation v1 document as the Web reads it: the same JSON shape as the Domain's
// MatchExplanation, read straight from the stored JSON, with enums as their names.

/// <summary>A pointer to the profile item behind a line.</summary>
public sealed record ProfileRefDto(string Kind, Guid? Id, string Label);

/// <summary>One line of evidence.</summary>
public sealed record ExplanationItemDto(string Label, string Status, string? JobQuote, bool Verified, ProfileRefDto? ProfileRef, string? Dimension);

/// <summary>One dimension's result.</summary>
public sealed record DimensionResultDto(string Name, string Status, decimal Earned, int Weight, IReadOnlyList<ExplanationItemDto> Items);

/// <summary>A dimension left out of the score.</summary>
public sealed record UnknownInfoDto(string Dimension, string Reason);

/// <summary>An item whose quote failed verification.</summary>
public sealed record UnverifiedItemDto(string Dimension, string Label, string JobQuote);

/// <summary>The whole explanation.</summary>
public sealed record MatchExplanationDto(
    int V,
    IReadOnlyList<DimensionResultDto> Dimensions,
    IReadOnlyList<ExplanationItemDto> Missing,
    IReadOnlyList<UnknownInfoDto> Unknown,
    IReadOnlyList<ExplanationItemDto> Blockers,
    IReadOnlyList<UnverifiedItemDto> Unverified,
    bool ExtractionFailed);
