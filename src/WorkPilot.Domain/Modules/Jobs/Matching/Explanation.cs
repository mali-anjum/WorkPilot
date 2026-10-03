namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>The eight scored dimensions, in their fixed explanation order (spec 0019).</summary>
public enum MatchDimension
{
    Skills,
    Title,
    Experience,
    Location,
    Salary,
    Education,
    JobType,
    WorkAuthorization,
}

/// <summary>How a dimension, or one item of it, came out.</summary>
public enum DimensionStatus
{
    Met,
    Partial,
    Missed,
    Unknown,
    Blocker,
}

/// <summary>Why a dimension could not be scored.</summary>
public enum UnknownReason
{
    JobSilent,
    ProfileNotSet,
    CurrencyDiffers,
    ExtractionFailed,
    Unparseable,
}

/// <summary>How much of the total weight was known when the score was computed (AC-3).</summary>
public enum MatchConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>Which kind of profile item an explanation line points at.</summary>
public enum ProfileRefKind
{
    Skill,
    TargetRole,
    Experience,
    Education,
    Preference,
}

/// <summary>A pointer to the profile item behind an explanation line.</summary>
public sealed class ProfileRef
{
    public ProfileRefKind Kind { get; init; }
    public Guid? Id { get; init; }
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// One line of evidence (spec 0019, explanation v1 <c>Item</c>). <see cref="JobQuote"/> is the
/// posting text (verified), or null for a line built from a job column in the fallback.
/// <see cref="Dimension"/> is set only on blockers.
/// </summary>
public sealed class ExplanationItem
{
    public string Label { get; init; } = string.Empty;
    public DimensionStatus Status { get; init; }
    public string? JobQuote { get; init; }
    public bool Verified { get; init; }
    public ProfileRef? ProfileRef { get; init; }
    public MatchDimension? Dimension { get; init; }
}

/// <summary>One dimension's result: its status, points earned out of its weight, and its evidence.</summary>
public sealed class DimensionResult
{
    public MatchDimension Name { get; init; }
    public DimensionStatus Status { get; init; }

    /// <summary>Points earned, rounded to 2 decimals for display; the score itself uses the unrounded value.</summary>
    public decimal Earned { get; init; }

    public int Weight { get; init; }
    public List<ExplanationItem> Items { get; init; } = [];
}

/// <summary>A dimension left out of the score, and why.</summary>
public sealed class UnknownInfo
{
    public MatchDimension Dimension { get; init; }
    public UnknownReason Reason { get; init; }
}

/// <summary>An extracted item whose quote failed verification; it scored nothing (AC-6).</summary>
public sealed class UnverifiedItem
{
    public MatchDimension Dimension { get; init; }
    public string Label { get; init; } = string.Empty;
    public string JobQuote { get; init; } = string.Empty;
}

/// <summary>
/// Why a job scored what it did (spec 0019, explanation v1). <see cref="Dimensions"/> always lists
/// all eight in <see cref="MatchDimension"/> order.
/// </summary>
public sealed class MatchExplanation
{
    public int V { get; init; } = 1;
    public List<DimensionResult> Dimensions { get; init; } = [];
    public List<ExplanationItem> Missing { get; init; } = [];
    public List<UnknownInfo> Unknown { get; init; } = [];
    public List<ExplanationItem> Blockers { get; init; } = [];
    public List<UnverifiedItem> Unverified { get; init; } = [];

    /// <summary>True when requirements could not be read, so only job columns were scored (AC-8).</summary>
    public bool ExtractionFailed { get; init; }
}

/// <summary>The scorer's output for one (job, profile).</summary>
/// <param name="Score">0 to 100, or null when nothing was known (AC-3).</param>
public sealed record MatchResult(int? Score, MatchConfidence Confidence, bool HasBlocker, MatchExplanation Explanation);
