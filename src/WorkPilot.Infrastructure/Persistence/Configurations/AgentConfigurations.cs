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
        builder.Property(e => e.Goal).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();

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
