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
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}
