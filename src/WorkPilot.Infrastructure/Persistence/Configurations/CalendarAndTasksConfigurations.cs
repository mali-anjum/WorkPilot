using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Calendar;
using WorkPilot.Domain.Modules.Tasks;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.ToTable("calendar_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Title).HasMaxLength(300).IsRequired();
    }
}

public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("tasks");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Title).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}
