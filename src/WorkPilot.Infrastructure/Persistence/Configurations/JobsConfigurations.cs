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
        builder.Property(e => e.Config).HasColumnType("jsonb");
    }
}

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.JobSourceId, e.ExternalId }).IsUnique();
        builder.Property(e => e.Title).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Company).HasMaxLength(200).IsRequired();

        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);

        builder.HasMany(e => e.Snapshots).WithOne().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
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
