using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Web.Features.Jobs;
using WorkPilot.Web.Features.Profile;

namespace WorkPilot.Web.Tests;

// The Web host's matching clients (spec 0019): the ETag round trip and the stale save flag of
// MatchProfileApiClient (AC-11), and the routes JobsApiClient calls (AC-9, AC-10). The Api is
// replaced at the HTTP boundary by a stub handler.
public class MatchingClientTests
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly Guid JobId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000002");

    private static readonly MatchProfileDocument Document = new(["Backend Engineer"], "Remote", [], ["FullTime"], null, null, [], true, 70, ["c#"], [], []);

    private static string DocumentJson => JsonSerializer.Serialize(Document, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // covers: AC-11
    [Fact]
    public async Task Get_returns_the_document_with_its_etag()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentJson, "\"42\"");
        var client = new MatchProfileApiClient(new StubFactory(handler));

        var result = await client.GetAsync(ProfileId);

        Assert.True(result.Succeeded);
        Assert.Equal("\"42\"", result.Value!.ETag);
        Assert.Equal(["c#"], result.Value.Document.Skills);
        Assert.Equal($"/internal/profile/{ProfileId}/match-profile", handler.Path);
    }

    // covers: AC-11
    [Fact]
    public async Task Save_sends_the_etag_as_if_match_and_returns_the_new_one()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentJson, "\"43\"");
        var client = new MatchProfileApiClient(new StubFactory(handler));

        var result = await client.SaveAsync(ProfileId, "\"42\"", Document);

        Assert.True(result.Succeeded);
        Assert.Equal("\"43\"", result.Saved!.ETag);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("\"42\"", handler.IfMatch);
    }

    // covers: AC-11
    [Fact]
    public async Task A_412_is_reported_as_stale()
    {
        var handler = new StubHandler(HttpStatusCode.PreconditionFailed, """{"status":412,"detail":"Your profile changed elsewhere."}""");
        var client = new MatchProfileApiClient(new StubFactory(handler));

        var result = await client.SaveAsync(ProfileId, "\"1\"", Document);

        Assert.False(result.Succeeded);
        Assert.True(result.Stale);
        Assert.Equal("Your profile changed elsewhere.", result.Error);
    }

    // covers: AC-11
    [Fact]
    public async Task Field_errors_are_joined_and_not_stale()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"status":400,"errors":{"salaryCurrency":["Currency must be a 3 letter ISO 4217 code."]}}""");
        var client = new MatchProfileApiClient(new StubFactory(handler));

        var result = await client.SaveAsync(ProfileId, "\"1\"", Document);

        Assert.False(result.Stale);
        Assert.Equal("Currency must be a 3 letter ISO 4217 code.", result.Error);
    }

    // covers: AC-9; spec 0021 AC-2
    [Fact]
    public async Task The_list_asks_for_the_profile_page_and_only_the_set_filters()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"items":[],"total":0,"page":2,"pageSize":25,"profileIncomplete":false}""");
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.SearchAsync(ProfileId, new JobListQuery { Page = 2, Q = "c# dev", MinScore = 70, SalaryMin = 1234.5m, HideBlocked = true, Sort = JobSorts.Newest });

        Assert.True(result.Succeeded);
        Assert.Equal(
            $"/internal/matches?profileId={ProfileId}&page=2&pageSize=25&q=c%23%20dev&minScore=70&salaryMin=1234.5&hideBlocked=true&sort=newest",
            handler.PathAndQuery);
    }

    // covers: spec 0021 AC-4
    [Fact]
    public async Task Dismiss_puts_the_profile_id_and_reads_204_as_success()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, string.Empty);
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.DismissAsync(JobId, ProfileId);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal($"/internal/jobs/{JobId}/dismissal", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(ProfileId, body.RootElement.GetProperty("profileId").GetGuid());
    }

    // covers: spec 0021 AC-4
    [Fact]
    public async Task Undo_deletes_the_dismissal_for_the_profile()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, string.Empty);
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.UndoDismissAsync(JobId, ProfileId);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal($"/internal/jobs/{JobId}/dismissal?profileId={ProfileId}", handler.PathAndQuery);
    }

    // covers: spec 0021 AC-6
    [Fact]
    public async Task A_rejected_board_comes_back_per_field()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"status":400,"errors":{"boardToken":["That is not a valid Lever board."]}}""");
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.AddSourceAsync(new TriggerJobIngestionRequest("lever", "Not A Board!"));

        Assert.False(result.Succeeded);
        Assert.Equal(["That is not a valid Lever board."], result.FieldErrors!["boardToken"]);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
    }

    // covers: spec 0021 AC-5
    [Fact]
    public async Task An_unknown_job_reads_as_not_found()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"status":404,"detail":"That job does not exist."}""");
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.GetJobAsync(JobId, ProfileId);

        Assert.True(result.IsNotFound);
        Assert.Equal($"/internal/jobs/{JobId}?profileId={ProfileId}", handler.PathAndQuery);
    }

    // covers: AC-10
    [Fact]
    public async Task Rescore_posts_the_profile_id()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, """{"queued":false}""");
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.RescoreAsync(JobId, ProfileId);

        Assert.False(result.Value!.Queued);
        Assert.Equal($"/internal/jobs/{JobId}/match/rescore", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(ProfileId, body.RootElement.GetProperty("profileId").GetGuid());
    }

    // covers: AC-15
    [Fact]
    public async Task A_missing_match_reads_as_a_message()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, string.Empty);
        var client = new JobsApiClient(new StubFactory(handler));

        var result = await client.GetMatchAsync(JobId, ProfileId);

        Assert.False(result.Succeeded);
        Assert.Equal("This job has no match for your profile yet.", result.Error);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api") };
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string? etag = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Body { get; private set; }
        public string? IfMatch { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            PathAndQuery = request.RequestUri.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IfMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (etag is not null)
            {
                response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            }

            return response;
        }
    }
}
