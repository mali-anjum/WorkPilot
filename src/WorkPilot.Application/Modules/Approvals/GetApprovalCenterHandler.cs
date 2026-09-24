using System.Text.Json;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Contracts.Approvals;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Application.Modules.Approvals;

/// <summary>
/// Builds the Approval center view for one profile (spec 0007, AC-4): its
/// pending approvals with their evidence, inputs, and (for the explicit
/// confirmation tier) the phrase to type, plus its most recent decisions.
/// </summary>
public sealed class GetApprovalCenterHandler(IApprovalRepository approvals, IToolRegistry registry)
{
    /// <summary>How many recent decisions the Approval center lists.</summary>
    public const int RecentDecisionLimit = 20;

    /// <summary>Reads the Approval center view for <paramref name="profileId"/>; never includes another profile's approvals.</summary>
    public async Task<ApprovalCenterDto> HandleAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var pending = await approvals.ListPendingAsync(profileId, cancellationToken);
        var decided = await approvals.ListRecentlyDecidedAsync(profileId, RecentDecisionLimit, cancellationToken);

        return new ApprovalCenterDto(
            pending.Select(ToDto).ToList(),
            decided.Select(d => new DecidedApprovalDto(
                d.ApprovalId, d.ToolName, d.Goal, d.RiskTier, d.Status.ToString(), d.DecidedAt, d.DecidedBy, d.ExplicitlyConfirmed)).ToList());
    }

    private PendingApprovalDto ToDto(PendingApprovalRow row)
    {
        var description = registry.TryGet(row.ToolName, out var tool) ? tool.Description : string.Empty;
        var tier = ApprovalPolicy.ParseTier(row.RiskTier);

        return new PendingApprovalDto(
            row.ApprovalId,
            row.RiskTier,
            row.RequestedAt,
            row.AgentRunId,
            row.Goal,
            row.StepOrdinal,
            row.ToolName,
            description,
            ParseArguments(row.ArgumentsJson),
            ParseEvidence(row.EvidenceJson),
            ApprovalPolicy.RequiresExplicitConfirmation(tier) ? ApprovalPolicy.ConfirmationPhraseFor(row.ToolName) : null);
    }

    private static IReadOnlyDictionary<string, string> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            // Shown raw rather than hidden: the approver should still see what the step carries.
            return new Dictionary<string, string> { ["(raw)"] = json };
        }
    }

    private static ApprovalEvidenceDto? ParseEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var evidence = JsonSerializer.Deserialize<ApprovalEvidence>(json, JsonSerializerOptions.Web);
            return evidence is null
                ? null
                : new ApprovalEvidenceDto(
                    evidence.Summary,
                    evidence.Target is null ? null : new ApprovalTargetDto(evidence.Target.Type, evidence.Target.Id, evidence.Target.Label),
                    (evidence.Documents ?? []).Select(d => new DocumentVersionDto(d.Kind, d.VersionId, d.Name, d.VersionNumber, d.CreatedAt)).ToList());
        }
        catch (JsonException)
        {
            return new ApprovalEvidenceDto("The evidence snapshot could not be read.", null, []);
        }
    }
}
