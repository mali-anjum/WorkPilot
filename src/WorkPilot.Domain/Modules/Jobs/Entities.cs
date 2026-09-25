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

    /// <summary>Plain text description, normalized from the source's HTML (spec 0008).</summary>
    public string? Description { get; set; }

    /// <summary>When the source says the posting was first published, if it says.</summary>
    public DateTimeOffset? PostedAt { get; set; }

    public List<JobSnapshot> Snapshots { get; init; } = [];

    /// <summary>
    /// Creates a canonical job from a normalized posting, together with its
    /// first snapshot. <paramref name="retrievedAt"/> is both the retrieval and
    /// the verification time: the source returning the posting is what
    /// verifies it is live (spec 0008, AC-2).
    /// </summary>
    public static Job Create(Guid jobSourceId, NormalizedJob posting, DateTimeOffset retrievedAt, decimal confidence)
    {
        var job = new Job
        {
            JobSourceId = jobSourceId,
            ExternalId = posting.ExternalId,
            Title = posting.Title,
            Company = posting.Company,
            Location = posting.Location,
            RemoteType = posting.RemoteType,
            Description = posting.Description,
            PostedAt = posting.PostedAt,
            Provenance = new Provenance
            {
                SourceUrl = posting.SourceUrl,
                RetrievedAt = retrievedAt,
                VerifiedAt = retrievedAt,
                Confidence = confidence,
            },
        };
        job.Snapshots.Add(JobSnapshot.Capture(job.Id, jobSourceId, posting, retrievedAt, confidence));
        return job;
    }

    /// <summary>
    /// Applies a fresh retrieval of the same posting (spec 0008, AC-4). The
    /// job is always re-verified and keeps its first <c>RetrievedAt</c>. When
    /// the content hash differs from the latest snapshot's, the canonical
    /// fields are updated and the new snapshot is returned (and added to
    /// <see cref="Snapshots"/>); otherwise the latest snapshot is re-verified
    /// and <c>null</c> is returned.
    /// </summary>
    public JobSnapshot? Refresh(NormalizedJob posting, DateTimeOffset retrievedAt, decimal confidence)
    {
        if (posting.ExternalId != ExternalId)
        {
            throw new InvalidOperationException($"Posting '{posting.ExternalId}' is not job '{ExternalId}'.");
        }

        Provenance.VerifiedAt = retrievedAt;
        Provenance.Confidence = confidence;

        var latest = Snapshots.MaxBy(s => s.Provenance.RetrievedAt);
        if (latest is not null && latest.ContentHash == posting.ContentHash)
        {
            latest.Provenance.VerifiedAt = retrievedAt;
            return null;
        }

        Title = posting.Title;
        Company = posting.Company;
        Location = posting.Location;
        RemoteType = posting.RemoteType;
        Description = posting.Description;
        PostedAt = posting.PostedAt;

        var snapshot = JobSnapshot.Capture(Id, JobSourceId, posting, retrievedAt, confidence);
        Snapshots.Add(snapshot);
        return snapshot;
    }
}

/// <summary>
/// A point in time capture of a job posting's raw content, used to detect the
/// same posting reposted or re-scraped elsewhere (spec 0002; <see cref="ContentHash"/>
/// seeds scope feature 10, job deduplication).
/// </summary>
public class JobSnapshot : Entity
{
    public required Guid JobId { get; init; }

    /// <summary>The source this capture came from; one canonical job may later gather snapshots from several sources (feature 10).</summary>
    public required Guid JobSourceId { get; init; }

    /// <summary>The source's own stable id for the posting.</summary>
    public required string ExternalId { get; init; }

    public required string RawContent { get; init; }
    public required string ContentHash { get; init; }
    public required Provenance Provenance { get; init; }

    /// <summary>Captures a normalized posting's raw content and hash as retrieved at <paramref name="retrievedAt"/>.</summary>
    public static JobSnapshot Capture(Guid jobId, Guid jobSourceId, NormalizedJob posting, DateTimeOffset retrievedAt, decimal confidence) => new()
    {
        JobId = jobId,
        JobSourceId = jobSourceId,
        ExternalId = posting.ExternalId,
        RawContent = posting.RawContent,
        ContentHash = posting.ContentHash,
        Provenance = new Provenance
        {
            SourceUrl = posting.SourceUrl,
            RetrievedAt = retrievedAt,
            VerifiedAt = retrievedAt,
            Confidence = confidence,
        },
    };
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
