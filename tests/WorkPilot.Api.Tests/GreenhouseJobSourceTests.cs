using System.Net;
using System.Text.Json;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs.Sources;

namespace WorkPilot.Api.Tests;

// Tests for the Greenhouse Job Board API adapter (spec 0008) against a
// payload shaped exactly like a live response (captured from the public
// gitlab board on 2026-09-24, trimmed), served by a stub HttpMessageHandler
// so no network is needed. The live source itself is proven in verify.md.
public class GreenhouseJobSourceTests
{
    private const string Payload = """
        {
          "jobs": [
            {
              "absolute_url": "https://job-boards.greenhouse.io/gitlab/jobs/8556658002",
              "internal_job_id": 6417799002,
              "location": { "name": "Remote, Bangalore" },
              "metadata": [],
              "id": 8556658002,
              "updated_at": "2026-09-14T16:01:39-04:00",
              "requisition_id": "6401",
              "title": "AI Engineer",
              "company_name": "GitLab",
              "first_published": "2026-05-22T09:16:29-04:00",
              "language": "en",
              "content": "&lt;p&gt;GitLab is the intelligent orchestration platform.&lt;/p&gt;"
            },
            {
              "absolute_url": "https://job-boards.greenhouse.io/gitlab/jobs/1",
              "id": 1,
              "updated_at": "2026-09-14T16:01:39-04:00",
              "title": "No Company Or Location",
              "location": null
            },
            {
              "absolute_url": "https://job-boards.greenhouse.io/gitlab/jobs/2",
              "id": 2,
              "company_name": "GitLab"
            }
          ],
          "meta": { "total": 3 }
        }
        """;

    [Fact]
    public void Parse_MapsGreenhouseFieldsToRawPostings()
    {
        // covers AC-2
        using var doc = JsonDocument.Parse(Payload);

        var postings = GreenhouseJobSource.Parse(doc.RootElement, "gitlab");

        Assert.Equal(3, postings.Count);
        var first = postings[0];
        Assert.Equal("8556658002", first.ExternalId);
        Assert.Equal("https://job-boards.greenhouse.io/gitlab/jobs/8556658002", first.SourceUrl);
        Assert.Equal("AI Engineer", first.Title);
        Assert.Equal("GitLab", first.Company);
        Assert.Equal("Remote, Bangalore", first.LocationText);
        Assert.Equal(new DateTimeOffset(2026, 5, 22, 9, 16, 29, TimeSpan.FromHours(-4)), first.PostedAt);
        Assert.Contains("\"requisition_id\"", first.RawContent);

        var normalized = JobNormalizer.Normalize(first)!;
        Assert.Equal("GitLab is the intelligent orchestration platform.", normalized.Description);
        Assert.Equal(RemoteTypes.Remote, normalized.RemoteType);
    }

    [Fact]
    public void Parse_FallsBackToTheBoardTokenAndUpdatedAt_AndLeavesMissingTitlesForTheNormalizerToSkip()
    {
        // covers AC-6: a posting with no title parses, then normalizes to null (skipped)
        using var doc = JsonDocument.Parse(Payload);

        var postings = GreenhouseJobSource.Parse(doc.RootElement, "gitlab");

        Assert.Equal("gitlab", postings[1].Company);
        Assert.Null(postings[1].LocationText);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 16, 1, 39, TimeSpan.FromHours(-4)), postings[1].PostedAt);
        Assert.Null(JobNormalizer.Normalize(postings[2]));
    }

    [Theory]
    [InlineData("""{"meta":{}}""")]
    [InlineData("""{"jobs":{}}""")]
    [InlineData("""[]""")]
    public void Parse_WhenTheBodyIsNotAJobsResponse_Throws(string body)
    {
        // covers AC-6: a malformed body fails the run rather than storing nothing silently
        using var doc = JsonDocument.Parse(body);

        Assert.Throws<JsonException>(() => GreenhouseJobSource.Parse(doc.RootElement, "gitlab"));
    }

    [Theory]
    [InlineData("gitlab", "greenhouse:gitlab")]
    [InlineData("  GitLab ", "greenhouse:gitlab")]
    [InlineData("my_board-2", "greenhouse:my_board-2")]
    public void Describe_NormalizesValidTokens(string board, string expectedName)
    {
        var definition = new GreenhouseJobSource(new HttpClient()).Describe(board);

        Assert.NotNull(definition);
        Assert.Equal(expectedName, definition!.Name);
        Assert.Contains("\"boardToken\"", definition.ConfigJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("evil.test")]
    [InlineData("board?x=1")]
    public void Describe_RejectsTokensThatCouldChangeTheUrl(string board)
    {
        // covers AC-1 and the spec's security model
        Assert.Null(new GreenhouseJobSource(new HttpClient()).Describe(board));
    }

    [Fact]
    public async Task FetchAsync_CallsTheBoardsJobsEndpointWithContent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        var source = new GreenhouseJobSource(new HttpClient(handler) { BaseAddress = new Uri("https://boards.test/v1/") });

        var postings = await source.FetchAsync(GreenhouseSource(), CancellationToken.None);

        Assert.Equal(3, postings.Count);
        Assert.Equal("https://boards.test/v1/boards/gitlab/jobs?content=true", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchAsync_WhenTheBoardDoesNotExist_Throws()
    {
        // covers AC-6: Greenhouse answers 404 for an unknown board
        var source = new GreenhouseJobSource(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}")) { BaseAddress = new Uri("https://boards.test/v1/") });

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(GreenhouseSource(), CancellationToken.None));
    }

    private static JobSource GreenhouseSource() =>
        new() { Type = "Greenhouse", Name = "greenhouse:gitlab", Config = """{"boardToken": "gitlab"}""" };

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
