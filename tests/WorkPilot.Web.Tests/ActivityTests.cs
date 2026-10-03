using System.Net;
using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Audit;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Audit;
using WorkPilot.Web.Components.Pages.Audit;
using WorkPilot.Web.Features.Audit;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Tests;

// The /activity page (spec 0011), rendered with a fake IActivityApiClient standing in for the
// internal Api, a signed in session carrying the profile_id claim, and the browser reporting
// Asia/Tokyo as its time zone. The query itself is covered in Api.Tests/ActivityFeedTests.
public class ActivityTests : BunitContext
{
    private static readonly Guid Me = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeActivityApiClient _api = new();

    public ActivityTests()
    {
        Services.AddSingleton<IActivityApiClient>(_api);
        Services.AddScoped<BrowserTimeZone>();
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, Me.ToString()));
        JSInterop.SetupModule("./js/interop.js").Setup<string?>("timeZone").SetResult("Asia/Tokyo");
    }

    private static ActivityEntryDto Entry(int minutesAgo, string action = "list_my_profile", string actor = "Agent", string category = "Agent", string targetType = "Profile", string? payload = null) =>
        new(Guid.CreateVersion7(), T0.AddMinutes(-minutesAgo), actor, action, category, targetType, Guid.CreateVersion7(), payload);

    private static ActivityPageDto Page(IReadOnlyList<ActivityEntryDto> items, bool more = false) =>
        new(items, more ? items[^1].OccurredAt : null, more ? items[^1].Id : null);

    private IRenderedComponent<Activity> RenderAt(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<Activity>();
        cut.WaitForState(() => cut.FindAll("[role=status]").Count == 0);
        return cut;
    }

    // covers: AC-1
    [Fact]
    public void Lists_entries_newest_first_as_the_api_returned_them()
    {
        _api.Pages.Enqueue(Page([Entry(1, "JobsMerged", category: "Jobs", targetType: "Job"), Entry(5, "PlanningFailed", category: "Errors", targetType: "AgentRun")]));

        var cut = RenderAt("/activity");

        Assert.Equal(["Merged duplicate jobs", "Agent planning failed"], cut.FindAll(".wp-activity__summary").Select(s => s.TextContent));
        Assert.Equal((null, (DateTimeOffset?)null, (Guid?)null, 50), _api.Calls.Single());
    }

    // covers: AC-1
    [Fact]
    public void Shows_who_acted_as_you_agent_or_system()
    {
        _api.Pages.Enqueue(Page([
            Entry(1, "ApprovalApproved", actor: Me.ToString(), targetType: "AgentStep"),
            Entry(2, "ApprovalRequested", targetType: "AgentStep"),
            Entry(3, "ApprovalRejected", actor: Guid.CreateVersion7().ToString(), targetType: "AgentStep"),
        ]));

        var cut = RenderAt("/activity");

        var who = cut.FindAll(".wp-activity__what .wp-activity__muted").Select(m => m.TextContent.Trim().Split(' ')[0]);
        Assert.Equal(["You", "Agent", "System"], who);
    }

    // covers: AC-1
    [Fact]
    public void The_exact_time_shows_in_the_browsers_zone()
    {
        _api.Pages.Enqueue(Page([Entry(0)]));

        var cut = RenderAt("/activity");

        cut.WaitForAssertion(() => Assert.Equal("Oct 3, 2026, 18:00:00 (Asia/Tokyo)", cut.Find("time").GetAttribute("title")));
        Assert.Equal(T0.ToString("O"), cut.Find("time").GetAttribute("datetime"));
    }

    // covers: AC-1, AC-8
    [Fact]
    public void Load_more_sends_the_cursor_and_appends_the_next_page()
    {
        var first = Enumerable.Range(0, 3).Select(i => Entry(i)).ToList();
        var second = Enumerable.Range(3, 2).Select(i => Entry(i)).ToList();
        _api.Pages.Enqueue(Page(first, more: true));
        _api.Pages.Enqueue(Page(second));
        var cut = RenderAt("/activity");

        cut.Find(".wp-activity__more button").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".wp-activity__entry").Count));
        Assert.Equal((null, first[^1].OccurredAt, first[^1].Id, 50), _api.Calls[1]);
        Assert.Contains("You have reached the start of the feed.", cut.Find(".wp-activity__more").TextContent);
    }

    // covers: AC-7
    [Fact]
    public void A_failed_load_more_keeps_the_entries_and_offers_retry()
    {
        _api.Pages.Enqueue(Page([Entry(0), Entry(1)], more: true));
        _api.Failures.Enqueue(new HttpRequestException("down"));
        _api.Pages.Enqueue(Page([Entry(2)]));
        var cut = RenderAt("/activity");

        cut.Find(".wp-activity__more button").Click();

        cut.WaitForAssertion(() => Assert.Equal("The activity feed could not be reached. Try again in a moment.", cut.Find(".wp-activity__error-text").TextContent));
        Assert.Equal(2, cut.FindAll(".wp-activity__entry").Count);
        var retry = cut.Find(".wp-activity__more button");
        Assert.Equal("Retry", retry.TextContent.Trim());

        retry.Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".wp-activity__entry").Count));
        Assert.Empty(cut.FindAll(".wp-activity__error-text"));
    }

    // covers: AC-2
    [Fact]
    public void The_category_from_the_url_filters_the_query_and_marks_its_chip_current()
    {
        _api.Pages.Enqueue(Page([Entry(0, "PlanningFailed", category: "Errors", targetType: "AgentRun")]));

        var cut = RenderAt("/activity?category=errors");

        Assert.Equal("errors", _api.Calls.Single().Category);
        var current = cut.Find(".wp-activity__chip[aria-current=page]");
        Assert.Equal("Errors", current.TextContent);
        Assert.Equal("/activity?category=errors", current.GetAttribute("href"));
        Assert.Single(cut.FindAll("[aria-current]"));
    }

    // covers: AC-2
    [Fact]
    public void All_is_current_when_no_category_is_given()
    {
        _api.Pages.Enqueue(Page([Entry(0)]));

        var cut = RenderAt("/activity");

        Assert.Equal("All", cut.Find(".wp-activity__chip[aria-current=page]").TextContent);
        Assert.Equal("/activity", cut.Find(".wp-activity__chip[aria-current=page]").GetAttribute("href"));
    }

    // covers: AC-2
    [Fact]
    public void Changing_the_category_reloads_the_first_page_for_it()
    {
        _api.Pages.Enqueue(Page([Entry(0), Entry(1)]));
        _api.Pages.Enqueue(Page([Entry(2, "PlanningFailed", category: "Errors", targetType: "AgentRun")]));
        var cut = RenderAt("/activity");

        // A chip is a link: only the query string changes, and the page stays.
        Services.GetRequiredService<NavigationManager>().NavigateTo("/activity?category=errors");

        cut.WaitForAssertion(() => Assert.Equal(["Agent planning failed"], cut.FindAll(".wp-activity__summary").Select(s => s.TextContent)));
        Assert.Equal((DateTimeOffset?)null, _api.Calls[1].Before);
        Assert.Equal("errors", _api.Calls[1].Category);
    }

    // covers: AC-7
    [Fact]
    public void An_empty_feed_says_nothing_has_happened_yet()
    {
        _api.Pages.Enqueue(Page([]));

        var cut = RenderAt("/activity");

        Assert.Equal("Nothing has happened yet", cut.Find(".wp-empty-state__title").TextContent);
    }

    // covers: AC-7
    [Fact]
    public void An_empty_category_says_nothing_in_this_category_yet()
    {
        _api.Pages.Enqueue(Page([]));

        var cut = RenderAt("/activity?category=email");

        Assert.Equal("Nothing in this category yet", cut.Find(".wp-empty-state__title").TextContent);
    }

    // covers: AC-7
    [Fact]
    public void A_failed_first_load_shows_the_message_and_retry_loads_again()
    {
        _api.Errors.Enqueue("Unknown category \"nope\".");
        _api.Pages.Enqueue(Page([Entry(0)]));
        var cut = RenderAt("/activity");

        var alert = cut.Find(".wp-activity__error[role=alert]");
        Assert.Contains("Unknown category \"nope\".", alert.TextContent);

        alert.QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wp-activity__entry")));
    }

    // covers: AC-4
    [Fact]
    public void Opening_an_entry_shows_its_evidence_and_closing_hides_it()
    {
        _api.Pages.Enqueue(Page([Entry(0, "PlanningFailed", category: "Errors", targetType: "AgentRun", payload: """{"reason":"PlanParseFailed"}""")]));
        var cut = RenderAt("/activity");
        var toggle = cut.Find(".wp-activity__toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));

        toggle.Click();

        Assert.Equal("true", cut.Find(".wp-activity__toggle").GetAttribute("aria-expanded"));
        var panel = cut.Find("#" + cut.Find(".wp-activity__toggle").GetAttribute("aria-controls"));
        Assert.Equal("reason", panel.QuerySelector("dt")!.TextContent);
        Assert.Equal("PlanParseFailed", panel.QuerySelector("dd")!.TextContent.Trim());

        cut.Find(".wp-activity__toggle").Click();

        Assert.Equal("false", cut.Find(".wp-activity__toggle").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".wp-activity__evidence"));
    }

    // covers: AC-5
    [Fact]
    public void A_target_with_a_page_is_a_link_and_any_other_is_plain_text()
    {
        var job = Entry(0, "JobsMerged", category: "Jobs", targetType: "Job");
        _api.Pages.Enqueue(Page([job, Entry(1)]));

        var cut = RenderAt("/activity");

        var targets = cut.FindAll(".wp-activity__target");
        Assert.Equal("A", targets[0].TagName);
        Assert.Equal($"/jobs/{job.TargetId}", targets[0].GetAttribute("href"));
        Assert.Equal("SPAN", targets[1].TagName);
        Assert.StartsWith("Profile ", targets[1].TextContent);
    }

    // covers: AC-2
    [Fact]
    public void Each_entry_shows_its_category_badge()
    {
        _api.Pages.Enqueue(Page([Entry(0, "PlanningFailed", category: "Errors", targetType: "AgentRun")]));

        var cut = RenderAt("/activity");

        Assert.Equal("Errors", cut.Find(".wp-activity__tags").FirstElementChild!.TextContent.Trim());
    }

    private sealed class FakeActivityApiClient : IActivityApiClient
    {
        public Queue<ActivityPageDto> Pages { get; } = new();
        public Queue<string> Errors { get; } = new();
        public Queue<Exception> Failures { get; } = new();
        public List<(string? Category, DateTimeOffset? Before, Guid? BeforeId, int Take)> Calls { get; } = [];

        public Task<ApiResult<ActivityPageDto>> GetPageAsync(string? category, DateTimeOffset? before, Guid? beforeId, int take = 50, CancellationToken cancellationToken = default)
        {
            Calls.Add((category, before, beforeId, take));
            if (Calls.Count > 1 && Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            return Task.FromResult(Errors.TryDequeue(out var error)
                ? ApiResult<ActivityPageDto>.Fail(error)
                : ApiResult<ActivityPageDto>.Ok(Pages.Dequeue()));
        }
    }
}

