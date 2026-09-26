using System.Text.Json;
using System.Text.Json.Serialization;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>Counts from one ingestion run, also written as the <c>JobsIngested</c> audit payload (spec 0008, AC-5; spec 0017, AC-12).</summary>
/// <param name="Merged">New links that joined an existing job instead of creating one.</param>
public sealed record JobIngestionSummary(
    Guid JobSourceId,
    int Fetched,
    int Matched,
    int Created,
    int Updated,
    int Unchanged,
    int Skipped,
    int Merged)
{
    /// <summary>True when the run left a job stale, so the reconcile job should run after it (spec 0017, AC-9).</summary>
    [JsonIgnore]
    public bool ReconcileNeeded { get; init; }
}

/// <summary>
/// Job source ingestion use case (specs 0008, 0017): registers boards as
/// <see cref="JobSource"/> rows, and runs one ingestion: fetch through the
/// source's <see cref="IJobSource"/>, keyword filter, normalize, then in one
/// transaction apply a company rename, lock the match keys of new postings,
/// and either refresh a known link, attach a new link to the job with the same
/// key, or create a job. Business rules live in the domain
/// (<see cref="JobNormalizer"/>, <see cref="JobDedupKey"/>, <see cref="Job"/>).
/// </summary>
public sealed class JobIngestionService(
    IEnumerable<IJobSource> sources,
    IJobRepository repository,
    JobMerger merger,
    IAuditService audit,
    TimeProvider time)
{
    /// <summary>Audit action written once per completed run.</summary>
    public const string AuditAction = "JobsIngested";

    /// <summary>Audit target type for ingestion runs.</summary>
    public const string AuditTargetType = "JobSource";

    /// <summary>Longest accepted <see cref="JobSource.CompanyName"/> (spec 0017).</summary>
    public const int MaxCompanyNameLength = 200;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Finds or creates the <see cref="JobSource"/> row for <paramref name="board"/>
    /// on the source named <paramref name="sourceType"/>. Returns <c>null</c> for an
    /// unknown source type or an identifier the source rejects (AC-1). A
    /// company name is not stored here: the ingestion run applies it (AC-7).
    /// </summary>
    public async Task<JobSource?> RegisterSourceAsync(string sourceType, string board, CancellationToken cancellationToken)
    {
        var source = JobSources.Resolve(sources, sourceType);
        var definition = source?.Describe(board);
        if (source is null || definition is null)
        {
            return null;
        }

        var existing = await repository.FindSourceAsync(source.SourceType, definition.Name, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new JobSource { Type = source.SourceType, Name = definition.Name, Config = definition.ConfigJson };
        repository.AddSource(created);
        await repository.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Runs one ingestion for <paramref name="jobSourceId"/>. Returns <c>null</c>
    /// when the source row no longer exists. A fetch failure propagates before
    /// anything is staged, so nothing partial is saved (spec 0008, AC-6). A
    /// non null <paramref name="companyName"/> different from the stored one is
    /// a rename: it is saved, and that source's jobs are refilled and
    /// rematched before the new postings are applied (spec 0017, AC-7).
    /// </summary>
    public async Task<JobIngestionSummary?> IngestAsync(Guid jobSourceId, string? keywords, string? companyName, CancellationToken cancellationToken)
    {
        var jobSource = await repository.GetSourceAsync(jobSourceId, cancellationToken);
        if (jobSource is null)
        {
            return null;
        }

        var source = JobSources.Resolve(sources, jobSource.Type)
            ?? throw new InvalidOperationException($"No IJobSource is registered for source type '{jobSource.Type}'.");

        var postings = await source.FetchAsync(jobSource, cancellationToken);
        var seenAt = time.GetUtcNow();
        var query = JobSearchQuery.Parse(keywords);

        var matched = postings.Where(p => query.Matches(JobNormalizer.CleanLine(p.Title))).ToList();
        var normalized = new List<NormalizedJob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var posting in matched)
        {
            // A posting missing a required field, or repeating an id already in this batch, is skipped and counted.
            var job = JobNormalizer.Normalize(posting);
            if (job is null || !seen.Add(job.ExternalId))
            {
                skipped++;
                continue;
            }

            normalized.Add(job);
        }

        return await repository.InTransactionAsync(
            ct => ApplyAsync(jobSourceId, source, normalized, companyName, seenAt, postings.Count, matched.Count, skipped, ct),
            cancellationToken);
    }

    private async Task<JobIngestionSummary?> ApplyAsync(
        Guid jobSourceId,
        IJobSource source,
        List<NormalizedJob> normalized,
        string? companyName,
        DateTimeOffset seenAt,
        int fetched,
        int matched,
        int skipped,
        CancellationToken cancellationToken)
    {
        // Loaded again inside the transaction: a retried attempt starts clean.
        var jobSource = await repository.GetSourceAsync(jobSourceId, cancellationToken);
        if (jobSource is null)
        {
            return null;
        }

        var rename = companyName is not null && companyName != jobSource.CompanyName;
        if (rename)
        {
            jobSource.CompanyName = companyName;
        }

        var postings = normalized.Select(p => JobSources.WithCompany(p, jobSource.CompanyName)).ToList();
        var externalIds = postings.Select(p => p.ExternalId).ToList();

        // Read before locking only to know which keys to lock; everything is read again once they are held.
        var renamed = new List<(Job Job, NormalizedJob Posting)>();
        if (rename)
        {
            var linked = await repository.GetJobsLinkedToSourceAsync(jobSourceId, cancellationToken);
            var resolver = await PostingResolver.CreateAsync(repository, sources, linked, cancellationToken);
            renamed = linked
                .Where(j => j.PrimaryLink?.JobSourceId == jobSourceId)
                .Select(j => (j, resolver.Of(j.PrimaryLink!)))
                .ToList();
        }

        var known = await repository.GetJobsByLinkAsync(jobSourceId, externalIds, cancellationToken);
        var keys = postings.Where(p => !known.ContainsKey(p.ExternalId)).Select(JobSources.KeyOf)
            .Concat(renamed.Select(r => JobSources.KeyOf(r.Posting)));
        await repository.LockDedupKeysAsync(keys, cancellationToken);

        var merged = 0;
        if (rename)
        {
            foreach (var (job, posting) in renamed)
            {
                job.ApplyPrimaryPosting(posting);
            }

            var renamedJobs = renamed.Select(r => r.Job).ToList();
            foreach (var key in renamedJobs.Select(j => j.DedupKey!).Distinct().Order(StringComparer.Ordinal))
            {
                merged += await merger.MergeGroupAsync(key, renamedJobs, JobMerger.Reasons.Rename, cancellationToken);
            }

            // Saved (not committed) so the lookups below see the merged links.
            await repository.SaveChangesAsync(cancellationToken);
        }

        // A competing run may have committed some of these links while this one waited on a lock.
        known = await repository.GetJobsByLinkAsync(jobSourceId, externalIds, cancellationToken);
        var postingsOf = await PostingResolver.CreateAsync(repository, sources, known.Values.Distinct().ToList(), cancellationToken);

        var createdThisRun = new Dictionary<string, Job>(StringComparer.Ordinal);
        var touched = new HashSet<Job>();
        int created = 0, updated = 0, unchanged = 0;
        foreach (var posting in postings)
        {
            if (known.TryGetValue(posting.ExternalId, out var job))
            {
                var link = job.Links.Single(l => l.JobSourceId == jobSourceId && l.ExternalId == posting.ExternalId);
                var snapshot = job.SeeAgain(link, posting, seenAt, source.ProvenanceConfidence, postingsOf.Of);
                if (snapshot is null)
                {
                    unchanged++;
                }
                else
                {
                    repository.AddSnapshot(snapshot);
                    updated++;
                }

                touched.Add(job);
                continue;
            }

            var key = JobSources.KeyOf(posting);
            var existing = createdThisRun.GetValueOrDefault(key) ?? await FindJobToJoinAsync(key, cancellationToken);
            if (existing is not null)
            {
                var sighting = existing.AttachLink(jobSourceId, posting, seenAt, source.ProvenanceConfidence);
                repository.AddLink(sighting.Link);
                repository.AddSnapshot(sighting.Snapshot);
                merger.RecordMerge(existing.Id, null, [sighting.Link.Id], JobMerger.Reasons.Ingest);
                touched.Add(existing);
                merged++;
                continue;
            }

            var newJob = Job.Create(jobSourceId, posting, seenAt, source.ProvenanceConfidence);
            repository.AddJob(newJob);
            createdThisRun[key] = newJob;
            created++;
        }

        var summary = new JobIngestionSummary(jobSourceId, fetched, matched, created, updated, unchanged, skipped, merged)
        {
            ReconcileNeeded = touched.Any(j => j.IsStale),
        };
        audit.Record("Agent", AuditAction, AuditTargetType, jobSourceId, JsonSerializer.Serialize(summary, AuditJson));
        await repository.SaveChangesAsync(cancellationToken);
        return summary;
    }

    // Which job a new link joins when several share its key (spec 0017): the
    // oldest (lowest UUIDv7 id) that is not soft deleted, else the oldest soft
    // deleted one, which AttachLink then revives.
    private async Task<Job?> FindJobToJoinAsync(string key, CancellationToken cancellationToken)
    {
        var candidates = (await repository.GetJobsByKeyAsync(key, cancellationToken))
            .Where(j => j.DedupKey == key)
            .OrderBy(j => j.Id)
            .ToList();
        return candidates.FirstOrDefault(j => !j.IsDeleted) ?? candidates.FirstOrDefault();
    }
}
