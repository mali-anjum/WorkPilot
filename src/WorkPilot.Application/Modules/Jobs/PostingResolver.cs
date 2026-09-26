using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>
/// Rebuilds a link's posting from its latest stored snapshot, with no fetch
/// (spec 0017): the owning source's <see cref="IJobSource.ParseStored"/>,
/// then <see cref="JobNormalizer"/>, then the source's company name, if set.
/// The snapshots are indexed when it is created, so a posting stays findable
/// while merges and splits move snapshots between jobs.
/// </summary>
public sealed class PostingResolver
{
    private readonly IReadOnlyDictionary<Guid, JobSource> _sources;
    private readonly IEnumerable<IJobSource> _adapters;
    private readonly Dictionary<(Guid, string), JobSnapshot> _latest = [];

    private PostingResolver(IReadOnlyDictionary<Guid, JobSource> sources, IEnumerable<IJobSource> adapters, IEnumerable<Job> jobs)
    {
        _sources = sources;
        _adapters = adapters;
        foreach (var snapshot in jobs.SelectMany(j => j.Snapshots))
        {
            var key = (snapshot.JobSourceId, snapshot.ExternalId);
            if (!_latest.TryGetValue(key, out var current) || snapshot.Provenance.RetrievedAt > current.Provenance.RetrievedAt)
            {
                _latest[key] = snapshot;
            }
        }
    }

    /// <summary>Indexes <paramref name="jobs"/>' snapshots and loads every source their links come from.</summary>
    public static async Task<PostingResolver> CreateAsync(
        IJobRepository repository,
        IEnumerable<IJobSource> adapters,
        IReadOnlyCollection<Job> jobs,
        CancellationToken cancellationToken)
    {
        var sourceIds = jobs.SelectMany(j => j.Links).Select(l => l.JobSourceId).Distinct().ToList();
        var sources = await repository.GetSourcesAsync(sourceIds, cancellationToken);
        return new PostingResolver(sources, adapters, jobs);
    }

    /// <summary>The posting <paramref name="link"/> last had, with its source's company name applied.</summary>
    public NormalizedJob Of(JobSourceLink link)
    {
        if (!_latest.TryGetValue((link.JobSourceId, link.ExternalId), out var snapshot))
        {
            throw new InvalidOperationException($"Link {link.Id} has no stored snapshot.");
        }

        var source = _sources.GetValueOrDefault(link.JobSourceId)
            ?? throw new InvalidOperationException($"Job source {link.JobSourceId} was not loaded.");
        var adapter = JobSources.Resolve(_adapters, source.Type)
            ?? throw new InvalidOperationException($"No IJobSource is registered for source type '{source.Type}'.");

        var posting = JobNormalizer.Normalize(adapter.ParseStored(source, snapshot.RawContent))
            ?? throw new InvalidOperationException($"The stored posting of link {link.Id} is no longer usable.");
        return JobSources.WithCompany(posting, source.CompanyName);
    }
}

/// <summary>Small helpers shared by the Jobs use cases.</summary>
public static class JobSources
{
    /// <summary>The adapter for <paramref name="sourceType"/> (case insensitive), or <c>null</c>.</summary>
    public static IJobSource? Resolve(IEnumerable<IJobSource> adapters, string sourceType) =>
        adapters.FirstOrDefault(s => string.Equals(s.SourceType, sourceType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies a source's configured company name, which is both the displayed
    /// company and the match company (spec 0017, AC-7). The content hash is
    /// kept, so a rename never counts as a change in the link's own history.
    /// </summary>
    public static NormalizedJob WithCompany(NormalizedJob posting, string? companyName) =>
        companyName is null ? posting : posting with { Company = companyName };

    /// <summary>The match key of a posting (spec 0017, AC-3).</summary>
    public static string KeyOf(NormalizedJob posting) => JobDedupKey.For(posting.Company, posting.Title, posting.Location);
}
