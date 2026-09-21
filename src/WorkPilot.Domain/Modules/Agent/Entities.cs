using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Agent;

/// <summary>
/// Hangfire backed durable execution state for one long running process.
/// Generic and reusable for any workflow, not agent specific (spec 0002:
/// kept separate from <see cref="AgentRun"/> so durability and agent
/// semantics don't get conflated).
/// </summary>
public class WorkflowInstance : Entity
{
    public required string DefinitionName { get; set; }
    public required string Status { get; set; }
}

/// <summary>
/// One step of a <see cref="WorkflowInstance"/>. High volume and internal;
/// hard deleted after the configured retention window, bulky payloads live
/// in Storage with only a reference kept here (spec 0002, AC-8).
/// </summary>
public class WorkflowStep : Entity
{
    public required Guid WorkflowInstanceId { get; init; }
    public required string StepName { get; set; }
    public required string Status { get; set; }
    public string? PayloadUrl { get; set; }
}

/// <summary>Same retention and offload rule as <see cref="WorkflowStep"/>.</summary>
public class WorkflowEvent : Entity
{
    public required Guid WorkflowInstanceId { get; init; }
    public required string EventType { get; init; }
    public string? PayloadUrl { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The agent's own semantic record of one run: what it decided and called,
/// distinct from the workflow's durability bookkeeping (spec 0002). 1:1 with
/// the <see cref="WorkflowInstance"/> actually executing it.
/// </summary>
public class AgentRun : Entity
{
    public required Guid WorkflowInstanceId { get; init; }
    public required string Goal { get; set; }
    public required string Status { get; set; }

    public List<AgentStep> Steps { get; init; } = [];
}

/// <summary>High volume; hard deleted after the retention window, reasoning offloaded to Storage when large (spec 0002, AC-8).</summary>
public class AgentStep : Entity
{
    public required Guid AgentRunId { get; init; }
    public string? ReasoningUrl { get; set; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public List<ToolCall> ToolCalls { get; init; } = [];
}

/// <summary>Same retention and offload rule as <see cref="AgentStep"/>.</summary>
public class ToolCall : Entity
{
    public required Guid AgentStepId { get; init; }
    public required string ToolName { get; init; }
    public string? InputPayloadUrl { get; init; }
    public string? OutputPayloadUrl { get; init; }
    public bool Success { get; init; }
}
