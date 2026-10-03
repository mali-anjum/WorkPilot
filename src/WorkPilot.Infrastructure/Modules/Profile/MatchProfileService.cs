using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;
using ProfileEntity = WorkPilot.Domain.Modules.Profile.Profile;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <inheritdoc cref="IMatchProfileService" />
public sealed class MatchProfileService(WorkPilotDbContext db, IEventPublisher events, TimeProvider clock) : IMatchProfileService
{
    private const string StaleMessage = "Your profile changed elsewhere. Reload to see the latest version, then make your edits again.";

    /// <inheritdoc />
    public async Task<Result<VersionedMatchProfile>> GetAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(profileId, cancellationToken);
        return profile is null
            ? Result<VersionedMatchProfile>.NotFound("That profile does not exist.")
            : Result<VersionedMatchProfile>.Ok(new VersionedMatchProfile(ToDocument(profile), ETagOf(db.Entry(profile).Property<uint>("xmin").CurrentValue)));
    }

    /// <inheritdoc />
    public async Task<Result<VersionedMatchProfile>> SaveAsync(Guid profileId, string? ifMatch, MatchProfileDocument document, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return Result<VersionedMatchProfile>.PreconditionRequired("Send the profile's ETag in If-Match.");
        }

        if (ParseETag(ifMatch) is not { } version)
        {
            return Result<VersionedMatchProfile>.PreconditionFailed(StaleMessage);
        }

        var (draft, errors) = ToDraft(document);
        foreach (var (field, messages) in MatchProfileRules.Validate(draft))
        {
            errors[field] = errors.TryGetValue(field, out var existing) ? [.. existing, .. messages] : messages;
        }

        if (errors.Count > 0)
        {
            return Result<VersionedMatchProfile>.Invalid(errors);
        }

        try
        {
            var saved = await db.Database.CreateExecutionStrategy().ExecuteAsync(
                (object?)null,
                async (_, _, ct) =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var profile = await LoadAsync(profileId, ct);
                    if (profile is null)
                    {
                        return (bool?)null;
                    }

                    // The ETag is the row's xmin: a stale one fails here, and a save that commits
                    // between this read and the write fails the concurrency check instead.
                    var xmin = db.Entry(profile).Property<uint>("xmin");
                    if (xmin.CurrentValue != version)
                    {
                        return false;
                    }

                    xmin.OriginalValue = version;
                    await ApplyAsync(profile, draft, ct);
                    events.Publish(new MatchProfileChanged(profileId));
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return true;
                },
                verifySucceeded: null,
                cancellationToken);

            return saved switch
            {
                null => Result<VersionedMatchProfile>.NotFound("That profile does not exist."),
                false => Result<VersionedMatchProfile>.PreconditionFailed(StaleMessage),
                true => await GetAsync(profileId, cancellationToken),
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<VersionedMatchProfile>.PreconditionFailed(StaleMessage);
        }
    }

    // Replaces the whole match profile: preferences, the skill set, and experience and education
    // rows added, updated or removed by id.
    private async Task ApplyAsync(ProfileEntity profile, MatchProfileDraft draft, CancellationToken cancellationToken)
    {
        MatchProfileRules.Apply(profile, draft.Preferences, clock.GetUtcNow());

        var names = draft.Skills.Select(SkillName.Normalize).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 0)
        {
            // Shared skills are inserted once by their normalized name; a concurrent insert of the
            // same name is absorbed by the unique index.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO app.skills ("Id", "Name")
                SELECT gen_random_uuid(), name FROM unnest(@names) AS name
                ON CONFLICT ("Name") DO NOTHING
                """,
                [new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names }],
                cancellationToken);
        }

        var skills = await db.Skills.Where(s => names.Contains(s.Name)).ToListAsync(cancellationToken);
        profile.Skills.Clear();
        profile.Skills.AddRange(skills.OrderBy(s => s.Name, StringComparer.Ordinal));

        var experiences = profile.Experiences.ToDictionary(e => e.Id);
        var keptExperiences = new HashSet<Guid>();
        foreach (var row in draft.Experiences)
        {
            if (row.Id is { } id && experiences.TryGetValue(id, out var existing) && keptExperiences.Add(id))
            {
                existing.Company = row.Company.Trim();
                existing.Title = row.Title.Trim();
                existing.StartDate = row.StartDate;
                existing.EndDate = row.EndDate;
                existing.Description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim();
                continue;
            }

            // Added explicitly: a new child with a preset key found only through the tracked
            // parent's collection would be taken as existing and never inserted.
            db.Experiences.Add(new Experience
            {
                ProfileId = profile.Id,
                Company = row.Company.Trim(),
                Title = row.Title.Trim(),
                StartDate = row.StartDate,
                EndDate = row.EndDate,
                Description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim(),
            });
        }

        db.Experiences.RemoveRange(experiences.Values.Where(e => !keptExperiences.Contains(e.Id)));

        var educations = profile.Education.ToDictionary(e => e.Id);
        var keptEducations = new HashSet<Guid>();
        foreach (var row in draft.Educations)
        {
            if (row.Id is { } id && educations.TryGetValue(id, out var existing) && keptEducations.Add(id))
            {
                existing.Institution = row.Institution.Trim();
                existing.Degree = row.Degree.Trim();
                existing.Field = row.Field.Trim();
                existing.DegreeLevel = row.DegreeLevel;
                existing.StartDate = row.StartDate;
                existing.EndDate = row.EndDate;
                continue;
            }

            db.Education.Add(new Education
            {
                ProfileId = profile.Id,
                Institution = row.Institution.Trim(),
                Degree = row.Degree.Trim(),
                Field = row.Field.Trim(),
                DegreeLevel = row.DegreeLevel,
                StartDate = row.StartDate,
                EndDate = row.EndDate,
            });
        }

        db.Education.RemoveRange(educations.Values.Where(e => !keptEducations.Contains(e.Id)));
    }

    // Tracked even for a read: the ETag comes from the change tracker's xmin shadow property.
    private Task<ProfileEntity?> LoadAsync(Guid profileId, CancellationToken cancellationToken) =>
        db.Profiles
            .Include(p => p.Skills)
            .Include(p => p.Experiences)
            .Include(p => p.Education)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

    private static MatchProfileDocument ToDocument(ProfileEntity profile) => new(
        profile.TargetRoles,
        profile.RemotePreference.ToString(),
        (profile.PreferredLocations ?? []).Select(l => new PreferredLocationDto(l.City, l.Country)).ToList(),
        profile.JobTypes.Select(t => t.ToString()).ToList(),
        profile.MinSalary,
        profile.SalaryCurrency,
        profile.AuthorizedCountries,
        profile.NeedsSponsorshipElsewhere,
        profile.StrongMatchThreshold,
        profile.Skills.Select(s => s.Name).Order(StringComparer.Ordinal).ToList(),
        profile.Experiences
            .OrderByDescending(e => e.StartDate).ThenBy(e => e.Id)
            .Select(e => new ExperienceDto(e.Id, e.Company, e.Title, e.StartDate, e.EndDate, e.Description))
            .ToList(),
        profile.Education
            .OrderByDescending(e => e.StartDate).ThenBy(e => e.Id)
            .Select(e => new EducationDto(e.Id, e.Institution, e.Degree, e.Field, e.DegreeLevel.ToString(), e.StartDate, e.EndDate))
            .ToList());

    // Maps the document onto the Domain draft; enum names that don't parse become field errors.
    private static (MatchProfileDraft Draft, Dictionary<string, string[]> Errors) ToDraft(MatchProfileDocument document)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!Enum.TryParse<RemotePreference>(document.RemotePreference, ignoreCase: true, out var remote) || !Enum.IsDefined(remote))
        {
            errors["remotePreference"] = [$"Remote preference must be one of {string.Join(", ", Enum.GetNames<RemotePreference>())}."];
        }

        var jobTypes = new List<JobType>();
        var jobTypeNames = document.JobTypes ?? [];
        for (var i = 0; i < jobTypeNames.Count; i++)
        {
            if (Enum.TryParse<JobType>(jobTypeNames[i], ignoreCase: true, out var type) && Enum.IsDefined(type))
            {
                jobTypes.Add(type);
            }
            else
            {
                errors[$"jobTypes[{i}]"] = [$"Job type must be one of {string.Join(", ", Enum.GetNames<JobType>())}."];
            }
        }

        var educationRows = document.Educations ?? [];
        var educations = new List<EducationDraft>();
        for (var i = 0; i < educationRows.Count; i++)
        {
            var row = educationRows[i];
            if (!Enum.TryParse<DegreeLevel>(row.DegreeLevel, ignoreCase: true, out var level) || !Enum.IsDefined(level))
            {
                errors[$"educations[{i}].degreeLevel"] = [$"Degree level must be one of {string.Join(", ", Enum.GetNames<DegreeLevel>())}."];
            }

            educations.Add(new EducationDraft(row.Id, row.Institution ?? string.Empty, row.Degree ?? string.Empty, row.Field ?? string.Empty, level, row.StartDate, row.EndDate));
        }

        var preferences = new MatchPreferencesDraft(
            document.TargetRoles ?? [],
            remote,
            (document.PreferredLocations ?? []).Select(l => new PreferredLocation { City = l.City, Country = l.Country ?? string.Empty }).ToList(),
            jobTypes,
            document.MinSalary,
            document.SalaryCurrency,
            document.AuthorizedCountries ?? [],
            document.NeedsSponsorshipElsewhere,
            document.StrongMatchThreshold);
        var experiences = (document.Experiences ?? [])
            .Select(e => new ExperienceDraft(e.Id, e.Company ?? string.Empty, e.Title ?? string.Empty, e.StartDate, e.EndDate, e.Description))
            .ToList();
        return (new MatchProfileDraft(preferences, document.Skills ?? [], experiences, educations), errors);
    }

    /// <summary>The ETag of a row version: its xmin as a quoted decimal string.</summary>
    public static string ETagOf(uint xmin) => $"\"{xmin.ToString(CultureInfo.InvariantCulture)}\"";

    // Accepts "123", "\"123\"" and a weak W/"123"; anything else is stale.
    private static uint? ParseETag(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return uint.TryParse(text.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;
    }
}
