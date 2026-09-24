using System.Text.Json;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent.Tools;

/// <summary>
/// Inert stand in for a destructive action (delete data, a permission
/// change), so the explicit confirmation tier can be proven end to end
/// (spec 0007): it suspends like any approval gated tool, and approving it
/// needs the typed confirmation phrase. Deletes nothing.
/// </summary>
public sealed class ExplicitConfirmationDemoTool(WorkPilotDbContext db) : ITool, IApprovalEvidenceProvider
{
    public string Name => "explicit_confirmation_demo";
    public string Description => "A harmless demo of a destructive action that needs an explicit, typed confirmation before it runs. Takes no arguments.";
    public IReadOnlyList<string> RequiredArguments => [];
    public IReadOnlyList<string> ExpectedOutputFields => ["acknowledged"];
    public ToolRiskTier RiskTier => ToolRiskTier.ExplicitConfirmation;
    public bool IsIdempotent => true;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public int MaxRetries => 0;
    public string? TargetType => null;

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { acknowledged = true }, JsonSerializerOptions.Web);
        return Task.FromResult(ToolExecutionResult.Ok(json));
    }

    public async Task<ApprovalEvidence> DescribeForApprovalAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
        new(
            "A harmless demo that stands in for deleting data from your profile. Nothing is deleted.",
            await DemoToolEvidence.ProfileTargetAsync(db, context.ProfileId, cancellationToken),
            []);
}
