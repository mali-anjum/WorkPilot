using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Profile;

/// <summary>Whether a resume is a general base resume or one tailored for a specific company (spec 0009).</summary>
public enum ResumeKind
{
    Base,
    Tailored,
}

/// <summary>What <see cref="Resume.Revise"/> did.</summary>
public enum ReviseOutcome
{
    /// <summary>The newest version was an unlocked draft and was changed in place.</summary>
    UpdatedDraft,

    /// <summary>The newest version was locked, so a new draft version was appended.</summary>
    CreatedVersion,

    /// <summary>The revision matched the newest version exactly; nothing changed.</summary>
    Unchanged,
}

/// <summary>The result of <see cref="Resume.Revise"/>: what happened and the version it happened to.</summary>
public sealed record ReviseResult(ReviseOutcome Outcome, ResumeVersion Version);

/// <summary>
/// A pointer to a stored resume file (the PDF or DOCX actually sent to an employer).
/// <see cref="StorageKey"/> is opaque to the domain; the file store that produced it owns its meaning.
/// </summary>
public sealed record ResumeFileRef(string StorageKey, string FileName, string ContentType, long SizeBytes);

/// <summary>Thrown when resume input breaks one of <see cref="ResumeRules"/> (spec 0009, AC-8).</summary>
public sealed class ResumeValidationException(string field, string message) : Exception(message)
{
    /// <summary>The input field at fault, e.g. <c>name</c> or <c>file</c>.</summary>
    public string Field { get; } = field;
}

/// <summary>Thrown when something tries to change a version an application already used (spec 0009, AC-5).</summary>
public sealed class ResumeVersionLockedException(Guid versionId)
    : InvalidOperationException($"Resume version {versionId} is locked because an application used it; edit the resume to create a new version instead.")
{
    /// <summary>The locked version.</summary>
    public Guid VersionId { get; } = versionId;
}

/// <summary>The input limits every resume operation enforces (spec 0009, AC-8).</summary>
public static class ResumeRules
{
    public const int NameMaxLength = 200;
    public const int TargetCompanyMaxLength = 200;
    public const int ContentMaxLength = 100_000;
    public const int NoteMaxLength = 500;
    public const int FileNameMaxLength = 255;
    public const long FileMaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
    };

    /// <summary>The allowed file extensions, for display (e.g. an upload control's accept list).</summary>
    public static IReadOnlyCollection<string> AllowedFileExtensions => AllowedFileTypes.Keys;

    /// <summary>
    /// Checks an uploaded file's name and size and returns the content type it will be stored and
    /// served with (derived from the extension, never from what the client claimed).
    /// </summary>
    public static string ValidateFile(string fileName, long sizeBytes)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > FileNameMaxLength)
        {
            throw new ResumeValidationException("file", $"The file name must be 1 to {FileNameMaxLength} characters.");
        }

        if (!AllowedFileTypes.TryGetValue(Path.GetExtension(name), out var contentType))
        {
            throw new ResumeValidationException("file", $"Only {string.Join(", ", AllowedFileTypes.Keys)} files are allowed.");
        }

        if (sizeBytes <= 0 || sizeBytes > FileMaxBytes)
        {
            throw new ResumeValidationException("file", $"The file must be between 1 byte and {FileMaxBytes / (1024 * 1024)} MB.");
        }

        return contentType;
    }

    internal static string Name(string? value, string field = "name", int max = NameMaxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > max)
        {
            throw new ResumeValidationException(field, $"The {field} must be 1 to {max} characters.");
        }

        return trimmed;
    }

    internal static string Content(string? value)
    {
        var content = value ?? string.Empty;
        if (content.Length > ContentMaxLength)
        {
            throw new ResumeValidationException("content", $"The resume text must be at most {ContentMaxLength} characters.");
        }

        return content;
    }

    internal static string? Note(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > NoteMaxLength)
        {
            throw new ResumeValidationException("note", $"The note must be at most {NoteMaxLength} characters.");
        }

        return trimmed;
    }

    internal static void ContentOrFile(string content, ResumeFileRef? file)
    {
        if (string.IsNullOrWhiteSpace(content) && file is null)
        {
            throw new ResumeValidationException("content", "A resume version needs text content, a file, or both.");
        }
    }
}

/// <summary>
/// A named resume (a base resume, or one tailored for a company) and its numbered version history.
/// Only the newest version may be an unlocked draft; every older one is locked (spec 0009).
/// </summary>
public class Resume : SoftDeletableEntity
{
    private readonly List<ResumeVersion> _versions = [];

    private Resume()
    {
    }

