using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Approvals;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages;
using WorkPilot.Web.Features.Approvals;

namespace WorkPilot.Web.Tests;

// The Approval center page (spec 0007, AC-5, AC-6), rendered with a fake
// IApprovalCenterClient standing in for the internal Api and a signed in
// session carrying the profile_id claim.
public class ApprovalsTests : TestContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0d8e5-086b-7f22-9936-3a2a10cec6f8");
    private static readonly DateTimeOffset At = new(2026, 9, 25, 14, 12, 0, TimeSpan.Zero);

    private readonly FakeApprovalCenterClient _client = new();

    public ApprovalsTests()
    {
        Services.AddSingleton<IApprovalCenterClient>(_client);
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgeryStateProvider());
    }

    private void SignIn(Guid? profileId)
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("founder@example.com");
        if (profileId is { } id)
        {
            auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, id.ToString()));
        }
    }

    private static PendingApprovalDto Pending(
        string toolName = "approval_required_demo",
        string riskTier = "ApprovalRequired",
        ApprovalEvidenceDto? evidence = null,
        IReadOnlyDictionary<string, string>? arguments = null,
        string? confirmationPhrase = null) =>
        new(Guid.NewGuid(), riskTier, At, Guid.NewGuid(), "apply to Acme", 0, toolName, "A demo tool.",
            arguments ?? new Dictionary<string, string>(), evidence, confirmationPhrase);

    // covers: AC-5
    [Fact]
    public void Renders_the_empty_state_and_no_decisions_when_nothing_is_pending()
    {
        SignIn(ProfileId);

        var cut = RenderComponent<Approvals>();

        Assert.Equal("Approvals", cut.Find("h1").TextContent);
        Assert.Equal("Nothing pending", cut.Find(".wp-empty-state__title").TextContent);
        Assert.Contains("No decisions yet.", cut.Markup);
        Assert.Empty(cut.FindAll("form"));
    }

    // covers: AC-4, AC-5: the page asks for the signed in session's profile only
    [Fact]
    public void Loads_the_view_for_the_signed_in_profile()
    {
        SignIn(ProfileId);

        RenderComponent<Approvals>();

        Assert.Equal([ProfileId], _client.Requested);
    }

    // covers: AC-5
    [Fact]
    public void Renders_a_card_with_the_evidence_to_decide_on()
    {
        SignIn(ProfileId);
        var versionId = Guid.NewGuid();
        var approval = Pending(
            evidence: new ApprovalEvidenceDto(
                "Sends your application to Acme",
                new ApprovalTargetDto("Job", Guid.NewGuid(), "Acme, Backend Engineer"),
                [new DocumentVersionDto("Resume", versionId, "Main CV", 2, At), new DocumentVersionDto("CoverLetter", Guid.NewGuid(), "Acme letter", 1, At)]),
            arguments: new Dictionary<string, string> { ["jobId"] = "123" });
        _client.View = new ApprovalCenterDto([approval], []);

        var cut = RenderComponent<Approvals>();

        var card = cut.Find($"article[data-approval-id='{approval.ApprovalId}']");
        Assert.Equal("approval_required_demo", card.QuerySelector("h3")!.TextContent);
        Assert.Contains("Approval required", card.TextContent); // risk badge
        Assert.Contains("Sends your application to Acme", card.TextContent);
        Assert.Contains("Acme, Backend Engineer", card.TextContent);
        Assert.Contains("apply to Acme", card.TextContent); // goal
        Assert.Contains("2026-09-25 14:12 UTC", card.TextContent);
        var input = card.QuerySelector(".wp-approval__inputs tbody tr")!;
        Assert.Equal("jobId", input.QuerySelector("th")!.TextContent);
        Assert.Equal("123", input.QuerySelector("td")!.TextContent);
        var documents = card.QuerySelectorAll(".wp-approval__documents li").Select(li => li.TextContent).ToList();
        Assert.Equal(2, documents.Count);
        Assert.Contains("Resume: Main CV, version 2", documents[0]);
        Assert.Contains(versionId.ToString(), documents[0]);
        Assert.Contains("Cover letter: Acme letter, version 1", documents[1]);
    }

    // covers: AC-5
    [Fact]
    public void Says_so_when_a_tool_gave_no_evidence_and_has_no_inputs()
    {
        SignIn(ProfileId);
        _client.View = new ApprovalCenterDto([Pending()], []);

        var cut = RenderComponent<Approvals>();

        Assert.Contains("This tool gave no extra evidence", cut.Markup);
        Assert.Contains("Not specified by the tool", cut.Markup);
        Assert.Contains("No inputs", cut.Markup);
        Assert.Empty(cut.FindAll(".wp-approval__documents"));
    }

    // covers: AC-6
    [Fact]
    public void Approve_and_reject_are_antiforgery_protected_posts_that_never_carry_a_profile()
    {
        SignIn(ProfileId);
        var approval = Pending();
        _client.View = new ApprovalCenterDto([approval], []);

        var cut = RenderComponent<Approvals>();

        var forms = cut.FindAll("form");
        Assert.Equal(2, forms.Count);
        Assert.All(forms, form =>
        {
            Assert.Equal("post", form.GetAttribute("method"));
            Assert.Equal($"/approvals/{approval.ApprovalId}/decide", form.GetAttribute("action"));
            Assert.NotNull(form.QuerySelector("input[name='__RequestVerificationToken']"));
            Assert.Null(form.QuerySelector("input[name='decidedBy']"));
            Assert.Null(form.QuerySelector("input[name='profileId']"));
        });
        Assert.Equal(["Approve", "Reject"], forms.Select(f => f.QuerySelector("input[name='decision']")!.GetAttribute("value")));
        Assert.Equal(["Approve", "Reject"], forms.Select(f => f.QuerySelector("button[type='submit']")!.TextContent.Trim()));
        Assert.Empty(cut.FindAll("input[name='confirmation']")); // not an explicit tier action
    }

    // covers: AC-5, AC-8
    [Fact]
    public void An_explicit_confirmation_action_asks_for_the_typed_phrase_with_a_labelled_required_input()
    {
        SignIn(ProfileId);
        var approval = Pending("explicit_confirmation_demo", "ExplicitConfirmation", confirmationPhrase: "explicit_confirmation_demo");
        _client.View = new ApprovalCenterDto([approval], []);

        var cut = RenderComponent<Approvals>();

        Assert.Contains("Explicit confirmation", cut.Find("article").TextContent);
        var input = cut.Find("input[name='confirmation']");
        Assert.True(input.HasAttribute("required"));
        var label = cut.Find($"label[for='{input.Id}']");
        Assert.Contains("explicit_confirmation_demo", label.TextContent);
        // Only the Approve form asks for it; Reject never needs a confirmation.
        Assert.Equal("Approve", input.Closest("form")!.QuerySelector("input[name='decision']")!.GetAttribute("value"));
    }

    // covers: AC-5
    [Fact]
    public void Lists_recent_decisions_marking_explicitly_confirmed_ones()
    {
        SignIn(ProfileId);
        _client.View = new ApprovalCenterDto([],
        [
            new DecidedApprovalDto(Guid.NewGuid(), "explicit_confirmation_demo", "delete my data", "ExplicitConfirmation", "Approved", At, ProfileId, true),
            new DecidedApprovalDto(Guid.NewGuid(), "approval_required_demo", "apply to Acme", "ApprovalRequired", "Rejected", At, ProfileId, false),
        ]);

        var cut = RenderComponent<Approvals>();

        var rows = cut.FindAll(".wp-approvals__recent tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Approved", rows[0].TextContent);
        Assert.Contains("(confirmed)", rows[0].TextContent);
        Assert.Contains("Rejected", rows[1].TextContent);
        Assert.DoesNotContain("(confirmed)", rows[1].TextContent);
    }

    // covers: AC-6
    [Theory]
    [InlineData("approved", "Approved.")]
    [InlineData("rejected", "Rejected.")]
    [InlineData("already-decided", "already decided")]
    [InlineData("confirmation-required", "type the exact confirmation phrase")]
    [InlineData("not-allowed", "You can't decide that approval.")]
    [InlineData("not-found", "no longer exists")]
    [InlineData("invalid", "wasn't understood")]
    public void Shows_the_outcome_of_the_last_decision(string outcome, string expected)
    {
        SignIn(ProfileId);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/approvals?outcome={outcome}");

        var cut = RenderComponent<Approvals>();

        Assert.Contains(expected, cut.Find("[role='status']").TextContent);
    }

    [Fact]
    public void Shows_no_outcome_message_for_an_unknown_outcome()
    {
        SignIn(ProfileId);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/approvals?outcome=<script>");

        var cut = RenderComponent<Approvals>();

        Assert.Empty(cut.FindAll("[role='status']"));
    }

    [Fact]
    public void Shows_an_error_when_the_approvals_cannot_be_loaded()
    {
        SignIn(ProfileId);
        _client.ThrowOnGet = true;

        var cut = RenderComponent<Approvals>();

        Assert.Contains("couldn't be loaded", cut.Find("[role='alert']").TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void Never_loads_anything_when_the_session_has_no_profile()
    {
        SignIn(profileId: null);

        var cut = RenderComponent<Approvals>();

        Assert.NotNull(cut.Find("[role='alert']"));
        Assert.Empty(_client.Requested);
    }

    private sealed class FakeApprovalCenterClient : IApprovalCenterClient
    {
        public ApprovalCenterDto View { get; set; } = new([], []);
        public bool ThrowOnGet { get; set; }
        public List<Guid> Requested { get; } = [];

        public Task<ApprovalCenterDto> GetAsync(Guid profileId, CancellationToken cancellationToken)
        {
            Requested.Add(profileId);
            return ThrowOnGet ? throw new HttpRequestException("api down") : Task.FromResult(View);
        }

        public Task<ApprovalDecisionOutcome> DecideAsync(Guid approvalId, string decision, Guid decidedBy, string? confirmation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The page never decides directly; decisions are form posts.");
    }

    private sealed class FixedAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }
}
