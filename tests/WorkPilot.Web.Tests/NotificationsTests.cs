using System.Net;
using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Notifications;
using WorkPilot.Web.Client;
using WorkPilot.Web.Client.Shared;
using WorkPilot.Web.Components.Pages.Notifications;
using WorkPilot.Web.Features.Common;
using WorkPilot.Web.Features.Notifications;

namespace WorkPilot.Web.Tests;

// The notification bell and drawer (spec 0020), rendered with a fake INotificationsApiClient in
// place of the internal Api, a signed in session carrying the profile_id claim, and a short
// refresh interval so polling is observable. The Api itself is covered in Api.Tests/NotificationsTests.
public class NotificationBellTests : BunitContext
{
    internal static readonly Guid Me = Guid.Parse("01a10275-c7cd-722f-9a13-c2b3c51c9259");

    private readonly FakeNotificationsApiClient _api = new();

    public NotificationBellTests()
    {
        Services.AddSingleton<INotificationsApiClient>(_api);
        Services.AddScoped<BrowserTimeZone>();
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, Me.ToString()));
        JSInterop.SetupModule("./js/interop.js").Setup<string?>("timeZone").SetResult("UTC");
    }

    private IRenderedComponent<NotificationBell> RenderBell(TimeSpan? interval = null) =>
        Render<NotificationBell>(p => p.Add(b => b.RefreshInterval, interval ?? TimeSpan.FromHours(1)));

    private static string? Count(IRenderedComponent<NotificationBell> cut) =>
        cut.FindAll("[data-testid=bell-count]").SingleOrDefault()?.TextContent;

    // covers: AC-4
    [Fact]
    public void No_unread_hides_the_count()
    {
        _api.Counts.Enqueue(0);

        var cut = RenderBell();

        cut.WaitForAssertion(() => Assert.Equal(1, _api.CountCalls));
        Assert.Null(Count(cut));
        Assert.Equal("Notifications, none unread", cut.Find(".wp-bell__button").GetAttribute("aria-label"));
        Assert.Equal(Me, _api.LastProfileId);
    }

    // covers: AC-4
    [Theory]
    [InlineData(1, "1", "Notifications, 1 unread")]
    [InlineData(99, "99", "Notifications, 99 unread")]
    [InlineData(100, "99+", "Notifications, 99+ unread")]
    public void The_count_shows_up_to_99_then_99_plus(int unread, string shown, string label)
    {
        _api.Counts.Enqueue(unread);

        var cut = RenderBell();

        cut.WaitForAssertion(() => Assert.Equal(shown, Count(cut)));
        Assert.Equal(label, cut.Find(".wp-bell__button").GetAttribute("aria-label"));
    }

    // covers: AC-4
    [Fact]
    public void The_count_refreshes_on_every_tick()
    {
        _api.Counts.Enqueue(1);
        _api.Counts.Enqueue(1);
        _api.Counts.Enqueue(3);

        var cut = RenderBell(TimeSpan.FromMilliseconds(30));

        cut.WaitForAssertion(() => Assert.Equal("3", Count(cut)), TimeSpan.FromSeconds(5));
    }

    // covers: AC-4
    [Fact]
    public void The_count_refreshes_on_navigation()
    {
        _api.Counts.Enqueue(0);
        _api.Counts.Enqueue(4);
        var cut = RenderBell();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.CountCalls));

        Services.GetRequiredService<NavigationManager>().NavigateTo("/jobs");

        cut.WaitForAssertion(() => Assert.Equal("4", Count(cut)));
    }

    // covers: AC-9
    [Fact]
    public void A_failed_refresh_keeps_the_last_count_and_the_next_tick_recovers()
    {
        _api.Counts.Enqueue(2);
        _api.CountFailures.Enqueue(new HttpRequestException("api down"));
        _api.CountErrors.Enqueue("The request failed (503).");
        _api.Counts.Enqueue(5);

        var cut = RenderBell(TimeSpan.FromMilliseconds(30));

        cut.WaitForAssertion(() => Assert.Equal("2", Count(cut)));
        cut.WaitForAssertion(() => Assert.True(_api.CountCalls >= 3), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Equal("5", Count(cut)), TimeSpan.FromSeconds(5));
    }

    // covers: AC-4 (stops when the circuit closes)
    [Fact]
    public async Task Disposing_the_bell_stops_polling()
    {
        RenderBell(TimeSpan.FromMilliseconds(20));
        // An unchanged count does not render, so wait on the calls directly.
        for (var i = 0; i < 250 && _api.CountCalls < 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(_api.CountCalls >= 2);
        await DisposeComponentsAsync();
        var calls = _api.CountCalls;
        await Task.Delay(200);

        Assert.Equal(calls, _api.CountCalls);
    }

    // covers: AC-4
    [Fact]
    public void Opening_the_drawer_shows_the_latest_10_with_unread_highlighted()
    {
        _api.Counts.Enqueue(1);
        _api.Pages.Enqueue(Page(Note("Approval needed: send_email", read: false, priority: "ActionRequired"), Note("Agent run failed", read: true, priority: "Error")));
        var cut = RenderBell();

        cut.Find(".wp-bell__button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".wp-bell__item").Count));
        Assert.Equal((false, 1, 10), _api.PageCalls.Single());
        var items = cut.FindAll(".wp-bell__item");
        Assert.Contains("wp-bell__item--unread", items[0].ClassList);
        Assert.DoesNotContain("wp-bell__item--unread", items[1].ClassList);
        Assert.Contains("Action required", items[0].TextContent);
        Assert.Equal("true", cut.Find(".wp-bell__button").GetAttribute("aria-expanded"));
        Assert.Equal("/notifications", cut.Find(".wp-bell__foot a").GetAttribute("href"));
    }

    // covers: AC-9
    [Fact]
    public void An_empty_drawer_says_you_are_all_caught_up()
    {
        _api.Pages.Enqueue(Page());
        var cut = RenderBell();

        cut.Find(".wp-bell__button").Click();

        cut.WaitForAssertion(() => Assert.Equal("You're all caught up.", cut.Find(".wp-bell__empty").TextContent));
    }

    // covers: AC-9
    [Fact]
    public void A_drawer_that_cannot_load_shows_the_error_and_retry_loads_it()
    {
        _api.PageFailures.Enqueue(new HttpRequestException("down"));
        _api.Pages.Enqueue(Page(Note("Back again")));
        var cut = RenderBell();

        cut.Find(".wp-bell__button").Click();
        cut.WaitForAssertion(() => Assert.Contains("could not be reached", cut.Find("[role=alert]").TextContent));
        cut.FindAll(".wp-bell__error button").Single(b => b.TextContent.Contains("Retry")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Back again", cut.Find(".wp-bell__item").TextContent));
    }

    // covers: AC-4
    [Fact]
    public void Clicking_an_unread_notification_marks_it_read_and_opens_its_link()
    {
        var note = Note("Approval needed: send_email", read: false, link: "/approvals");
        _api.Counts.Enqueue(1);
        _api.Counts.Enqueue(1);
        _api.Pages.Enqueue(Page(note));
        var cut = RenderBell();
        cut.WaitForAssertion(() => Assert.Equal("1", Count(cut)));
        cut.Find(".wp-bell__button").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wp-bell__item")));
        _api.Counts.Enqueue(0);

        cut.Find(".wp-bell__item").Click();

        Assert.Equal((note.Id, true), _api.ReadCalls.Single());
        Assert.EndsWith("/approvals", Services.GetRequiredService<NavigationManager>().Uri);
        cut.WaitForAssertion(() => Assert.Null(Count(cut)));
        Assert.Empty(cut.FindAll(".wp-bell__drawer"));
    }

    // Key invariant: a notification never navigates off site, even from a bad row.
    [Fact]
    public void A_link_that_is_not_app_relative_is_never_followed()
    {
        var note = Note("Odd", read: true, link: "https://evil.example.com");
        _api.Pages.Enqueue(Page(note));
        var nav = Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;
        var cut = RenderBell();
        cut.Find(".wp-bell__button").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wp-bell__item")));

        cut.Find(".wp-bell__item").Click();

        Assert.Equal(before, nav.Uri);
        Assert.Empty(_api.ReadCalls);
    }

    // covers: AC-4
    [Fact]
    public void Mark_all_read_clears_the_count_and_the_highlight()
    {
        _api.Counts.Enqueue(2);
        _api.Counts.Enqueue(2);
        _api.Pages.Enqueue(Page(Note("One"), Note("Two")));
        var cut = RenderBell();
        cut.Find(".wp-bell__button").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".wp-bell__item--unread").Count));

        cut.FindAll(".wp-bell__head button").Single(b => b.TextContent.Contains("Mark all read")).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".wp-bell__item--unread")));
        Assert.Null(Count(cut));
        Assert.Equal(1, _api.MarkAllCalls);
    }

    [Fact]
    public void Escape_closes_the_drawer()
    {
        _api.Pages.Enqueue(Page());
        var cut = RenderBell();
        cut.Find(".wp-bell__button").Click();

        cut.Find(".wp-bell__drawer").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll(".wp-bell__drawer"));
        Assert.Equal("false", cut.Find(".wp-bell__button").GetAttribute("aria-expanded"));
    }

    internal static NotificationDto Note(string title, bool read = false, string priority = "Info", string? link = "/activity", string? body = null, DateTimeOffset? createdAt = null) =>
        new(Guid.CreateVersion7(), "tests.kind", priority, title, body, link, null, createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-5), read ? DateTimeOffset.UtcNow : null);

    internal static NotificationPageDto Page(params NotificationDto[] items) => new(items, items.Length, 1, 10);
}

