using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>A profile skill as the scorer sees it.</summary>
public sealed record ProfileSkillInput(Guid Id, string Name);

/// <summary>A profile experience row as the scorer sees it.</summary>
public sealed record ExperienceInput(Guid Id, string Title, string Company, DateOnly StartDate, DateOnly? EndDate);

/// <summary>A profile education row as the scorer sees it.</summary>
public sealed record EducationInput(Guid Id, string Degree, string Field, DegreeLevel Level);

/// <summary>
/// The match profile read model (spec 0019): everything about a profile the scorer may read, and
/// nothing else. Never sent to an AI model.
/// </summary>
public sealed record MatchProfileInput(
    Guid ProfileId,
    IReadOnlyList<ProfileSkillInput> Skills,
    IReadOnlyList<string> TargetRoles,
    IReadOnlyList<ExperienceInput> Experiences,
    IReadOnlyList<EducationInput> Educations,
    RemotePreference RemotePreference,
    IReadOnlyList<PreferredLocation> PreferredLocations,
    IReadOnlyList<JobType> JobTypes,
    decimal? MinSalary,
    string? SalaryCurrency,
    IReadOnlyList<string> AuthorizedCountries,
    bool NeedsSponsorshipElsewhere,
    int StrongMatchThreshold)
{
    /// <summary>True when the profile lacks skills or experience, so scores are thin (AC-9 banner).</summary>
    public bool IsIncomplete => Skills.Count == 0 || Experiences.Count == 0;

    /// <summary>True when an experience row has no end date, so its years grow every day.</summary>
    public bool HasOpenEndedExperience => Experiences.Any(e => e.EndDate is null);

    /// <summary>
    /// SHA-256 hex of a canonical text of this profile (skills sorted, rows by id), so the same
    /// profile always gives the same fingerprint (spec 0019, Value sourcing).
    /// </summary>
    public string Fingerprint()
    {
        var text = new CanonicalText()
            .Add("skills").AddAll(Skills.OrderBy(s => s.Id).Select(s => $"{s.Id}:{s.Name}"))
            .Add("roles").AddAll(TargetRoles)
            .Add("experiences").AddAll(Experiences.OrderBy(e => e.Id).Select(e => $"{e.Id}:{e.StartDate:O}:{e.EndDate:O}:{e.Title}:{e.Company}"))
            .Add("educations").AddAll(Educations.OrderBy(e => e.Id).Select(e => $"{e.Id}:{e.Level}:{e.Degree}:{e.Field}"))
            .Add(RemotePreference.ToString())
            .Add("locations").AddAll(PreferredLocations.Select(l => $"{l.City}:{l.Country}"))
            .Add("jobTypes").AddAll(JobTypes.Select(t => t.ToString()))
            .Add(MinSalary?.ToString(CultureInfo.InvariantCulture) ?? "-")
            .Add(SalaryCurrency ?? "-")
            .Add("authorized").AddAll(AuthorizedCountries)
            .Add(NeedsSponsorshipElsewhere ? "sponsor" : "no-sponsor")
            .Add(StrongMatchThreshold.ToString(CultureInfo.InvariantCulture));
        return text.Hash();
    }
}

/// <summary>A job as the scorer sees it.</summary>
/// <param name="VerificationText">The description cut at <c>MaxDescriptionChars</c>, exactly what extraction saw.</param>
/// <param name="Requirements">The extracted requirements, or null when extraction failed.</param>
/// <param name="ExtractionFailed">True when the requirements row is Failed (AC-8).</param>
public sealed record JobMatchInput(
    Guid JobId,
    string Title,
    string? Location,
    string? RemoteType,
    string VerificationText,
    JobRequirementsV1? Requirements,
    bool ExtractionFailed);

/// <summary>The parts of a job's requirements row the inputs fingerprint reads.</summary>
public sealed record RequirementsStamp(string ContentHash, int ExtractorVersion, RequirementsStatus Status);

/// <summary>
/// The inputs fingerprint of one (job, profile) match (spec 0019, AC-7): when it is unchanged no
/// row is written; when any input changes the match is rescored.
/// </summary>
public static class MatchFingerprint
{
    /// <summary>
    /// SHA-256 hex of the requirements stamp, the job columns the scorer reads, the profile
    /// fingerprint, the whole scoring config, <see cref="MatchScorer.ScoringVersion"/>, and today's
    /// UTC date only when the profile has an open ended experience row.
    /// </summary>
    public static string Compute(
        RequirementsStamp requirements,
        JobMatchInput job,
        string profileFingerprint,
        MatchingSettings settings,
        bool includesToday,
        DateOnly today) =>
        new CanonicalText()
            .Add(requirements.ContentHash)
            .Add(requirements.ExtractorVersion.ToString(CultureInfo.InvariantCulture))
            .Add(requirements.Status.ToString())
            .Add(job.Title)
            .Add(job.Location ?? "-")
            .Add(job.RemoteType ?? "-")
            .Add(profileFingerprint)
            .Add(settings.Canonical())
            .Add(MatchScorer.ScoringVersion.ToString(CultureInfo.InvariantCulture))
            .Add(includesToday ? today.ToString("O", CultureInfo.InvariantCulture) : "-")
            .Hash();
}

/// <summary>Builds an unambiguous text (each part length prefixed) and hashes it.</summary>
internal sealed class CanonicalText
{
    private readonly StringBuilder _builder = new();

    public CanonicalText Add(string part)
    {
        _builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append(';');
        return this;
    }

    public CanonicalText AddAll(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        Add(list.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var part in list)
        {
            Add(part);
        }

        return this;
    }

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_builder.ToString())));
}

/// <summary>A place as the scorer compares it: a normalized city (or null) and an ISO country (or null when unknown).</summary>
public sealed record PlaceInput(string? City, string? Country);

/// <summary>
/// Reads places out of text (spec 0019): <c>City, Country</c> job locations for the fallback, and
/// city and country normalization for comparisons. Uses the static <see cref="IsoCodes"/> table.
/// </summary>
public static class PlaceText
{
    private static readonly HashSet<string> UsStates = new(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY",
        "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND",
        "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC",
    };

    /// <summary>Lower case, trimmed, with whitespace runs collapsed: how cities are compared.</summary>
    public static string? NormalizeCity(string? city) =>
        string.IsNullOrWhiteSpace(city) ? null : QuoteVerifier.Normalize(city);

    /// <summary>
    /// Parses a job's <c>Location</c> column (AC-8 fallback): only <c>City, Country</c> (the last
    /// comma separated part a recognizable country name or ISO code) counts; anything else is null.
    /// A bare country (<c>Germany</c>) also counts, with no city.
    /// </summary>
    public static PlaceInput? ParseJobLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        // "San Francisco, CA" names a US state, not Canada: a bare 2 letter code that is also a
        // US state is ambiguous, so it is left unparsed rather than guessed.
        var last = parts[^1];
        if (last.Length == 2 && UsStates.Contains(last.ToUpperInvariant()))
        {
            return null;
        }

        var country = IsoCodes.FindCountry(last);
        if (country is null)
        {
            return null;
        }

        return new PlaceInput(parts.Length > 1 ? NormalizeCity(parts[0]) : null, country);
    }
}
