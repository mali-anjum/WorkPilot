using System.Net;
using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages.Jobs;
using WorkPilot.Web.Features.Common;
using WorkPilot.Web.Features.Jobs;

namespace WorkPilot.Web.Tests;

// The /jobs list, the /jobs/{id} detail with its match panel, and the Job sources drawer
// (spec 0019, AC-9, AC-10; spec 0021, AC-1 to AC-8), rendered with a fake IJobsApiClient standing
// in for the internal Api and a signed in session carrying the profile_id claim. The real
// ordering, filters and scoring are covered in Api.Tests.
public class JobsTests : BunitContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly Guid JobId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000002");
    private static readonly Guid SourceId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000003");
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeJobsApiClient _api = new();

    public JobsTests()
    {
        Services.AddSingleton<IJobsApiClient>(_api);
        Services.AddScoped<BrowserTimeZone>();
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, ProfileId.ToString()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/interop.js").Setup<string?>("timeZone").SetResult("UTC");
    }

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private static JobListItemDto Item(string title, int? score, bool blocked = false, string status = JobMatchStatuses.Scored, bool dismissed = false) =>
        new(Guid.NewGuid(), title, "Acme", "Berlin", "Remote", At, At, ["Greenhouse", "Lever"], score, score is null ? null : "High", blocked, score is null ? null : At,
            score is null && status == JobMatchStatuses.Scored ? JobMatchStatuses.Pending : status, dismissed);

    private IRenderedComponent<Jobs> RenderList(string query = "")
    {
        Navigation.NavigateTo($"/jobs{query}");
        return Render<Jobs>();
    }

    // ---------- the list ----------

    // covers: spec 0021 AC-8
    [Fact]
    public void No_jobs_yet_offers_to_add_a_source()
    {
        var cut = RenderList();

        Assert.Equal("Jobs", cut.Find("h1").TextContent);
        Assert.Equal("No jobs yet", cut.Find(".wp-empty-state__title").TextContent);
        cut.FindAll("button").Single(b => b.TextContent == "Add a job source").Click();
        Assert.NotNull(cut.Find("[role=dialog]"));
    }

    // covers: spec 0021 AC-1, AC-3
    [Fact]
    public void Each_row_shows_its_score_confidence_badges_and_link()
    {
        _api.List = new([Item("Backend Engineer", 82), Item("Platform Engineer", 20, blocked: true)], 2, 1, 25, false);

        var cut = RenderList();

        var rows = cut.FindAll(".wp-jobs__row");
        Assert.Equal(2, rows.Count);
        Assert.Equal("82 · High", rows[0].QuerySelector(".wp-badge")!.TextContent.Trim());
        Assert.Contains("Greenhouse", rows[0].TextContent);
        Assert.Contains("Lever", rows[0].TextContent);
        Assert.Contains("Remote", rows[0].TextContent);
        Assert.Contains("Acme · Berlin", rows[0].TextContent);
        Assert.Contains("Posted", rows[0].QuerySelector("time")!.TextContent);
        Assert.Equal($"/jobs/{_api.List.Items[0].JobId}", rows[0].QuerySelector(".wp-jobs__title")!.GetAttribute("href"));
        Assert.Contains("Blocker", rows[1].TextContent);
        Assert.Contains("2 jobs", cut.Find(".wp-jobs__toolbar").TextContent);
    }

    // covers: spec 0021 AC-3, AC-7
    [Theory]
    [InlineData(JobMatchStatuses.Pending, "Scoring…")]
    [InlineData(JobMatchStatuses.NotEnoughInfo, "Not enough information")]
    [InlineData(JobMatchStatuses.ProfileIncomplete, "Complete your profile")]
    public void An_unscored_row_says_why(string status, string reason)
    {
        _api.List = new([Item("Data Analyst", null, status: status)], 1, 1, 25, status == JobMatchStatuses.ProfileIncomplete);

        var cut = RenderList();

        var score = cut.Find(".wp-jobs__score");
        Assert.Contains("Not scored", score.TextContent);
        Assert.Equal(reason, score.QuerySelector(".wp-jobs__reason")!.TextContent);
        if (status == JobMatchStatuses.ProfileIncomplete)
        {
            Assert.Equal("/profile", score.QuerySelector("a")!.GetAttribute("href"));
            Assert.Equal("/profile", cut.Find(".wp-match__banner a").GetAttribute("href"));
        }
        else
        {
            Assert.Empty(cut.FindAll(".wp-match__banner"));
        }
    }

    // covers: spec 0021 AC-2
    [Fact]
    public void The_url_query_becomes_the_search()
    {
        RenderList($"?q=dev&company=Acme&location=berlin&remote=Remote&source={SourceId}&minScore=70&posted=7&salaryMin=50000&hideBlocked=true&dismissed=true&sort=newest&page=3");

        var query = Assert.Single(_api.Searched);
        Assert.Equal(
            new JobListQuery
            {
                Page = 3,
                PageSize = 25,
                Q = "dev",
                Company = "Acme",
                Location = "berlin",
                RemoteType = "Remote",
                SourceId = SourceId,
                MinScore = 70,
                PostedWithinDays = 7,
                SalaryMin = 50000,
                HideBlocked = true,
                IncludeDismissed = true,
                Sort = JobSorts.Newest,
            },
            query);
    }

    // covers: spec 0021 AC-2
    [Fact]
    public void Changing_a_filter_goes_back_to_page_1()
    {
        _api.List = new([Item("Backend Engineer", 82)], 80, 3, 25, false);
        _api.Facets = new(["Acme", "Globex"], ["Remote"], []);
        var cut = RenderList("?page=3&minScore=50");

        cut.FindAll("select")[0].Change("Globex");

        Assert.Equal("http://localhost/jobs?minScore=50&company=Globex", Navigation.Uri);
    }

    // covers: spec 0021 AC-2
    [Fact]
    public void A_typed_filter_applies_on_submit()
    {
        _api.List = new([Item("Backend Engineer", 82)], 1, 1, 25, false);
        var cut = RenderList("?page=2");

        cut.Find("input[type=search]").Input("platform");
        cut.Find("form[role=search]").Submit();

        Assert.Equal("http://localhost/jobs?q=platform", Navigation.Uri);
    }

    // covers: spec 0021 AC-2, AC-8
    [Fact]
    public void No_match_for_the_filters_offers_to_clear_them()
    {
        var cut = RenderList("?minScore=90&remote=Remote&sort=newest");

        Assert.Equal("No jobs match these filters", cut.Find(".wp-empty-state__title").TextContent);
        cut.Find(".wp-jobs__empty-action button").Click();

        // Clearing keeps the sort: it is not a filter.
        Assert.Equal("http://localhost/jobs?sort=newest", Navigation.Uri);
    }

    // covers: spec 0021 AC-1
    [Fact]
    public void Page_numbers_link_to_each_page_with_the_filters_kept()
    {
        _api.List = new(Enumerable.Range(0, 25).Select(i => Item($"Job {i}", 50)).ToList(), 260, 1, 25, false);

        var cut = RenderList("?minScore=50");

        var pages = cut.FindAll(".wp-jobs__page");
        Assert.Equal(["1", "2", "3", "11"], pages.Select(p => p.TextContent));
        Assert.Equal("page", pages[0].GetAttribute("aria-current"));
        Assert.Equal("http://localhost/jobs?minScore=50&page=2", pages[1].GetAttribute("href"));
        Assert.Single(cut.FindAll(".wp-jobs__gap"));
    }

    // covers: spec 0021 AC-4
    [Fact]
    public void Dismiss_hides_the_row_and_undo_brings_it_back()
    {
        var job = Item("Backend Engineer", 82);
        _api.List = new([job, Item("Platform Engineer", 60)], 2, 1, 25, false);
        var cut = RenderList();

        cut.Find($"button[aria-label='Dismiss {job.Title}']").Click();

        Assert.Equal([(job.JobId, ProfileId)], _api.Dismissed);
        Assert.Single(cut.FindAll(".wp-jobs__row"));
        Assert.Contains("Dismissed Backend Engineer", cut.Find(".wp-jobs__notice").TextContent);

        cut.Find(".wp-jobs__notice button").Click();

        Assert.Equal([(job.JobId, ProfileId)], _api.Undone);
        Assert.Empty(cut.FindAll(".wp-jobs__notice"));
        Assert.Equal(2, _api.Searched.Count);
    }

    // covers: spec 0021 AC-8
    [Fact]
    public void A_failed_dismiss_shows_an_error_and_keeps_the_row()
    {
        var job = Item("Backend Engineer", 82);
        _api.List = new([job], 1, 1, 25, false);
        _api.DismissError = "That job no longer exists.";
        var cut = RenderList();

        cut.Find($"button[aria-label='Dismiss {job.Title}']").Click();

        Assert.Equal("That job no longer exists.", cut.Find("[role=alert]").TextContent);
        Assert.Single(cut.FindAll(".wp-jobs__row"));
        Assert.Empty(cut.FindAll(".wp-jobs__notice"));
    }

    // covers: spec 0021 AC-4
    [Fact]
    public void Show_dismissed_marks_dismissed_rows_with_an_undo()
    {
        var job = Item("Backend Engineer", 82, dismissed: true);
        _api.List = new([job], 1, 1, 25, false);
        var cut = RenderList("?dismissed=true");

        var row = cut.Find(".wp-jobs__row");
        Assert.Contains("wp-jobs__row--dismissed", row.ClassList);
        cut.Find($"button[aria-label='Undo dismiss of {job.Title}']").Click();

        Assert.Equal([(job.JobId, ProfileId)], _api.Undone);
        Assert.DoesNotContain("wp-jobs__row--dismissed", cut.Find(".wp-jobs__row").ClassList);
    }

    // covers: spec 0021 AC-8
    [Fact]
    public void A_failed_list_shows_the_message_and_a_retry()
    {
        _api.ListError = "minScore must be 0 to 100.";

        var cut = RenderList("?minScore=500");

        Assert.Equal("minScore must be 0 to 100.", cut.Find(".wp-jobs__error p").TextContent);
        cut.FindAll(".wp-jobs__error button").Single(b => b.TextContent == "Retry").Click();
        Assert.Equal(2, _api.Searched.Count);
    }

    // ---------- the detail ----------

    private static JobDetailView Job(bool deleted = false, bool dismissed = false, string url = "https://boards.test/1") => new(
        JobId,
        "Backend Engineer",
        "Acme",
        "Berlin",
        "Remote",
        "Build APIs.\nShip often.",
        At,
        url,
        At,
        At,
        1m,
        2,
        [
            new JobLinkView(Guid.NewGuid(), SourceId, "Greenhouse", "1", url, At, At, 1m, true, null),
            new JobLinkView(Guid.NewGuid(), Guid.NewGuid(), "Lever", "abc", "https://jobs.lever.test/abc", At, At, 0.9m, false, null),
        ],
        90000m,
        120000m,
        deleted,
        dismissed);

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

    private IRenderedComponent<JobDetail> RenderDetail() => Render<JobDetail>(p => p.Add(x => x.JobId, JobId));

    // covers: spec 0021 AC-5
    [Fact]
    public void The_detail_shows_the_job_on_the_left_and_the_analysis_on_the_right()
    {
        _api.Job = Job();
        _api.Match = Detail();

        var cut = RenderDetail();

        Assert.Equal("Backend Engineer", cut.Find("h1").TextContent);
        var main = cut.Find(".wp-jobs__detail-main");
        Assert.Contains("90,000 to 120,000", main.TextContent);
        Assert.Contains("Build APIs.", main.QuerySelector(".wp-jobs__description")!.TextContent);
        var links = main.QuerySelectorAll(".wp-jobs__link");
        Assert.Equal(2, links.Length);
        Assert.Contains("Greenhouse", links[0].TextContent);
        Assert.Contains("Primary", links[0].TextContent);
        Assert.Equal("https://jobs.lever.test/abc", links[1].QuerySelector("a")!.GetAttribute("href"));
        var side = cut.Find(".wp-jobs__detail-side");
        Assert.Equal("87", side.QuerySelector(".wp-match__big-score")!.TextContent);
        Assert.Empty(cut.FindAll(".wp-match__banner"));
    }

    // covers: spec 0019 AC-2, AC-10
    [Fact]
    public void The_panel_shows_the_score_evidence_gaps_and_breakdown()
    {
        _api.Job = Job();
        _api.Match = Detail();

        var cut = RenderDetail();

        var cards = cut.FindAll(".wp-match-panel .wp-card");
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
    }

    // covers: spec 0019 AC-5
    [Fact]
    public void A_blocked_match_lists_its_blocker()
    {
        _api.Job = Job();
        _api.Match = Detail(score: 20, blocked: true);

        var cut = RenderDetail();

        Assert.Contains("We do not sponsor visas", cut.FindAll(".wp-card").Single(c => c.TextContent.Contains("Blockers", StringComparison.Ordinal)).TextContent);
        Assert.Contains("Blocker", cut.Find(".wp-match-panel__summary").TextContent);
    }

    // covers: spec 0019 AC-8
    [Fact]
    public void A_failed_extraction_shows_the_notice_and_its_reason()
    {
        _api.Job = Job();
        _api.Match = Detail(failed: true);

        var cut = RenderDetail();

        var notice = cut.FindAll(".wp-match__banner").Single(b => b.TextContent.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains("provider down", notice.TextContent);
    }

    // covers: spec 0019 AC-3
    [Fact]
    public void A_null_score_says_there_is_not_enough_information()
    {
        _api.Job = Job();
        _api.Match = Detail(score: null);

        var cut = RenderDetail();

        Assert.Contains(cut.FindAll(".wp-match__banner"), b => b.TextContent.Contains("Not enough information to score", StringComparison.Ordinal));
    }

    // covers: spec 0019 AC-10
    [Theory]
    [InlineData(true, "Rescore queued.")]
    [InlineData(false, "A rescore is already running for this job.")]
    public void Rescore_reports_whether_it_was_queued(bool queued, string message)
    {
        _api.Job = Job();
        _api.Match = Detail();
        _api.Queued = queued;
        var cut = RenderDetail();

        cut.Find(".wp-match-panel__summary button").Click();

        Assert.StartsWith(message, cut.Find(".wp-match__message").TextContent);
        Assert.Equal([(JobId, ProfileId)], _api.Rescored);
    }

    // covers: spec 0019 AC-10 (review finding: a failed rescore hid the panel)
    [Fact]
    public void A_failed_rescore_shows_its_error_beside_the_panel()
    {
        _api.Job = Job();
        _api.Match = Detail();
        _api.RescoreError = "That job no longer exists.";
        var cut = RenderDetail();

        cut.Find(".wp-match-panel__summary button").Click();

        Assert.Equal("That job no longer exists.", cut.Find("[role=alert]").TextContent);
        Assert.Equal("87", cut.Find(".wp-match__big-score").TextContent);
    }

    [Fact]
    public void A_posting_url_that_is_not_http_is_not_linked()
    {
        _api.Job = Job(url: "javascript:alert(1)");

        var cut = RenderDetail();

        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href")?.StartsWith("javascript", StringComparison.Ordinal) == true);
    }

    // covers: spec 0019 AC-15
    [Fact]
    public void A_job_with_no_match_says_so_and_offers_to_score_it()
    {
        _api.Job = Job();

        var cut = RenderDetail();

        var side = cut.Find(".wp-jobs__detail-side");
        Assert.Contains("Not scored yet.", side.TextContent);
        Assert.Contains("This job has no match for your profile yet.", side.TextContent);
        side.QuerySelector("button")!.Click();
        Assert.Equal([(JobId, ProfileId)], _api.Rescored);
    }

    // covers: spec 0021 AC-5
    [Fact]
    public void A_soft_deleted_job_shows_no_longer_listed_and_no_rescore()
    {
        _api.Job = Job(deleted: true);
        _api.Match = Detail();

        var cut = RenderDetail();

        Assert.Contains("No longer listed.", cut.Find(".wp-match__banner").TextContent);
        Assert.Empty(cut.FindAll(".wp-match-panel__summary button"));
        Assert.Contains("Build APIs.", cut.Find(".wp-jobs__detail-main").TextContent);
    }

    // covers: spec 0021 AC-5
    [Fact]
    public void An_unknown_id_shows_not_found()
    {
        var cut = RenderDetail();

        Assert.Equal("Job not found", cut.Find(".wp-empty-state__title").TextContent);
    }

    // covers: spec 0021 AC-4
    [Fact]
    public void Dismiss_from_the_detail_page_flips_to_undo()
    {
        _api.Job = Job();
        var cut = RenderDetail();

        cut.FindAll("button").Single(b => b.TextContent == "Dismiss").Click();

        Assert.Equal([(JobId, ProfileId)], _api.Dismissed);
        Assert.Contains("You dismissed this job", cut.Find(".wp-jobs__notice").TextContent);
        cut.FindAll("button").Single(b => b.TextContent == "Undo dismiss").Click();
        Assert.Equal([(JobId, ProfileId)], _api.Undone);
        Assert.Empty(cut.FindAll(".wp-jobs__notice"));
    }

    // ---------- the Job sources drawer ----------

    private IRenderedComponent<Jobs> OpenDrawer()
    {
        var cut = RenderList();
        cut.FindAll("button").First(b => b.TextContent == "Job sources").Click();
        return cut;
    }

    // covers: spec 0021 AC-6
    [Fact]
    public void The_drawer_lists_each_source_with_its_jobs_and_last_run()
    {
        _api.Sources =
        [
            new(SourceId, "Greenhouse", "greenhouse:gitlab", "GitLab", 74, At, 12, 3),
            new(Guid.NewGuid(), "Lever", "lever:netflix", null, 0, null, null, null),
        ];

        var cut = OpenDrawer();

        var items = cut.FindAll(".wp-sources__item");
        Assert.Equal(2, items.Count);
        Assert.Contains("gitlab", items[0].TextContent);
        Assert.Contains("GitLab", items[0].TextContent);
        Assert.Contains("74 jobs", items[0].TextContent);
        Assert.Contains("12 new, 3 updated", items[0].TextContent);
        Assert.Contains("Never run", items[1].TextContent);
    }

    // covers: spec 0021 AC-6
    [Fact]
    public void Run_now_queues_the_source()
    {
        _api.Sources = [new(SourceId, "Greenhouse", "greenhouse:gitlab", null, 1, At, 1, 0)];
        var cut = OpenDrawer();

        cut.Find("button[aria-label='Run gitlab now']").Click();

        Assert.Equal([SourceId], _api.Ran);
        Assert.Contains("Run queued for gitlab", cut.Find(".wp-drawer .wp-match__message").TextContent);
    }

    // covers: spec 0021 AC-6
    [Fact]
    public void A_rejected_board_shows_the_error_next_to_its_field()
    {
        _api.AddErrors = new Dictionary<string, string[]> { ["boardToken"] = ["That is not a valid Lever board."] };
        var cut = OpenDrawer();

        cut.Find(".wp-drawer select").Change("lever");
        cut.Find(".wp-drawer input[type=text]").Change("Not A Board!");
        cut.Find(".wp-drawer form").Submit();

        Assert.Equal(new TriggerJobIngestionRequest("lever", "Not A Board!"), _api.Added.Single());
        Assert.Equal("That is not a valid Lever board.", cut.Find("#wp-source-board-error").TextContent);
        Assert.Equal("true", cut.Find(".wp-drawer input[type=text]").GetAttribute("aria-invalid"));
    }

    // covers: spec 0021 AC-6
    [Fact]
    public void Adding_a_board_queues_it_and_refreshes_the_list()
    {
        var cut = OpenDrawer();

        cut.Find(".wp-drawer input[type=text]").Change("gitlab");
        cut.FindAll(".wp-drawer input[type=text]")[1].Change("GitLab");
        cut.Find(".wp-drawer form").Submit();

        Assert.Equal(new TriggerJobIngestionRequest("greenhouse", "gitlab", CompanyName: "GitLab"), _api.Added.Single());
        Assert.Contains("Added gitlab", cut.Find(".wp-drawer .wp-match__message").TextContent);
        Assert.Equal(2, _api.SourceLoads);
    }

    // covers: spec 0021 AC-6
    [Fact]
    public void Escape_closes_the_drawer()
    {
        var cut = OpenDrawer();

        cut.Find("[role=dialog]").KeyDown("Escape");

        Assert.Empty(cut.FindAll("[role=dialog]"));
    }

    private sealed class FakeJobsApiClient : IJobsApiClient
    {
        public MatchListDto List { get; set; } = new([], 0, 1, 25, false);
        public string? ListError { get; set; }
        public JobFacetsDto Facets { get; set; } = new([], [], []);
        public JobDetailView? Job { get; set; }
        public JobMatchDetailDto? Match { get; set; }
        public bool Queued { get; set; } = true;
        public string? RescoreError { get; set; }
        public string? DismissError { get; set; }
        public IReadOnlyList<JobSourceSummaryDto> Sources { get; set; } = [];
        public IReadOnlyDictionary<string, string[]>? AddErrors { get; set; }
        public List<JobListQuery> Searched { get; } = [];
        public List<(Guid JobId, Guid ProfileId)> Rescored { get; } = [];
        public List<(Guid JobId, Guid ProfileId)> Dismissed { get; } = [];
        public List<(Guid JobId, Guid ProfileId)> Undone { get; } = [];
        public List<TriggerJobIngestionRequest> Added { get; } = [];
        public List<Guid> Ran { get; } = [];
        public int SourceLoads { get; private set; }

        public Task<ApiResult<MatchListDto>> SearchAsync(Guid profileId, JobListQuery query, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ProfileId, profileId);
            Searched.Add(query);
            return Task.FromResult(ListError is null ? ApiResult<MatchListDto>.Ok(List) : ApiResult<MatchListDto>.Fail(ListError));
        }

        public Task<ApiResult<JobFacetsDto>> GetFacetsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<JobFacetsDto>.Ok(Facets));

        public Task<ApiResult<JobDetailView>> GetJobAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Job is null
                ? ApiResult<JobDetailView>.Fail("That job does not exist.", status: HttpStatusCode.NotFound)
                : ApiResult<JobDetailView>.Ok(Job));

        public Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Match is null
                ? ApiResult<JobMatchDetailDto>.Fail("This job has no match for your profile yet.", status: HttpStatusCode.NotFound)
                : ApiResult<JobMatchDetailDto>.Ok(Match));

        public Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
        {
            Rescored.Add((jobId, profileId));
            return Task.FromResult(RescoreError is null
                ? ApiResult<RescoreMatchResponse>.Ok(new RescoreMatchResponse(Queued))
                : ApiResult<RescoreMatchResponse>.Fail(RescoreError));
        }

        public Task<ApiResult<bool>> DismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
        {
            if (DismissError is not null)
            {
                return Task.FromResult(ApiResult<bool>.Fail(DismissError));
            }

            Dismissed.Add((jobId, profileId));
            return Task.FromResult(ApiResult<bool>.Ok(true));
        }

        public Task<ApiResult<bool>> UndoDismissAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default)
        {
            Undone.Add((jobId, profileId));
            return Task.FromResult(ApiResult<bool>.Ok(true));
        }

        public Task<ApiResult<IReadOnlyList<JobSourceSummaryDto>>> ListSourcesAsync(CancellationToken cancellationToken = default)
        {
            SourceLoads++;
            return Task.FromResult(ApiResult<IReadOnlyList<JobSourceSummaryDto>>.Ok(Sources));
        }

        public Task<ApiResult<TriggerJobIngestionResponse>> AddSourceAsync(TriggerJobIngestionRequest request, CancellationToken cancellationToken = default)
        {
            Added.Add(request);
            return Task.FromResult(AddErrors is null
                ? ApiResult<TriggerJobIngestionResponse>.Ok(new TriggerJobIngestionResponse(SourceId, "1"))
                : ApiResult<TriggerJobIngestionResponse>.Fail("Invalid.", AddErrors, HttpStatusCode.BadRequest));
        }

        public Task<ApiResult<TriggerJobIngestionResponse>> RunSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
        {
            Ran.Add(sourceId);
            return Task.FromResult(ApiResult<TriggerJobIngestionResponse>.Ok(new TriggerJobIngestionResponse(sourceId, "2")));
        }
    }
}
