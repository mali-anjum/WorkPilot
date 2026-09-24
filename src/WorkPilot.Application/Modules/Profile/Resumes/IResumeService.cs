namespace WorkPilot.Application.Modules.Profile.Resumes;

/// <summary>
/// Resume management use cases (spec 0009). Every call is scoped to <c>profileId</c>: a resume or
/// version owned by another profile behaves exactly like one that does not exist (AC-9). The rules
/// themselves (drafts, locking, versioning) live in the domain's <c>Resume</c>/<c>ResumeVersion</c>.
/// </summary>
public interface IResumeService
{
    /// <summary>Every resume of the profile, most recently updated first.</summary>
    Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>One resume with its full version history, or null when not found for this profile.</summary>
    Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken);

    /// <summary>Creates a base resume with version 1 as a draft (AC-1).</summary>
    Task<ResumeResult<ResumeDetailDto>> CreateAsync(CreateResumeCommand command, CancellationToken cancellationToken);

    /// <summary>Creates a tailored resume copied from one of the profile's versions (AC-6).</summary>
    Task<ResumeResult<ResumeDetailDto>> TailorAsync(TailorResumeCommand command, CancellationToken cancellationToken);

    /// <summary>Edits the resume: updates the draft in place, or appends a new version when the newest is locked (AC-2, AC-4, AC-10).</summary>
    Task<ResumeResult<ReviseResumeResultDto>> ReviseAsync(ReviseResumeCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Locks a version because an application used it (AC-3). Feature 17 can call this, or call
    /// <c>ResumeVersion.Lock</c> itself inside its own unit of work.
    /// </summary>
    Task<ResumeResult<ResumeVersionDto>> LockVersionAsync(Guid profileId, Guid versionId, Guid applicationId, CancellationToken cancellationToken);

    /// <summary>A version's stored file, or null when the version is not found or has no file (AC-7).</summary>
    Task<ResumeFileDownload?> GetFileAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken);
}

/// <summary>
/// Where resume files are kept (spec 0009). The returned key is stored on the version as
/// <c>StorageUrl</c>. An implementation may defer the write to the caller's unit of work, so a key is
/// only guaranteed durable once the caller has saved. Stored files are never changed or deleted.
/// </summary>
public interface IResumeFileStore
{
    /// <summary>Stores a file and returns its opaque key.</summary>
    Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken);

    /// <summary>Reads a stored file's bytes, or null when the key is unknown.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);
}
