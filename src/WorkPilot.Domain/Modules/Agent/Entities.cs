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

/// <summary>docs/specs/0005-agent-orchestrator-core, State transitions.</summary>
public enum AgentRunStatus
{
    Planning,
    PolicyCheck,
    Executing,
    AwaitingApproval,
    Completed,
    Failed,
}

/// <summary>
/// The agent's own semantic record of one run: what it decided and called,
/// distinct from the workflow's durability bookkeeping (spec 0002). 1:1 with
/// the <see cref="WorkflowInstance"/> actually executing it, whose Status is
/// mirrored from this one (spec 0005).
/// </summary>
public class AgentRun : Entity
{
    private static readonly Dictionary<AgentRunStatus, AgentRunStatus[]> ValidTransitions = new()
    {
        [AgentRunStatus.Planning] = [AgentRunStatus.PolicyCheck, AgentRunStatus.Failed],
        [AgentRunStatus.PolicyCheck] = [AgentRunStatus.Executing, AgentRunStatus.Failed],
        [AgentRunStatus.Executing] = [AgentRunStatus.AwaitingApproval, AgentRunStatus.Completed, AgentRunStatus.Failed],
        [AgentRunStatus.AwaitingApproval] = [AgentRunStatus.Executing, AgentRunStatus.Failed],
        [AgentRunStatus.Completed] = [],
        [AgentRunStatus.Failed] = [],
    };

    public required Guid WorkflowInstanceId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string Goal { get; set; }
    public AgentRunStatus Status { get; private set; } = AgentRunStatus.Planning;

    public List<AgentStep> Steps { get; init; } = [];

    /// <summary>Moves to <paramref name="next"/>, or throws if that transition is not allowed from the current status.</summary>
    public void TransitionTo(AgentRunStatus next)
    {
        if (!ValidTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidOperationException($"Cannot transition an agent run from {Status} to {next}.");
        }

        Status = next;
    }
}

/// <summary>docs/specs/0005-agent-orchestrator-core, State transitions.</summary>
public enum AgentStepStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>
/// One planned step of an <see cref="AgentRun"/>: which tool, with what
/// arguments, and where it stands. High volume; hard deleted after the
/// retention window, reasoning offloaded to Storage when large (spec 0002,
/// AC-8). <see cref="Ordinal"/>/<see cref="ToolName"/>/<see cref="ArgumentsJson"/>
/// and <see cref="Status"/> were added in spec 0005 to actually hold the plan
/// and drive restart-safe resumption.
/// </summary>
public class AgentStep : Entity
{
    private static readonly Dictionary<AgentStepStatus, AgentStepStatus[]> ValidTransitions = new()
    {
        [AgentStepStatus.Pending] = [AgentStepStatus.Running, AgentStepStatus.AwaitingApproval],
        [AgentStepStatus.AwaitingApproval] = [AgentStepStatus.Running, AgentStepStatus.Skipped],
        [AgentStepStatus.Running] = [AgentStepStatus.Succeeded, AgentStepStatus.Failed],
        [AgentStepStatus.Succeeded] = [],
        [AgentStepStatus.Failed] = [],
        [AgentStepStatus.Skipped] = [],
    };

    public required Guid AgentRunId { get; init; }
    public required int Ordinal { get; init; }
    public required string ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? ReasoningUrl { get; set; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public AgentStepStatus Status { get; private set; } = AgentStepStatus.Pending;

    public List<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Moves to <paramref name="next"/>, or throws if that transition is not allowed from the current status.</summary>
    public void TransitionTo(AgentStepStatus next)
    {
        if (!ValidTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidOperationException($"Cannot transition an agent step from {Status} to {next}.");
        }

        Status = next;
    }
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
