using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Audit;

namespace WorkPilot.Infrastructure.Modules.Audit.Persistence;

/// <summary><c>app.outbox_messages</c> (spec 0018, section 3), owned by Audit.</summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();

        // The dispatcher's claim query reads only undispatched rows, oldest first.
        builder.HasIndex(e => e.OccurredAt)
            .HasFilter("\"DispatchedAt\" IS NULL")
            .HasDatabaseName("IX_outbox_messages_undispatched");
    }
}

/// <summary><c>app.outbox_deliveries</c> (spec 0018, section 3): one row per handled (message, handler).</summary>
public class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> builder)
    {
        builder.ToTable("outbox_deliveries");
        builder.HasKey(e => new { e.MessageId, e.HandlerKey });
        builder.Property(e => e.HandlerKey).HasMaxLength(200);

        // The retention sweep deletes old messages; their delivery records go with them.
        builder.HasOne<OutboxMessage>()
            .WithMany()
            .HasForeignKey(e => e.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
