using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>A place jobs are discovered from (a board, a company site).</summary>
public class JobSource : Entity
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Config { get; set; }
}

/// <summary>A job posting discovered from a <see cref="JobSource"/>.</summary>
public class Job : SoftDeletableEntity
{
    public required Guid JobSourceId { get; init; }
    public required string Title { get; set; }
    public required string Company { get; set; }
    public string? Location { get; set; }
    public string? RemoteType { get; set; }
    public decimal? SalaryRangeMin { get; set; }
    public decimal? SalaryRangeMax { get; set; }
    public required string ExternalId { get; init; }
    public required Provenance Provenance { get; init; }

    public List<JobSnapshot> Snapshots { get; init; } = [];
}

/// <summary>
/// A point in time capture of a job posting's raw content, used to detect the
/// same posting reposted or re-scraped elsewhere (spec 0002; <see cref="ContentHash"/>
/// seeds scope feature 10, job deduplication).
/// </summary>
public class JobSnapshot : Entity
{
    public required Guid JobId { get; init; }
    public required string RawContent { get; init; }
    public required string ContentHash { get; init; }
    public required Provenance Provenance { get; init; }
}

/// <summary>How well a job fits a profile, computed once per profile per job.</summary>
public class JobMatch : Entity
{
    public required Guid JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required decimal Score { get; set; }
    public string? MatchedSkills { get; set; }
    public DateTimeOffset RankedAt { get; init; } = DateTimeOffset.UtcNow;
}
