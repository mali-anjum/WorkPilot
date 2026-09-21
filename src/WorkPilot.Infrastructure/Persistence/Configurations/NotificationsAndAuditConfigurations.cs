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
        builder.HasIndex(e => e.OccurredAt);
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb");
    }
}