// The /notifications page (spec 0020, AC-5, AC-9), with the same fake client and the browser
// reporting Asia/Tokyo as its time zone.
public class NotificationsPageTests : BunitContext
{
    private readonly FakeNotificationsApiClient _api = new();

    public NotificationsPageTests()
    {
        Services.AddSingleton<INotificationsApiClient>(_api);
        Services.AddScoped<BrowserTimeZone>();
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, NotificationBellTests.Me.ToString()));
        JSInterop.SetupModule("./js/interop.js").Setup<string?>("timeZone").SetResult("Asia/Tokyo");
    }

    private IRenderedComponent<Notifications> RenderAt(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<Notifications>();
        cut.WaitForState(() => cut.FindAll("[role=status]").Count == 0);
        return cut;
    }

    private static NotificationPageDto PageOf(int total, int page, params NotificationDto[] items) => new(items, total, page, 25);

    // covers: AC-5
    [Fact]
    public void Lists_the_first_page_with_page_numbers()
    {
        _api.Pages.Enqueue(PageOf(60, 1, NotificationBellTests.Note("Agent run failed", priority: "Error", body: "A step failed. Goal: x"), NotificationBellTests.Note("Older", read: true)));

        var cut = RenderAt("/notifications");

        Assert.Equal((false, 1, 25), _api.PageCalls.Single());
        Assert.Equal(["Agent run failed", "Older"], cut.FindAll(".wp-notes__title").Select(t => t.TextContent));
        Assert.Equal("A step failed. Goal: x", cut.Find(".wp-notes__body").TextContent);
        Assert.Equal(["1", "2", "3"], cut.FindAll(".wp-notes__page").Select(a => a.TextContent));
        Assert.Equal("page", cut.FindAll(".wp-notes__page")[0].GetAttribute("aria-current"));
        Assert.Equal("/notifications?page=2", cut.FindAll(".wp-notes__page")[1].GetAttribute("href"));
        Assert.Single(cut.FindAll(".wp-notes__item--unread"));
    }

    // covers: AC-5
    [Fact]
    public void The_page_and_unread_toggle_come_from_the_url()
    {
        _api.Pages.Enqueue(PageOf(30, 2, NotificationBellTests.Note("Unread one")));

        var cut = RenderAt("/notifications?unread=true&page=2");

        Assert.Equal((true, 2, 25), _api.PageCalls.Single());
        Assert.Equal("Unread only", cut.Find(".wp-notes__chip--active").TextContent);
        Assert.Equal("/notifications?unread=true&page=2", cut.FindAll(".wp-notes__page")[1].GetAttribute("href"));
    }

    // covers: AC-5
    [Fact]
    public void Relative_time_has_the_exact_time_in_the_browsers_zone()
    {
        var at = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        _api.Pages.Enqueue(PageOf(1, 1, NotificationBellTests.Note("Timed", createdAt: at)));

        var cut = RenderAt("/notifications");

        cut.WaitForAssertion(() => Assert.Equal("Oct 3, 2026, 18:00:00 (Asia/Tokyo)", cut.Find("time").GetAttribute("title")));
    }

    // covers: AC-9
    [Fact]
    public void No_notifications_says_you_are_all_caught_up()
    {
        _api.Pages.Enqueue(PageOf(0, 1));

        var cut = RenderAt("/notifications");

        Assert.Contains("You're all caught up", cut.Markup);
        Assert.Empty(cut.FindAll(".wp-notes__pages"));
    }

    // covers: AC-9
    [Fact]
    public void A_failed_load_shows_the_error_and_retry_loads_again()
    {
        _api.PageErrors.Enqueue("The request failed (500).");
        _api.Pages.Enqueue(PageOf(1, 1, NotificationBellTests.Note("Recovered")));
        var cut = RenderAt("/notifications");
        Assert.Contains("The request failed (500).", cut.Find("[role=alert]").TextContent);

        cut.FindAll(".wp-notes__error button").Single().Click();

        cut.WaitForAssertion(() => Assert.Equal("Recovered", cut.Find(".wp-notes__title").TextContent));
    }

    // covers: AC-5
    [Fact]
    public void Mark_read_and_mark_unread_toggle_one_row()
    {
        var note = NotificationBellTests.Note("Toggle me");
        _api.Pages.Enqueue(PageOf(1, 1, note));
        var cut = RenderAt("/notifications");

        cut.FindAll(".wp-notes__actions button").Single(b => b.TextContent.Trim() == "Mark read").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".wp-notes__item--unread")));
        cut.FindAll(".wp-notes__actions button").Single(b => b.TextContent.Trim() == "Mark unread").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wp-notes__item--unread")));
        Assert.Equal([(note.Id, true), (note.Id, false)], _api.ReadCalls);
    }

    // covers: AC-5
    [Fact]
    public void Dismiss_removes_the_row()
    {
        var keep = NotificationBellTests.Note("Keep");
        var drop = NotificationBellTests.Note("Drop");
        _api.Pages.Enqueue(PageOf(2, 1, keep, drop));
        var cut = RenderAt("/notifications");

        cut.Find($"button[aria-label='Dismiss Drop']").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Keep"], cut.FindAll(".wp-notes__title").Select(t => t.TextContent)));
        Assert.Equal([drop.Id], _api.DismissCalls);
    }

    // covers: AC-5
    [Fact]
    public void A_failed_dismiss_keeps_the_row_and_shows_why()
    {
        var note = NotificationBellTests.Note("Stays");
        _api.Pages.Enqueue(PageOf(1, 1, note));
        _api.DismissErrors.Enqueue("The notification was not found.");
        var cut = RenderAt("/notifications");

        cut.Find("button[aria-label='Dismiss Stays']").Click();

        cut.WaitForAssertion(() => Assert.Contains("The notification was not found.", cut.Find("[role=alert]").TextContent));
        Assert.Single(cut.FindAll(".wp-notes__item"));
    }

    // covers: AC-5
    [Fact]
    public void Mark_all_read_marks_everything_and_reloads()
    {
        _api.Pages.Enqueue(PageOf(2, 1, NotificationBellTests.Note("A"), NotificationBellTests.Note("B")));
        _api.Pages.Enqueue(PageOf(2, 1, NotificationBellTests.Note("A", read: true), NotificationBellTests.Note("B", read: true)));
        var cut = RenderAt("/notifications");

        cut.FindAll(".wp-notes__toolbar button").Single().Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".wp-notes__item--unread")));
        Assert.Equal(1, _api.MarkAllCalls);
        Assert.Equal(2, _api.PageCalls.Count);
    }

    // Key invariant: a link that could leave the app is shown as plain text, not a link.
    [Fact]
    public void A_title_with_an_unsafe_link_is_not_a_link()
    {
        _api.Pages.Enqueue(PageOf(1, 1, NotificationBellTests.Note("Odd", link: "//evil.example.com")));

        var cut = RenderAt("/notifications");

        Assert.Equal("SPAN", cut.Find(".wp-notes__title").TagName);
    }
}

