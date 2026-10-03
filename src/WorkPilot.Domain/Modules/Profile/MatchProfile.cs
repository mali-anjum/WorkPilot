using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Profile;

/// <summary>Which work arrangements you accept (spec 0019).</summary>
public enum RemotePreference
{
    Any,
    Remote,
    Hybrid,
    Onsite,
}

/// <summary>The kinds of employment a posting offers and a profile accepts (spec 0019).</summary>
public enum JobType
{
    FullTime,
    PartTime,
    Contract,
    Internship,
    Temporary,
}

/// <summary>A degree's level, ordered so a higher value is a higher degree (spec 0019).</summary>
public enum DegreeLevel
{
    None = 0,
    Associate = 1,
    Bachelor = 2,
    Master = 3,
    Doctorate = 4,
}

/// <summary>One place you want to work: a country (ISO 3166 alpha 2), optionally narrowed to a city.</summary>
public sealed class PreferredLocation
{
    public string? City { get; set; }
    public required string Country { get; set; }
}

/// <summary>The match profile preferences as one value, before they are applied (spec 0019, AC-11).</summary>
public sealed record MatchPreferencesDraft(
    IReadOnlyList<string> TargetRoles,
    RemotePreference RemotePreference,
    IReadOnlyList<PreferredLocation> PreferredLocations,
    IReadOnlyList<JobType> JobTypes,
    decimal? MinSalary,
    string? SalaryCurrency,
    IReadOnlyList<string> AuthorizedCountries,
    bool NeedsSponsorshipElsewhere,
    int StrongMatchThreshold);

/// <summary>One experience row of a match profile save; <see cref="Id"/> is null for a new row.</summary>
public sealed record ExperienceDraft(Guid? Id, string Company, string Title, DateOnly StartDate, DateOnly? EndDate, string? Description);

/// <summary>One education row of a match profile save; <see cref="Id"/> is null for a new row.</summary>
public sealed record EducationDraft(Guid? Id, string Institution, string Degree, string Field, DegreeLevel DegreeLevel, DateOnly StartDate, DateOnly? EndDate);

/// <summary>A whole match profile save (spec 0019, AC-11).</summary>
public sealed record MatchProfileDraft(
    MatchPreferencesDraft Preferences,
    IReadOnlyList<string> Skills,
    IReadOnlyList<ExperienceDraft> Experiences,
    IReadOnlyList<EducationDraft> Educations);

/// <summary>
/// The match profile's rules (spec 0019, AC-11): field validation with one message per broken rule,
/// and applying a valid draft to the profile's own preference columns.
/// </summary>
public static class MatchProfileRules
{
    /// <summary>The strong match threshold a new profile starts with.</summary>
    public const int DefaultStrongMatchThreshold = 70;

    /// <summary>Most target roles a profile may list.</summary>
    public const int MaxTargetRoles = 20;

    /// <summary>Longest target role.</summary>
    public const int MaxTargetRoleLength = 100;

    /// <summary>Longest skill name (the shared <c>skills.Name</c> column).</summary>
    public const int MaxSkillLength = 100;

    /// <summary>Most skills a profile may list.</summary>
    public const int MaxSkills = 200;

    /// <summary>Most preferred locations a profile may list.</summary>
    public const int MaxPreferredLocations = 50;

    /// <summary>Longest company, title, institution, degree or field text.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Longest preferred city.</summary>
    public const int MaxCityLength = 100;

    /// <summary>
    /// Every broken rule, keyed by the field it belongs to (<c>salaryCurrency</c>,
    /// <c>experiences[2].endDate</c>); empty when the draft is valid. Codes are compared upper case.
    /// </summary>
    public static Dictionary<string, string[]> Validate(MatchProfileDraft draft)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        var p = draft.Preferences;
        if (p.TargetRoles.Count > MaxTargetRoles)
        {
            Add("targetRoles", $"List at most {MaxTargetRoles} target roles.");
        }

        for (var i = 0; i < p.TargetRoles.Count; i++)
        {
            var role = p.TargetRoles[i]?.Trim() ?? string.Empty;
            if (role.Length is 0 or > MaxTargetRoleLength)
            {
                Add($"targetRoles[{i}]", $"A target role must be 1 to {MaxTargetRoleLength} characters.");
            }
        }

        if (p.PreferredLocations.Count > MaxPreferredLocations)
        {
            Add("preferredLocations", $"List at most {MaxPreferredLocations} preferred locations.");
        }

        for (var i = 0; i < p.PreferredLocations.Count; i++)
        {
            var location = p.PreferredLocations[i];
            if (!IsoCodes.IsCountry(location.Country?.Trim().ToUpperInvariant()))
            {
                Add($"preferredLocations[{i}].country", "Country must be a 2 letter ISO 3166 code, like DE or US.");
            }

            if (location.City is { } city && city.Trim().Length > MaxCityLength)
            {
                Add($"preferredLocations[{i}].city", $"City must be at most {MaxCityLength} characters.");
            }
        }

