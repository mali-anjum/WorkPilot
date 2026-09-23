using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Agent;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class WorkflowInstanceConfiguration : IEntityTypeConfiguration<WorkflowInstance>
{
    public void Configure(EntityTypeBuilder<WorkflowInstance> builder)
    {
        builder.ToTable("workflow_instances");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DefinitionName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}

public class WorkflowStepConfiguration : IEntityTypeConfiguration<WorkflowStep>
{
    public void Configure(EntityTypeBuilder<WorkflowStep> builder)
    {
        builder.ToTable("workflow_steps");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.WorkflowInstanceId);
        builder.Property(e => e.StepName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}

public class WorkflowEventConfiguration : IEntityTypeConfiguration<WorkflowEvent>
{
    public void Configure(EntityTypeBuilder<WorkflowEvent> builder)
    {
        builder.ToTable("workflow_events");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.OccurredAt);
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
    }
}

public class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable("agent_runs");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.WorkflowInstanceId).IsUnique();
        builder.HasIndex(e => e.ProfileId);
        builder.Property(e => e.ProfileId).IsRequired();
        builder.Property(e => e.Goal).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30).IsRequired();

        // Postgres's own system column, mapped as a shadow property (not a new
        // stored property on the entity): guards a step-advance job and a
        // concurrent approval decision on the same run from a lost update
        // (spec 0005, AC-10).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(e => e.Steps).WithOne().HasForeignKey(s => s.AgentRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AgentStepConfiguration : IEntityTypeConfiguration<AgentStep>
{
    public void Configure(EntityTypeBuilder<AgentStep> builder)
    {
        builder.ToTable("agent_steps");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => new { e.AgentRunId, e.Ordinal }).IsUnique();
        builder.Property(e => e.Ordinal).IsRequired();
        builder.Property(e => e.ToolName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ArgumentsJson).HasColumnType("jsonb");
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.HasMany(e => e.ToolCalls).WithOne().HasForeignKey(t => t.AgentStepId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ToolCallConfiguration : IEntityTypeConfiguration<ToolCall>
{
    public void Configure(EntityTypeBuilder<ToolCall> builder)
    {
        builder.ToTable("tool_calls");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ToolName).HasMaxLength(200).IsRequired();
    }
}
