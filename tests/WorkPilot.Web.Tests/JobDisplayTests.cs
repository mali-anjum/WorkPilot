using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Web.Client.Shared;
using WorkPilot.Web.Features.Jobs;

namespace WorkPilot.Web.Tests;

// How the jobs pages word and color a job (spec 0021, AC-1, AC-3, AC-5, AC-9): the pure JobDisplay
// rules on their own, so a wording or band change is caught without rendering a page.
public class JobDisplayTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static JobListItemDto Item(int? score, string? confidence) =>
        new(Guid.NewGuid(), "Backend Engineer", "Acme", null, null, At, At, [], score, confidence, false, At, JobMatchStatuses.Scored, false);

    // covers: spec 0021 AC-3
    [Theory]
    [InlineData(null, false, StatusKind.Neutral)]
    [InlineData(null, true, StatusKind.Neutral)]
    [InlineData(95, true, StatusKind.Danger)]
    [InlineData(100, false, StatusKind.Success)]
    [InlineData(70, false, StatusKind.Success)]
    [InlineData(69, false, StatusKind.Warning)]
    [InlineData(40, false, StatusKind.Warning)]
    [InlineData(39, false, StatusKind.Danger)]
    [InlineData(0, false, StatusKind.Danger)]
    public void The_score_band_is_blocked_then_70_then_40(int? score, bool blocked, StatusKind expected)
    {
        Assert.Equal(expected, JobDisplay.ScoreStatus(score, blocked));
    }

    // covers: spec 0021 AC-3
    [Fact]
    public void The_score_label_adds_the_confidence_when_known()
    {
        Assert.Equal("82 · High", JobDisplay.ScoreLabel(Item(82, "High")));
        Assert.Equal("82", JobDisplay.ScoreLabel(Item(82, null)));
    }

    // covers: spec 0021 AC-3
    [Theory]
    [InlineData(JobMatchStatuses.NotEnoughInfo, "Not enough information")]
    [InlineData(JobMatchStatuses.Pending, "Scoring…")]
    [InlineData(JobMatchStatuses.ProfileIncomplete, "Complete your profile")]
    [InlineData(JobMatchStatuses.Scored, null)]
    [InlineData("SomethingNew", null)]
    public void An_unscored_reason_names_why_and_a_scored_row_has_none(string status, string? expected)
    {
        Assert.Equal(expected, JobDisplay.NotScoredReason(status));
    }

    // covers: spec 0021 AC-3
    [Theory]
    [InlineData("greenhouse", "Greenhouse")]
    [InlineData("lever", "Lever")]
    [InlineData("L", "L")]
    [InlineData("", "")]
    public void A_source_label_capitalizes_the_type(string type, string expected)
    {
        Assert.Equal(expected, JobDisplay.SourceLabel(type));
    }

    // covers: spec 0021 AC-5
    [Fact]
    public void The_salary_reads_by_which_ends_are_known()
    {
        Assert.Null(JobDisplay.Salary(null, null));
        Assert.Equal("From 90,000", JobDisplay.Salary(90000m, null));
        Assert.Equal("Up to 120,000", JobDisplay.Salary(null, 120000m));
        Assert.Equal("100,000", JobDisplay.Salary(100000m, 100000m));
        Assert.Equal("90,000 to 120,000", JobDisplay.Salary(90000m, 120000m));
    }

    // covers: spec 0021 AC-1
    [Fact]
    public void The_page_window_keeps_the_ends_and_two_either_side_with_gaps()
    {
        Assert.Equal([1], JobDisplay.PageWindow(1, 1));
        Assert.Equal([1, 2, 3, 4, 5], JobDisplay.PageWindow(3, 5));
        Assert.Equal([1, 2, 3, null, 11], JobDisplay.PageWindow(1, 11));
        Assert.Equal([1, null, 4, 5, 6, 7, 8, null, 11], JobDisplay.PageWindow(6, 11));
        Assert.Equal([1, null, 9, 10, 11], JobDisplay.PageWindow(11, 11));
    }

    // covers: spec 0021 AC-1
    [Fact]
    public void A_page_window_has_no_gap_when_every_page_is_near_the_current_one()
    {
        // Page 4 of 7 reaches every page, and no two gaps ever sit side by side.
        var window = JobDisplay.PageWindow(4, 7);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], window);
        Assert.DoesNotContain(window.Zip(window.Skip(1)), pair => pair.First is null && pair.Second is null);
    }

    // covers: spec 0021 AC-1
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 1)]
    [InlineData(26, 2)]
    [InlineData(260, 11)]
    public void The_page_count_rounds_up_and_is_at_least_one(int total, int expected)
    {
        Assert.Equal(expected, JobDisplay.PageCount(total, JobDisplay.PageSize));
    }

    // covers: spec 0021 AC-5 (a feed's URL is only linked when it is the web)
    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/1", true)]
    [InlineData("http://jobs.lever.co/acme/1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com/job", false)]
    [InlineData("/jobs/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_http_or_https_url_is_a_web_url(string? url, bool expected)
    {
        Assert.Equal(expected, JobDisplay.IsWebUrl(url));
    }
}
