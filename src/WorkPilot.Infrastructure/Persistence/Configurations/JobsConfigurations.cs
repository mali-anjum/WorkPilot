using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;

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

        // The Newest sort of the jobs list (spec 0021, AC-1).
        builder.HasIndex(e => e.PostedAt).IsDescending().HasFilter("\"IsDeleted\" = false").HasDatabaseName("IX_jobs_PostedAt_listed");

        // PrimaryLinkId is a foreign key to job_source_links, but it is added by
        // hand in AddJobDeduplication as DEFERRABLE INITIALLY DEFERRED: a new job
        // and its first link point at each other, a cycle EF Core can't order
        // inside one SaveChanges. Keep that SQL if the migration is regenerated.
        builder.Property(e => e.PrimaryLinkId);

        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);

        // Postgres's own system column as a concurrency token (as on agent_runs):
        // a split, an ingestion and a reconcile touching one job can't silently
        // overwrite each other; the loser retries its whole unit on fresh data
        // (JobRepository.InTransactionAsync, spec 0017).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

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
        builder.ToTable("job_matches", t =>
            t.HasCheckConstraint("ck_job_matches_score", "\"Score\" IS NULL OR \"Score\" BETWEEN 0 AND 100"));
        builder.HasKey(e => e.Id);

        // One match per (job, profile); the scoring upsert's ON CONFLICT target (spec 0019).
        builder.HasIndex(e => new { e.JobId, e.ProfileId }).IsUnique();

        // The /jobs list for one profile: unblocked first, best score first (AC-9).
        builder.HasIndex(e => new { e.ProfileId, e.HasBlocker, e.Score }).IsDescending(false, false, true);

        builder.Property(e => e.Confidence).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Explanation).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.InputsFingerprint).HasMaxLength(64).IsRequired();
        builder.HasOne<Profile>().WithMany().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class JobRequirementConfiguration : IEntityTypeConfiguration<JobRequirement>
{
    public void Configure(EntityTypeBuilder<JobRequirement> builder)
    {
        builder.ToTable("job_requirements");
        builder.HasKey(e => e.Id);

        // One row per job; it goes with the job when a merge hard deletes it (spec 0019, AC-14).
        builder.HasIndex(e => e.JobId).IsUnique();
        builder.HasOne<Job>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.ContentHash).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.Requirements).HasColumnType("jsonb");
        builder.Property(e => e.Model).HasMaxLength(200);
        builder.Property(e => e.FailureReason).HasMaxLength(JobRequirement.MaxFailureReasonLength);
    }
}

public class JobDismissalConfiguration : IEntityTypeConfiguration<JobDismissal>
{
    public void Configure(EntityTypeBuilder<JobDismissal> builder)
    {
        // One dismissal per (profile, job) (spec 0021); only the Jobs module writes it.
        builder.ToTable("job_dismissals");
        builder.HasKey(e => new { e.ProfileId, e.JobId });
        builder.HasIndex(e => e.JobId);
        builder.HasOne<Profile>().WithMany().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Job>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}
