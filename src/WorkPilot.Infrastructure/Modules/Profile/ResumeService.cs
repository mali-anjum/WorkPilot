using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <inheritdoc cref="IResumeService" />
public sealed class ResumeService(WorkPilotDbContext db, IResumeFileStore files, TimeProvider clock) : IResumeService
{
    /// <summary>The SQLSTATE the <c>resume_versions_block_locked_changes</c> trigger raises (spec 0009, AC-5).</summary>
    public const string LockedVersionSqlState = "WP409";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var rows = await db.Resumes
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Kind,
                r.TargetCompany,
                VersionCount = r.Versions.Count,
                Latest = r.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new { v.VersionNumber, v.LockedAt, v.UpdatedAt })
                    .First(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new ResumeSummaryDto(
                r.Id,
                r.Name,
                r.Kind.ToString(),
                r.TargetCompany,
                r.Latest.VersionNumber,
                r.Latest.LockedAt is not null,
                r.VersionCount,
                r.Latest.UpdatedAt))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadAsync(profileId, resumeId, tracked: false, cancellationToken);
        return resume is null ? null : ToDetail(resume);
    }

    /// <inheritdoc />
    public async Task<ResumeResult<ResumeDetailDto>> CreateAsync(CreateResumeCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Profiles.AnyAsync(p => p.Id == command.ProfileId, cancellationToken))
        {
            return ResumeResult<ResumeDetailDto>.NotFound();
        }

        try
        {
            var file = await StoreFileAsync(command.File, cancellationToken);
            var resume = Resume.CreateBase(command.ProfileId, command.Name ?? string.Empty, command.Content, command.Note, file, clock.GetUtcNow());
            db.Resumes.Add(resume);
            await db.SaveChangesAsync(cancellationToken);
            return ResumeResult<ResumeDetailDto>.Ok(ToDetail(resume));
        }
        catch (ResumeValidationException ex)
        {
            return ResumeResult<ResumeDetailDto>.Invalid(ex.Field, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResumeResult<ResumeDetailDto>> TailorAsync(TailorResumeCommand command, CancellationToken cancellationToken)
    {
        var source = await db.ResumeVersions
            .AsNoTracking()
            .Where(v => v.Id == command.SourceVersionId)
            .Join(
                db.Resumes.Where(r => r.ProfileId == command.ProfileId),
                v => v.ResumeId,
                r => r.Id,
                (v, r) => new { Version = v, ResumeName = r.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            return ResumeResult<ResumeDetailDto>.NotFound();
        }

        try
        {
            var note = command.Note;
            if (string.IsNullOrWhiteSpace(note))
            {
                note = $"Tailored for {command.TargetCompany?.Trim()} from {source.ResumeName} v{source.Version.VersionNumber}";
                if (note.Length > ResumeRules.NoteMaxLength)
                {
                    note = note[..ResumeRules.NoteMaxLength];
                }
            }

            var resume = Resume.CreateTailored(
                command.ProfileId,
                command.Name ?? string.Empty,
                command.TargetCompany ?? string.Empty,
                source.Version,
                note,
                clock.GetUtcNow());
            db.Resumes.Add(resume);
            await db.SaveChangesAsync(cancellationToken);
            return ResumeResult<ResumeDetailDto>.Ok(ToDetail(resume));
        }
        catch (ResumeValidationException ex)
        {
            return ResumeResult<ResumeDetailDto>.Invalid(ex.Field, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResumeResult<ReviseResumeResultDto>> ReviseAsync(ReviseResumeCommand command, CancellationToken cancellationToken)
    {
        var resume = await LoadAsync(command.ProfileId, command.ResumeId, tracked: true, cancellationToken);
        if (resume is null)
        {
            return ResumeResult<ReviseResumeResultDto>.NotFound();
        }

        try
        {
            var file = await StoreFileAsync(command.File, cancellationToken);
            var result = resume.Revise(command.Content, command.Note, file, command.RemoveFile, clock.GetUtcNow());

            if (result.Outcome == ReviseOutcome.CreatedVersion)
            {
                // Explicit, rather than relying on DetectChanges to classify a new child with a preset key.
                db.ResumeVersions.Add(result.Version);
            }

            if (result.Outcome != ReviseOutcome.Unchanged)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return ResumeResult<ReviseResumeResultDto>.Ok(
                new ReviseResumeResultDto(result.Outcome.ToString(), result.Version.VersionNumber, ToDetail(resume)));
        }
        catch (ResumeValidationException ex)
        {
            return ResumeResult<ReviseResumeResultDto>.Invalid(ex.Field, ex.Message);
        }
        catch (DbUpdateException ex) when (IsConflict(ex))
        {
            // Lost a race: an application locked the draft (trigger) or another edit appended the
            // same version number (unique index) between our read and our write (AC-5).
            return ResumeResult<ReviseResumeResultDto>.Conflict(
                "The resume changed while you were editing (its newest version was locked or replaced). Reload and try again.");
        }
    }

    /// <inheritdoc />
    public async Task<ResumeResult<ResumeVersionDto>> LockVersionAsync(Guid profileId, Guid versionId, Guid applicationId, CancellationToken cancellationToken)
    {
        if (applicationId == Guid.Empty)
        {
            return ResumeResult<ResumeVersionDto>.Invalid("applicationId", "An application id is required to lock a resume version.");
        }

        var version = await OwnedVersions(profileId).FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version is null)
        {
            return ResumeResult<ResumeVersionDto>.NotFound();
        }

        if (version.Lock(applicationId, clock.GetUtcNow()))
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsConflict(ex))
            {
                // Someone else locked it first; the first lock wins (AC-3), so report the stored state.
                db.ChangeTracker.Clear();
                var stored = await OwnedVersions(profileId).AsNoTracking().FirstAsync(v => v.Id == versionId, cancellationToken);
                return ResumeResult<ResumeVersionDto>.Ok(ToDto(stored));
            }
        }

        return ResumeResult<ResumeVersionDto>.Ok(ToDto(version));
    }

    /// <inheritdoc />
    public async Task<ResumeFileDownload?> GetFileAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await OwnedVersions(profileId).AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version?.File is not { } file)
        {
            return null;
        }

        var bytes = await files.ReadAsync(file.StorageKey, cancellationToken);
        return bytes is null ? null : new ResumeFileDownload(file.FileName, file.ContentType, bytes);
    }

    private IQueryable<ResumeVersion> OwnedVersions(Guid profileId) =>
        db.ResumeVersions.Where(v => db.Resumes.Any(r => r.Id == v.ResumeId && r.ProfileId == profileId));

    private async Task<Resume?> LoadAsync(Guid profileId, Guid resumeId, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.Resumes.Include(r => r.Versions).Where(r => r.Id == resumeId && r.ProfileId == profileId);
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ResumeFileRef?> StoreFileAsync(UploadedResumeFile? upload, CancellationToken cancellationToken)
    {
        if (upload is null)
        {
            return null;
        }

        // Validate before storing so a rejected file is never written (AC-8).
        var contentType = ResumeRules.ValidateFile(upload.FileName, upload.Length);
        var fileName = Path.GetFileName(upload.FileName).Trim();
        var key = await files.SaveAsync(upload.Content, fileName, contentType, cancellationToken);
        return new ResumeFileRef(key, fileName, contentType, upload.Length);
    }

    private static bool IsConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: LockedVersionSqlState or PostgresErrorCodes.UniqueViolation };

    private static ResumeDetailDto ToDetail(Resume resume) => new(
        resume.Id,
        resume.Name,
        resume.Kind.ToString(),
        resume.TargetCompany,
        resume.SourceVersionId,
        resume.CreatedAt,
        resume.Versions.OrderByDescending(v => v.VersionNumber).Select(ToDto).ToList());

    private static ResumeVersionDto ToDto(ResumeVersion v) => new(
        v.Id,
        v.ResumeId,
        v.VersionNumber,
        v.Content,
        v.Note,
        v.FileName,
        v.FileContentType,
        v.FileSizeBytes,
        v.CreatedAt,
        v.UpdatedAt,
        v.IsLocked,
        v.LockedAt,
        v.LockedByApplicationId);
}
