using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <summary><c>app.resumes</c> (spec 0002, extended by spec 0009).</summary>
public class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    public void Configure(EntityTypeBuilder<Resume> builder)
    {
        builder.ToTable("resumes");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(ResumeRules.NameMaxLength).IsRequired();
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.TargetCompany).HasMaxLength(ResumeRules.TargetCompanyMaxLength);
        builder.HasIndex(e => e.ProfileId);

        builder.HasMany(e => e.Versions).WithOne().HasForeignKey(v => v.ResumeId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        // A tailored resume remembers the version it was copied from; that version can never be
        // removed out from under it (locked rows can't be deleted anyway, see the trigger).
        builder.HasOne<ResumeVersion>().WithMany().HasForeignKey(e => e.SourceVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// <c>app.resume_versions</c>. A row whose <c>LockedAt</c> is set is also guarded by the
/// <c>resume_versions_block_locked_changes</c> trigger (migration <c>AddResumeManagement</c>, spec 0009 AC-5).
/// </summary>
public class ResumeVersionConfiguration : IEntityTypeConfiguration<ResumeVersion>
{
    public void Configure(EntityTypeBuilder<ResumeVersion> builder)
    {
        builder.ToTable("resume_versions");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.ResumeId, e.VersionNumber }).IsUnique();
        builder.Property(e => e.Content).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(ResumeRules.NoteMaxLength);
        builder.Property(e => e.StorageUrl);
        builder.Property(e => e.FileName).HasMaxLength(ResumeRules.FileNameMaxLength);
        builder.Property(e => e.FileContentType).HasMaxLength(100);
        builder.Property(e => e.ParsedContent).HasColumnType("jsonb");
        builder.Ignore(e => e.File);
        builder.Ignore(e => e.IsLocked);
    }
}

/// <summary><c>app.resume_files</c>, the Postgres backed file store's table (spec 0009).</summary>
public class StoredResumeFileConfiguration : IEntityTypeConfiguration<StoredResumeFile>
{
    public void Configure(EntityTypeBuilder<StoredResumeFile> builder)
    {
        builder.ToTable("resume_files");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Data).HasColumnType("bytea").IsRequired();
        builder.Property(e => e.FileName).HasMaxLength(ResumeRules.FileNameMaxLength).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Sha256).HasMaxLength(64).IsRequired();
    }
}
