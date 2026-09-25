using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkPilot.Web.Client;
using WorkPilot.Web.Features.Approvals;

namespace WorkPilot.Web.Tests;

// POST /approvals/{id}/decide (spec 0007, AC-6), hosted in a minimal in
// process server with the real endpoint mapping, the real antiforgery
// service, and a header based test sign in standing in for the session
// cookie. The internal Api is replaced by a recording IApprovalCenterClient.
public sealed class ApprovalCenterEndpointsTests : IAsyncLifetime
{
    private static readonly Guid SessionProfile = Guid.Parse("01a0d8e8-9a80-7267-9c41-37468f0a7a7d");
    private static readonly Guid ApprovalId = Guid.Parse("01a0d8e8-c040-74ce-86e4-4b8291a210aa");

    private readonly RecordingClient _client = new();
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(HeaderAuthHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.Scheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IApprovalCenterClient>(_client);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        // Hands out a token pair the way a rendered page's <AntiforgeryToken /> does.
        _app.MapGet("/token", (HttpContext ctx, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(ctx).RequestToken!);
        _app.MapApprovalCenterEndpoints();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private HttpClient CreateClient(Guid? signedInAs)
    {
        var client = _app.GetTestClient();
        if (signedInAs is { } id)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthHandler.Header, id.ToString());
        }

        return client;
    }

    private static async Task<(string Token, string Cookie)> GetTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/token");
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        return (await response.Content.ReadAsStringAsync(), cookie);
    }

    private static async Task<HttpResponseMessage> PostDecisionAsync(HttpClient client, Dictionary<string, string> form, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/approvals/{ApprovalId}/decide") { Content = new FormUrlEncodedContent(form) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    // covers: AC-6
    [Fact]
    public async Task Approve_decides_as_the_signed_in_profile_and_redirects_back_with_the_outcome()
    {
        using var client = CreateClient(SessionProfile);
        var (token, cookie) = await GetTokenAsync(client);

        var response = await PostDecisionAsync(client, new() { ["decision"] = "Approve", ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/approvals?outcome=approved", response.Headers.Location!.OriginalString);
        var call = Assert.Single(_client.Calls);
        Assert.Equal((ApprovalId, "Approve", SessionProfile), (call.ApprovalId, call.Decision, call.DecidedBy));
    }

    // covers: AC-6: the deciding profile is never taken from the form
    [Fact]
    public async Task A_forged_profile_in_the_form_is_ignored()
    {
        using var client = CreateClient(SessionProfile);
        var (token, cookie) = await GetTokenAsync(client);
        var forged = Guid.NewGuid();

        await PostDecisionAsync(client, new()
        {
            ["decision"] = "Reject",
            ["decidedBy"] = forged.ToString(),
            ["profileId"] = forged.ToString(),
            ["__RequestVerificationToken"] = token,
        }, cookie);

        Assert.Equal(SessionProfile, Assert.Single(_client.Calls).DecidedBy);
    }

    // covers: AC-6, AC-8
    [Fact]
    public async Task Passes_the_typed_confirmation_through()
    {
        using var client = CreateClient(SessionProfile);
        var (token, cookie) = await GetTokenAsync(client);

        await PostDecisionAsync(client, new() { ["decision"] = "Approve", ["confirmation"] = "explicit_confirmation_demo", ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal("explicit_confirmation_demo", Assert.Single(_client.Calls).Confirmation);
    }

    // covers: AC-6
    [Theory]
    [InlineData(ApprovalDecisionOutcome.Rejected, "rejected")]
    [InlineData(ApprovalDecisionOutcome.AlreadyDecided, "already-decided")]
    [InlineData(ApprovalDecisionOutcome.ConfirmationRequired, "confirmation-required")]
    [InlineData(ApprovalDecisionOutcome.NotAllowed, "not-allowed")]
    [InlineData(ApprovalDecisionOutcome.NotFound, "not-found")]
    [InlineData(ApprovalDecisionOutcome.Invalid, "invalid")]
    public async Task Redirects_with_each_outcome_the_api_reports(ApprovalDecisionOutcome outcome, string expected)
    {
        _client.Outcome = outcome;
        using var client = CreateClient(SessionProfile);
        var (token, cookie) = await GetTokenAsync(client);

        var response = await PostDecisionAsync(client, new() { ["decision"] = "Approve", ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal($"/approvals?outcome={expected}", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("approve")]
    [InlineData("")]
    public async Task An_unknown_decision_never_reaches_the_api(string decision)
    {
        using var client = CreateClient(SessionProfile);
        var (token, cookie) = await GetTokenAsync(client);

        var response = await PostDecisionAsync(client, new() { ["decision"] = decision, ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal("/approvals?outcome=invalid", response.Headers.Location!.OriginalString);
        Assert.Empty(_client.Calls);
    }

    // covers: AC-6: cross site request forgery protection
    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_rejected_with_400()
    {
        using var client = CreateClient(SessionProfile);

        var response = await PostDecisionAsync(client, new() { ["decision"] = "Approve" }, cookie: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task A_token_issued_to_another_session_is_rejected_with_400()
    {
        using var attacker = CreateClient(Guid.NewGuid());
        var (token, cookie) = await GetTokenAsync(attacker);
        using var victim = CreateClient(SessionProfile);

        var response = await PostDecisionAsync(victim, new() { ["decision"] = "Approve", ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task A_signed_out_post_is_challenged_and_decides_nothing()
    {
        using var client = CreateClient(signedInAs: null);
        var (token, cookie) = await GetTokenAsync(client);

        var response = await PostDecisionAsync(client, new() { ["decision"] = "Approve", ["__RequestVerificationToken"] = token }, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // the real cookie scheme turns this into a /login redirect
        Assert.Empty(_client.Calls);
    }

    private sealed record Call(Guid ApprovalId, string Decision, Guid DecidedBy, string? Confirmation);

    private sealed class RecordingClient : IApprovalCenterClient
    {
        public ApprovalDecisionOutcome Outcome { get; set; } = ApprovalDecisionOutcome.Approved;
        public List<Call> Calls { get; } = [];

        public Task<WorkPilot.Contracts.Approvals.ApprovalCenterDto> GetAsync(Guid profileId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by the decide endpoint.");

        public Task<ApprovalDecisionOutcome> DecideAsync(Guid approvalId, string decision, Guid decidedBy, string? confirmation, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(approvalId, decision, decidedBy, confirmation));
            return Task.FromResult(Outcome);
        }
    }

    // Signs the request in as the profile named in a header, with the same
    // claims the real session cookie carries (sub, profile_id).
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "TestSession";
        public const string Header = "X-Test-Profile";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, value.ToString()), new Claim(PersistedAuthState.ProfileIdClaimType, value.ToString())],
                Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }
}
