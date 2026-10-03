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

        // Match preferences (spec 0019). Their database defaults (Any, [], true, 70) live in the
        // AddJobMatching migration, not here: a CLR default of false or 0 must still be written.
        builder.Property(e => e.RemotePreference).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.OwnsMany(e => e.PreferredLocations, b =>
        {
            b.ToJson();
            b.Property(l => l.City).HasMaxLength(MatchProfileRules.MaxCityLength);
            b.Property(l => l.Country).HasMaxLength(2);
        });
        builder.PrimitiveCollection(e => e.JobTypes).ElementType(t => t.HasConversion<string>());
        builder.PrimitiveCollection(e => e.AuthorizedCountries);
        builder.Property(e => e.MinSalary).HasColumnType("numeric(12,2)");
        builder.Property(e => e.SalaryCurrency).HasColumnType("char(3)");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_profiles_strong_match_threshold", "\"StrongMatchThreshold\" BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_profiles_salary_currency", "(\"MinSalary\" IS NULL) = (\"SalaryCurrency\" IS NULL)");
        });

        // Postgres's own system column as the match profile ETag (spec 0019, AC-11): a save with a
        // stale If-Match fails instead of overwriting another save.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

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
        builder.Property(e => e.DegreeLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
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
