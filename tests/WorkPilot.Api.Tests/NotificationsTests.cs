using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkPilot.Application.Modules.Notifications;
using WorkPilot.Application.Modules.Notifications.Handlers;
using WorkPilot.Contracts.Notifications;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Notifications;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Audit;
using WorkPilot.Workers.Notifications;

namespace WorkPilot.Api.Tests;

// Notifications (spec 0020) against the real Postgres: the three handlers driven through the
// outbox's HandleEventJob (so delivery, replay and the handler's transaction are the real ones),
// the digest under concurrency, the /internal/notifications endpoints with their profile scoping,
// and the daily cleanup. Each test seeds its own profile; deleting it cascades its notifications.
// Needs WORKPILOTDB_CONNECTION.
[Collection("Api")]
public class NotificationsTests(SharedApiFactory factory) : IAsyncLifetime
{
    private readonly List<Guid> _profiles = [];
    private readonly List<Guid> _messages = [];
    private readonly List<Guid> _sources = [];
    private readonly List<Guid> _runs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        await db.OutboxMessages.Where(m => _messages.Contains(m.Id)).ExecuteDeleteAsync();
        var stepIds = await db.AgentSteps.Where(s => _runs.Contains(s.AgentRunId)).Select(s => s.Id).ToListAsync();
        var workflowIds = await db.AgentRuns.Where(r => _runs.Contains(r.Id)).Select(r => r.WorkflowInstanceId).ToListAsync();
        await db.Approvals.Where(a => stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.AgentSteps.Where(s => stepIds.Contains(s.Id)).ExecuteDeleteAsync();
        await db.AgentRuns.Where(r => _runs.Contains(r.Id)).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(w => workflowIds.Contains(w.Id)).ExecuteDeleteAsync();
        await db.Jobs.IgnoreQueryFilters().Where(j => j.Links.Any(l => _sources.Contains(l.JobSourceId))).ExecuteDeleteAsync();
        await db.JobSources.Where(s => _sources.Contains(s.Id)).ExecuteDeleteAsync();
        await db.Profiles.Where(p => _profiles.Contains(p.Id)).ExecuteDeleteAsync();
    }

    // ---------- approval requested (AC-1) ----------

    // covers: AC-1
    [Fact]
    public async Task An_approval_request_notifies_the_runs_profile_with_the_tool_and_goal()
    {
        var (profileId, approvalId) = await SeedApprovalAsync("approval_required_demo", "Send the follow up to Ada");

        await DeliverAsync(new ApprovalRequested(approvalId, ApprovalTargets.AgentStep, Guid.Empty), NotifyOnApprovalRequested.HandlerKey);

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal("approvals.requested", n.Type);
        Assert.Equal(NotificationPriority.ActionRequired, n.Priority);
        Assert.Equal("Approval needed: approval_required_demo", n.Title);
        Assert.Equal("Send the follow up to Ada", n.Body);
        Assert.Equal("/approvals", n.Link);
        Assert.Null(n.ReadAt);
        Assert.Contains(approvalId.ToString(), n.Payload);
    }

    // covers: AC-1
    [Fact]
    public async Task The_approval_body_shows_at_most_300_characters_of_the_goal()
    {
        var (profileId, approvalId) = await SeedApprovalAsync("approval_required_demo", new string('g', 900));

        await DeliverAsync(new ApprovalRequested(approvalId, ApprovalTargets.AgentStep, Guid.Empty), NotifyOnApprovalRequested.HandlerKey);

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal(NotifyOnApprovalRequested.GoalMaxLength, n.Body!.Length);
    }

    // Key invariant: a missing target makes the handler return without writing, not fail.
    [Fact]
    public async Task An_approval_that_no_longer_exists_is_skipped_without_failing()
    {
        var profileId = await SeedProfileAsync();
        var before = await CountAllAsync();

        await DeliverAsync(new ApprovalRequested(Guid.CreateVersion7(), ApprovalTargets.AgentStep, Guid.Empty), NotifyOnApprovalRequested.HandlerKey);

        Assert.Empty(await NotificationsOfAsync(profileId));
        Assert.Equal(before, await CountAllAsync());
    }