    public Guid ProfileId { get; private init; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Unused; kept from spec 0002's original shape.</summary>
    public bool IsActive { get; set; }

    public ResumeKind Kind { get; private init; }

    /// <summary>The company a <see cref="ResumeKind.Tailored"/> resume is for; null for a base resume.</summary>
    public string? TargetCompany { get; private init; }

    /// <summary>The version a tailored resume was copied from; null for a base resume.</summary>
    public Guid? SourceVersionId { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>Every version, in no particular order; see <see cref="LatestVersion"/>.</summary>
    public IReadOnlyList<ResumeVersion> Versions => _versions;

    /// <summary>The highest numbered version (the only one that can be a draft).</summary>
    public ResumeVersion LatestVersion =>
        _versions.MaxBy(v => v.VersionNumber)
        ?? throw new InvalidOperationException($"Resume {Id} has no versions loaded.");

    /// <summary>Creates a base resume whose version 1 is an unlocked draft (spec 0009, AC-1).</summary>
    public static Resume CreateBase(Guid profileId, string name, string? content, string? note, ResumeFileRef? file, DateTimeOffset now)
    {
        var resume = new Resume
        {
            ProfileId = profileId,
            Name = ResumeRules.Name(name),
            Kind = ResumeKind.Base,
            CreatedAt = now,
        };
        resume._versions.Add(ResumeVersion.Create(resume.Id, 1, content, note, file, now));
        return resume;
    }

    /// <summary>
    /// Creates a separate tailored resume whose version 1 is a draft copy of <paramref name="source"/>'s
    /// content and file. The source is only read, never changed (spec 0009, AC-6).
    /// </summary>
    public static Resume CreateTailored(Guid profileId, string name, string targetCompany, ResumeVersion source, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resume = new Resume
        {
            ProfileId = profileId,
            Name = ResumeRules.Name(name),
            Kind = ResumeKind.Tailored,
            TargetCompany = ResumeRules.Name(targetCompany, "targetCompany", ResumeRules.TargetCompanyMaxLength),
            SourceVersionId = source.Id,
            CreatedAt = now,
        };
        resume._versions.Add(ResumeVersion.Create(resume.Id, 1, source.Content, note, source.File, now));
        return resume;
    }

    /// <summary>
    /// Applies an edit to the resume. If the newest version is an unlocked draft it is changed in place
    /// (AC-2); if it is locked, a new draft version is appended and the locked one is left untouched
    /// (AC-4); an edit identical to the newest version does nothing (AC-10). With no
    /// <paramref name="newFile"/>, the newest version's file is kept unless <paramref name="removeFile"/>.
    /// </summary>
    public ReviseResult Revise(string? content, string? note, ResumeFileRef? newFile, bool removeFile, DateTimeOffset now)
    {
        var latest = LatestVersion;
        var validContent = ResumeRules.Content(content);
        var validNote = ResumeRules.Note(note);
        var file = newFile ?? (removeFile ? null : latest.File);
        ResumeRules.ContentOrFile(validContent, file);

        if (validContent == latest.Content && validNote == latest.Note && file == latest.File)
        {
            return new ReviseResult(ReviseOutcome.Unchanged, latest);
        }

        if (!latest.IsLocked)
        {
            latest.EditDraft(validContent, validNote, file, now);
            return new ReviseResult(ReviseOutcome.UpdatedDraft, latest);
        }

        var next = ResumeVersion.Create(Id, latest.VersionNumber + 1, validContent, validNote, file, now);
        _versions.Add(next);
        return new ReviseResult(ReviseOutcome.CreatedVersion, next);
    }
}

/// <summary>
/// One numbered version of a resume: its text, an optional stored file, and whether an application has
/// locked it. A locked version never changes again, in memory or in the database (spec 0009, AC-5).
/// </summary>
public class ResumeVersion : Entity
{
    private ResumeVersion()
    {
    }

    public Guid ResumeId { get; private init; }
    public int VersionNumber { get; private init; }

    /// <summary>The resume's editable text (plain text or Markdown).</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>A short free text note about this version, e.g. what changed.</summary>
    public string? Note { get; private set; }

    /// <summary>The file store key of this version's file, or null when it has none.</summary>
    public string? StorageUrl { get; private set; }

    public string? FileName { get; private set; }
    public string? FileContentType { get; private set; }
    public long? FileSizeBytes { get; private set; }

    /// <summary>Unused; kept from spec 0002's original shape.</summary>
    public string? ParsedContent { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When an application first used this version; null while it is still a draft.</summary>
    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>The application that first used (and so locked) this version.</summary>
    public Guid? LockedByApplicationId { get; private set; }

    public bool IsLocked => LockedAt is not null;

    /// <summary>This version's stored file, or null.</summary>
    public ResumeFileRef? File =>
        StorageUrl is null ? null : new ResumeFileRef(StorageUrl, FileName ?? string.Empty, FileContentType ?? string.Empty, FileSizeBytes ?? 0);

    internal static ResumeVersion Create(Guid resumeId, int versionNumber, string? content, string? note, ResumeFileRef? file, DateTimeOffset now)
    {
        var validContent = ResumeRules.Content(content);
        ResumeRules.ContentOrFile(validContent, file);
        var version = new ResumeVersion
        {
            ResumeId = resumeId,
            VersionNumber = versionNumber,
            CreatedAt = now,
        };
        version.Apply(validContent, ResumeRules.Note(note), file, now);
        return version;
    }

    /// <summary>
    /// Marks this version as used by an application, making it immutable forever. This is the call
    /// feature 17 makes when an application uses the version. Locking an already locked version is a
    /// no op that keeps the first application (spec 0009, AC-3). Returns true only when this call locked it.
    /// </summary>
    public bool Lock(Guid applicationId, DateTimeOffset now)
    {
        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException("An application id is required to lock a resume version.", nameof(applicationId));
        }

        if (IsLocked)
        {
            return false;
        }

        LockedAt = now;
        LockedByApplicationId = applicationId;
        return true;
    }

    /// <summary>Changes a draft in place; throws <see cref="ResumeVersionLockedException"/> once locked (AC-5).</summary>
    public void EditDraft(string? content, string? note, ResumeFileRef? file, DateTimeOffset now)
    {
        if (IsLocked)
        {
            throw new ResumeVersionLockedException(Id);
        }

        var validContent = ResumeRules.Content(content);
        ResumeRules.ContentOrFile(validContent, file);
        Apply(validContent, ResumeRules.Note(note), file, now);
    }

    private void Apply(string content, string? note, ResumeFileRef? file, DateTimeOffset now)
    {
        Content = content;
        Note = note;
        StorageUrl = file?.StorageKey;
        FileName = file?.FileName;
        FileContentType = file?.ContentType;
        FileSizeBytes = file?.SizeBytes;
        UpdatedAt = now;
    }
}
