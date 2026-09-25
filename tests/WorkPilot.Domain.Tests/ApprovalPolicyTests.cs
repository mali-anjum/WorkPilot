using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Domain.Tests;

// Covers the three tier policy and the confirmed decision (spec 0007, AC-1,
// AC-2, AC-8), enforced in the domain itself, no database involved.
public class ApprovalPolicyTests
{
    private static readonly DateTimeOffset DecidedAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static Approval NewApproval(ToolRiskTier tier) => NewApproval(tier.ToString());

    private static Approval NewApproval(string riskTier) => new()
    {
        TargetType = ApprovalTargets.AgentStep,
        TargetId = Guid.NewGuid(),
        RiskTier = riskTier,
    };

    // covers: AC-1
    [Theory]
    [InlineData(ToolRiskTier.AutoAllowed, false)]
    [InlineData(ToolRiskTier.ApprovalRequired, true)]
    [InlineData(ToolRiskTier.ExplicitConfirmation, true)]
    public void RequiresDecision_IsFalseOnlyForAutoAllowed(ToolRiskTier tier, bool expected)
    {
        Assert.Equal(expected, ApprovalPolicy.RequiresDecision(tier));
    }

    // covers: AC-8
    [Theory]
    [InlineData(ToolRiskTier.AutoAllowed, false)]
    [InlineData(ToolRiskTier.ApprovalRequired, false)]
    [InlineData(ToolRiskTier.ExplicitConfirmation, true)]
    public void RequiresExplicitConfirmation_IsTrueOnlyForTheExplicitTier(ToolRiskTier tier, bool expected)
    {
        Assert.Equal(expected, ApprovalPolicy.RequiresExplicitConfirmation(tier));
    }

    // covers: AC-8
    [Fact]
    public void ConfirmationPhrase_IsTheToolName()
    {
        Assert.Equal("delete_resume", ApprovalPolicy.ConfirmationPhraseFor("delete_resume"));
    }

    // covers: AC-8
    [Theory]
    [InlineData("delete_resume", true)]
    [InlineData("  delete_resume \t", true)] // surrounding whitespace is ignored
    [InlineData("Delete_Resume", false)] // case sensitive
    [InlineData("delete resume", false)]
    [InlineData("delete_resume_now", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConfirmed_NeedsTheExactPhrase(string? typed, bool expected)
    {
        Assert.Equal(expected, ApprovalPolicy.IsConfirmed("delete_resume", typed));
    }

    [Theory]
    [InlineData("AutoAllowed", ToolRiskTier.AutoAllowed)]
    [InlineData("ApprovalRequired", ToolRiskTier.ApprovalRequired)]
    [InlineData("ExplicitConfirmation", ToolRiskTier.ExplicitConfirmation)]
    [InlineData("SomethingNew", ToolRiskTier.ExplicitConfirmation)] // unknown is never looser
    [InlineData("", ToolRiskTier.ExplicitConfirmation)]
    [InlineData("42", ToolRiskTier.ExplicitConfirmation)] // numeric but undefined
    public void ParseTier_TreatsAnUnknownValueAsTheStrictestTier(string stored, ToolRiskTier expected)
    {
        Assert.Equal(expected, ApprovalPolicy.ParseTier(stored));
    }

    [Theory]
    [InlineData(ToolRiskTier.AutoAllowed, ToolRiskTier.ApprovalRequired, ToolRiskTier.ApprovalRequired)]
    [InlineData(ToolRiskTier.ExplicitConfirmation, ToolRiskTier.ApprovalRequired, ToolRiskTier.ExplicitConfirmation)]
    [InlineData(ToolRiskTier.AutoAllowed, ToolRiskTier.AutoAllowed, ToolRiskTier.AutoAllowed)]
    public void Stricter_PicksTheMoreRestrictiveTier(ToolRiskTier a, ToolRiskTier b, ToolRiskTier expected)
    {
        Assert.Equal(expected, ApprovalPolicy.Stricter(a, b));
        Assert.Equal(expected, ApprovalPolicy.Stricter(b, a));
    }

    // covers: AC-1
    [Fact]
    public void PermitsExecution_AllowsAnAutoAllowedToolWithNoApproval()
    {
        Assert.True(ApprovalPolicy.PermitsExecution(ToolRiskTier.AutoAllowed, null));
    }

    // covers: AC-2
    [Theory]
    [InlineData(ToolRiskTier.ApprovalRequired)]
    [InlineData(ToolRiskTier.ExplicitConfirmation)]
    public void PermitsExecution_RefusesAGatedToolWithNoApproval(ToolRiskTier tier)
    {
        Assert.False(ApprovalPolicy.PermitsExecution(tier, null));
    }

    // covers: AC-2
    [Fact]
    public void PermitsExecution_RefusesAPendingApproval()
    {
        Assert.False(ApprovalPolicy.PermitsExecution(ToolRiskTier.ApprovalRequired, NewApproval(ToolRiskTier.ApprovalRequired)));
    }

    // covers: AC-2
    [Fact]
    public void PermitsExecution_RefusesARejectedApproval()
    {
        var approval = NewApproval(ToolRiskTier.ApprovalRequired);
        approval.Decide(ApprovalStatus.Rejected, Guid.NewGuid(), DecidedAt);

        Assert.False(ApprovalPolicy.PermitsExecution(ToolRiskTier.ApprovalRequired, approval));
    }

    // covers: AC-2
    [Fact]
    public void PermitsExecution_AllowsAnApprovedApprovalRequiredStep()
    {
        var approval = NewApproval(ToolRiskTier.ApprovalRequired);
        approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DecidedAt);

        Assert.True(ApprovalPolicy.PermitsExecution(ToolRiskTier.ApprovalRequired, approval));
    }

    // covers: AC-2
    [Fact]
    public void PermitsExecution_AllowsAnExplicitlyConfirmedExplicitStep()
    {
        var approval = NewApproval(ToolRiskTier.ExplicitConfirmation);
        approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DecidedAt, explicitlyConfirmed: true);

        Assert.True(ApprovalPolicy.PermitsExecution(ToolRiskTier.ExplicitConfirmation, approval));
    }