    // ---------- run failed (AC-2) ----------

    // covers: AC-2
    [Theory]
    [InlineData(AgentRunFailureReasons.ProviderError, "The AI provider failed")]
    [InlineData(AgentRunFailureReasons.ApprovalRejected, "You rejected an approval")]
    public async Task A_failed_run_notifies_its_profile_with_the_readable_reason_and_goal(string reason, string text)
    {
        var (profileId, runId) = await SeedRunAsync("Find Rust jobs in Berlin");

        await DeliverAsync(new AgentRunFailed(runId, reason), NotifyOnAgentRunFailed.HandlerKey);

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal("agent.run-failed", n.Type);
        Assert.Equal(NotificationPriority.Error, n.Priority);
        Assert.Equal("Agent run failed", n.Title);
        Assert.Equal($"{text}. Goal: Find Rust jobs in Berlin", n.Body);
        Assert.Equal("/agent/runs", n.Link);
    }

    [Fact]
    public async Task A_failed_run_that_no_longer_exists_is_skipped()
    {
        var before = await CountAllAsync();

        await DeliverAsync(new AgentRunFailed(Guid.CreateVersion7(), AgentRunFailureReasons.StepFailed), NotifyOnAgentRunFailed.HandlerKey);

        Assert.Equal(before, await CountAllAsync());
    }

    // ---------- strong match digest (AC-3, AC-6) ----------

    // covers: AC-3
    [Fact]
    public async Task Five_strong_matches_in_a_day_make_one_digest_with_the_top_three()
    {
        var profileId = await SeedProfileAsync(threshold: 75);
        var jobs = await SeedJobsAsync(5);
        int[] scores = [76, 92, 81, 99, 77];

        for (var i = 0; i < jobs.Count; i++)
        {
            await DeliverAsync(new JobMatched(jobs[i].Id, profileId, scores[i]), NotifyOnJobMatched.HandlerKey);
        }

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal("jobs.strong-matches", n.Type);
        Assert.Equal(NotificationPriority.Info, n.Priority);
        Assert.Equal("5 new strong matches today", n.Title);
        Assert.Equal("/jobs?sort=score&minScore=75", n.Link);
        Assert.Equal(StrongMatchDigest.GroupKeyFor(DateTimeOffset.UtcNow), n.GroupKey);
        var digest = NotificationPayloads.ReadDigest(n.Payload);
        Assert.Equal(5, digest.Count);
        Assert.Equal([99, 92, 81], digest.Top.Select(j => j.Score));
        Assert.Equal(jobs[3].Title, digest.Top[0].Title);
        Assert.Equal("Notify Co", digest.Top[0].Company);
    }

    // covers: AC-6
    [Fact]
    public async Task Replaying_the_same_delivery_changes_nothing()
    {
        var profileId = await SeedProfileAsync();
        var job = (await SeedJobsAsync(1))[0];
        var messageId = await StoreAsync(new JobMatched(job.Id, profileId, 90));

        await HandleAsync(messageId, NotifyOnJobMatched.HandlerKey);
        await HandleAsync(messageId, NotifyOnJobMatched.HandlerKey);

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal(1, NotificationPayloads.ReadDigest(n.Payload).Count);
    }

    // covers: AC-3, AC-6
    [Fact]
    public async Task A_job_already_in_todays_digest_is_not_counted_twice()
    {
        var profileId = await SeedProfileAsync();
        var job = (await SeedJobsAsync(1))[0];

        await DeliverAsync(new JobMatched(job.Id, profileId, 90), NotifyOnJobMatched.HandlerKey);
        await DeliverAsync(new JobMatched(job.Id, profileId, 95), NotifyOnJobMatched.HandlerKey);

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal("1 new strong match today", n.Title);
        Assert.Equal(1, NotificationPayloads.ReadDigest(n.Payload).Count);
    }

