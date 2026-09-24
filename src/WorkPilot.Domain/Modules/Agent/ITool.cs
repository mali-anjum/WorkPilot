using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Domain.Modules.Agent;

/// <summary>The three tier policy from the product spec, baked into each tool's own declaration (spec 0005).</summary>
public enum ToolRiskTier
{
    /// <summary>Read/search/analyze/classify/dedupe/generate/prepare/monitor/detect/suggest: runs without pausing.</summary>
    AutoAllowed,

    /// <summary>Send email, submit/withdraw an application, connect an external account: suspends the run for a decision.</summary>
    ApprovalRequired,

    /// <summary>Delete data, security/permission changes, destructive ops: suspends like <see cref="ApprovalRequired"/>, and an Approve also needs the typed confirmation phrase (spec 0007).</summary>
    ExplicitConfirmation,
}

/// <summary>
/// Optional companion to <see cref="ITool"/> for approval gated tools: describes
/// what the action will do, its target, and the document versions it involves,
/// so the Approval center can show evidence (spec 0007, AC-3). Called once,
/// when the run suspends, with the same context the tool will execute with;
/// the result is frozen onto the approval. A tool that uses a document should
/// take its version id as an argument (pinned), so the version shown here is
/// the version that executes.
/// </summary>
public interface IApprovalEvidenceProvider
{
    Task<ApprovalEvidence> DescribeForApprovalAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>What a tool needs to execute: the calling profile and the plan's arguments for this step.</summary>
public sealed record ToolExecutionContext(Guid ProfileId, IReadOnlyDictionary<string, string> Arguments);

/// <summary>A tool's own outcome. <see cref="TargetId"/> is the entity the tool actually acted on, when it has one.</summary>
public sealed record ToolExecutionResult(bool Success, string? OutputJson, Guid? TargetId, string? Error = null)
{
    public static ToolExecutionResult Ok(string? outputJson = null, Guid? targetId = null) => new(true, outputJson, targetId);

    public static ToolExecutionResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// One action the orchestrator can plan and execute. Every load bearing
/// property (risk tier, approval requirement, idempotency, timeout, retry)
/// is baked into the declaration itself, not runtime configurable (spec
/// 0005's confirmed choice): there is no product need yet to change a tool's
/// risk tier without a code review.
/// </summary>
public interface ITool
{
    /// <summary>The name the Planner and the plan itself refer to this tool by.</summary>
    string Name { get; }

    /// <summary>Shown to the Planner's LLM call so it knows when and how to use this tool.</summary>
    string Description { get; }

    /// <summary>Argument names the Policy Engine requires present on any planned call to this tool (AC-2).</summary>
    IReadOnlyList<string> RequiredArguments { get; }

    /// <summary>Output JSON field names the Verification Engine checks are present on a successful call (AC-3).</summary>
    IReadOnlyList<string> ExpectedOutputFields { get; }

    ToolRiskTier RiskTier { get; }

    /// <summary>Whether a step for this tool found <see cref="AgentStepStatus.Running"/> on restart may be safely re-executed (AC-9).</summary>
    bool IsIdempotent { get; }

    TimeSpan Timeout { get; }

    /// <summary>Retries after a failed attempt, not counting the first. An approval-required tool should declare 0 (spec 0005: retrying a side effect a human already approved once risks a double execution).</summary>
    int MaxRetries { get; }

    /// <summary>What kind of entity this tool acts on, for the audit trail; falls back to the step itself when a tool has no single target.</summary>
    string? TargetType { get; }

    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}
