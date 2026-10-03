using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WorkPilot.Application.Common;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;

namespace WorkPilot.Application.Modules.Jobs.Matching;

/// <summary>
/// Reads a job description into structured, quoted requirements (spec 0019, AC-12). It sees only
/// the description text, never profile data, and calls the model with no tools.
/// </summary>
public interface IJobRequirementExtractor
{
    /// <summary>
    /// Extracts the requirements of <paramref name="description"/> (already cut at
    /// <c>MaxDescriptionChars</c>). Throws <see cref="JobRequirementExtractionException"/> when the
    /// output breaks the v1 schema, and the provider's own exception when the call fails; either
    /// counts as one failed attempt.
    /// </summary>
    Task<JobRequirementExtraction> ExtractAsync(string description, CancellationToken cancellationToken);
}

/// <summary>A valid extraction and the model id that produced it.</summary>
public sealed record JobRequirementExtraction(JobRequirementsV1 Requirements, string Model);

/// <summary>The model answered, but not with a valid v1 requirements document (AC-12).</summary>
public sealed class JobRequirementExtractionException(string message) : Exception(message);

/// <summary>Queues the matching background jobs (implemented with Hangfire in Workers).</summary>
public interface IMatchJobScheduler
{
    /// <summary>Queues requirements extraction (then scoring) for one job.</summary>
    void EnqueueExtraction(Guid jobId, bool force);

    /// <summary>Queues a rescore of every job for one profile.</summary>
    void EnqueueProfileRescore(Guid profileId);
}

/// <summary>A non deleted job's description and its primary link's latest snapshot hash.</summary>
public sealed record JobContentState(Guid JobId, string? Description, string ContentHash);

/// <summary>Everything the scorer reads about one job, with its requirements row (Extracted or Failed).</summary>
public sealed record ScoringJobRow(
    Guid JobId,
    string Title,
    string? Location,
    string? RemoteType,
    string? Description,
    string ContentHash,
    int ExtractorVersion,
    RequirementsStatus Status,
    string? RequirementsJson);

/// <summary>The stored match the threshold rule and the fingerprint check compare against.</summary>
public sealed record StoredMatch(int? Score, bool HasBlocker, string InputsFingerprint);

/// <summary>One match row to upsert.</summary>
public sealed record MatchWrite(
    Guid JobId,
    Guid ProfileId,
    int? Score,
    MatchConfidence Confidence,
    bool HasBlocker,
    string ExplanationJson,
    string InputsFingerprint,
    int ScoringVersion,
    DateTimeOffset RankedAt);

/// <summary>What the hourly sweep found to do.</summary>
public sealed record MatchSweepWork(IReadOnlyList<Guid> JobsToExtract, IReadOnlyList<Guid> ProfilesToRescore);

/// <summary>
/// Persistence port for matching (spec 0019). Only the Jobs module writes <c>job_requirements</c>
/// and <c>job_matches</c>; profile tables are only read.
/// </summary>
public interface IMatchRepository
{
    /// <summary>Runs <paramref name="work"/> in one transaction (retried as a whole on a transient failure) and commits it.</summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    /// <summary>A non deleted job's current content, or null when the job is missing or soft deleted.</summary>
    Task<JobContentState?> GetJobContentAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The job's requirements row, tracked, or null.</summary>
    Task<JobRequirement?> GetRequirementAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Stages a new requirements row.</summary>
    void AddRequirement(JobRequirement requirement);

    /// <summary>Every non deleted profile id.</summary>
    Task<IReadOnlyList<Guid>> GetProfileIdsAsync(CancellationToken cancellationToken);

    /// <summary>The match profile read model, or null for a missing or soft deleted profile.</summary>
    Task<MatchProfileInput?> GetMatchProfileAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="take"/> non deleted job ids after <paramref name="afterJobId"/>, in id order.</summary>
    Task<IReadOnlyList<Guid>> GetJobIdsPageAsync(Guid? afterJobId, int take, CancellationToken cancellationToken);

    /// <summary>The scoring inputs of those jobs that are not deleted and have an Extracted or Failed requirements row.</summary>
    Task<IReadOnlyList<ScoringJobRow>> GetScoringJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken);

    /// <summary>The stored matches of one profile for these jobs, keyed by job id.</summary>
    Task<IReadOnlyDictionary<Guid, StoredMatch>> GetStoredMatchesAsync(Guid profileId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates the (job, profile) match, writing nothing when the stored fingerprint is
    /// the same (AC-7). Runs immediately in the current transaction; true when a row was written.
    /// </summary>
    Task<bool> UpsertMatchAsync(MatchWrite write, CancellationToken cancellationToken);

    /// <summary>The jobs needing extraction and the profiles needing a rescore (spec 0019, <c>MatchSweepJob</c>).</summary>
    Task<MatchSweepWork> FindSweepWorkAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Commits what is staged (requirements rows, outbox events).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The read side of matching: the list and the match panel (spec 0019, AC-9, AC-10, AC-15).</summary>
public interface IMatchQueries
{
    /// <summary>
    /// One page of non deleted jobs with the profile's match, filtered and sorted by
    /// <paramref name="query"/> (spec 0021, AC-1, AC-2); 404 for an unknown profile, 400 for a bad
    /// filter or paging (see <see cref="JobSearchValidation"/>).
    /// </summary>
    Task<Result<MatchListDto>> ListAsync(Guid profileId, JobListQuery query, CancellationToken cancellationToken);

    /// <summary>The profile's match of one job; 404 when the job, the profile or that profile's match is missing.</summary>
    Task<Result<JobMatchDetailDto>> GetAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken);
}

/// <summary>The JSON settings for stored requirements and explanations: web defaults, enums as strings, nulls left out.</summary>
public static class MatchingJson
{
    /// <summary>
    /// The shared serializer options, frozen up front: the AI structured output call marks the
    /// options it is given read only, which throws under concurrent use unless a resolver is set.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes <paramref name="value"/> with <see cref="Options"/>.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes <paramref name="json"/> with <see cref="Options"/>.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}