// The evidence panel on its own (spec 0011, AC-4).
public class ActivityEvidenceTests : BunitContext
{
    // covers: AC-4
    [Fact]
    public void No_payload_says_no_evidence_was_recorded()
    {
        var cut = Render<ActivityEvidence>(p => p.Add(e => e.Payload, null));

        Assert.Equal("No evidence was recorded for this entry.", cut.Find("p").TextContent);
    }

    // covers: AC-4
    [Fact]
    public void An_empty_object_says_the_evidence_is_empty()
    {
        var cut = Render<ActivityEvidence>(p => p.Add(e => e.Payload, "{}"));

        Assert.Equal("The evidence is empty.", cut.Find("p").TextContent);
    }

    // covers: AC-4
    [Fact]
    public void Nested_values_render_as_indented_json_blocks()
    {
        var cut = Render<ActivityEvidence>(p => p.Add(e => e.Payload, """{"detail":{"model":"fake"},"n":1}"""));

        Assert.Equal(["detail", "n"], cut.FindAll("dt").Select(d => d.TextContent));
        Assert.Contains("\"model\": \"fake\"", cut.Find("dd pre").TextContent);
    }

    // covers: AC-4
    [Fact]
    public void Text_that_is_not_json_renders_as_is()
    {
        var cut = Render<ActivityEvidence>(p => p.Add(e => e.Payload, "<b>not json</b>"));

        Assert.Equal("<b>not json</b>", cut.Find("pre").TextContent);
        Assert.Empty(cut.FindAll("b"));
    }

