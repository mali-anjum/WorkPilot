using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>A place jobs are discovered from (a board, a company site).</summary>
public class JobSource : Entity
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Config { get; set; }

    /// <summary>
    /// The company every posting from this source belongs to, given at the
    /// ingestion trigger. When set it is both the displayed company and the
    /// company in the match key, for every source type (spec 0017, AC-7).
    /// </summary>
    public string? CompanyName { get; set; }
}

/// <summary>
/// One canonical role (spec 0017). It collects one <see cref="JobSourceLink"/>
/// per place the role was seen; its displayed fields come from the primary
/// link, and its <see cref="DedupKey"/> is how new sightings find it.
/// </summary>
public class Job : SoftDeletableEntity
{
    public string Title { get; private set; } = string.Empty;
    public string Company { get; private set; } = string.Empty;
    public string? Location { get; private set; }
    public string? RemoteType { get; private set; }
    public decimal? SalaryRangeMin { get; set; }
    public decimal? SalaryRangeMax { get; set; }

    /// <summary>Plain text description, normalized from the source's HTML (spec 0008).</summary>
    public string? Description { get; private set; }

    /// <summary>When the source says the posting was first published, if it says.</summary>
    public DateTimeOffset? PostedAt { get; private set; }

    /// <summary>
    /// Mirrors the links: the primary link's URL and confidence, the earliest
    /// first seen and the latest last seen time. Only this class replaces it.
    /// </summary>
    public Provenance Provenance { get; private set; } = null!;

    /// <summary>The <see cref="JobDedupKey"/> of the current company, title and location.</summary>
    public string? DedupKey { get; private set; }

    /// <summary>The rule version <see cref="DedupKey"/> was checked against; lower than current means stale.</summary>
    public int DedupRuleVersion { get; private set; }

    /// <summary>The link whose posting fills the displayed fields.</summary>
    public Guid? PrimaryLinkId { get; private set; }

    public List<JobSourceLink> Links { get; init; } = [];
    public List<JobSnapshot> Snapshots { get; init; } = [];

    /// <summary>True when the key needs the reconcile job: a rule change, or a field change since the last match (spec 0017).</summary>
    public bool IsStale => DedupRuleVersion < JobDedupKey.CurrentRuleVersion;

    /// <summary>True when one of the links was split off another job; such a job is never merged automatically.</summary>
    public bool HasSplitLink => Links.Any(l => l.IsSplit);

    /// <summary>The link whose posting fills the displayed fields.</summary>
    public JobSourceLink? PrimaryLink => Links.FirstOrDefault(l => l.Id == PrimaryLinkId);

    /// <summary>
    /// Creates a job for a posting seen for the first time and matching no
    /// existing job, with its first link and snapshot (spec 0017, AC-1).
    /// </summary>
    public static Job Create(Guid jobSourceId, NormalizedJob posting, DateTimeOffset seenAt, decimal confidence)
    {
        var job = new Job();
        var link = JobSourceLink.FirstSeen(job.Id, jobSourceId, posting, seenAt, confidence);
        job.Links.Add(link);
        job.Snapshots.Add(JobSnapshot.Capture(job.Id, jobSourceId, posting, seenAt, confidence));
        job.PrimaryLinkId = link.Id;
        job.ApplyFields(posting);
        job.MarkCurrent();
        job.SyncProvenance();
        return job;
    }

    /// <summary>
    /// Attaches a posting seen for the first time whose match key equals this
    /// job's (AC-2), reviving the job when it was soft deleted (AC-11). The
    /// new link becomes primary when it ranks first (AC-4). Returns the new
    /// link and snapshot, which the caller must stage.
    /// </summary>
    public LinkSighting AttachLink(Guid jobSourceId, NormalizedJob posting, DateTimeOffset seenAt, decimal confidence)
    {
        if (Links.Any(l => l.JobSourceId == jobSourceId && l.ExternalId == posting.ExternalId))
        {
            throw new InvalidOperationException($"Job {Id} already has a link for posting '{posting.ExternalId}'.");
        }

        Restore();
        var link = JobSourceLink.FirstSeen(Id, jobSourceId, posting, seenAt, confidence);
        Links.Add(link);
        var snapshot = JobSnapshot.Capture(Id, jobSourceId, posting, seenAt, confidence);
        Snapshots.Add(snapshot);

        UpdatePrimary(link, posting, NoOtherPosting);
        return new LinkSighting(link, snapshot);
    }

