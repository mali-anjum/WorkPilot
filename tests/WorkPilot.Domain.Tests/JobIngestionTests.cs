using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Domain.Tests;

// Unit tests for spec 0008's domain rules: the source independent
// normalizer (AC-3), the keyword search, and Job.Create / Job.Refresh's
// provenance and snapshot rules (AC-2, AC-4). No database.
public class JobIngestionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static RawJobPosting Raw(
        string id = "123",
        string url = "https://example.test/jobs/123",
        string title = "Backend Engineer",
        string company = "Acme",
        string? location = "Remote, Europe",
        string? description = "<p>Build things.</p>",
        DateTimeOffset? postedAt = null) =>
        new(id, url, title, company, location, description, postedAt, "{\"id\":123}");

    private static NormalizedJob Normalized(string title = "Backend Engineer", string? description = "<p>Build things.</p>") =>
        JobNormalizer.Normalize(Raw(title: title, description: description))!;

    [Fact]
    public void Normalize_TrimsAndCollapsesWhitespaceInEveryTextField()
    {
        // covers AC-3
        var job = JobNormalizer.Normalize(Raw(title: "  Senior \t Backend  Engineer \n", company: " Acme  Corp ", location: " Berlin ,  Germany "))!;

        Assert.Equal("Senior Backend Engineer", job.Title);
        Assert.Equal("Acme Corp", job.Company);
        Assert.Equal("Berlin , Germany", job.Location);
    }

    [Theory]
    [InlineData("", "https://x.test/1", "Engineer", "Acme")]
    [InlineData("1", " ", "Engineer", "Acme")]
    [InlineData("1", "https://x.test/1", "  ", "Acme")]
    [InlineData("1", "https://x.test/1", "Engineer", "")]
    public void Normalize_WhenARequiredFieldIsMissing_ReturnsNull(string id, string url, string title, string company)
    {
        // covers AC-6: the caller skips and counts such postings
        Assert.Null(JobNormalizer.Normalize(Raw(id: id, url: url, title: title, company: company)));
    }

    [Fact]
    public void Normalize_TurnsEntityEncodedHtmlIntoPlainText()
    {
        // covers AC-3: Greenhouse sends its HTML entity encoded
        const string encoded = "&lt;div&gt;&lt;h2&gt;About&lt;/h2&gt;&lt;p&gt;We ship &amp;amp; learn.&lt;/p&gt;&lt;ul&gt;&lt;li&gt;C#&lt;/li&gt;&lt;li&gt;Postgres&lt;/li&gt;&lt;/ul&gt;&lt;/div&gt;";

        var job = JobNormalizer.Normalize(Raw(description: encoded))!;

        Assert.Equal("About\nWe ship & learn.\n\n- C#\n- Postgres", job.Description);
    }

    [Fact]
    public void HtmlToPlainText_DropsScriptsAndStylesAndCollapsesBlankLines()
    {
        const string html = "<style>p{color:red}</style><p>One</p><br><br><br><p>Two&nbsp;&nbsp;words</p><script>alert(1)</script>";

        Assert.Equal("One\n\nTwo words", JobNormalizer.HtmlToPlainText(html));
    }

    [Fact]
    public void Normalize_WithNoDescriptionOrLocation_LeavesThemNull()
    {
        var job = JobNormalizer.Normalize(Raw(location: "  ", description: "<p> </p>"))!;

        Assert.Null(job.Location);
        Assert.Null(job.Description);
        Assert.Null(job.RemoteType);
    }

    [Theory]
    [InlineData("Remote, Bangalore", RemoteTypes.Remote)]
    [InlineData("REMOTE - US", RemoteTypes.Remote)]
    [InlineData("Hybrid - London (remote 2 days)", RemoteTypes.Hybrid)]
    [InlineData("San Francisco, CA", null)]
    [InlineData("Remoteville", null)]
    [InlineData(null, null)]
    public void DeriveRemoteType_ReadsTheLocationText(string? location, string? expected)
    {
        // covers AC-3: unknown stays null, never assumed on site
        Assert.Equal(expected, JobNormalizer.DeriveRemoteType(location));
    }

    [Fact]
    public void ContentHash_IsStableForTheSameContentAndChangesWithIt()
    {
        // covers AC-3: same content, same hash, even when raw whitespace differs
        var a = JobNormalizer.Normalize(Raw(title: "Backend Engineer"))!;
        var b = JobNormalizer.Normalize(Raw(title: "  Backend   Engineer "))!;
        var c = JobNormalizer.Normalize(Raw(title: "Frontend Engineer"))!;

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", a.ContentHash);
    }

    [Fact]
    public void Normalize_ConvertsPostedAtToUtc()
    {
        var job = JobNormalizer.Normalize(Raw(postedAt: new DateTimeOffset(2026, 5, 22, 9, 16, 29, TimeSpan.FromHours(-4))))!;

        Assert.Equal(new DateTimeOffset(2026, 5, 22, 13, 16, 29, TimeSpan.Zero), job.PostedAt);
        Assert.Equal(TimeSpan.Zero, job.PostedAt!.Value.Offset);
    }

    [Theory]
    [InlineData(null, "Anything At All", true)]
    [InlineData("", "Anything At All", true)]
    [InlineData("engineer", "Senior Backend ENGINEER", true)]
    [InlineData("backend, platform", "Platform Lead", true)]
    [InlineData("backend platform", "Sales Manager", false)]
    public void JobSearchQuery_MatchesAnyTermCaseInsensitively(string? keywords, string title, bool expected)
    {
        Assert.Equal(expected, JobSearchQuery.Parse(keywords).Matches(title));
    }

    [Fact]
    public void Create_PopulatesProvenanceAndAFirstSnapshot()
    {
        // covers AC-2
        var sourceId = Guid.NewGuid();
        var posting = Normalized();

        var job = Job.Create(sourceId, posting, T0, 1.0m);

        Assert.Equal(posting.SourceUrl, job.Provenance.SourceUrl);
        Assert.Equal(T0, job.Provenance.RetrievedAt);
        Assert.Equal(T0, job.Provenance.VerifiedAt);
        Assert.Equal(1.0m, job.Provenance.Confidence);
        Assert.Equal("Build things.", job.Description);

        var snapshot = Assert.Single(job.Snapshots);
        Assert.Equal(job.Id, snapshot.JobId);
        Assert.Equal(sourceId, snapshot.JobSourceId);
        Assert.Equal(posting.ExternalId, snapshot.ExternalId);
        Assert.Equal(posting.ContentHash, snapshot.ContentHash);
        Assert.Equal(posting.RawContent, snapshot.RawContent);
        Assert.Equal(T0, snapshot.Provenance.RetrievedAt);
    }

    [Fact]
    public void Refresh_WithUnchangedContent_OnlyReverifies()
    {
        // covers AC-4
        var job = Job.Create(Guid.NewGuid(), Normalized(), T0, 1.0m);
        var later = T0.AddHours(6);

        var added = job.Refresh(Normalized(), later, 1.0m);

        Assert.Null(added);
        Assert.Single(job.Snapshots);
        Assert.Equal(T0, job.Provenance.RetrievedAt);
        Assert.Equal(later, job.Provenance.VerifiedAt);
        Assert.Equal(later, job.Snapshots[0].Provenance.VerifiedAt);
    }

    [Fact]
    public void Refresh_WithChangedContent_UpdatesFieldsAndAddsASnapshot()
    {
        // covers AC-4
        var job = Job.Create(Guid.NewGuid(), Normalized(), T0, 1.0m);
        var later = T0.AddDays(1);
        var changed = Normalized(title: "Staff Backend Engineer");

        var added = job.Refresh(changed, later, 1.0m);

        Assert.NotNull(added);
        Assert.Equal(2, job.Snapshots.Count);
        Assert.Equal("Staff Backend Engineer", job.Title);
        Assert.Equal(changed.ContentHash, added!.ContentHash);
        Assert.Equal(later, added.Provenance.RetrievedAt);
        Assert.Equal(T0, job.Provenance.RetrievedAt);
        Assert.Equal(later, job.Provenance.VerifiedAt);
    }

    [Fact]
    public void Refresh_WithADifferentPosting_Throws()
    {
        var job = Job.Create(Guid.NewGuid(), Normalized(), T0, 1.0m);
        var other = JobNormalizer.Normalize(Raw(id: "999"))!;

        Assert.Throws<InvalidOperationException>(() => job.Refresh(other, T0, 1.0m));
    }
}
