using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Domain.Modules.Notifications;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(Notification.TitleMaxLength).IsRequired();
        builder.Property(e => e.Body).HasMaxLength(Notification.BodyMaxLength);
        builder.Property(e => e.Link).HasMaxLength(Notification.LinkMaxLength);
        builder.Property(e => e.GroupKey).HasMaxLength(200);
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.HasOne<Profile>().WithMany().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Cascade);

        // Spec 0020: the list (newest first), the bell's unread count, and at most one open digest
        // per (profile, day).
        builder.HasIndex(e => new { e.ProfileId, e.CreatedAt })
            .IsDescending(false, true)
            .HasFilter("\"IsDeleted\" = false")
            .HasDatabaseName("IX_notifications_list");
        builder.HasIndex(e => e.ProfileId)
            .HasFilter("\"ReadAt\" IS NULL AND \"IsDeleted\" = false")
            .HasDatabaseName("IX_notifications_unread");
        builder.HasIndex(e => new { e.ProfileId, e.GroupKey })
            .IsUnique()
            .HasFilter("\"GroupKey\" IS NOT NULL AND \"ReadAt\" IS NULL AND \"IsDeleted\" = false")
            .HasDatabaseName("UX_notifications_open_digest");
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
