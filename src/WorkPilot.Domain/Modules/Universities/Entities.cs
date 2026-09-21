using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Universities;

public class University : SoftDeletableEntity
{
    public required string Name { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }
    public required Provenance Provenance { get; init; }

    public List<Program> Programs { get; init; } = [];
    public List<Professor> Professors { get; init; } = [];
    public List<Scholarship> Scholarships { get; init; } = [];
}

public class Program : Entity
{
    public required Guid UniversityId { get; init; }
    public required string Name { get; set; }
    public required string Degree { get; set; }
    public required string Field { get; set; }
    public required Provenance Provenance { get; init; }
}

public class Professor : SoftDeletableEntity
{
    public required Guid UniversityId { get; init; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? ProfileUrl { get; set; }
    public required Provenance Provenance { get; init; }

    public List<ResearchArea> ResearchAreas { get; init; } = [];
}

/// <summary>A named research area, shared across professors (M:N via the join below).</summary>
public class ResearchArea : Entity
{
    public required string Name { get; set; }
}

/// <summary>Join row for the Professor &lt;-&gt; ResearchArea many to many relationship.</summary>
public class ProfessorResearchArea
{
    public required Guid ProfessorId { get; init; }
    public required Guid ResearchAreaId { get; init; }
}

public class Scholarship : SoftDeletableEntity
{
    public Guid? UniversityId { get; init; }
    public required string Name { get; set; }
    public string? AmountRange { get; set; }
    public DateOnly? DeadlineDate { get; set; }
    public required Provenance Provenance { get; init; }
}
