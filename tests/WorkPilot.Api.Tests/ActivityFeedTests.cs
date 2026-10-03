using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Audit;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Infrastructure.Persistence.Migrations;

namespace WorkPilot.Api.Tests;

// The activity feed (spec 0011) against the real Postgres: GET /internal/audit/activity with its
// keyset paging, category filter and validation, the category the writer stores, and the
// AddActivityFeed backfill matching AuditCategories.For. The database is shared with other rows,
// so every test seeds its own rows in the year 2099 (the top of the feed), each tagged with this
// class's target id, and removes them afterwards. Needs WORKPILOTDB_CONNECTION.
[Collection("Api")]
public class ActivityFeedTests(SharedApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset Future = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Guid _target = Guid.CreateVersion7();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TargetId == _target).ExecuteDeleteAsync();
    }

    // ---------- paging (AC-1, key invariants) ----------

    // covers: AC-1
    [Fact]
    public async Task The_feed_is_newest_first_with_a_cursor_to_the_next_page()
    {
        var seeded = await SeedAsync(Enumerable.Range(0, 5).Select(i => Row(Future.AddMinutes(i))).ToArray());
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, "take=3");

        Assert.Equal(seeded.OrderByDescending(r => r.OccurredAt).Take(3).Select(r => r.Id), page.Items.Select(i => i.Id));
        Assert.Equal(page.Items[^1].OccurredAt, page.NextBefore);
        Assert.Equal(page.Items[^1].Id, page.NextBeforeId);
    }

    // covers: AC-1
    [Fact]
    public async Task Following_the_cursor_returns_strictly_older_rows_with_no_overlap()
    {
        var seeded = await SeedAsync(Enumerable.Range(0, 5).Select(i => Row(Future.AddMinutes(i))).ToArray());
        using var client = factory.CreateClient();

        var first = await GetPageAsync(client, "take=3");
        var second = await GetPageAsync(client, $"take=2&{Cursor(first)}");

        Assert.Equal(seeded.OrderByDescending(r => r.OccurredAt).Skip(3).Select(r => r.Id), second.Items.Select(i => i.Id));
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
    }

    // covers: AC-1 (key invariant: rows arriving between two calls never shift the next page)
    [Fact]
    public async Task A_row_inserted_at_the_top_between_two_calls_leaves_the_next_page_unchanged()
    {
        await SeedAsync(Enumerable.Range(0, 5).Select(i => Row(Future.AddMinutes(i))).ToArray());
        using var client = factory.CreateClient();
        var first = await GetPageAsync(client, "take=2");
        var before = await GetPageAsync(client, $"take=2&{Cursor(first)}");

        await SeedAsync(Row(Future.AddHours(1)));
        var after = await GetPageAsync(client, $"take=2&{Cursor(first)}");

        Assert.Equal(before.Items.Select(i => i.Id), after.Items.Select(i => i.Id));
    }

    // covers: AC-1 (key invariant: ties on the time are broken by the id, so no row is skipped)
    [Fact]
    public async Task Rows_with_the_same_time_page_by_id_without_skipping_any()
    {
        var seeded = await SeedAsync(Enumerable.Range(0, 4).Select(_ => Row(Future)).ToArray());
        using var client = factory.CreateClient();

        var first = await GetPageAsync(client, "take=2");
        var second = await GetPageAsync(client, $"take=2&{Cursor(first)}");

        // Postgres orders uuid by its bytes, which is its text order, not .NET's Guid order.
        var expected = seeded.Select(r => r.Id).OrderByDescending(id => id.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(expected, first.Items.Concat(second.Items).Where(i => i.OccurredAt == Future).Select(i => i.Id));
    }

    // covers: AC-1
    [Fact]
    public async Task The_cursor_works_whatever_offset_it_is_sent_in()
    {
        await SeedAsync(Enumerable.Range(0, 3).Select(i => Row(Future.AddMinutes(i))).ToArray());
        using var client = factory.CreateClient();
        var first = await GetPageAsync(client, "take=1");
        var shifted = first.NextBefore!.Value.ToOffset(TimeSpan.FromHours(5));

        var utc = await GetPageAsync(client, $"take=2&{Cursor(first)}");
        var plusFive = await GetPageAsync(client, $"take=2&before={Uri.EscapeDataString(shifted.ToString("O"))}&beforeId={first.NextBeforeId}");

        Assert.Equal(utc.Items.Select(i => i.Id), plusFive.Items.Select(i => i.Id));
    }

    // covers: AC-1
    [Fact]
    public async Task The_last_page_has_no_cursor()
    {
        var rows = await SeedAsync(Row(Future, action: "Booked", targetType: "CalendarEvent"));
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, "category=calendar&take=100");

        Assert.Equal(rows[0].Id, page.Items[0].Id);
        Assert.True(page.Items.Count < 100, "this test needs fewer than 100 Calendar rows in the database");
        Assert.Null(page.NextBefore);
        Assert.Null(page.NextBeforeId);
    }

    // covers: AC-1
    [Fact]
    public async Task A_soft_deleted_row_is_not_in_the_feed()
    {
        var rows = await SeedAsync(Row(Future), Row(Future.AddMinutes(1)));
        await using (var db = CreateDbContext())
        {
            await db.AuditLogs.Where(a => a.Id == rows[1].Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true));
        }

        using var client = factory.CreateClient();
        var page = await GetPageAsync(client, "take=5");

        Assert.DoesNotContain(rows[1].Id, page.Items.Select(i => i.Id));
        Assert.Contains(rows[0].Id, page.Items.Select(i => i.Id));
    }

    // covers: AC-1, AC-4
    [Fact]
    public async Task An_entry_carries_its_fields_and_payload_as_stored()
    {
        var row = Row(Future, actor: "01a0da9c-0d42-7000-8000-000000000001", action: "ApprovalApproved", targetType: "AgentStep", payload: """{"decision": "Approve"}""");
        await SeedAsync(row);
        using var client = factory.CreateClient();

        var entry = (await GetPageAsync(client, "take=1")).Items.Single();

        Assert.Equal(row.Id, entry.Id);
        Assert.Equal(Future, entry.OccurredAt);
        Assert.Equal(row.Actor, entry.Actor);
        Assert.Equal("ApprovalApproved", entry.Action);
        Assert.Equal("Agent", entry.Category);
        Assert.Equal("AgentStep", entry.TargetType);
        Assert.Equal(_target, entry.TargetId);
        using var payload = JsonDocument.Parse(entry.Payload!);
        Assert.Equal("Approve", payload.RootElement.GetProperty("decision").GetString());
    }

    // ---------- the filter (AC-2) ----------

    // covers: AC-2
    [Fact]
    public async Task A_category_returns_only_its_rows()
    {
        var rows = await SeedAsync(
            Row(Future.AddMinutes(3), action: "PlanningFailed", targetType: "AgentRun"),
            Row(Future.AddMinutes(2), action: "JobsMerged", targetType: "Job"),
            Row(Future.AddMinutes(1), action: "ApprovalGateRefused", targetType: "AgentStep"));
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, "category=errors&take=2");

        Assert.Equal([rows[0].Id, rows[2].Id], page.Items.Select(i => i.Id));
        Assert.All(page.Items, i => Assert.Equal("Errors", i.Category));
    }

    // covers: AC-2
    [Fact]
    public async Task The_category_ignores_case_and_surrounding_space()
    {
        await SeedAsync(Row(Future, action: "JobsMerged", targetType: "Job"));
        using var client = factory.CreateClient();

        var lower = await GetPageAsync(client, "category=jobs&take=3");
        var mixed = await GetPageAsync(client, $"category={Uri.EscapeDataString(" JoBs ")}&take=3");

        Assert.Equal(lower.Items.Select(i => i.Id), mixed.Items.Select(i => i.Id));
    }

    // covers: AC-2, AC-8
    [Fact]
    public async Task The_cursor_and_the_filter_combine()
    {
        var rows = await SeedAsync(Enumerable.Range(0, 4).Select(i => Row(Future.AddMinutes(i), action: "SyncFailed")).ToArray());
        using var client = factory.CreateClient();

        var first = await GetPageAsync(client, "category=errors&take=2");
        var second = await GetPageAsync(client, $"category=errors&take=2&{Cursor(first)}");

        Assert.Equal(rows.OrderByDescending(r => r.OccurredAt).Select(r => r.Id), first.Items.Concat(second.Items).Select(i => i.Id));
    }

    // ---------- validation (AC-2, AC-7) ----------

    // covers: AC-2, AC-7
    [Theory]
    [InlineData("category=nope", "category")]
    [InlineData("category=3", "category")]
    [InlineData("before=2026-10-01T00:00:00Z", "beforeId")]
    [InlineData("beforeId=01a0da9c-0d42-7000-8000-000000000001", "beforeId")]
    [InlineData("take=0", "take")]
    [InlineData("take=101", "take")]
    [InlineData("take=-1", "take")]
    public async Task A_bad_query_is_a_400_problem_naming_the_field(string query, string field)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/internal/audit/activity?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    // covers: AC-1
    [Theory]
    [InlineData("take=1")]
    [InlineData("take=100")]
    [InlineData("category=")]
    public async Task Edge_values_are_accepted(string query)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/internal/audit/activity?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // covers: AC-1
    [Fact]
    public async Task Fifty_rows_come_back_by_default()
    {
        await SeedAsync(Enumerable.Range(0, 51).Select(i => Row(Future.AddSeconds(i))).ToArray());
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, string.Empty);

        Assert.Equal(50, page.Items.Count);
        Assert.NotNull(page.NextBeforeId);
    }

    // ---------- the stored category (AC-3) ----------

    // covers: AC-3
    [Theory]
    [InlineData("Agent", "JobsIngested", "JobSource", false, ActivityCategory.Jobs)]
    [InlineData("Agent", "PlanningFailed", "AgentRun", false, ActivityCategory.Errors)]
    [InlineData("Agent", "list_my_profile", "Profile", true, ActivityCategory.Errors)]
    [InlineData("Agent", "list_my_profile", "Profile", false, ActivityCategory.Agent)]
    [InlineData("01a0da9c-0d42-7000-8000-000000000001", "ProfileUpdated", "Profile", false, ActivityCategory.System)]
    public async Task The_writer_stores_the_category_the_rule_gives(string actor, string action, string targetType, bool failed, ActivityCategory expected)
    {
        await using (var db = CreateDbContext())
        {
            new AuditService(db).Record(actor, action, targetType, _target, null, failed);
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var stored = await verify.AuditLogs.SingleAsync(a => a.TargetId == _target);
        Assert.Equal(expected, stored.Category);
    }

    // covers: AC-3 (the backfill mirrors AuditCategories.For, so old and new rows agree)
    [Fact]
    public async Task The_backfill_gives_every_row_the_category_the_rule_gives()
    {
        const string Me = "01a0da9c-0d42-7000-8000-000000000001";
        (string Actor, string Action, string TargetType, string? Payload)[] shapes =
        [
            ("Agent", "PlanningFailed", "AgentRun", null),
            ("Agent", "ApprovalGateRefused", "AgentStep", null),
            ("Agent", "SendFailed", "OutreachMessage", null),
            ("Agent", "JobsIngested", "JobSource", """{"created": 1}"""),
            ("Agent", "JobsMerged", "Job", null),
            ("Agent", "Scored", "JobMatch", null),
            ("Agent", "ApprovalRequested", "AgentStep", null),
            ("Agent", "Started", "AgentRun", null),
            ("Agent", "Decided", "Approval", null),
            ("Agent", "Sent", "OutreachMessage", null),
            ("Agent", "Read", "EmailThread", null),
            ("Agent", "Found", "OutreachContact", null),
            ("Agent", "Booked", "CalendarEvent", null),
            ("Agent", "list_my_profile", "Profile", null),
            ("Agent", "list_my_profile", "Profile", """{"error": "boom"}"""),
            ("Agent", "always_fails", "AgentStep", """{"error": "boom"}"""),
            ("Agent", "returns_output", "Profile", """{"error": "none", "name": "Founder"}"""),
            ("Agent", "returns_list", "Profile", """["error"]"""),
            (Me, "ApprovalApproved", "AgentStep", null),
            (Me, "ProfileUpdated", "Profile", """{"error": "boom"}"""),
            (Me, "ProfileUpdated", "Profile", null),
        ];
        var rows = shapes.Select((s, i) => Row(Future.AddSeconds(i), s.Actor, s.Action, s.TargetType, s.Payload, ActivityCategory.Calendar)).ToArray();
        await SeedAsync(rows);
        var backfill = BackfillSql();

        await using (var db = CreateDbContext())
        {
            // Scoped to this test's rows; the migration itself runs it over the whole table.
            var scoped = backfill.TrimEnd().TrimEnd(';') + " WHERE \"TargetId\" = {0}";
            await db.Database.ExecuteSqlRawAsync(scoped, _target);
        }

        await using var verify = CreateDbContext();
        var stored = await verify.AuditLogs.Where(a => a.TargetId == _target).ToDictionaryAsync(a => a.Id, a => a.Category);
        Assert.All(rows, r => Assert.Equal(AuditCategories.For(r.Actor, r.Action, r.TargetType, IsWrappedError(r.Actor, r.Payload)), stored[r.Id]));
        Assert.Equal(ActivityCategory.Errors, stored[rows[14].Id]);
        Assert.Equal(ActivityCategory.Agent, stored[rows[16].Id]);
        Assert.Equal(ActivityCategory.System, stored[rows[19].Id]);
    }

    // AdvanceRunJob writes a failed tool call's error as {"error": "..."} and nothing else.
    private static bool IsWrappedError(string actor, string? payload)
    {
        if (actor != "Agent" || payload is null)
        {
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(["error"]);
    }

    private static string BackfillSql() =>
        new AddActivityFeed().UpOperations.OfType<SqlOperation>().Single(o => o.Sql.Contains("UPDATE app.audit_logs", StringComparison.Ordinal)).Sql;

    // ---------- helpers ----------

    private AuditLog Row(DateTimeOffset at, string actor = "Agent", string action = "list_my_profile", string targetType = "Profile", string? payload = null, ActivityCategory? category = null) => new()
    {
        Actor = actor,
        Action = action,
        TargetType = targetType,
        TargetId = _target,
        Payload = payload,
        OccurredAt = at,
        Category = category ?? AuditCategories.For(actor, action, targetType),
    };

    private async Task<AuditLog[]> SeedAsync(params AuditLog[] rows)
    {
        await using var db = CreateDbContext();
        db.AuditLogs.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    private static string Cursor(ActivityPageDto page) =>
        $"before={Uri.EscapeDataString(page.NextBefore!.Value.ToString("O"))}&beforeId={page.NextBeforeId}";

    private static async Task<ActivityPageDto> GetPageAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<ActivityPageDto>($"/internal/audit/activity?{query}"))!;

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();
}
