using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages;
using WorkPilot.Web.Features.Common;
using WorkPilot.Web.Features.Jobs;

namespace WorkPilot.Web.Tests;

// The /jobs list and /jobs/{id} match panel (spec 0019, AC-2, AC-8, AC-9, AC-10), rendered with a
// fake IJobsApiClient standing in for the internal Api and a signed in session carrying the
// profile_id claim. The real ordering and scoring are covered in Api.Tests/JobMatchingTests.
public class JobsTests : BunitContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly Guid JobId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000002");
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeJobsApiClient _api = new();

    public JobsTests()
    {
        Services.AddSingleton<IJobsApiClient>(_api);
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, ProfileId.ToString()));
    }

    private static MatchListItemDto Item(string title, int? score, bool blocked = false) =>
        new(Guid.NewGuid(), title, "Acme", "Berlin", score, score is null ? null : "High", blocked, score is null ? null : At);

    // covers: AC-9
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = Render<Jobs>();

        Assert.Equal("Jobs", cut.Find("h1").TextContent);
        Assert.Equal("No jobs yet", cut.Find(".wp-empty-state__title").TextContent);
        Assert.Equal([(ProfileId, 1)], _api.Listed);
    }

    // covers: AC-9
    [Fact]
    public void Lists_each_job_with_its_score_confidence_and_blocker()
    {
        _api.List = new([Item("Backend Engineer", 92), Item("Platform Engineer", 20, blocked: true), Item("Data Analyst", null)], 3, 1, 25, false);

        var cut = Render<Jobs>();

        var rows = cut.FindAll(".wp-match__row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("92", rows[0].TextContent);
        Assert.Contains("High confidence", rows[0].TextContent);
        Assert.Contains("Blocker", rows[1].TextContent);
        Assert.Contains("Not scored yet", rows[2].TextContent);
        Assert.Equal($"/jobs/{_api.List.Items[0].JobId}", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Empty(cut.FindAll(".wp-match__banner"));
    }

    // covers: AC-9
    [Fact]
    public void An_incomplete_profile_shows_a_banner_linking_to_the_profile()
    {
        _api.List = new([Item("Backend Engineer", null)], 1, 1, 25, true);

        var cut = Render<Jobs>();

        Assert.Equal("/profile", cut.Find(".wp-match__banner a").GetAttribute("href"));
    }

    // covers: AC-9
    [Fact]
    public void Next_loads_the_following_page()
    {
        _api.List = new(Enumerable.Range(0, 25).Select(i => Item($"Job {i}", 50)).ToList(), 30, 1, 25, false);
        var cut = Render<Jobs>();
        Assert.Contains("Page 1 of 2", cut.Find(".wp-match__pager").TextContent);

        cut.FindAll(".wp-match__pager button")[1].Click();

        Assert.Equal((ProfileId, 2), _api.Listed[^1]);
    }

    [Fact]
    public void A_failed_list_shows_the_message()
    {
        _api.ListError = "Your profile could not be found; sign in again.";

        var cut = Render<Jobs>();

        Assert.Equal("Your profile could not be found; sign in again.", cut.Find("[role=alert]").TextContent);
    }

    // ---------- the match panel ----------

    private static ExplanationItemDto Line(string label, string status, string? quote, string? profileLabel = null, string? dimension = null) =>
        new(label, status, quote, true, profileLabel is null ? null : new ProfileRefDto("Skill", Guid.NewGuid(), profileLabel), dimension);

    private static JobMatchDetailDto Detail(bool failed = false, int? score = 87, bool blocked = false) => new(
        JobId,
        "Backend Engineer",
        "Acme",
        "Berlin",
        "https://boards.test/1",
        score,
        failed ? "Low" : "High",
        blocked,
        new MatchExplanationDto(
            1,
            [
                new DimensionResultDto("Skills", "Partial", 20, 30, [Line("C# (required)", "Met", "You need C# daily", "c#"), Line("Go (required)", "Missed", "Go is a must")]),
                new DimensionResultDto("Title", "Met", 15, 15, [Line("Title \"Backend Engineer\"", "Met", "Backend Engineer")]),
                new DimensionResultDto("Experience", "Unknown", 0, 15, []),
                new DimensionResultDto("Location", "Unknown", 0, 15, []),
                new DimensionResultDto("Salary", "Unknown", 0, 10, []),
                new DimensionResultDto("Education", "Unknown", 0, 5, []),
                new DimensionResultDto("JobType", "Unknown", 0, 5, []),
                new DimensionResultDto("WorkAuthorization", blocked ? "Blocker" : "Unknown", 0, 5, []),
            ],
            [Line("Go (required)", "Missed", "Go is a must")],
            [new UnknownInfoDto("Experience", "JobSilent"), new UnknownInfoDto("Location", "ProfileNotSet")],
            blocked ? [Line("No visa sponsorship", "Blocker", "We do not sponsor visas", dimension: "WorkAuthorization")] : [],
            [new UnverifiedItemDto("Skills", "COBOL", "Deep COBOL expertise")],
            failed),
        failed ? "Failed" : "Extracted",
        failed ? "AiProviderException: provider down" : null,
        At);

    // covers: AC-2, AC-10
    [Fact]
    public void The_panel_shows_the_score_evidence_gaps_and_breakdown()
    {
        _api.Match = Detail();

        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        Assert.Equal("Backend Engineer", cut.Find("h1").TextContent);
        Assert.Equal("87", cut.Find(".wp-match__big-score").TextContent);
        var cards = cut.FindAll(".wp-card");
        var why = cards.Single(c => c.TextContent.Contains("Why it matches", StringComparison.Ordinal));
        Assert.Contains("your skill: c#", why.TextContent);
        Assert.Contains("You need C# daily", why.QuerySelector("blockquote")!.TextContent);
        Assert.DoesNotContain("Go (required)", why.TextContent);
        Assert.Contains("Go is a must", cards.Single(c => c.TextContent.Contains("Missing requirements", StringComparison.Ordinal)).TextContent);
        var unknown = cards.Single(c => c.TextContent.Contains("Unknown information", StringComparison.Ordinal)).TextContent;
        Assert.Contains("the job did not say", unknown);
        Assert.Contains("your profile does not say", unknown);
        Assert.Equal(8, cut.FindAll(".wp-match__table tbody tr").Count);
        Assert.Contains("COBOL", cards.Single(c => c.TextContent.Contains("Unverified", StringComparison.Ordinal)).TextContent);
        Assert.Equal("https://boards.test/1", cut.Find("a[target=_blank]").GetAttribute("href"));
    }

    // covers: AC-5
    [Fact]
    public void A_blocked_match_lists_its_blocker()
    {
        _api.Match = Detail(score: 20, blocked: true);

        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        Assert.Contains("We do not sponsor visas", cut.FindAll(".wp-card").Single(c => c.TextContent.Contains("Blockers", StringComparison.Ordinal)).TextContent);
        Assert.Contains("Blocker", cut.Find(".wp-match__summary").TextContent);
    }

    // covers: AC-8
    [Fact]
    public void A_failed_extraction_shows_the_notice_and_its_reason()
    {
        _api.Match = Detail(failed: true);

        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        var notice = cut.FindAll(".wp-match__banner").Single(b => b.TextContent.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains("provider down", notice.TextContent);
    }

    // covers: AC-3
    [Fact]
    public void A_null_score_says_there_is_not_enough_information()
    {
        _api.Match = Detail(score: null);

        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        Assert.Contains(cut.FindAll(".wp-match__banner"), b => b.TextContent.Contains("Not enough information to score", StringComparison.Ordinal));
    }

    // covers: AC-10
    [Theory]
    [InlineData(true, "Rescore queued.")]
    [InlineData(false, "A rescore is already running for this job.")]
    public void Rescore_reports_whether_it_was_queued(bool queued, string message)
    {
        _api.Match = Detail();
        _api.Queued = queued;
        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        cut.Find(".wp-match__summary button").Click();

        Assert.StartsWith(message, cut.Find(".wp-match__message").TextContent);
        Assert.Equal([(JobId, ProfileId)], _api.Rescored);
    }

    // covers: AC-10 (review finding: a failed rescore hid the panel)
    [Fact]
    public void A_failed_rescore_shows_its_error_beside_the_panel()
    {
        _api.Match = Detail();
        _api.RescoreError = "That job no longer exists.";
        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        cut.Find(".wp-match__summary button").Click();

        Assert.Equal("That job no longer exists.", cut.Find("[role=alert]").TextContent);
        Assert.Equal("87", cut.Find(".wp-match__big-score").TextContent);
    }

    [Fact]
    public void A_posting_url_that_is_not_http_is_not_linked()
    {
        _api.Match = Detail() with { JobUrl = "javascript:alert(1)" };

        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        Assert.Empty(cut.FindAll("a[target=_blank]"));
    }

    // covers: AC-15
    [Fact]
    public void A_job_with_no_match_shows_the_message_and_offers_a_rescore()
    {
        var cut = Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

        Assert.Equal("This job has no match for your profile yet.", cut.Find("[role=alert]").TextContent);
        Assert.Contains("Rescore this job", cut.Find("button").TextContent);
    }

    private sealed class FakeJobsApiClient : IJobsApiClient
    {
        public MatchListDto List { get; set; } = new([], 0, 1, 25, false);
        public string? ListError { get; set; }
        public JobMatchDetailDto? Match { get; set; }
        public bool Queued { get; set; } = true;
        public string? RescoreError { get; set; }
        public List<(Guid ProfileId, int Page)> Listed { get; } = [];
        public List<(Guid JobId, Guid ProfileId)> Rescored { get; } = [];

        public Task<ApiResult<MatchListDto>> ListMatchesAsync(Guid profileId, int page, CancellationToken cancellationToken = default)
        {
            Listed.Add((profileId, page));
            return Task.FromResult(ListError is null ? ApiResult<MatchListDto>.Ok(List with { Page = page }) : ApiResult<MatchListDto>.Fail(ListError));
        }

        public Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Match is null ? ApiResult<JobMatchDetailDto>.Fail("This job has no match for your profile yet.") : ApiResult<JobMatchDetailDto>.Ok(Match));

        public Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
        {
            Rescored.Add((jobId, profileId));
            return Task.FromResult(RescoreError is null
                ? ApiResult<RescoreMatchResponse>.Ok(new RescoreMatchResponse(Queued))
                : ApiResult<RescoreMatchResponse>.Fail(RescoreError));
        }
    }
}
