using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Outreach;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class OutreachContactConfiguration : IEntityTypeConfiguration<OutreachContact>
{
    public void Configure(EntityTypeBuilder<OutreachContact> builder)
    {
        builder.ToTable("outreach_contacts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Email).HasMaxLength(320).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();

        builder.HasMany(e => e.Messages).WithOne().HasForeignKey(m => m.OutreachContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class OutreachMessageConfiguration : IEntityTypeConfiguration<OutreachMessage>
{
    public void Configure(EntityTypeBuilder<OutreachMessage> builder)
    {
        builder.ToTable("outreach_messages");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Subject).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();

        builder.HasOne(e => e.Thread).WithOne().HasForeignKey<EmailThread>(t => t.OutreachMessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.FollowUps).WithOne().HasForeignKey(f => f.OutreachMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class EmailThreadConfiguration : IEntityTypeConfiguration<EmailThread>
{
    public void Configure(EntityTypeBuilder<EmailThread> builder)
    {
        builder.ToTable("email_threads");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.GmailThreadId).IsUnique();
        builder.Property(e => e.GmailThreadId).IsRequired();
    }
}

public class FollowUpConfiguration : IEntityTypeConfiguration<FollowUp>
{
    public void Configure(EntityTypeBuilder<FollowUp> builder)
    {
        builder.ToTable("follow_ups");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}