// The display rules shared by the bell and the page (spec 0020).
public class NotificationDisplayTests
{
    // covers: AC-4
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, null)]
    [InlineData(7, "7")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(5000, "99+")]
    public void The_count_label_hides_at_zero_and_caps_at_99_plus(int count, string? label)
    {
        Assert.Equal(label, NotificationDisplay.CountLabel(count));
    }

    [Theory]
    [InlineData("Error", StatusKind.Danger)]
    [InlineData("ActionRequired", StatusKind.Warning)]
    [InlineData("Warning", StatusKind.Warning)]
    [InlineData("Success", StatusKind.Success)]
    [InlineData("Info", StatusKind.Info)]
    [InlineData("Something", StatusKind.Neutral)]
    public void Each_priority_has_its_badge_color(string priority, StatusKind status)
    {
        Assert.Equal(status, NotificationDisplay.PriorityStatus(priority));
    }

    [Fact]
    public void Action_required_reads_in_plain_words()
    {
        Assert.Equal("Action required", NotificationDisplay.PriorityLabel("ActionRequired"));
        Assert.Equal("Error", NotificationDisplay.PriorityLabel("Error"));
    }

    // covers: AC-5
    [Theory]
    [InlineData(1, false, "/notifications")]
    [InlineData(1, true, "/notifications?unread=true")]
    [InlineData(3, false, "/notifications?page=3")]
    [InlineData(2, true, "/notifications?unread=true&page=2")]
    public void Page_links_keep_the_toggle_and_page_in_the_url(int page, bool unread, string href)
    {
        Assert.Equal(href, NotificationDisplay.PageHref(page, unread));
    }

    [Theory]
    [InlineData("/approvals", "/approvals")]
    [InlineData("//evil.example.com", null)]
    [InlineData("/\\evil.example.com", null)]
    [InlineData("https://evil.example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_app_relative_links_are_safe(string? link, string? safe)
    {
        Assert.Equal(safe, NotificationDisplay.SafeLink(link));
    }
}

// The Web host's notifications client: the requests it sends and how it reads ProblemDetails.
// The Api is replaced at the HTTP boundary by a stub handler.
public class NotificationsApiClientTests
{
    private static readonly Guid Me = NotificationBellTests.Me;
    private static readonly Guid Id = Guid.Parse("01a10276-9964-7b7f-af28-1c0ecab2fcd4");

    [Fact]
    public async Task The_unread_count_is_asked_for_the_profile()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"count":4}""");

        var result = await new NotificationsApiClient(new StubFactory(handler)).GetUnreadCountAsync(Me);

        Assert.Equal(4, result.Value!.Count);
        Assert.Equal($"GET /internal/notifications/unread-count?profileId={Me}", handler.Request);
    }

    [Fact]
    public async Task A_page_sends_the_toggle_page_and_size()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"items":[],"total":0,"page":2,"pageSize":25}""");

        await new NotificationsApiClient(new StubFactory(handler)).GetPageAsync(Me, unreadOnly: true, page: 2, pageSize: 25);

        Assert.Equal($"GET /internal/notifications?profileId={Me}&unreadOnly=true&page=2&pageSize=25", handler.Request);
    }

    [Fact]
    public async Task Mark_read_posts_the_profile_and_flag()
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"id":"{{Id}}","type":"t","priority":"Info","title":"x","createdAt":"2026-10-03T09:00:00+00:00","readAt":"2026-10-03T09:01:00+00:00"}""");

        var result = await new NotificationsApiClient(new StubFactory(handler)).SetReadAsync(Me, Id, true);

        Assert.True(result.Succeeded);
        Assert.Equal($"POST /internal/notifications/{Id}/read", handler.Request);
        Assert.Contains($"\"profileId\":\"{Me}\"", handler.Body);
        Assert.Contains("\"read\":true", handler.Body);
    }

    [Fact]
    public async Task Dismiss_sends_a_delete_and_succeeds_on_204()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, "");

        var result = await new NotificationsApiClient(new StubFactory(handler)).DismissAsync(Me, Id);

        Assert.True(result.Succeeded);
        Assert.Equal($"DELETE /internal/notifications/{Id}?profileId={Me}", handler.Request);
    }

    // covers: AC-8
    [Fact]
    public async Task A_404_problem_reads_as_its_detail()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"status":404,"detail":"The notification was not found."}""");

        var result = await new NotificationsApiClient(new StubFactory(handler)).DismissAsync(Me, Id);

        Assert.False(result.Succeeded);
        Assert.Equal("The notification was not found.", result.Error);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api") };
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}

