using System.Text.Json;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent.Tools;

/// <summary>
/// Deliberately approval-gated and otherwise inert: exists only to prove the
/// suspend/resume path end to end (spec 0005's milestone tool 2), not real
/// domain value. A real approval-required tool (send email, submit an
/// application) is later, per-domain-agent work. Describes evidence for the
/// Approval center (spec 0007): the calling profile as its target and the
/// newest resume and cover letter versions, when present.
/// </summary>
public sealed class ApprovalRequiredDemoTool(WorkPilotDbContext db) : ITool, IApprovalEvidenceProvider
{
    public string Name => "approval_required_demo";
    public string Description => "A harmless demo action that always needs approval before it runs. Takes no arguments.";
    public IReadOnlyList<string> RequiredArguments => [];
    public IReadOnlyList<string> ExpectedOutputFields => ["acknowledged"];
    public ToolRiskTier RiskTier => ToolRiskTier.ApprovalRequired;
    public bool IsIdempotent => true;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    // Retrying an approved side effect risks a double execution (spec 0005): approval-required tools default to 0 retries.
    public int MaxRetries => 0;
    public string? TargetType => null;

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { acknowledged = true }, JsonSerializerOptions.Web);
        return Task.FromResult(ToolExecutionResult.Ok(json));
    }

    public async Task<ApprovalEvidence> DescribeForApprovalAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
        new(
            "A harmless demo action that stands in for sending an application with your documents. Nothing is sent.",
            await DemoToolEvidence.ProfileTargetAsync(db, context.ProfileId, cancellationToken),
            await DemoToolEvidence.LatestDocumentsAsync(db, context.ProfileId, cancellationToken));
}
