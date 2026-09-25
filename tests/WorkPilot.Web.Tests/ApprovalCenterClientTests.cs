using System.Net;
using System.Text;
using System.Text.Json;
using WorkPilot.Contracts.Approvals;
using WorkPilot.Web.Features.Approvals;

namespace WorkPilot.Web.Tests;

// ApprovalCenterClient (spec 0007, AC-6): how the Web host turns the internal
// Api's decide responses into the outcome the Approval center shows. The Api
// itself is replaced at the HTTP boundary by a stub handler.
public class ApprovalCenterClientTests
{
    private static readonly Guid ApprovalId = Guid.NewGuid();
    private static readonly Guid DecidedBy = Guid.NewGuid();

    private static (ApprovalCenterClient Client, StubHandler Handler) Create(HttpStatusCode status, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        return (new ApprovalCenterClient(new StubFactory(handler)), handler);
    }

    // covers: AC-6
    [Theory]
    [InlineData(HttpStatusCode.OK, "Approve", ApprovalDecisionOutcome.Approved)]
    [InlineData(HttpStatusCode.OK, "Reject", ApprovalDecisionOutcome.Rejected)]
    [InlineData(HttpStatusCode.Conflict, "Approve", ApprovalDecisionOutcome.AlreadyDecided)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Approve", ApprovalDecisionOutcome.ConfirmationRequired)]
    [InlineData(HttpStatusCode.Forbidden, "Approve", ApprovalDecisionOutcome.NotAllowed)]
    [InlineData(HttpStatusCode.NotFound, "Approve", ApprovalDecisionOutcome.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, "Approve", ApprovalDecisionOutcome.Invalid)]
    public async Task DecideAsync_maps_each_api_status_to_an_outcome(HttpStatusCode status, string decision, ApprovalDecisionOutcome expected)
    {
        var (client, _) = Create(status);

        var outcome = await client.DecideAsync(ApprovalId, decision, DecidedBy, null, CancellationToken.None);

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task DecideAsync_throws_on_an_unexpected_server_error()
    {
        var (client, _) = Create(HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.DecideAsync(ApprovalId, "Approve", DecidedBy, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    // covers: AC-6
    [Fact]
    public async Task DecideAsync_posts_the_decision_decider_and_confirmation_to_the_internal_decide_endpoint()
    {
        var (client, handler) = Create(HttpStatusCode.OK);

        await client.DecideAsync(ApprovalId, "Approve", DecidedBy, "explicit_confirmation_demo", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"/internal/agent/approvals/{ApprovalId}/decide", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Approve", body.RootElement.GetProperty("decision").GetString());
        Assert.Equal(DecidedBy, body.RootElement.GetProperty("decidedBy").GetGuid());
        Assert.Equal("explicit_confirmation_demo", body.RootElement.GetProperty("confirmation").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DecideAsync_sends_no_confirmation_when_none_was_typed(string? confirmation)
    {
        var (client, handler) = Create(HttpStatusCode.OK);

        await client.DecideAsync(ApprovalId, "Reject", DecidedBy, confirmation, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("confirmation").ValueKind);
    }

    // covers: AC-4
    [Fact]
    public async Task GetAsync_asks_for_one_profiles_view_and_reads_it_back()
    {
        var profileId = Guid.NewGuid();
        var (client, handler) = Create(HttpStatusCode.OK, """{"pending":[],"recentlyDecided":[]}""");

        var view = await client.GetAsync(profileId, CancellationToken.None);

        Assert.Equal($"/internal/approvals?profileId={profileId}", handler.PathAndQuery);
        Assert.Empty(view.Pending);
        Assert.Empty(view.RecentlyDecided);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api") };
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            PathAndQuery = request.RequestUri.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
