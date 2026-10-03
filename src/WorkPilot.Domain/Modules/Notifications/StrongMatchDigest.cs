namespace WorkPilot.Domain.Modules.Notifications;

/// <summary>One job as a strong match digest lists it.</summary>
public sealed record DigestJob(Guid JobId, string Title, string Company, int Score);

/// <summary>
/// The day's "N new strong matches" notification content (spec 0020, AC-3): every job id it has
/// counted, so a job is never counted twice, and the top 3 jobs by score to show.
/// </summary>
public sealed record StrongMatchDigest(int Count, IReadOnlyList<DigestJob> Top, IReadOnlyList<Guid> JobIds)
{
    /// <summary>How many jobs the digest shows.</summary>
    public const int TopSize = 3;

    /// <summary>The notification type of a digest.</summary>
    public const string Type = "jobs.strong-matches";

    /// <summary>An empty digest.</summary>
    public static StrongMatchDigest Empty { get; } = new(0, [], []);

    /// <summary>The digest key for the UTC day of <paramref name="now"/>: <c>jobs.strong-matches:yyyy-MM-dd</c>.</summary>
    public static string GroupKeyFor(DateTimeOffset now) =>
        $"{Type}:{now.UtcDateTime:yyyy'-'MM'-'dd}";

    /// <summary>The digest's link: the jobs list, best first, from <paramref name="threshold"/> up.</summary>
    public static string LinkFor(int threshold) => $"/jobs?sort=score&minScore={threshold}";

    /// <summary>"1 new strong match today", "5 new strong matches today".</summary>
    public string Title => Count == 1 ? "1 new strong match today" : $"{Count} new strong matches today";

    /// <summary>True when <paramref name="jobId"/> is already counted.</summary>
    public bool Contains(Guid jobId) => JobIds.Contains(jobId);

    /// <summary>
    /// The digest with <paramref name="job"/> counted and the top 3 by score (ties keep the earlier
    /// one) recomputed; the same digest when the job is already counted.
    /// </summary>
    public StrongMatchDigest Add(DigestJob job)
    {
        if (Contains(job.JobId))
        {
            return this;
        }

        var top = Top.Append(job)
            .Select((j, i) => (j, i))
            .OrderByDescending(x => x.j.Score)
            .ThenBy(x => x.i)
            .Take(TopSize)
            .Select(x => x.j)
            .ToList();
        return new StrongMatchDigest(Count + 1, top, [.. JobIds, job.JobId]);
    }
}
