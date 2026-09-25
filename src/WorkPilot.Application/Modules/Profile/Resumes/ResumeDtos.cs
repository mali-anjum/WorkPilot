namespace WorkPilot.Application.Modules.Profile.Resumes;

/// <summary>One resume as listed on <c>/resumes</c> (spec 0009, AC-1).</summary>
public sealed record ResumeSummaryDto(
    Guid Id,
    string Name,
    string Kind,
    string? TargetCompany,
    int LatestVersionNumber,
    bool LatestIsLocked,
    int VersionCount,
    DateTimeOffset UpdatedAt);

/// <summary>One version in a resume's history (spec 0009, AC-7).</summary>
public sealed record ResumeVersionDto(
    Guid Id,
    Guid ResumeId,
    int VersionNumber,
    string Content,
    string? Note,
    string? FileName,
    string? FileContentType,
    long? FileSizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsLocked,
    DateTimeOffset? LockedAt,
    Guid? LockedByApplicationId);

/// <summary>A resume with its full version history, newest version first (spec 0009, AC-7).</summary>
public sealed record ResumeDetailDto(
    Guid Id,
    string Name,
    string Kind,
    string? TargetCompany,
    Guid? SourceVersionId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ResumeVersionDto> Versions);

/// <summary>The result of a revision: what happened, to which version number, and the resume afterwards.</summary>
public sealed record ReviseResumeResultDto(string Outcome, int VersionNumber, ResumeDetailDto Resume);

/// <summary>A file uploaded with a create or revise request. The caller owns and disposes <see cref="Content"/>.</summary>
public sealed record UploadedResumeFile(string FileName, long Length, Stream Content);

/// <summary>Input for <see cref="IResumeService.CreateAsync"/>.</summary>
public sealed record CreateResumeCommand(Guid ProfileId, string? Name, string? Content, string? Note, UploadedResumeFile? File);

/// <summary>Input for <see cref="IResumeService.TailorAsync"/>.</summary>
public sealed record TailorResumeCommand(Guid ProfileId, Guid SourceVersionId, string? Name, string? TargetCompany, string? Note);

/// <summary>Input for <see cref="IResumeService.ReviseAsync"/>.</summary>
public sealed record ReviseResumeCommand(Guid ProfileId, Guid ResumeId, string? Content, string? Note, UploadedResumeFile? File, bool RemoveFile);

/// <summary>A version's stored file, ready to send to the browser.</summary>
public sealed record ResumeFileDownload(string FileName, string ContentType, byte[] Content);

/// <summary>How a resume operation ended.</summary>
public enum ResumeResultStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>
/// The outcome of a resume write: a value on success, or why it failed. Keeps the not found /
/// invalid / conflict cases explicit without a project wide error pattern (still undecided per AGENTS.md).
/// </summary>
public sealed record ResumeResult<T>(ResumeResultStatus Status, T? Value, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static ResumeResult<T> Ok(T value) => new(ResumeResultStatus.Ok, value);

    public static ResumeResult<T> NotFound() => new(ResumeResultStatus.NotFound, default);

    public static ResumeResult<T> Invalid(string field, string message) =>
        new(ResumeResultStatus.Invalid, default, new Dictionary<string, string[]> { [field] = [message] });

    public static ResumeResult<T> Conflict(string message) =>
        new(ResumeResultStatus.Conflict, default, new Dictionary<string, string[]> { ["resume"] = [message] });
}
