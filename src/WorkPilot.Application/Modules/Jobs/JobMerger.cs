using System.Text.Json;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Application.Modules.Applications;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>
/// Merges every job sharing one match key into the oldest (spec 0017), for a
/// company rename and the reconcile run. The caller holds the key's advisory
/// lock and the transaction; the rules themselves live in <see cref="Job.MergeFrom"/>.
/// </summary>
public sealed class JobMerger(
    IJobRepository repository,
    IJobApplicationReassigner applications,
    IAuditService audit,
    IEnumerable<IJobSource> adapters)
{
    /// <summary>Audit action for a job merged into another, or a new link joining a job.</summary>
    public const string MergedAction = "JobsMerged";

    /// <summary>Audit action for a link split off into a job of its own.</summary>
    public const string SplitAction = "JobLinkSplit";

    /// <summary>Audit target type for merges and splits.</summary>
    public const string AuditTargetType = "Job";

    /// <summary>Why a merge happened, written in its audit row.</summary>
    public static class Reasons
    {
        public const string Ingest = "ingest";
        public const string Rename = "rename";
        public const string Reconcile = "reconcile";
        public const string Split = "split";
    }

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Merges the jobs whose key is <paramref name="dedupKey"/> (the stored
    /// ones plus <paramref name="known"/>, whose keys may not be saved yet)
    /// into the oldest one (lowest id), skipping soft deleted jobs and jobs
    /// holding a split link, then marks every one of them current. Returns
    /// how many jobs were merged away.
    /// </summary>
    public async Task<int> MergeGroupAsync(string dedupKey, IEnumerable<Job> known, string reason, CancellationToken cancellationToken)
    {
        var stored = await repository.GetJobsByKeyAsync(dedupKey, cancellationToken);
        var group = stored.Concat(known)
            .DistinctBy(j => j.Id)
            .Where(j => j.DedupKey == dedupKey)
            .OrderBy(j => j.Id)
            .ToList();

        var mergeable = group.Where(j => !j.IsDeleted && !j.HasSplitLink).ToList();
        var merged = 0;
        if (mergeable.Count > 1)
        {
            var target = mergeable[0];
            var postings = await PostingResolver.CreateAsync(repository, adapters, mergeable, cancellationToken);
            foreach (var other in mergeable.Skip(1))
            {
                var linkIds = other.Links.Select(l => l.Id).ToList();
                await applications.ReassignJobAsync(other.Id, target.Id, cancellationToken);
                await repository.ReassignMatchesAsync(other.Id, target.Id, cancellationToken);
                target.MergeFrom(other, postings.Of);
                repository.RemoveJob(other);
                group.Remove(other);
                RecordMerge(target.Id, other.Id, linkIds, reason);
                merged++;
            }
        }

        foreach (var job in group)
        {
            job.MarkCurrent();
        }

        return merged;
    }

    /// <summary>Writes a <c>JobsMerged</c> audit row (spec 0017, AC-12).</summary>
    public void RecordMerge(Guid jobId, Guid? mergedJobId, IReadOnlyList<Guid> linkIds, string reason) =>
        audit.Record("Agent", MergedAction, AuditTargetType, jobId, JsonSerializer.Serialize(
            new { jobId, mergedJobId, linkIds, reason }, AuditJson));

    /// <summary>Writes a <c>JobLinkSplit</c> audit row (spec 0017, AC-12).</summary>
    public void RecordSplit(Guid jobId, Guid newJobId, Guid linkId) =>
        audit.Record("Agent", SplitAction, AuditTargetType, jobId, JsonSerializer.Serialize(
            new { jobId, newJobId, linkId, reason = Reasons.Split }, AuditJson));
}