    /// <summary>
    /// Applies a fresh retrieval of a known link (spec 0008, AC-4; spec 0017,
    /// AC-5, AC-11): revives the job, re-verifies the link, and adds a
    /// snapshot only when that link's own content hash changed (returned so
    /// the caller can stage it). When the link ranks first its posting fills
    /// the job; a changed key marks the job stale, never merges it here.
    /// <paramref name="postingOf"/> gives another link's posting, in case
    /// that one becomes primary instead.
    /// </summary>
    public JobSnapshot? SeeAgain(JobSourceLink link, NormalizedJob posting, DateTimeOffset seenAt, decimal confidence, Func<JobSourceLink, NormalizedJob> postingOf)
    {
        if (!Links.Contains(link) || link.ExternalId != posting.ExternalId)
        {
            throw new InvalidOperationException($"Posting '{posting.ExternalId}' is not a link of job {Id}.");
        }

        Restore();
        link.SeenAgain(posting.SourceUrl, seenAt, confidence);

        JobSnapshot? snapshot = null;
        var latest = LatestSnapshotOf(link);
        if (latest is not null && latest.ContentHash == posting.ContentHash)
        {
            latest.Provenance.VerifiedAt = seenAt;
        }
        else
        {
            snapshot = JobSnapshot.Capture(Id, link.JobSourceId, posting, seenAt, confidence);
            Snapshots.Add(snapshot);
        }

        UpdatePrimary(link, posting, postingOf);
        return snapshot;
    }

    /// <summary>
    /// Fills the displayed fields from the primary link's posting again (a
    /// company rename, spec 0017, AC-7). A changed key marks the job stale.
    /// </summary>
    public void ApplyPrimaryPosting(NormalizedJob posting)
    {
        ApplyFields(posting);
        SyncProvenance();
    }

    /// <summary>
    /// Merges <paramref name="other"/> (the newer job) into this one: its
    /// links and snapshots move here, and the primary link is chosen again
    /// across both. The caller re-points what else references
    /// <paramref name="other"/> and hard deletes it (spec 0017).
    /// </summary>
    public void MergeFrom(Job other, Func<JobSourceLink, NormalizedJob> postingOf)
    {
        if (ReferenceEquals(other, this) || other.Id == Id)
        {
            throw new InvalidOperationException("A job cannot be merged into itself.");
        }

        var otherPrimaryId = other.PrimaryLinkId;
        var otherFields = other.CurrentFields();

        foreach (var link in other.Links)
        {
            link.JobId = Id;
            Links.Add(link);
        }

        foreach (var snapshot in other.Snapshots)
        {
            snapshot.JobId = Id;
            Snapshots.Add(snapshot);
        }

        other.Links.Clear();
        other.Snapshots.Clear();

        var best = RankLinks().First();
        if (best.Id != PrimaryLinkId)
        {
            PrimaryLinkId = best.Id;
            ApplyFields(best.Id == otherPrimaryId ? otherFields : postingOf(best));
        }

        SyncProvenance();
    }

    /// <summary>
    /// Moves one link and its snapshots into a new job of its own, built from
    /// that link's posting, and marks the link split so it is never merged
    /// back automatically (spec 0017, AC-8). Both jobs choose their primary
    /// link again. Returns the new job, which the caller must stage.
    /// </summary>
    public Job SplitLink(Guid linkId, DateTimeOffset splitAt, Func<JobSourceLink, NormalizedJob> postingOf)
    {
        var link = Links.SingleOrDefault(l => l.Id == linkId)
            ?? throw new InvalidOperationException($"Link {linkId} is not on job {Id}.");
        if (Links.Count == 1)
        {
            throw new InvalidOperationException($"Link {linkId} is job {Id}'s only link.");
        }

        var created = new Job();
        link.MarkSplit(splitAt);
        link.JobId = created.Id;
        Links.Remove(link);
        created.Links.Add(link);

        foreach (var snapshot in SnapshotsOf(link).ToList())
        {
            snapshot.JobId = created.Id;
            Snapshots.Remove(snapshot);
            created.Snapshots.Add(snapshot);
        }

        created.PrimaryLinkId = link.Id;
        created.ApplyFields(postingOf(link));
        created.MarkCurrent();
        created.SyncProvenance();

        if (PrimaryLinkId == link.Id)
        {
            var best = RankLinks().First();
            PrimaryLinkId = best.Id;
            ApplyFields(postingOf(best));
        }

        SyncProvenance();
        return created;
    }

    /// <summary>Records that the key was checked against the current rule: it is recomputed and the job is no longer stale.</summary>
    public void MarkCurrent()
    {
        DedupKey = JobDedupKey.For(Company, Title, Location);
        DedupRuleVersion = JobDedupKey.CurrentRuleVersion;
    }

    /// <summary>The newest snapshot of <paramref name="link"/>, or <c>null</c>.</summary>
    public JobSnapshot? LatestSnapshotOf(JobSourceLink link) =>
        SnapshotsOf(link).MaxBy(s => s.Provenance.RetrievedAt);

    /// <summary>The links in primary order: highest confidence, then most recently seen, then oldest id (AC-4).</summary>
    public IEnumerable<JobSourceLink> RankLinks() =>
        Links.OrderByDescending(l => l.Confidence).ThenByDescending(l => l.LastSeenAt).ThenBy(l => l.Id);

