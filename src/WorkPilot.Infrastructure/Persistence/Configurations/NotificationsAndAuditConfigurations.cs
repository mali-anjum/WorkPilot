using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Domain.Modules.Notifications;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb");
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.Property(e => e.Category).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The activity feed's keyset pages (spec 0011): newest first, all rows or one category.
        builder.HasIndex(e => new { e.OccurredAt, e.Id })
            .IsDescending(true, true)
            .HasFilter("\"IsDeleted\" = false")
            .HasDatabaseName("IX_audit_logs_feed");
        builder.HasIndex(e => new { e.Category, e.OccurredAt, e.Id })
            .IsDescending(false, true, true)
            .HasFilter("\"IsDeleted\" = false")
            .HasDatabaseName("IX_audit_logs_feed_category");
    }
}
