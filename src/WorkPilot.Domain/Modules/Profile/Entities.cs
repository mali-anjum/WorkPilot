using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Profile;

/// <summary>The founder's profile. 1:1 with Supabase's own <c>auth.users</c> row.</summary>
public class Profile : SoftDeletableEntity
{
    public required Guid AuthUserId { get; init; }
    public required string Name { get; set; }
    public string? Headline { get; set; }
    public List<string> TargetRoles { get; set; } = [];
    public string? Location { get; set; }

    /// <summary>Which work arrangements you accept (spec 0019).</summary>
    public RemotePreference RemotePreference { get; set; } = RemotePreference.Any;

    /// <summary>Where you want to work; empty means "not set".</summary>
    public List<PreferredLocation> PreferredLocations { get; set; } = [];

    /// <summary>The job types you accept; empty means "not set".</summary>
    public List<JobType> JobTypes { get; set; } = [];

    /// <summary>Your minimum yearly salary, in <see cref="SalaryCurrency"/>.</summary>
    public decimal? MinSalary { get; set; }

    /// <summary>ISO 4217 code; set if and only if <see cref="MinSalary"/> is set.</summary>
    public string? SalaryCurrency { get; set; }

    /// <summary>ISO 3166 alpha 2 codes of the countries you may work in without sponsorship.</summary>
    public List<string> AuthorizedCountries { get; set; } = [];

    /// <summary>True when you need visa sponsorship outside <see cref="AuthorizedCountries"/>.</summary>
    public bool NeedsSponsorshipElsewhere { get; set; } = true;

    /// <summary>The score (0 to 100) at or above which a job is a strong match (spec 0019, AC-17; spec 0020).</summary>
    public int StrongMatchThreshold { get; set; } = MatchProfileRules.DefaultStrongMatchThreshold;

    /// <summary>Set on every match profile save, so the row (and its ETag) changes even when only a child row did.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Skill> Skills { get; init; } = [];
    public List<Experience> Experiences { get; init; } = [];
    public List<Education> Education { get; init; } = [];
}

/// <summary>A named skill, shared across profiles (M:N via the join below).</summary>
public class Skill : Entity
{
    public required string Name { get; set; }
    public string? Category { get; set; }
}

public class Experience : Entity
{
    public required Guid ProfileId { get; init; }
    public required string Company { get; set; }
    public required string Title { get; set; }
    public required DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? Description { get; set; }
}

public class Education : Entity
{
    public required Guid ProfileId { get; init; }
    public required string Institution { get; set; }
    public required string Degree { get; set; }
    public required string Field { get; set; }
    public required DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>The degree's level, compared against a posting's required degree (spec 0019).</summary>
    public DegreeLevel DegreeLevel { get; set; } = DegreeLevel.None;
}

public class CoverLetter : SoftDeletableEntity
{
    public required Guid ProfileId { get; init; }
    public required string Name { get; set; }

    public List<CoverLetterVersion> Versions { get; init; } = [];
}

/// <summary>Immutable snapshot: never updated after creation (spec 0002, AC-5).</summary>
public class CoverLetterVersion : Entity
{
    public required Guid CoverLetterId { get; init; }
    public required string StorageUrl { get; init; }
    public required int VersionNumber { get; init; }
    public Guid? GeneratedForApplicationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Join row for the Profile &lt;-&gt; Skill many to many relationship.</summary>
public class ProfileSkill
{
    public required Guid ProfileId { get; init; }
    public required Guid SkillId { get; init; }
}