    private IEnumerable<JobSnapshot> SnapshotsOf(JobSourceLink link) =>
        Snapshots.Where(s => s.JobSourceId == link.JobSourceId && s.ExternalId == link.ExternalId);

    // After a link was added or seen, only it or the old primary can rank first,
    // unless the old primary itself dropped, which is when postingOf is needed.
    private void UpdatePrimary(JobSourceLink touched, NormalizedJob touchedPosting, Func<JobSourceLink, NormalizedJob> postingOf)
    {
        var best = RankLinks().First();
        if (best.Id == touched.Id)
        {
            PrimaryLinkId = touched.Id;
            ApplyFields(touchedPosting);
        }
        else if (best.Id != PrimaryLinkId)
        {
            PrimaryLinkId = best.Id;
            ApplyFields(postingOf(best));
        }

        SyncProvenance();
    }

    private void ApplyFields(NormalizedJob posting)
    {
        Title = posting.Title;
        Company = posting.Company;
        Location = posting.Location;
        RemoteType = posting.RemoteType;
        Description = posting.Description;
        PostedAt = posting.PostedAt;

        // A key change outside a first sighting never merges on the spot: the
        // job goes stale and the reconcile run after this one decides.
        var key = JobDedupKey.For(Company, Title, Location);
        if (key != DedupKey)
        {
            DedupKey = key;
            DedupRuleVersion = 0;
        }
    }

    private NormalizedJob CurrentFields() => new(
        string.Empty, Provenance.SourceUrl, Title, Company, Location, RemoteType, Description, PostedAt, string.Empty, string.Empty);

    private void SyncProvenance()
    {
        var primary = PrimaryLink ?? throw new InvalidOperationException($"Job {Id} has no primary link.");
        var retrievedAt = Links.Min(l => l.FirstSeenAt);
        var verifiedAt = Links.Max(l => l.LastSeenAt);

        // SourceUrl and RetrievedAt are init only, so a change replaces the whole value.
        if (Provenance is null || Provenance.SourceUrl != primary.SourceUrl || Provenance.RetrievedAt != retrievedAt)
        {
            Provenance = new Provenance
            {
                SourceUrl = primary.SourceUrl,
                RetrievedAt = retrievedAt,
                VerifiedAt = verifiedAt,
                Confidence = primary.Confidence,
            };
            return;
        }

        Provenance.VerifiedAt = verifiedAt;
        Provenance.Confidence = primary.Confidence;
    }

    private static NormalizedJob NoOtherPosting(JobSourceLink link) =>
        throw new InvalidOperationException($"Link {link.Id} unexpectedly became primary.");
}

/// <summary>A link and snapshot created by <see cref="Job.AttachLink"/>.</summary>
public sealed record LinkSighting(JobSourceLink Link, JobSnapshot Snapshot);

/// <summary>
/// One place a <see cref="Job"/> was seen: a source and that source's own id
/// for the posting (spec 0017). Unique per <c>(JobSourceId, ExternalId)</c>;
/// once matched it never changes jobs, except by a split.
/// </summary>
public class JobSourceLink : Entity
{
    public Guid JobId { get; internal set; }
    public Guid JobSourceId { get; private init; }

    /// <summary>The source's own stable id for the posting.</summary>
    public string ExternalId { get; private init; } = string.Empty;

    public string SourceUrl { get; private set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; private init; }
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>How much the source is trusted (0 to 1); the highest ranks first for the primary link.</summary>
    public decimal Confidence { get; private set; }

    /// <summary>When this link was split off another job; a split link is never merged back automatically.</summary>
    public DateTimeOffset? SplitAt { get; private set; }

    public bool IsSplit => SplitAt is not null;

    /// <summary>A posting seen for the first time.</summary>
    public static JobSourceLink FirstSeen(Guid jobId, Guid jobSourceId, NormalizedJob posting, DateTimeOffset seenAt, decimal confidence) => new()
    {
        JobId = jobId,
        JobSourceId = jobSourceId,
        ExternalId = posting.ExternalId,
        SourceUrl = posting.SourceUrl,
        FirstSeenAt = seenAt,
        LastSeenAt = seenAt,
        Confidence = confidence,
    };

    /// <summary>The same posting seen again: it is re-verified, and keeps its job.</summary>
    public void SeenAgain(string sourceUrl, DateTimeOffset seenAt, decimal confidence)
    {
        SourceUrl = sourceUrl;
        LastSeenAt = seenAt;
        Confidence = confidence;
    }

    internal void MarkSplit(DateTimeOffset splitAt) => SplitAt = splitAt;
}

/// <summary>
/// A point in time capture of one link's raw posting (spec 0002, 0017). It
/// belongs to its link through <c>(JobSourceId, ExternalId)</c>, so a second
/// source's posting never counts as a change to the first one's history.
/// </summary>
public class JobSnapshot : Entity
{
    public Guid JobId { get; internal set; }

    /// <summary>The source this capture came from.</summary>
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