internal sealed class FakeNotificationsApiClient : INotificationsApiClient
{
    private int _countCalls;

    public Queue<int> Counts { get; } = new();
    public Queue<string> CountErrors { get; } = new();
    public Queue<Exception> CountFailures { get; } = new();
    public Queue<NotificationPageDto> Pages { get; } = new();
    public Queue<string> PageErrors { get; } = new();
    public Queue<Exception> PageFailures { get; } = new();
    public Queue<string> DismissErrors { get; } = new();
    public List<(bool UnreadOnly, int Page, int PageSize)> PageCalls { get; } = [];
    public List<(Guid Id, bool Read)> ReadCalls { get; } = [];
    public List<Guid> DismissCalls { get; } = [];
    public int MarkAllCalls { get; private set; }
    public int CountCalls => Volatile.Read(ref _countCalls);
    public Guid? LastProfileId { get; private set; }

    // A failure is consumed before an error, an error before a count; with nothing queued the
    // last answer repeats (0 when none was ever queued).
    private int _lastCount;

    public Task<ApiResult<UnreadCountDto>> GetUnreadCountAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _countCalls);
        LastProfileId = profileId;
        if (CountFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (CountErrors.TryDequeue(out var error))
        {
            return Task.FromResult(ApiResult<UnreadCountDto>.Fail(error));
        }

        if (Counts.TryDequeue(out var count))
        {
            _lastCount = count;
        }

        return Task.FromResult(ApiResult<UnreadCountDto>.Ok(new UnreadCountDto(_lastCount)));
    }

    public Task<ApiResult<NotificationPageDto>> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        PageCalls.Add((unreadOnly, page, pageSize));
        if (PageFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        return Task.FromResult(PageErrors.TryDequeue(out var error)
            ? ApiResult<NotificationPageDto>.Fail(error)
            : ApiResult<NotificationPageDto>.Ok(Pages.TryDequeue(out var next) ? next : new NotificationPageDto([], 0, page, pageSize)));
    }

    public Task<ApiResult<NotificationDto>> SetReadAsync(Guid profileId, Guid notificationId, bool read, CancellationToken cancellationToken = default)
    {
        ReadCalls.Add((notificationId, read));
        var dto = new NotificationDto(notificationId, "tests.kind", "Info", "x", null, "/activity", null, DateTimeOffset.UtcNow, read ? DateTimeOffset.UtcNow : null);
        return Task.FromResult(ApiResult<NotificationDto>.Ok(dto));
    }

    public Task<ApiResult<MarkAllNotificationsReadResponse>> MarkAllReadAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        MarkAllCalls++;
        _lastCount = 0;
        return Task.FromResult(ApiResult<MarkAllNotificationsReadResponse>.Ok(new MarkAllNotificationsReadResponse(2)));
    }

    public Task<ApiResult<bool>> DismissAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        DismissCalls.Add(notificationId);
        return Task.FromResult(DismissErrors.TryDequeue(out var error) ? ApiResult<bool>.Fail(error) : ApiResult<bool>.Ok(true));
    }
}
