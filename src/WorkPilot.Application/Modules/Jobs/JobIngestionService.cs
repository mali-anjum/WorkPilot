using System.Text.Json;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>Counts from one ingestion run, also written as the <c>JobsIngested</c> audit payload (spec 0008, AC-5).</summary>
public sealed record JobIngestionSummary(
    Guid JobSourceId,
    int Fetched,
    int Matched,
    int Created,
    int Updated,
    int Unchanged,
    int Skipped);

/// <summary>
/// Job source ingestion use case (spec 0008): registers boards as
/// <see cref="JobSource"/> rows, and runs one ingestion (fetch through the
/// source's <see cref="IJobSource"/>, keyword filter, normalize, upsert by
/// external id, audit, one save). Business rules live in the domain
/// (<see cref="JobNormalizer"/>, <see cref="Job.Create"/>, <see cref="Job.Refresh"/>).
/// </summary>
public sealed class JobIngestionService(
    IEnumerable<IJobSource> sources,
    IJobIngestionRepository repository,
    IAuditService audit,
    TimeProvider time)
{
    /// <summary>Audit action written once per completed run.</summary>
    public const string AuditAction = "JobsIngested";

    /// <summary>Audit target type for ingestion runs.</summary>
    public const string AuditTargetType = "JobSource";

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Finds or creates the <see cref="JobSource"/> row for <paramref name="board"/>
    /// on the source named <paramref name="sourceType"/>. Returns <c>null</c> for an
    /// unknown source type or an identifier the source rejects (AC-1).
    /// </summary>
    public async Task<JobSource?> RegisterSourceAsync(string sourceType, string board, CancellationToken cancellationToken)
    {
        var source = Resolve(sourceType);
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
    /// anything is staged, so nothing partial is saved (AC-6).
    /// </summary>
    public async Task<JobIngestionSummary?> IngestAsync(Guid jobSourceId, string? keywords, CancellationToken cancellationToken)
    {
        var jobSource = await repository.GetSourceAsync(jobSourceId, cancellationToken);
        if (jobSource is null)
        {
            return null;
        }

        var source = Resolve(jobSource.Type)
            ?? throw new InvalidOperationException($"No IJobSource is registered for source type '{jobSource.Type}'.");

        var postings = await source.FetchAsync(jobSource, cancellationToken);
        var retrievedAt = time.GetUtcNow();
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

        var existing = await repository.GetJobsByExternalIdAsync(
            jobSourceId, normalized.Select(n => n.ExternalId).ToList(), cancellationToken);

        int created = 0, updated = 0, unchanged = 0;
        foreach (var posting in normalized)
        {
            if (existing.TryGetValue(posting.ExternalId, out var job))
            {
                var snapshot = job.Refresh(posting, retrievedAt, source.ProvenanceConfidence);
                if (snapshot is null)
                {
                    unchanged++;
                }
                else
                {
                    repository.AddSnapshot(snapshot);
                    updated++;
                }
            }
            else
            {
                repository.AddJob(Job.Create(jobSourceId, posting, retrievedAt, source.ProvenanceConfidence));
                created++;
            }
        }

        var summary = new JobIngestionSummary(jobSourceId, postings.Count, matched.Count, created, updated, unchanged, skipped);
        audit.Record("Agent", AuditAction, AuditTargetType, jobSourceId, JsonSerializer.Serialize(summary, AuditJson));
        await repository.SaveChangesAsync(cancellationToken);
        return summary;
    }

    private IJobSource? Resolve(string sourceType) =>
        sources.FirstOrDefault(s => string.Equals(s.SourceType, sourceType, StringComparison.OrdinalIgnoreCase));
}
