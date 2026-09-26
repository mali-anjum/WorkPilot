using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class JobSourceConfiguration : IEntityTypeConfiguration<JobSource>
{
    public void Configure(EntityTypeBuilder<JobSource> builder)
    {
        builder.ToTable("job_sources");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Type).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Config).HasColumnType("jsonb");
        builder.Property(e => e.CompanyName).HasMaxLength(200);

        // One row per board per source type, found or created by the ingestion trigger (spec 0008).
        builder.HasIndex(e => new { e.Type, e.Name }).IsUnique();
    }
}

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Title).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Company).HasMaxLength(200).IsRequired();

        // The match key (spec 0017): looked up on every first sighting, never unique
        // (a split job may share it).
        builder.Property(e => e.DedupKey).HasMaxLength(64);
        builder.Property(e => e.DedupRuleVersion).HasDefaultValue(0);
        builder.HasIndex(e => e.DedupKey);

        // PrimaryLinkId is a foreign key to job_source_links, but it is added by
        // hand in AddJobDeduplication as DEFERRABLE INITIALLY DEFERRED: a new job
        // and its first link point at each other, a cycle EF Core can't order
        // inside one SaveChanges. Keep that SQL if the migration is regenerated.
        builder.Property(e => e.PrimaryLinkId);

        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);

        builder.HasMany(e => e.Links).WithOne().HasForeignKey(l => l.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Snapshots).WithOne().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class JobSourceLinkConfiguration : IEntityTypeConfiguration<JobSourceLink>
{
    public void Configure(EntityTypeBuilder<JobSourceLink> builder)
    {
        builder.ToTable("job_source_links");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.SourceUrl).IsRequired();
        builder.Property(e => e.Confidence).HasColumnType("numeric(3,2)");

        // A source's own posting id is the link's identity (spec 0017, AC-1).
        builder.HasIndex(e => new { e.JobSourceId, e.ExternalId }).IsUnique();
        builder.HasIndex(e => e.JobId);
        builder.HasOne<JobSource>().WithMany().HasForeignKey(e => e.JobSourceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class JobSnapshotConfiguration : IEntityTypeConfiguration<JobSnapshot>
{
    public void Configure(EntityTypeBuilder<JobSnapshot> builder)
    {
        builder.ToTable("job_snapshots");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.ContentHash);
        builder.Property(e => e.RawContent).IsRequired();
        builder.Property(e => e.ContentHash).HasMaxLength(128).IsRequired();

        // Each snapshot belongs to its link through (JobSourceId, ExternalId), so
        // one canonical job gathers every source's own history (specs 0008, 0017).
        builder.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
        builder.HasOne<JobSource>().WithMany().HasForeignKey(e => e.JobSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.JobSourceId, e.ExternalId });

        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);
    }
}

public class JobMatchConfiguration : IEntityTypeConfiguration<JobMatch>
{
    public void Configure(EntityTypeBuilder<JobMatch> builder)
    {
        builder.ToTable("job_matches");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.JobId, e.ProfileId }).IsUnique();
        builder.Property(e => e.MatchedSkills).HasColumnType("jsonb");
    }
}
