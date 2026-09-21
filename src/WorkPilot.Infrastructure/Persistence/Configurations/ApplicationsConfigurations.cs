using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Applications;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> builder)
    {
        builder.ToTable("job_applications");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.HasMany(e => e.Answers).WithOne().HasForeignKey(a => a.JobApplicationId).OnDelete(DeleteBehavior.Cascade);
        // ApplicationEvent is append only history: it must outlive the application row it describes.
        builder.HasMany(e => e.Events).WithOne().HasForeignKey(a => a.JobApplicationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApplicationAnswerConfiguration : IEntityTypeConfiguration<ApplicationAnswer>
{
    public void Configure(EntityTypeBuilder<ApplicationAnswer> builder)
    {
        builder.ToTable("application_answers");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Question).IsRequired();
        builder.Property(e => e.Answer).IsRequired();
    }
}

public class ApplicationEventConfiguration : IEntityTypeConfiguration<ApplicationEvent>
{
    public void Configure(EntityTypeBuilder<ApplicationEvent> builder)
    {
        builder.ToTable("application_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb");
    }
}
