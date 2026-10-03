using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>How much a posting wants a skill.</summary>
public enum SkillImportance
{
    Required,
    Preferred,
}

/// <summary>The work arrangement a posting states.</summary>
public enum WorkArrangement
{
    Remote,
    Hybrid,
    Onsite,
}

/// <summary>The period a posted salary is quoted per.</summary>
public enum SalaryPeriod
{
    Year,
    Month,
    Hour,
}

/// <summary>A skill the posting asks for, with the sentence that says so.</summary>
public sealed class SkillRequirement
{
    public string Name { get; init; } = string.Empty;
    public SkillImportance Importance { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>The minimum years of experience the posting asks for.</summary>
public sealed class YearsRequirement
{
    public int Value { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>The degree the posting asks for; <see cref="Level"/> is never <see cref="DegreeLevel.None"/>.</summary>
public sealed class DegreeRequirement
{
    public DegreeLevel Level { get; init; }
    public string? Field { get; init; }
    public bool OrEquivalentExperience { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>The work arrangement the posting states.</summary>
public sealed class RemoteRequirement
{
    public WorkArrangement Type { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>A place the posting says the work is; <see cref="Country"/> is an ISO code or a country name.</summary>
public sealed class LocationRequirement
{
    public string? City { get; init; }
    public string Country { get; init; } = string.Empty;
    public string Quote { get; init; } = string.Empty;
}

/// <summary>The salary the posting states.</summary>
public sealed class SalaryRequirement
{
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string Currency { get; init; } = string.Empty;
    public SalaryPeriod Period { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>The job type the posting states.</summary>
public sealed class JobTypeRequirement
{
    public JobType Value { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>Whether the posting offers visa sponsorship.</summary>
public sealed class SponsorshipRequirement
{
    public bool Offered { get; init; }
    public string Quote { get; init; } = string.Empty;
}

/// <summary>
/// A job's requirements as the extractor read them (spec 0019, "Requirements v1 document"). Every
/// item carries a <c>Quote</c>, a verbatim sentence from the description, checked by
/// <see cref="QuoteVerifier"/> before the item may count toward a score (AC-6).
/// </summary>
public sealed class JobRequirementsV1
{
    /// <summary>Most skills one document may hold (AC-12).</summary>
    public const int MaxSkills = 60;

    /// <summary>Most locations one document may hold.</summary>
    public const int MaxLocations = 20;

    /// <summary>Longest quote (AC-12).</summary>
    public const int MaxQuoteLength = 300;

    /// <summary>Longest skill name (the shared <c>skills.Name</c> column).</summary>
    public const int MaxSkillNameLength = 100;

    /// <summary>The schema version, always 1.</summary>
    public int V { get; init; } = 1;

    public List<SkillRequirement> Skills { get; init; } = [];
    public YearsRequirement? MinYears { get; init; }
    public DegreeRequirement? Degree { get; init; }
    public RemoteRequirement? Remote { get; init; }
    public List<LocationRequirement> Locations { get; init; } = [];
    public SalaryRequirement? Salary { get; init; }
    public JobTypeRequirement? JobType { get; init; }
    public SponsorshipRequirement? Sponsorship { get; init; }

    /// <summary>
    /// Why this document breaks the v1 schema's bounds, or null when it is within them (AC-12). A
    /// short quote is not a schema error: it only leaves its item unverified (AC-6).
    /// </summary>
    public string? FindBoundsError()
    {
        if (V != 1)
        {
            return $"Unsupported requirements version {V}.";
        }

        if (Skills is null || Locations is null)
        {
            return "Skills and Locations must be lists.";
        }

        if (Skills.Count > MaxSkills)
        {
            return $"{Skills.Count} skills is more than the {MaxSkills} allowed.";
        }

        if (Locations.Count > MaxLocations)
        {
            return $"{Locations.Count} locations is more than the {MaxLocations} allowed.";
        }

        foreach (var skill in Skills)
        {
            if (skill is null || string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Trim().Length > MaxSkillNameLength)
            {
                return $"A skill name must be 1 to {MaxSkillNameLength} characters.";
            }
        }

        if (MinYears is { Value: < 0 or > 60 })
        {
            return "MinYears must be 0 to 60.";
        }

        if (Degree is { Level: DegreeLevel.None })
        {
            return "Degree.Level cannot be None.";
        }

        if (Locations.Any(l => l is null || string.IsNullOrWhiteSpace(l.Country)))
        {
            return "Every location needs a country.";
        }

        if (Salary is not null && (string.IsNullOrWhiteSpace(Salary.Currency) || Salary.Min is < 0 || Salary.Max is < 0))
        {
            return "A salary needs a currency and amounts that are not negative.";
        }

        var quotes = Skills.Select(s => s.Quote)
            .Concat(Locations.Select(l => l.Quote))
            .Append(MinYears?.Quote)
            .Append(Degree?.Quote)
            .Append(Remote?.Quote)
            .Append(Salary?.Quote)
            .Append(JobType?.Quote)
            .Append(Sponsorship?.Quote);
        foreach (var quote in quotes)
        {
            if (quote is not null && quote.Length > MaxQuoteLength)
            {
                return $"A quote is longer than {MaxQuoteLength} characters.";
            }
        }

        var missingQuote = Skills.Any(s => s.Quote is null) || Locations.Any(l => l.Quote is null)
            || MinYears is { Quote: null } || Degree is { Quote: null } || Remote is { Quote: null }
            || Salary is { Quote: null } || JobType is { Quote: null } || Sponsorship is { Quote: null };
        return missingQuote ? "Every item needs a quote." : null;
    }
}

/// <summary>Where a job's requirements extraction stands (spec 0019, "State transitions").</summary>
public enum RequirementsStatus
{
    Pending,
    Extracted,
    Failed,
}

/// <summary>
/// A job's extracted requirements, one row per job (spec 0019). Only the Jobs module writes it.
/// <see cref="Status"/> moves Pending → Extracted, Pending → Failed (third failed attempt), and
/// Extracted or Failed → Pending (a new content hash, a newer extractor, or a manual Rescore).
/// </summary>
public class JobRequirement : Entity
{
    /// <summary>The extractor's prompt and schema version; bump it to re-extract every job.</summary>
    public const int CurrentExtractorVersion = 1;

    /// <summary>How many failed attempts move a row to <see cref="RequirementsStatus.Failed"/>.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Longest stored failure reason.</summary>
    public const int MaxFailureReasonLength = 1000;

    public Guid JobId { get; private init; }

    /// <summary>The primary link's latest snapshot hash this row was (or is being) extracted from.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public int ExtractorVersion { get; private set; }
    public RequirementsStatus Status { get; private set; }

    /// <summary>The v1 document as JSON; null unless <see cref="Status"/> is Extracted.</summary>
    public string? Requirements { get; private set; }

    /// <summary>The model id that answered.</summary>
    public string? Model { get; private set; }

    /// <summary>Failed attempts since the row last went Pending.</summary>
    public int Attempts { get; private set; }

    /// <summary>When the row last went Pending; lets the sweep find lost extractions.</summary>
    public DateTimeOffset? PendingSince { get; private set; }

    public string? FailureReason { get; private set; }
    public DateTimeOffset? ExtractedAt { get; private set; }

    /// <summary>A new Pending row for a job's current content.</summary>
    public static JobRequirement Start(Guid jobId, string contentHash, DateTimeOffset now) => new()
    {
        JobId = jobId,
        ContentHash = contentHash,
        ExtractorVersion = CurrentExtractorVersion,
        Status = RequirementsStatus.Pending,
        PendingSince = now,
    };

    /// <summary>True when this row already holds an extraction of <paramref name="contentHash"/> by the current extractor.</summary>
    public bool IsCurrentFor(string contentHash) =>
        Status == RequirementsStatus.Extracted && ContentHash == contentHash && ExtractorVersion == CurrentExtractorVersion;

    /// <summary>
    /// Moves the row to Pending for <paramref name="contentHash"/>, resetting the attempt counter.
    /// A row already Pending for the same content keeps its counter, so retries still end at
    /// <see cref="MaxAttempts"/>.
    /// </summary>
    public void MarkPending(string contentHash, DateTimeOffset now)
    {
        if (Status == RequirementsStatus.Pending && ContentHash == contentHash && ExtractorVersion == CurrentExtractorVersion)
        {
            return;
        }

        ContentHash = contentHash;
        ExtractorVersion = CurrentExtractorVersion;
        Status = RequirementsStatus.Pending;
        Requirements = null;
        Attempts = 0;
        PendingSince = now;
        FailureReason = null;
    }

    /// <summary>Stores a valid extraction (Pending → Extracted).</summary>
    public void MarkExtracted(string requirementsJson, string model, DateTimeOffset now)
    {
        if (Status != RequirementsStatus.Pending)
        {
            throw new InvalidOperationException($"Requirements of job {JobId} are {Status}, not Pending.");
        }

        Status = RequirementsStatus.Extracted;
        Requirements = requirementsJson;
        Model = model;
        FailureReason = null;
        ExtractedAt = now;
    }

    /// <summary>
    /// Counts one failed attempt. Returns true when it was the last one allowed, which moves the
    /// row to Failed (AC-8); false when another attempt should follow.
    /// </summary>
    public bool RecordFailure(string reason)
    {
        if (Status != RequirementsStatus.Pending)
        {
            throw new InvalidOperationException($"Requirements of job {JobId} are {Status}, not Pending.");
        }

        Attempts++;
        FailureReason = reason.Length > MaxFailureReasonLength ? reason[..MaxFailureReasonLength] : reason;
        if (Attempts < MaxAttempts)
        {
            return false;
        }

        Status = RequirementsStatus.Failed;
        return true;
    }
}

/// <summary>
/// Checks an extracted quote against the description text the extractor was given (spec 0019,
/// AC-6): it must be at least <see cref="MinQuoteLength"/> characters and appear in that text after
/// whitespace and case normalization.
/// </summary>
public sealed class QuoteVerifier
{
    /// <summary>Shortest quote that can verify an item.</summary>
    public const int MinQuoteLength = 10;

    private readonly string _text;

    /// <param name="descriptionText">The description exactly as the extractor saw it (already truncated).</param>
    public QuoteVerifier(string? descriptionText) => _text = Normalize(descriptionText ?? string.Empty);

    /// <summary>True when <paramref name="quote"/> verifies.</summary>
    public bool IsVerified(string? quote)
    {
        if (quote is null || quote.Trim().Length < MinQuoteLength)
        {
            return false;
        }

        return _text.Contains(Normalize(quote), StringComparison.Ordinal);
    }

    /// <summary>Lower case, with every run of whitespace turned into one space, trimmed.</summary>
    public static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