    // covers: AC-4
    [Fact]
    public void A_large_payload_is_truncated_until_show_all()
    {
        var payload = $$"""{"n":1,"blob":"{{new string('x', 12000)}}"}""";
        var cut = Render<ActivityEvidence>(p => p.Add(e => e.Payload, payload));

        Assert.Empty(cut.FindAll("dl"));
        Assert.True(cut.Find("pre").TextContent.Length <= ActivitySummaries.EvidenceLimitBytes + 1);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Show all")).Click();

        Assert.Equal(["n", "blob"], cut.FindAll("dt").Select(d => d.TextContent));
        Assert.Equal(12000, cut.FindAll("dd")[1].TextContent.Trim().Length);
    }
}

// The Web host's activity client (spec 0011): the query it sends (AC-1, AC-2) and how it reads a
// ProblemDetails answer (AC-7). The Api is replaced at the HTTP boundary by a stub handler.
public class ActivityApiClientTests
{
    // covers: AC-1
    [Fact]
    public async Task The_first_page_asks_for_fifty_with_no_cursor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"items":[],"nextBefore":null,"nextBeforeId":null}""");

        var result = await new ActivityApiClient(new StubFactory(handler)).GetPageAsync(null, null, null);

        Assert.True(result.Succeeded);
        Assert.Equal("/internal/audit/activity?take=50", handler.PathAndQuery);
    }

    // covers: AC-1, AC-2
    [Fact]
    public async Task A_category_and_cursor_are_sent_escaped_with_the_offset_kept()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"items":[],"nextBefore":null,"nextBeforeId":null}""");
        var id = Guid.Parse("01a0da9c-0d42-7000-8000-000000000003");
        var before = new DateTimeOffset(2026, 10, 3, 9, 0, 0, 123, TimeSpan.FromHours(5));

        await new ActivityApiClient(new StubFactory(handler)).GetPageAsync("errors", before, id, 10);

        Assert.Equal($"/internal/audit/activity?take=10&category=errors&before=2026-10-03T09%3A00%3A00.1230000%2B05%3A00&beforeId={id}", handler.PathAndQuery);
    }

    // covers: AC-1
    [Fact]
    public async Task A_half_cursor_is_not_sent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"items":[],"nextBefore":null,"nextBeforeId":null}""");

        await new ActivityApiClient(new StubFactory(handler)).GetPageAsync(null, DateTimeOffset.UtcNow, null);

        Assert.Equal("/internal/audit/activity?take=50", handler.PathAndQuery);
    }

    // covers: AC-1
    [Fact]
    public async Task The_page_and_its_next_cursor_are_read()
    {
        var id = Guid.Parse("01a0da9c-0d42-7000-8000-000000000004");
        var handler = new StubHandler(HttpStatusCode.OK, $$"""
            {"items":[{"id":"{{id}}","occurredAt":"2026-10-03T09:00:00+00:00","actor":"Agent","action":"JobsMerged","category":"Jobs","targetType":"Job","targetId":"{{id}}","payload":null}],
             "nextBefore":"2026-10-03T09:00:00+00:00","nextBeforeId":"{{id}}"}
            """);

        var result = await new ActivityApiClient(new StubFactory(handler)).GetPageAsync(null, null, null);

        Assert.Equal("JobsMerged", result.Value!.Items.Single().Action);
        Assert.Equal(id, result.Value.NextBeforeId);
    }

    // covers: AC-7
    [Fact]
    public async Task A_validation_problem_reads_as_its_message()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"status":400,"errors":{"category":["Unknown category \"nope\"."]}}""");

        var result = await new ActivityApiClient(new StubFactory(handler)).GetPageAsync("nope", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal("Unknown category \"nope\".", result.Error);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api") };
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? PathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            PathAndQuery = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
