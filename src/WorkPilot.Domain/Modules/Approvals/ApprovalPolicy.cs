using WorkPilot.Domain.Modules.Agent;

namespace WorkPilot.Domain.Modules.Approvals;

/// <summary>
/// The three tier policy (spec 0007, AC-1, AC-2, AC-8), in one place: whether
/// a tool's tier needs a human decision, whether that decision needs a typed
/// confirmation, and whether a recorded approval actually permits execution.
/// Pure: no I/O, so it is checked both when a decision is made and again
/// right before a gated step executes.
/// </summary>
public static class ApprovalPolicy
{
    /// <summary>True for every tier except <see cref="ToolRiskTier.AutoAllowed"/>: the run suspends for a decision first.</summary>
    public static bool RequiresDecision(ToolRiskTier tier) => tier != ToolRiskTier.AutoAllowed;

    /// <summary>True for <see cref="ToolRiskTier.ExplicitConfirmation"/> (delete data, security/permission changes, destructive ops).</summary>
    public static bool RequiresExplicitConfirmation(ToolRiskTier tier) => tier == ToolRiskTier.ExplicitConfirmation;

    /// <summary>The phrase the approver must type to confirm an explicit confirmation tier action: the tool's own name.</summary>
    public static string ConfirmationPhraseFor(string toolName) => toolName;

    /// <summary>Whether <paramref name="typed"/> matches the confirmation phrase for <paramref name="toolName"/> (exact, case sensitive, surrounding whitespace ignored).</summary>
    public static bool IsConfirmed(string toolName, string? typed) =>
        typed is not null && string.Equals(typed.Trim(), ConfirmationPhraseFor(toolName), StringComparison.Ordinal);

    /// <summary>Parses a stored tier name; an unknown value is treated as the strictest tier, never a looser one.</summary>
    public static ToolRiskTier ParseTier(string riskTier) =>
        Enum.TryParse<ToolRiskTier>(riskTier, out var tier) && Enum.IsDefined(tier) ? tier : ToolRiskTier.ExplicitConfirmation;

    /// <summary>The stricter of two tiers (the enum is ordered from least to most restrictive).</summary>
    public static ToolRiskTier Stricter(ToolRiskTier a, ToolRiskTier b) => (ToolRiskTier)Math.Max((int)a, (int)b);

    /// <summary>
    /// Whether a step for a tool of <paramref name="toolTier"/> may execute
    /// given its recorded <paramref name="approval"/> (null when none exists).
    /// Uses the stricter of the tool's current tier and the tier recorded on
    /// the approval, so a tier change can only make approval harder. A gated
    /// tier needs an <see cref="ApprovalStatus.Approved"/> approval with a
    /// <see cref="Approval.DecidedBy"/>, and the explicit tier also needs
    /// <see cref="Approval.ExplicitlyConfirmed"/>.
    /// </summary>
    public static bool PermitsExecution(ToolRiskTier toolTier, Approval? approval)
    {
        var tier = approval is null ? toolTier : Stricter(toolTier, approval.Tier);
        if (!RequiresDecision(tier))
        {
            return true;
        }

        if (approval is not { Status: ApprovalStatus.Approved, DecidedBy: not null })
        {
            return false;
        }

        return !RequiresExplicitConfirmation(tier) || approval.ExplicitlyConfirmed;
    }
}
