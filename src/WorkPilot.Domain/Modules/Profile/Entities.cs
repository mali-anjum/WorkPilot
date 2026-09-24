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
