using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    public void Configure(EntityTypeBuilder<Approval> builder)
    {
        builder.ToTable("approvals");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.TargetType, e.TargetId });
        builder.Property(e => e.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.RiskTier).HasMaxLength(30).IsRequired();

        // A concurrency token (spec 0007, AC-9): every UPDATE EF issues for a
        // decided approval carries "WHERE Status = <the status it was loaded
        // with>", so two concurrent decisions can't both win. The loser gets a
        // DbUpdateConcurrencyException and its whole SaveChanges (step/run
        // transitions, audit row) rolls back with it.
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30).IsRequired().IsConcurrencyToken();

        // Spec 0007: the frozen evidence snapshot, when it was requested, and
        // whether an explicit confirmation tier approval was confirmed.
        builder.Property(e => e.EvidenceJson).HasColumnType("jsonb");
        builder.Property(e => e.RequestedAt).HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.ExplicitlyConfirmed).HasDefaultValue(false).IsRequired();
        builder.Ignore(e => e.Tier);

        // The Approval center's pending list (Status = 'Pending', oldest first).
        builder.HasIndex(e => new { e.Status, e.RequestedAt });
    }
}
