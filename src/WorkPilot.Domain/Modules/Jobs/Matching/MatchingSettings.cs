namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>Each dimension's weight; the eight must sum to 100 (spec 0019, AC-16).</summary>
public sealed class MatchWeights
{
    public int Skills { get; set; } = 30;
    public int Title { get; set; } = 15;
    public int Experience { get; set; } = 15;
    public int Location { get; set; } = 15;
    public int Salary { get; set; } = 10;
    public int Education { get; set; } = 5;
    public int JobType { get; set; } = 5;
    public int WorkAuthorization { get; set; } = 5;

    /// <summary>The weight of <paramref name="dimension"/>.</summary>
    public int Of(MatchDimension dimension) => dimension switch
    {
        MatchDimension.Skills => Skills,
        MatchDimension.Title => Title,
        MatchDimension.Experience => Experience,
        MatchDimension.Location => Location,
        MatchDimension.Salary => Salary,
        MatchDimension.Education => Education,
        MatchDimension.JobType => JobType,
        MatchDimension.WorkAuthorization => WorkAuthorization,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };

    /// <summary>All eight weights, in dimension order.</summary>
    public IEnumerable<int> All() => Enum.GetValues<MatchDimension>().Select(Of);
}

/// <summary>The known weight shares at which confidence is High or Medium (AC-3).</summary>
public sealed class ConfidenceThresholds
{
    public decimal High { get; set; } = 0.8m;
    public decimal Medium { get; set; } = 0.5m;
}

/// <summary>
/// The <c>Matching</c> configuration section (spec 0019, "Configuration required"): weights, the
/// blocker cap, confidence thresholds, how much description the extractor sees, and skill aliases.
/// Bound from configuration and checked with <see cref="Validate"/> at startup (AC-16).
/// </summary>
public sealed class MatchingSettings
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Matching";

    public MatchWeights Weights { get; set; } = new();

    /// <summary>The highest score a job with a blocker can get (AC-5).</summary>
    public int BlockerCap { get; set; } = 20;

    public ConfidenceThresholds Confidence { get; set; } = new();

    /// <summary>Where the description is cut before extraction and quote checks (AC-12).</summary>
    public int MaxDescriptionChars { get; set; } = 20000;

    /// <summary>Alias to canonical skill name, both normalized the same way before use.</summary>
    public Dictionary<string, string> SkillAliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Every broken rule (AC-16); empty when the settings are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var weights = Weights.All().ToList();
        if (weights.Any(w => w < 0))
        {
            errors.Add("Matching:Weights must not be negative.");
        }

        if (weights.Sum() != 100)
        {
            errors.Add($"Matching:Weights must sum to 100, not {weights.Sum()}.");
        }

        if (BlockerCap is < 0 or > 100)
        {
            errors.Add("Matching:BlockerCap must be 0 to 100.");
        }

        if (!(Confidence.Medium > 0 && Confidence.Medium < Confidence.High && Confidence.High <= 1))
        {
            errors.Add("Matching:Confidence must satisfy 0 < Medium < High <= 1.");
        }

        if (MaxDescriptionChars <= 0)
        {
            errors.Add("Matching:MaxDescriptionChars must be positive.");
        }

        foreach (var (alias, canonical) in SkillAliases)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(canonical))
            {
                errors.Add("Matching:SkillAliases entries must name both an alias and a skill.");
                break;
            }
        }

        return errors;
    }

    /// <summary><paramref name="description"/> cut at <see cref="MaxDescriptionChars"/>: exactly what the extractor sees.</summary>
    public string Truncate(string? description)
    {
        var text = description ?? string.Empty;
        return text.Length > MaxDescriptionChars ? text[..MaxDescriptionChars] : text;
    }

    /// <summary>
    /// A stable text of every scoring setting, aliases sorted, for the inputs fingerprint: any
    /// config change rescores what it touches (AC-7).
    /// </summary>
    public string Canonical()
    {
        var aliases = SkillAliases
            .Select(a => (Alias: SkillName.Normalize(a.Key), Canonical: SkillName.Normalize(a.Value)))
            .OrderBy(a => a.Alias, StringComparer.Ordinal)
            .Select(a => $"{a.Alias}={a.Canonical}");
        return string.Join(
            "|",
            string.Join(",", Weights.All()),
            BlockerCap,
            Confidence.High.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Confidence.Medium.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MaxDescriptionChars,
            string.Join(";", aliases));
    }
}
