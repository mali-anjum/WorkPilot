using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Domain.Tests;

// Job deduplication domain rules (spec 0017): the exact normalized match key,
// and Job's link rules (attach, see again, primary link, stale, merge, split,
// revive). Pure unit tests; the Postgres side is in the Api tests.
public class JobDedupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Board = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000b1");
    private static readonly Guid Ats = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000b2");

    [Theory]
    [InlineData("Acme, Inc.", "acme")]
    [InlineData("ACME   Incorporated", "acme")]
    [InlineData("Acme GmbH", "acme")]
    [InlineData("Acme Co Ltd", "acme co")]
    [InlineData("Co", "co")]
    [InlineData("  Acme Labs!  ", "acme labs")]
    public void NormalizeCompany_DropsCasePunctuationAndOneTrailingLegalSuffix(string company, string expected)
    {
        // covers AC-3 (a lone suffix word is the name itself, so it stays)
        Assert.Equal(expected, JobDedupKey.NormalizeCompany(company));
    }

    [Theory]
    [InlineData("Sr. Backend Engineer", "senior backend engineer")]
    [InlineData("Jr Designer", "junior designer")]
    [InlineData("Eng. Mgr", "eng manager")]
    [InlineData("Front-End  Engineer (Remote)", "frontend engineer remote")]
    public void NormalizeTitle_ExpandsAbbreviationsAndDropsPunctuation(string title, string expected)
    {
        // covers AC-3
        Assert.Equal(expected, JobDedupKey.NormalizeTitle(title));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("Berlin, Germany", "berlin germany")]
    public void NormalizeLocation_TurnsMissingIntoEmpty(string? location, string expected)
    {
        // covers AC-3
        Assert.Equal(expected, JobDedupKey.NormalizeLocation(location));
    }

    [Fact]
    public void For_GivesTheSameKeyToPostingsThatDifferOnlyInFormatting()
    {
        // covers AC-3
        var a = JobDedupKey.For("Acme, Inc.", "Sr. Backend Engineer", "Berlin");
        var b = JobDedupKey.For("acme", "senior backend engineer!", "BERLIN");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData("Acme", "Backend Engineer", "Munich")]
    [InlineData("Acme", "Frontend Engineer", "Berlin")]
    [InlineData("Globex", "Backend Engineer", "Berlin")]
    [InlineData("Acme", "Backend Engineer", null)]
    public void For_KeepsPostingsApartWhenAnyPartDiffers(string company, string title, string? location)
    {
        // covers AC-3: a missing location only matches a missing location
        Assert.NotEqual(JobDedupKey.For("Acme", "Backend Engineer", "Berlin"), JobDedupKey.For(company, title, location));
    }

    [Fact]
    public void Create_MakesOneCurrentJobWithItsFirstLinkAsPrimary()
    {
        // covers AC-1
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        var link = Assert.Single(job.Links);
        Assert.Equal(link.Id, job.PrimaryLinkId);
        Assert.Equal((Board, "b1", T0, T0, 0.9m), (link.JobSourceId, link.ExternalId, link.FirstSeenAt, link.LastSeenAt, link.Confidence));
        Assert.Equal(JobDedupKey.For("Acme", "Backend Engineer", "Berlin"), job.DedupKey);
        Assert.False(job.IsStale);
        Assert.Equal("https://board.test/b1", job.Provenance.SourceUrl);
    }

    [Fact]
    public void AttachLink_WithHigherConfidence_BecomesPrimaryAndFillsTheJob()
    {
        // covers AC-2, AC-4
        var job = Job.Create(Board, Posting("b1", "board", description: "Board text."), T0, 0.9m);

        var sighting = job.AttachLink(Ats, Posting("a1", "ats", description: "ATS text."), T0.AddHours(1), 1.0m);

        Assert.Equal(sighting.Link.Id, job.PrimaryLinkId);
        Assert.Equal("ATS text.", job.Description);
        Assert.Equal("https://ats.test/a1", job.Provenance.SourceUrl);
        Assert.Equal(T0, job.Provenance.RetrievedAt);
        Assert.Equal(T0.AddHours(1), job.Provenance.VerifiedAt);
        Assert.Equal(1.0m, job.Provenance.Confidence);
        Assert.Equal(2, job.Snapshots.Count);
    }

    [Fact]
    public void AttachLink_WithLowerConfidence_KeepsThePrimaryAndItsFields()
    {
        // covers AC-4
        var job = Job.Create(Ats, Posting("a1", "ats", description: "ATS text."), T0, 1.0m);
        var primary = job.PrimaryLinkId;

        job.AttachLink(Board, Posting("b1", "board", description: "Board text."), T0.AddHours(1), 0.9m);

        Assert.Equal(primary, job.PrimaryLinkId);
        Assert.Equal("ATS text.", job.Description);
    }

    [Fact]
    public void AttachLink_TwiceForTheSamePosting_Throws()
    {
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        Assert.Throws<InvalidOperationException>(() => job.AttachLink(Board, Posting("b1", "board"), T0, 0.9m));
    }

    [Fact]
    public void AttachLink_RevivesASoftDeletedJob()
    {
        // covers AC-11
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);
        job.SoftDelete(T0);

        job.AttachLink(Ats, Posting("a1", "ats"), T0.AddHours(1), 1.0m);

        Assert.False(job.IsDeleted);
        Assert.Null(job.DeletedAt);
    }

    [Fact]
    public void SeeAgain_OnASoftDeletedJob_RevivesIt()
    {
        // covers AC-11
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);
        job.SoftDelete(T0);

        job.SeeAgain(job.Links[0], Posting("b1", "board"), T0.AddDays(1), 0.9m, NoOther);

        Assert.False(job.IsDeleted);
    }

    [Fact]
    public void SeeAgain_OnlyAddsASnapshotToTheLinkWhoseContentChanged()
    {
        // covers AC-5
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);
        job.AttachLink(Ats, Posting("a1", "ats"), T0.AddHours(1), 1.0m);
        var boardLink = job.Links.Single(l => l.JobSourceId == Board);

        var added = job.SeeAgain(boardLink, Posting("b1", "board", description: "Changed."), T0.AddDays(1), 0.9m, NoOther);

        Assert.NotNull(added);
        Assert.Equal(2, job.Snapshots.Count(s => s.ExternalId == "b1"));
        Assert.Single(job.Snapshots, s => s.ExternalId == "a1");
    }

    [Fact]
    public void SeeAgain_ATieOnConfidenceGoesToTheMostRecentlySeenLink()
    {
        // covers AC-4
        var job = Job.Create(Board, Posting("b1", "board", description: "Board text."), T0, 1.0m);
        job.AttachLink(Ats, Posting("a1", "ats", description: "ATS text."), T0.AddHours(1), 1.0m);
        Assert.Equal("ATS text.", job.Description);

        job.SeeAgain(job.Links.Single(l => l.JobSourceId == Board), Posting("b1", "board", description: "Board text."), T0.AddHours(2), 1.0m, NoOther);

        Assert.Equal(Board, job.PrimaryLink!.JobSourceId);
        Assert.Equal("Board text.", job.Description);
    }

    [Fact]
    public void SeeAgain_WithANewTitle_MarksTheJobStaleInsteadOfMerging()
    {
        // covers AC-9 (the stale rule in the spec's Decision)
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        job.SeeAgain(job.Links[0], Posting("b1", "board", title: "Staff Engineer"), T0.AddDays(1), 0.9m, NoOther);

        Assert.True(job.IsStale);
        Assert.Equal(JobDedupKey.For("Acme", "Staff Engineer", "Berlin"), job.DedupKey);

        job.MarkCurrent();
        Assert.False(job.IsStale);
    }

    [Fact]
    public void MergeFrom_MovesEveryLinkAndSnapshotAndKeepsTheBestPrimary()
    {
        // covers AC-9 (merge), AC-4
        var older = Job.Create(Board, Posting("b1", "board", description: "Board text."), T0, 0.9m);
        var newer = Job.Create(Ats, Posting("a1", "ats", description: "ATS text."), T0.AddHours(1), 1.0m);

        older.MergeFrom(newer, NoOther);

        Assert.Equal(2, older.Links.Count);
        Assert.All(older.Links, l => Assert.Equal(older.Id, l.JobId));
        Assert.All(older.Snapshots, s => Assert.Equal(older.Id, s.JobId));
        Assert.Empty(newer.Links);
        Assert.Empty(newer.Snapshots);
        Assert.Equal(Ats, older.PrimaryLink!.JobSourceId);
        Assert.Equal("ATS text.", older.Description);
        Assert.Equal(T0, older.Provenance.RetrievedAt);
    }

    [Fact]
    public void MergeFrom_ItselfThrows()
    {
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        Assert.Throws<InvalidOperationException>(() => job.MergeFrom(job, NoOther));
    }

    [Fact]
    public void SplitLink_MovesTheLinkAndItsSnapshotsIntoANewJobAndRefillsTheOriginal()
    {
        // covers AC-8
        var board = Posting("b1", "board", description: "Board text.");
        var ats = Posting("a1", "ats", description: "ATS text.");
        var job = Job.Create(Board, board, T0, 0.9m);
        job.AttachLink(Ats, ats, T0.AddHours(1), 1.0m);
        var atsLink = job.Links.Single(l => l.JobSourceId == Ats);

        var created = job.SplitLink(atsLink.Id, T0.AddDays(1), l => l.JobSourceId == Ats ? ats : board);

        Assert.Equal(T0.AddDays(1), Assert.Single(created.Links).SplitAt);
        Assert.True(created.HasSplitLink);
        Assert.False(created.IsStale);
        Assert.All(created.Snapshots, s => Assert.Equal("a1", s.ExternalId));
        Assert.Equal("ATS text.", created.Description);
        Assert.Equal(Board, Assert.Single(job.Links).JobSourceId);
        Assert.All(job.Snapshots, s => Assert.Equal("b1", s.ExternalId));
        Assert.Equal("Board text.", job.Description);
        Assert.Equal("https://board.test/b1", job.Provenance.SourceUrl);
    }

    [Fact]
    public void SplitLink_OnTheOnlyLinkOrAnUnknownLink_Throws()
    {
        // covers AC-8
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        Assert.Throws<InvalidOperationException>(() => job.SplitLink(job.Links[0].Id, T0, NoOther));
        Assert.Throws<InvalidOperationException>(() => job.SplitLink(Guid.NewGuid(), T0, NoOther));
    }

    [Fact]
    public void ApplyPrimaryPosting_WithARenamedCompany_UpdatesTheCompanyAndGoesStale()
    {
        // covers AC-7
        var job = Job.Create(Board, Posting("b1", "board"), T0, 0.9m);

        job.ApplyPrimaryPosting(Posting("b1", "board") with { Company = "Acme Renamed" });

        Assert.Equal("Acme Renamed", job.Company);
        Assert.True(job.IsStale);
    }

    private static NormalizedJob Posting(string id, string host, string title = "Backend Engineer", string description = "Build it.")
    {
        var raw = new RawJobPosting(id, $"https://{host}.test/{id}", title, "Acme", "Berlin", $"<p>{description}</p>", T0.AddDays(-3), $"{{\"id\":\"{id}\",\"d\":\"{description}\"}}");
        return JobNormalizer.Normalize(raw)!;
    }

    private static NormalizedJob NoOther(JobSourceLink link) =>
        throw new InvalidOperationException("No other link's posting is needed here.");
}
