using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Agent;

/// <summary>
/// An agent run ended <see cref="AgentRunStatus.Failed"/> (spec 0018 event catalog). Raised at
/// every place a run becomes Failed; <see cref="Reason"/> is one of <see cref="AgentRunFailureReasons"/>.
/// </summary>
public sealed record AgentRunFailed(Guid AgentRunId, string Reason) : IDomainEvent
{
    public static string EventName => "agent.agent-run-failed.v1";
}

/// <summary>The <see cref="AgentRunFailed.Reason"/> values, so no caller hand types them.</summary>
public static class AgentRunFailureReasons
{
    /// <summary>The planner's provider failed (spec 0006, AC-6).</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The planner answered with something that is not a valid plan.</summary>
    public const string UnparseablePlan = "unparseable_plan";

    /// <summary>The Policy Engine rejected the plan.</summary>
    public const string PolicyViolation = "policy_violation";

    /// <summary>A step's tool failed, was missing, or could not safely run again.</summary>
    public const string StepFailed = "step_failed";

    /// <summary>The approval gate refused a step at execution time (spec 0007, AC-2).</summary>
    public const string ApprovalGateRefused = "approval_gate_refused";

    /// <summary>The founder rejected the step's approval.</summary>
    public const string ApprovalRejected = "approval_rejected";
}