        if (p.MinSalary is < 0)
        {
            Add("minSalary", "Minimum salary cannot be negative.");
        }

        var currency = string.IsNullOrWhiteSpace(p.SalaryCurrency) ? null : p.SalaryCurrency.Trim().ToUpperInvariant();
        if (currency is not null && !IsoCodes.IsCurrency(currency))
        {
            Add("salaryCurrency", "Currency must be a 3 letter ISO 4217 code, like EUR or USD.");
        }

        if (p.MinSalary is not null && currency is null)
        {
            Add("salaryCurrency", "Pick a currency for your minimum salary.");
        }

        if (p.MinSalary is null && currency is not null)
        {
            Add("minSalary", "Enter a minimum salary for this currency, or clear the currency.");
        }

        for (var i = 0; i < p.AuthorizedCountries.Count; i++)
        {
            if (!IsoCodes.IsCountry(p.AuthorizedCountries[i]?.Trim().ToUpperInvariant()))
            {
                Add($"authorizedCountries[{i}]", "Country must be a 2 letter ISO 3166 code, like DE or US.");
            }
        }

        if (p.StrongMatchThreshold is < 0 or > 100)
        {
            Add("strongMatchThreshold", "The strong match threshold must be 0 to 100.");
        }

        if (draft.Skills.Count > MaxSkills)
        {
            Add("skills", $"List at most {MaxSkills} skills.");
        }

        for (var i = 0; i < draft.Skills.Count; i++)
        {
            var skill = draft.Skills[i]?.Trim() ?? string.Empty;
            if (skill.Length is 0 or > MaxSkillLength)
            {
                Add($"skills[{i}]", $"A skill must be 1 to {MaxSkillLength} characters.");
            }
        }

        for (var i = 0; i < draft.Experiences.Count; i++)
        {
            var row = draft.Experiences[i];
            RequireText(row.Company, $"experiences[{i}].company", "Company", Add);
            RequireText(row.Title, $"experiences[{i}].title", "Title", Add);
            if (row.EndDate is { } end && end < row.StartDate)
            {
                Add($"experiences[{i}].endDate", "End date cannot be before the start date.");
            }
        }

        for (var i = 0; i < draft.Educations.Count; i++)
        {
            var row = draft.Educations[i];
            RequireText(row.Institution, $"educations[{i}].institution", "Institution", Add);
            RequireText(row.Degree, $"educations[{i}].degree", "Degree", Add);
            RequireText(row.Field, $"educations[{i}].field", "Field", Add);
            if (row.EndDate is { } end && end < row.StartDate)
            {
                Add($"educations[{i}].endDate", "End date cannot be before the start date.");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes a draft's preferences onto <paramref name="profile"/> (trimmed, codes upper cased,
    /// duplicates dropped) and stamps <see cref="Profile.UpdatedAt"/>.
    /// Throws when the draft breaks a rule; call <see cref="Validate"/> first.
    /// </summary>
    public static void Apply(Profile profile, MatchPreferencesDraft preferences, DateTimeOffset now)
    {
        if (Validate(new MatchProfileDraft(preferences, [], [], [])).Count > 0)
        {
            throw new InvalidOperationException("The match preferences break a rule; validate them first.");
        }

        var currency = string.IsNullOrWhiteSpace(preferences.SalaryCurrency) ? null : preferences.SalaryCurrency.Trim().ToUpperInvariant();
        profile.TargetRoles = preferences.TargetRoles.Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.RemotePreference = preferences.RemotePreference;
        profile.PreferredLocations = preferences.PreferredLocations
            .Select(l => new PreferredLocation
            {
                City = string.IsNullOrWhiteSpace(l.City) ? null : l.City.Trim(),
                Country = l.Country.Trim().ToUpperInvariant(),
            })
            .DistinctBy(l => (l.City?.ToLowerInvariant(), l.Country))
            .ToList();
        profile.JobTypes = preferences.JobTypes.Distinct().Order().ToList();
        profile.MinSalary = preferences.MinSalary;
        profile.SalaryCurrency = currency;
        profile.AuthorizedCountries = preferences.AuthorizedCountries.Select(c => c.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();
        profile.NeedsSponsorshipElsewhere = preferences.NeedsSponsorshipElsewhere;
        profile.StrongMatchThreshold = preferences.StrongMatchThreshold;
        profile.UpdatedAt = now;
    }

    private static void RequireText(string? value, string field, string label, Action<string, string> add)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxTextLength)
        {
            add(field, $"{label} must be 1 to {MaxTextLength} characters.");
        }
    }
}

/// <summary>
/// The match profile was saved, or a profile was first provisioned (spec 0019): every job's match
/// for it may need rescoring. Raised by Profile, handled by Jobs.
/// </summary>
public sealed record MatchProfileChanged(Guid ProfileId) : IDomainEvent
{
    public static string EventName => "profile.match-profile-changed.v1";
}
