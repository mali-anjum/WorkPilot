using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Application.Modules.Jobs;

/// <summary>
/// The one source specific seam of job ingestion (spec 0008, AC-8). An
/// implementation knows how to describe a board of its kind as a
/// <see cref="JobSource"/> row and how to fetch that board's raw postings;
/// normalization, storage and scheduling never branch on the source type.
/// </summary>
public interface IJobSource
{
    /// <summary>Stored as <see cref="JobSource.Type"/>, e.g. <c>Greenhouse</c>. Matched case insensitively against a trigger's <c>source</c>.</summary>
    string SourceType { get; }

    /// <summary>How much this source is trusted, stored as <c>Provenance.Confidence</c> (0 to 1).</summary>
    decimal ProvenanceConfidence { get; }

    /// <summary>
    /// Describes the board identified by <paramref name="board"/> (e.g. a
    /// Greenhouse board token), or returns <c>null</c> when the identifier is
    /// not valid for this source.
    /// </summary>
    JobSourceDefinition? Describe(string board);

    /// <summary>
    /// Fetches every current posting on <paramref name="source"/>'s board.
    /// Throws on a transport or response format failure, so nothing partial
    /// is stored and the background job is retried (AC-6).
    /// </summary>
    Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Maps one stored posting (the <see cref="RawJobPosting.RawContent"/> this
    /// source wrote to a snapshot) back into a raw posting, with no fetch
    /// (spec 0017). Used to rebuild a job's fields from a link that wasn't
    /// fetched in this run, and by the split. Throws when it isn't one.
    /// </summary>
    RawJobPosting ParseStored(JobSource source, string rawContent);
}

/// <summary>What a <see cref="JobSource"/> row for one board looks like.</summary>
/// <param name="Name">Unique per source type, e.g. <c>greenhouse:gitlab</c>.</param>
/// <param name="ConfigJson">Source specific JSON config, e.g. <c>{"boardToken":"gitlab"}</c>.</param>
public sealed record JobSourceDefinition(string Name, string ConfigJson);
