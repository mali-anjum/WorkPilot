namespace WorkPilot.Contracts.Approvals;

/// <summary>Response body for <c>GET /internal/approvals</c>: the Approval center view for one profile (spec 0007, AC-4).</summary>
/// <param name="Pending">Every pending approval, oldest first.</param>
/// <param name="RecentlyDecided">The most recently decided approvals, newest first.</param>
public sealed record ApprovalCenterDto(IReadOnlyList<PendingApprovalDto> Pending, IReadOnlyList<DecidedApprovalDto> RecentlyDecided);

/// <summary>One pending approval with the evidence the founder needs to decide.</summary>
/// <param name="ApprovalId">The approval to decide.</param>
/// <param name="RiskTier"><c>ApprovalRequired</c> or <c>ExplicitConfirmation</c>.</param>
/// <param name="RequestedAt">When the run suspended.</param>
/// <param name="AgentRunId">The run waiting on this decision.</param>
/// <param name="Goal">The run's goal.</param>
/// <param name="StepOrdinal">The gated step's position in the plan (zero based).</param>
/// <param name="ToolName">The tool that will execute on Approve.</param>
/// <param name="ToolDescription">The tool's own description, from the Tool Registry.</param>
/// <param name="Arguments">The exact inputs the tool will execute with.</param>
/// <param name="Evidence">The frozen evidence snapshot, or null when the tool described none.</param>
/// <param name="ConfirmationPhrase">The phrase to type on Approve, only for the explicit confirmation tier.</param>
public sealed record PendingApprovalDto(
    Guid ApprovalId,
    string RiskTier,
    DateTimeOffset RequestedAt,
    Guid AgentRunId,
    string Goal,
    int StepOrdinal,
    string ToolName,
    string ToolDescription,
    IReadOnlyDictionary<string, string> Arguments,
    ApprovalEvidenceDto? Evidence,
    string? ConfirmationPhrase);

/// <summary>The evidence snapshot, as shown in the Approval center.</summary>
public sealed record ApprovalEvidenceDto(string Summary, ApprovalTargetDto? Target, IReadOnlyList<DocumentVersionDto> Documents);

/// <summary>What the action acts on.</summary>
public sealed record ApprovalTargetDto(string Type, Guid? Id, string Label);

/// <summary>One immutable document version the action involves.</summary>
public sealed record DocumentVersionDto(string Kind, Guid VersionId, string Name, int VersionNumber, DateTimeOffset CreatedAt);

/// <summary>One decided approval, for the recent decisions list.</summary>
public sealed record DecidedApprovalDto(
    Guid ApprovalId,
    string ToolName,
    string Goal,
    string RiskTier,
    string Status,
    DateTimeOffset? DecidedAt,
    Guid? DecidedBy,
    bool ExplicitlyConfirmed);

/// <summary>Request body for <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
/// <param name="Decision"><c>Approve</c> or <c>Reject</c>.</param>
/// <param name="DecidedBy">The deciding profile; must own the run.</param>
/// <param name="Confirmation">The typed confirmation phrase; required only to approve an explicit confirmation tier action.</param>
public sealed record DecideApprovalRequest(string Decision, Guid DecidedBy, string? Confirmation = null);

/// <summary>Response body for <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
public sealed record DecideApprovalResponse(Guid ApprovalId, string Status, bool Resumed);
