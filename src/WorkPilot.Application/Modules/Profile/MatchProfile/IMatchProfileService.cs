using WorkPilot.Application.Common;

namespace WorkPilot.Application.Modules.Profile.MatchProfile;

/// <summary>A preferred place in the match profile document.</summary>
public sealed record PreferredLocationDto(string? City, string Country);

/// <summary>An experience row; <see cref="Id"/> is null for a row to add.</summary>
public sealed record ExperienceDto(Guid? Id, string Company, string Title, DateOnly StartDate, DateOnly? EndDate, string? Description);

/// <summary>An education row; <see cref="Id"/> is null for a row to add.</summary>
public sealed record EducationDto(Guid? Id, string Institution, string Degree, string Field, string DegreeLevel, DateOnly StartDate, DateOnly? EndDate);

/// <summary>
/// The whole match profile (spec 0019, AC-11): preferences, skills, experience and education.
/// The same shape is read and written; enums travel as their names.
/// </summary>
public sealed record MatchProfileDocument(
    IReadOnlyList<string> TargetRoles,
    string RemotePreference,
    IReadOnlyList<PreferredLocationDto> PreferredLocations,
    IReadOnlyList<string> JobTypes,
    decimal? MinSalary,
    string? SalaryCurrency,
    IReadOnlyList<string> AuthorizedCountries,
    bool NeedsSponsorshipElsewhere,
    int StrongMatchThreshold,
    IReadOnlyList<string> Skills,
    IReadOnlyList<ExperienceDto> Experiences,
    IReadOnlyList<EducationDto> Educations);

/// <summary>A match profile with its version, sent back as the <c>ETag</c>.</summary>
public sealed record VersionedMatchProfile(MatchProfileDocument Document, string ETag);

/// <summary>
/// Reads and saves the match profile (spec 0019, AC-11). Profile owns and alone writes these
/// tables; a save raises <c>MatchProfileChanged</c> so Jobs rescores.
/// </summary>
public interface IMatchProfileService
{
    /// <summary>The profile's match profile and its ETag; 404 for an unknown or soft deleted profile.</summary>
    Task<Result<VersionedMatchProfile>> GetAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole match profile in one transaction. 428 without <paramref name="ifMatch"/>,
    /// 412 when it is stale, 400 with field errors, 404 for an unknown profile.
    /// </summary>
    Task<Result<VersionedMatchProfile>> SaveAsync(Guid profileId, string? ifMatch, MatchProfileDocument document, CancellationToken cancellationToken);
}
