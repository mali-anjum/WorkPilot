using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("profiles");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.AuthUserId).IsUnique();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();

        builder.HasMany(e => e.Experiences).WithOne().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Education).WithOne().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Skills).WithMany().UsingEntity<ProfileSkill>(
            j => j.HasOne<Skill>().WithMany().HasForeignKey(ps => ps.SkillId),
            j => j.HasOne<Profile>().WithMany().HasForeignKey(ps => ps.ProfileId),
            j =>
            {
                j.HasKey(ps => new { ps.ProfileId, ps.SkillId });
                j.ToTable("profile_skills");
            });
    }
}

public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("skills");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
    }
}

public class ExperienceConfiguration : IEntityTypeConfiguration<Experience>
{
    public void Configure(EntityTypeBuilder<Experience> builder)
    {
        builder.ToTable("experiences");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Company).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
    }
}

public class EducationConfiguration : IEntityTypeConfiguration<Education>
{
    public void Configure(EntityTypeBuilder<Education> builder)
    {
        builder.ToTable("education");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Institution).HasMaxLength(200).IsRequired();
    }
}

public class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    public void Configure(EntityTypeBuilder<Resume> builder)
    {
        builder.ToTable("resumes");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.HasMany(e => e.Versions).WithOne().HasForeignKey(v => v.ResumeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ResumeVersionConfiguration : IEntityTypeConfiguration<ResumeVersion>
{
    public void Configure(EntityTypeBuilder<ResumeVersion> builder)
    {
        builder.ToTable("resume_versions");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.ResumeId, e.VersionNumber }).IsUnique();
        builder.Property(e => e.StorageUrl).IsRequired();
        builder.Property(e => e.ParsedContent).HasColumnType("jsonb");
    }
}

public class CoverLetterConfiguration : IEntityTypeConfiguration<CoverLetter>
{
    public void Configure(EntityTypeBuilder<CoverLetter> builder)
    {
        builder.ToTable("cover_letters");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.HasMany(e => e.Versions).WithOne().HasForeignKey(v => v.CoverLetterId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CoverLetterVersionConfiguration : IEntityTypeConfiguration<CoverLetterVersion>
{
    public void Configure(EntityTypeBuilder<CoverLetterVersion> builder)
    {
        builder.ToTable("cover_letter_versions");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.CoverLetterId, e.VersionNumber }).IsUnique();
        builder.Property(e => e.StorageUrl).IsRequired();
    }
}
