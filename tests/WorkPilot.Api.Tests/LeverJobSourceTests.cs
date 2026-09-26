using System.Net;
using System.Text.Json;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs.Sources;

namespace WorkPilot.Api.Tests;

// The Lever adapter (spec 0017, AC-6) against a trimmed postings payload
// served by a stub HttpMessageHandler, so no live Lever site is needed.
public class LeverJobSourceTests
{
    private const string Payload = """
        [
          {
            "id": "36233884-41a7-47d7-90b5-73369d32d68c",
            "text": "Engineering Manager",
            "hostedUrl": "https://jobs.lever.co/hive/36233884-41a7-47d7-90b5-73369d32d68c",
            "createdAt": 1758873600000,
            "workplaceType": "hybrid",
            "categories": { "location": "San Francisco" },
            "description": "<p>Lead the team.</p>",
            "lists": [ { "text": "What you'll do", "content": "<li>Ship</li>" } ],
            "additional": "<p>Benefits.</p>"
          }
        ]
        """;

    [Fact]
    public async Task FetchAsync_MapsThePostingAndCallsTheSitesPostingsEndpoint()
    {
        // covers AC-6
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        var source = new LeverJobSource(new HttpClient(handler) { BaseAddress = new Uri("https://lever.test/v0/") });

        var posting = Assert.Single(await source.FetchAsync(LeverSource(companyName: null), CancellationToken.None));

        Assert.Equal("https://lever.test/v0/postings/hive?mode=json", handler.LastUri!.ToString());
        Assert.Equal("36233884-41a7-47d7-90b5-73369d32d68c", posting.ExternalId);
        Assert.Equal("Engineering Manager", posting.Title);
        Assert.Equal("hive", posting.Company);
        Assert.Equal("San Francisco (Hybrid)", posting.LocationText);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1758873600000), posting.PostedAt);
        Assert.Contains("What you'll do", posting.DescriptionHtml);
    }

    [Fact]
    public async Task A_company_name_on_the_source_never_changes_the_raw_posting_or_its_hash()
    {
        // covers AC-5, AC-7: a rename is applied to display and match only, so it
        // must not look like a changed posting (found by /check verify).
        var client = new HttpClient(new StubHandler(HttpStatusCode.OK, Payload)) { BaseAddress = new Uri("https://lever.test/v0/") };
        var source = new LeverJobSource(client);

        var unnamed = Assert.Single(await source.FetchAsync(LeverSource(companyName: null), CancellationToken.None));
        var named = Assert.Single(await source.FetchAsync(LeverSource(companyName: "Hive AI"), CancellationToken.None));
        var stored = source.ParseStored(LeverSource(companyName: "Hive AI"), unnamed.RawContent);

        Assert.Equal("hive", named.Company);
        Assert.Equal(unnamed, named);
        Assert.Equal(unnamed, stored);
        Assert.Equal(JobNormalizer.Normalize(unnamed)!.ContentHash, JobNormalizer.Normalize(named)!.ContentHash);
    }

    [Theory]
    [InlineData("../evil.test/x")]
    [InlineData("")]
    [InlineData("has space")]
    public void Describe_RejectsABadSiteName(string site)
    {
        // covers AC-6
        Assert.Null(new LeverJobSource(new HttpClient()).Describe(site));
    }

    [Theory]
    [InlineData("""{"workplaceType":"remote","categories":{}}""", "Remote")]
    [InlineData("""{"workplaceType":"remote","categories":{"location":"Remote - EU"}}""", "Remote - EU")]
    [InlineData("""{"workplaceType":"on-site","categories":{"location":"Oslo"}}""", "Oslo")]
    [InlineData("""{"categories":{}}""", null)]
    public void Parse_AddsTheWorkplaceTypeOnlyWhenTheLocationDoesNotSayIt(string posting, string? expected)
    {
        // covers AC-6
        using var doc = JsonDocument.Parse($"[{posting}]");

        Assert.Equal(expected, Assert.Single(LeverJobSource.Parse(doc.RootElement, "hive")).LocationText);
    }

    [Fact]
    public void Parse_OfABodyThatIsNotAnArray_Throws()
    {
        // covers AC-6: a bad body stores nothing (the run fails and is retried)
        using var doc = JsonDocument.Parse("""{"ok":false}""");

        Assert.Throws<JsonException>(() => LeverJobSource.Parse(doc.RootElement, "hive"));
    }

    [Fact]
    public async Task FetchAsync_WhenTheSiteDoesNotExist_Throws()
    {
        // covers AC-6
        var source = new LeverJobSource(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}")) { BaseAddress = new Uri("https://lever.test/v0/") });

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(LeverSource(companyName: null), CancellationToken.None));
    }

    [Fact]
    public void Describe_LowercasesTheSiteIntoOneSourcePerSite()
    {
        // covers AC-6
        var definition = new LeverJobSource(new HttpClient()).Describe("  Hive ");

        Assert.Equal("lever:hive", definition!.Name);
        Assert.Contains("\"site\":\"hive\"", definition.ConfigJson);
    }

    [Fact]
    public void ParseStored_RejectsContentThatIsNotOnePosting()
    {
        Assert.Throws<JsonException>(() => new LeverJobSource(new HttpClient()).ParseStored(LeverSource(companyName: null), "[]"));
    }

    private static JobSource LeverSource(string? companyName) =>
        new() { Type = "Lever", Name = "lever:hive", Config = """{"site": "hive"}""", CompanyName = companyName };

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
