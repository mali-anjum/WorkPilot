namespace WorkPilot.Domain.Common;

/// <summary>
/// Where an externally sourced record came from. Applied to every entity the
/// agent discovers rather than the user creates (spec 0002), so provenance is
/// consistent by construction instead of by per-entity convention.
/// </summary>
public sealed class Provenance
{
    public required string SourceUrl { get; init; }
    public required DateTimeOffset RetrievedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public decimal? Confidence { get; set; }
}

/// <summary>
/// Marks an entity as soft deleted rather than physically removed, so history
/// referencing it (audit logs, application events) stays intact. A global
/// query filter in <c>WorkPilotDbContext</c> excludes deleted rows by default.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }

    void SoftDelete(DateTimeOffset occurredAtUtc);
}