    // covers: AC-3
    [Fact]
    public async Task Once_todays_digest_is_read_a_new_unread_one_starts()
    {
        var profileId = await SeedProfileAsync();
        var jobs = await SeedJobsAsync(2);
        await DeliverAsync(new JobMatched(jobs[0].Id, profileId, 90), NotifyOnJobMatched.HandlerKey);
        await using (var db = CreateDbContext())
        {
            await db.Notifications.Where(n => n.ProfileId == profileId).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow));
        }

        await DeliverAsync(new JobMatched(jobs[1].Id, profileId, 80), NotifyOnJobMatched.HandlerKey);

        var all = await NotificationsOfAsync(profileId);
        Assert.Equal(2, all.Count);
        var open = Assert.Single(all, n => n.ReadAt is null);
        Assert.Equal("1 new strong match today", open.Title);
        Assert.Equal([jobs[1].Id], NotificationPayloads.ReadDigest(open.Payload).JobIds);
    }

    // covers: AC-3 (key invariant: at most one open digest per profile and day)
    [Fact]
    public async Task Two_deliveries_racing_to_start_the_digest_end_with_one_row_counting_both()
    {
        var profileId = await SeedProfileAsync();
        var jobs = await SeedJobsAsync(2);
        var first = await StoreAsync(new JobMatched(jobs[0].Id, profileId, 90));
        var second = await StoreAsync(new JobMatched(jobs[1].Id, profileId, 85));

        var results = await Task.WhenAll(
            TryHandleAsync(first, NotifyOnJobMatched.HandlerKey),
            TryHandleAsync(second, NotifyOnJobMatched.HandlerKey));

        // A loser hits the partial unique index; its retry (Hangfire's, here by hand) finds the row and updates it.
        if (!results[0])
        {
            await HandleAsync(first, NotifyOnJobMatched.HandlerKey);
        }

        if (!results[1])
        {
            await HandleAsync(second, NotifyOnJobMatched.HandlerKey);
        }

        var n = Assert.Single(await NotificationsOfAsync(profileId));
        Assert.Equal(2, NotificationPayloads.ReadDigest(n.Payload).Count);
        Assert.Equal("2 new strong matches today", n.Title);
    }

    [Fact]
    public async Task A_matched_job_that_no_longer_exists_is_skipped()
    {
        var profileId = await SeedProfileAsync();

        await DeliverAsync(new JobMatched(Guid.CreateVersion7(), profileId, 90), NotifyOnJobMatched.HandlerKey);

        Assert.Empty(await NotificationsOfAsync(profileId));
    }

    // ---------- endpoints (AC-4, AC-5, AC-8) ----------

    // covers: AC-4
    [Fact]
    public async Task The_unread_count_counts_only_unread_not_dismissed_notifications()
    {
        var profileId = await SeedProfileAsync();
        await SeedNotificationsAsync(profileId, 3);
        var read = await SeedNotificationsAsync(profileId, 1, read: true);
        var dismissed = await SeedNotificationsAsync(profileId, 1, dismissed: true);
        using var client = factory.CreateClient();

        var count = await client.GetFromJsonAsync<UnreadCountDto>($"/internal/notifications/unread-count?profileId={profileId}");

        Assert.Equal(3, count!.Count);
        Assert.Single(read);
        Assert.Single(dismissed);
    }

    // covers: AC-5
    [Fact]
    public async Task The_list_is_newest_first_25_a_page_with_the_total()
    {
        var profileId = await SeedNotificationsProfileAsync(30);
        using var client = factory.CreateClient();

        var first = await GetPageAsync(client, profileId, "");
        var second = await GetPageAsync(client, profileId, "&page=2");

        Assert.Equal(30, first.Total);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(25, first.PageSize);
        Assert.Equal(5, second.Items.Count);
        var all = first.Items.Concat(second.Items).ToList();
        Assert.Equal(all.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), all.Select(n => n.Id));
        Assert.Equal(30, all.Select(n => n.Id).Distinct().Count());
    }

    // covers: AC-5
    [Fact]
    public async Task Unread_only_lists_only_unread_notifications()
    {
        var profileId = await SeedProfileAsync();
        var unread = await SeedNotificationsAsync(profileId, 2);
        await SeedNotificationsAsync(profileId, 3, read: true);
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, profileId, "&unreadOnly=true");

        Assert.Equal(2, page.Total);
        Assert.Equal(unread.Order(), page.Items.Select(i => i.Id).Order());
        Assert.All(page.Items, i => Assert.Null(i.ReadAt));
    }

    // covers: AC-5
    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=51", "pageSize")]
    public async Task Bad_paging_is_a_400_problem_naming_the_field(string query, string field)
    {
        var profileId = await SeedProfileAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/notifications?profileId={profileId}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("/internal/notifications")]
    [InlineData("/internal/notifications/unread-count")]
    [InlineData("/internal/notifications/unread-count?profileId=00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_profile_is_a_400_problem(string url)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // covers: AC-5
    [Fact]
    public async Task Marking_read_then_unread_round_trips_and_returns_the_notification()
    {
        var profileId = await SeedProfileAsync();
        var id = (await SeedNotificationsAsync(profileId, 1))[0];
        using var client = factory.CreateClient();

        var read = await client.PostAsJsonAsync($"/internal/notifications/{id}/read", new MarkNotificationReadRequest(profileId, true));
        var readDto = await read.Content.ReadFromJsonAsync<NotificationDto>();
        var unread = await client.PostAsJsonAsync($"/internal/notifications/{id}/read", new MarkNotificationReadRequest(profileId, false));
        var unreadDto = await unread.Content.ReadFromJsonAsync<NotificationDto>();

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.NotNull(readDto!.ReadAt);
        Assert.Equal(id, readDto.Id);
        Assert.Null(unreadDto!.ReadAt);
    }

    // covers: AC-4, AC-5
    [Fact]
    public async Task Mark_all_read_marks_every_unread_one_of_that_profile_only()
    {
        var profileId = await SeedProfileAsync();
        var otherId = await SeedProfileAsync();
        await SeedNotificationsAsync(profileId, 4);
        await SeedNotificationsAsync(otherId, 2);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/notifications/read-all", new MarkAllNotificationsReadRequest(profileId));

        Assert.Equal(4, (await response.Content.ReadFromJsonAsync<MarkAllNotificationsReadResponse>())!.Updated);
        Assert.All(await NotificationsOfAsync(profileId), n => Assert.NotNull(n.ReadAt));
        Assert.All(await NotificationsOfAsync(otherId), n => Assert.Null(n.ReadAt));
    }

    // covers: AC-5
    [Fact]
    public async Task Dismiss_removes_it_from_the_list_and_count()
    {
        var profileId = await SeedProfileAsync();
        var id = (await SeedNotificationsAsync(profileId, 1))[0];
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/internal/notifications/{id}?profileId={profileId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, (await GetPageAsync(client, profileId, "")).Total);
        await using var db = CreateDbContext();
        var row = await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == id);
        Assert.True(row.IsDeleted);
    }

    // covers: AC-8
    [Fact]
    public async Task Another_profile_cannot_read_unread_or_dismiss_your_notification()
    {
        var mine = await SeedProfileAsync();
        var theirs = await SeedProfileAsync();
        var id = (await SeedNotificationsAsync(mine, 1))[0];
        using var client = factory.CreateClient();

        var read = await client.PostAsJsonAsync($"/internal/notifications/{id}/read", new MarkNotificationReadRequest(theirs, true));
        var dismiss = await client.DeleteAsync($"/internal/notifications/{id}?profileId={theirs}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("application/problem+json", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
        var row = Assert.Single(await NotificationsOfAsync(mine));
        Assert.Null(row.ReadAt);
        Assert.False(row.IsDeleted);
        Assert.Equal(0, (await GetPageAsync(client, theirs, "")).Total);
    }

    // covers: AC-8
    [Fact]
    public async Task A_missing_notification_is_a_404_problem()
    {
        var profileId = await SeedProfileAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/internal/notifications/{Guid.CreateVersion7()}/read", new MarkNotificationReadRequest(profileId, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not found", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- cleanup (AC-7) ----------

    // covers: AC-7
    [Fact]
    public async Task Cleanup_dismisses_read_notifications_past_retention_and_keeps_unread_ones()
    {
        var profileId = await SeedProfileAsync();
        var now = DateTimeOffset.UtcNow;
        var oldRead = (await SeedNotificationsAsync(profileId, 1, read: true))[0];
        var recentRead = (await SeedNotificationsAsync(profileId, 1, read: true))[0];
        var oldUnread = (await SeedNotificationsAsync(profileId, 1, createdAt: now.AddDays(-200)))[0];
        await using (var db = CreateDbContext())
        {
            await db.Notifications.Where(n => n.Id == oldRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now.AddDays(-91)));
            await db.Notifications.Where(n => n.Id == recentRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now.AddDays(-89)));
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationsCleanupJob>().RunAsync();
        }

        await using var verify = CreateDbContext();
        var rows = await verify.Notifications.IgnoreQueryFilters().Where(n => n.ProfileId == profileId).ToDictionaryAsync(n => n.Id);
        Assert.True(rows[oldRead].IsDeleted);
        Assert.False(rows[recentRead].IsDeleted);
        Assert.False(rows[oldUnread].IsDeleted);
    }

    // covers: AC-7
    [Fact]
    public void The_cleanup_runs_daily_at_three_utc_with_a_90_day_default()
    {
        var definition = factory.Services.GetServices<WorkPilot.Workers.Common.IRecurringJobDefinition>()
            .OfType<WorkPilot.Workers.Common.RecurringJobDefinition<NotificationsCleanupJob>>()
            .Single();

        Assert.Equal("notifications.cleanup", definition.Id);
        Assert.Equal("0 3 * * *", definition.Cron);
        Assert.Equal(90, factory.Services.GetRequiredService<IOptions<NotificationsOptions>>().Value.ReadRetentionDays);
    }

    [Fact]
    public void Every_notification_handler_is_registered_under_its_stable_key()
    {
        var registry = factory.Services.GetRequiredService<EventRegistry>();

        Assert.Contains(registry.HandlersFor(ApprovalRequested.EventName), h => h.HandlerKey == "notifications.on-approval-requested");
        Assert.Contains(registry.HandlersFor(AgentRunFailed.EventName), h => h.HandlerKey == "notifications.on-agent-run-failed");
        Assert.Contains(registry.HandlersFor(JobMatched.EventName), h => h.HandlerKey == "notifications.on-job-matched");
    }

    // ---------- helpers ----------

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<Guid> SeedProfileAsync(int threshold = MatchProfileRules.DefaultStrongMatchThreshold)
    {
        await using var db = CreateDbContext();
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Notification Tester", StrongMatchThreshold = threshold };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        _profiles.Add(profile.Id);
        return profile.Id;
    }

    private async Task<(Guid ProfileId, Guid RunId)> SeedRunAsync(string goal)
    {
        var profileId = await SeedProfileAsync();
        await using var db = CreateDbContext();
        var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.Planning.ToString() };
        db.WorkflowInstances.Add(workflow);
        var run = new AgentRun { WorkflowInstanceId = workflow.Id, ProfileId = profileId, Goal = goal };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        _runs.Add(run.Id);
        return (profileId, run.Id);
    }

    private async Task<(Guid ProfileId, Guid ApprovalId)> SeedApprovalAsync(string toolName, string goal)
    {
        var (profileId, runId) = await SeedRunAsync(goal);
        await using var db = CreateDbContext();
        var step = new AgentStep { AgentRunId = runId, Ordinal = 0, ToolName = toolName };
        db.AgentSteps.Add(step);
        var approval = new Approval { TargetType = ApprovalTargets.AgentStep, TargetId = step.Id, RiskTier = ToolRiskTier.ApprovalRequired.ToString() };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        return (profileId, approval.Id);
    }

    private async Task<List<Job>> SeedJobsAsync(int count)
    {
        await using var db = CreateDbContext();
        var source = new JobSource { Type = "greenhouse", Name = $"notifications-test:{Guid.NewGuid():N}", Config = "{}" };
        db.JobSources.Add(source);
        await db.SaveChangesAsync();
        _sources.Add(source.Id);
        var jobs = Enumerable.Range(0, count)
            .Select(i => Job.Create(
                source.Id,
                new NormalizedJob($"n{i}", $"https://example.com/{i}", $"Engineer {i} {Guid.NewGuid():N}", "Notify Co", "Paris", null, "Build it.", DateTimeOffset.UtcNow, "{}", $"hash-{Guid.NewGuid():N}"),
                DateTimeOffset.UtcNow,
                1m))
            .ToList();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
        return jobs;
    }

    private async Task<List<Guid>> SeedNotificationsAsync(Guid profileId, int count, bool read = false, bool dismissed = false, DateTimeOffset? createdAt = null)
    {
        await using var db = CreateDbContext();
        var start = createdAt ?? DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, count)
            .Select(i => Notification.Create(profileId, "tests.seed", NotificationPriority.Info, $"Seed {i}", null, "/activity", start.AddMinutes(-i)))
            .ToList();
        foreach (var row in rows)
        {
            if (read)
            {
                row.MarkRead(start);
            }

            if (dismissed)
            {
                row.Dismiss(start);
            }
        }

        db.Notifications.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    }

    private async Task<Guid> SeedNotificationsProfileAsync(int count)
    {
        var profileId = await SeedProfileAsync();
        await SeedNotificationsAsync(profileId, count);
        return profileId;
    }

    private async Task<List<Notification>> NotificationsOfAsync(Guid profileId)
    {
        await using var db = CreateDbContext();
        return await db.Notifications.IgnoreQueryFilters().AsNoTracking().Where(n => n.ProfileId == profileId).OrderBy(n => n.CreatedAt).ToListAsync();
    }

    private async Task<int> CountAllAsync()
    {
        await using var db = CreateDbContext();
        return await db.Notifications.IgnoreQueryFilters().CountAsync();
    }

    private static async Task<NotificationPageDto> GetPageAsync(HttpClient client, Guid profileId, string query)
    {
        var response = await client.GetAsync($"/internal/notifications?profileId={profileId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NotificationPageDto>())!;
    }

    // Stores the event as an outbox message (as Publish plus the caller's save would) and runs the
    // handler's real delivery job for it.
    private async Task DeliverAsync<TEvent>(TEvent domainEvent, string handlerKey) where TEvent : IDomainEvent
    {
        var messageId = await StoreAsync(domainEvent);
        await HandleAsync(messageId, handlerKey);
    }

    private async Task<Guid> StoreAsync<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent
    {
        await using var db = CreateDbContext();
        var message = new OutboxMessage
        {
            EventName = TEvent.EventName,
            Payload = JsonSerializer.Serialize(domainEvent, EventPublisher.PayloadJson),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        message.MarkDispatched(DateTimeOffset.UtcNow); // only this test delivers it
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        _messages.Add(message.Id);
        return message.Id;
    }

    private async Task HandleAsync(Guid messageId, string handlerKey)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HandleEventJob>().RunAsync(messageId, handlerKey);
    }

    private async Task<bool> TryHandleAsync(Guid messageId, string handlerKey)
    {
        try
        {
            await HandleAsync(messageId, handlerKey);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