    // covers: AC-2. The tool became explicit after an ApprovalRequired
    // approval was granted: the stricter tier wins, so it can't run unconfirmed.
    [Fact]
    public void PermitsExecution_UsesTheToolsStricterCurrentTier()
    {
        var approval = NewApproval(ToolRiskTier.ApprovalRequired);
        approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DecidedAt);

        Assert.False(ApprovalPolicy.PermitsExecution(ToolRiskTier.ExplicitConfirmation, approval));
    }

    // covers: AC-2. The tool was loosened to AutoAllowed after an approval was
    // requested: the recorded tier still gates it until that approval is granted.
    [Fact]
    public void PermitsExecution_UsesTheApprovalsStricterRecordedTier()
    {
        var pending = NewApproval(ToolRiskTier.ExplicitConfirmation);

        Assert.False(ApprovalPolicy.PermitsExecution(ToolRiskTier.AutoAllowed, pending));
    }

    // covers: AC-2. A stored tier nobody recognizes is read as explicit, so an
    // approval recorded under it only permits execution once confirmed.
    [Fact]
    public void PermitsExecution_ReadsAnUnknownRecordedTierAsExplicit()
    {
        var approval = NewApproval("UnknownTierFromTheFuture");
        approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DecidedAt, explicitlyConfirmed: true);

        Assert.Equal(ToolRiskTier.ExplicitConfirmation, approval.Tier);
        Assert.True(ApprovalPolicy.PermitsExecution(ToolRiskTier.ApprovalRequired, approval));
    }

    // covers: AC-8
    [Fact]
    public void Decide_RefusesAnUnconfirmedApproveOnTheExplicitTier()
    {
        var approval = NewApproval(ToolRiskTier.ExplicitConfirmation);

        Assert.Throws<InvalidOperationException>(() => approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DecidedAt));

        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Null(approval.DecidedBy);
        Assert.False(approval.ExplicitlyConfirmed);
    }

    // covers: AC-8
    [Fact]
    public void Decide_ApprovesTheExplicitTierWhenConfirmed()
    {
        var approval = NewApproval(ToolRiskTier.ExplicitConfirmation);
        var decidedBy = Guid.NewGuid();

        approval.Decide(ApprovalStatus.Approved, decidedBy, DecidedAt, explicitlyConfirmed: true);

        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(decidedBy, approval.DecidedBy);
        Assert.True(approval.ExplicitlyConfirmed);
    }

    // covers: AC-8
    [Fact]
    public void Decide_RejectsTheExplicitTierWithoutAConfirmation()
    {
        var approval = NewApproval(ToolRiskTier.ExplicitConfirmation);

        approval.Decide(ApprovalStatus.Rejected, Guid.NewGuid(), DecidedAt);

        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.False(approval.ExplicitlyConfirmed);
    }

    [Fact]
    public void Decide_NeverMarksARejectionAsExplicitlyConfirmed()
    {
        var approval = NewApproval(ToolRiskTier.ExplicitConfirmation);

        approval.Decide(ApprovalStatus.Rejected, Guid.NewGuid(), DecidedAt, explicitlyConfirmed: true);

        Assert.False(approval.ExplicitlyConfirmed);
    }

    [Fact]
    public void Decide_RefusesPendingAsADecision()
    {
        var approval = NewApproval(ToolRiskTier.ApprovalRequired);

        Assert.Throws<ArgumentOutOfRangeException>(() => approval.Decide(ApprovalStatus.Pending, Guid.NewGuid(), DecidedAt));
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
    }
}
