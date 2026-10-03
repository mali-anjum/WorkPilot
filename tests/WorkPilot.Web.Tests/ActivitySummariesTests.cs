using WorkPilot.Contracts.Audit;
using WorkPilot.Web.Features.Audit;

namespace WorkPilot.Web.Tests;

// How the activity feed presents an audit entry (spec 0011): who and when (AC-1), the chips (AC-2),
// evidence and its 10 KB truncation (AC-4), target links (AC-5) and readable summaries (AC-6).
public class ActivitySummariesTests
{
    private static readonly Guid Me = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly Guid Target = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000aa");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static ActivityEntryDto Entry(string action, string? payload = null) =>
        new(Guid.NewGuid(), Now, "Agent", action, "Agent", "AgentStep", Target, payload);

    // covers: AC-6
    [Theory]
    [InlineData("JobsMerged", "Merged duplicate jobs")]
    [InlineData("JobLinkSplit", "Split a job link")]
    [InlineData("ApprovalRequested", "Asked for your approval")]
    [InlineData("ApprovalApproved", "You approved a step")]
    [InlineData("ApprovalRejected", "You rejected a step")]
    [InlineData("ApprovalGateRefused", "Blocked a step at the approval gate")]
    [InlineData("PlanningFailed", "Agent planning failed")]
    public void A_known_action_reads_as_a_sentence(string action, string expected)
    {
        Assert.Equal(expected, ActivitySummaries.Summary(Entry(action)));
    }

    // covers: AC-6
    [Fact]
    public void An_unknown_action_shows_its_raw_name()
    {
        Assert.Equal("list_my_profile", ActivitySummaries.Summary(Entry("list_my_profile")));
    }

    // covers: AC-6
    [Fact]
    public void An_ingestion_shows_its_new_and_updated_counts()
    {
        var entry = Entry("JobsIngested", """{"created":12,"updated":3,"fetched":40}""");

        Assert.Equal("Ingested jobs from a source: 12 new, 3 updated", ActivitySummaries.Summary(entry));
    }

    // covers: AC-6
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"created":12}""")]
    [InlineData("""{"created":"12","updated":3}""")]
    [InlineData("""{"created":1.5,"updated":3}""")]
    public void An_ingestion_without_usable_counts_falls_back_to_the_plain_sentence(string? payload)
    {
        Assert.Equal("Ingested jobs from a source", ActivitySummaries.Summary(Entry("JobsIngested", payload)));
    }

    // covers: AC-1
    [Fact]
    public void Who_is_you_for_the_viewers_own_profile_id()
    {
        Assert.Equal("You", ActivitySummaries.Who(Me.ToString(), Me));
    }

    // covers: AC-1
    [Fact]
    public void Who_is_agent_for_the_agent()
    {
        Assert.Equal("Agent", ActivitySummaries.Who("Agent", Me));
    }

    // covers: AC-1
    [Theory]
    [InlineData("01a0da9c-0d42-7000-8000-000000000999")]
    [InlineData("Scheduler")]
    [InlineData("")]
    public void Who_is_system_for_anyone_else(string actor)
    {
        Assert.Equal("System", ActivitySummaries.Who(actor, Me));
    }

    // covers: AC-1
    [Fact]
    public void Who_is_system_when_the_viewer_is_unknown()
    {
        Assert.Equal("System", ActivitySummaries.Who(Me.ToString(), null));
    }

    // covers: AC-5
    [Theory]
    [InlineData("Job", "/jobs/01a0da9c-0d42-7000-8000-0000000000aa")]
    [InlineData("JobSource", "/jobs?source=01a0da9c-0d42-7000-8000-0000000000aa")]
    [InlineData("AgentStep", "/approvals")]
    [InlineData("AgentRun", "/agent/runs")]
    public void A_known_target_links_to_its_page(string targetType, string expected)
    {
        Assert.Equal(expected, ActivitySummaries.TargetHref(targetType, Target));
    }

    // covers: AC-5
    [Theory]
    [InlineData("Profile")]
    [InlineData("Approval")]
    [InlineData("job")]
    public void Any_other_target_has_no_link(string targetType)
    {
        Assert.Null(ActivitySummaries.TargetHref(targetType, Target));
    }

    // covers: AC-5
    [Fact]
    public void The_target_label_is_the_type_and_a_short_id()
    {
        Assert.Equal("Profile 01a0da9c", ActivitySummaries.TargetLabel("Profile", Target));
    }

    // covers: AC-2
    [Fact]
    public void The_chips_are_all_then_each_category_in_spec_order()
    {
        Assert.Equal(["All", "Agent", "Jobs", "Email", "Calendar", "System", "Errors"], ActivitySummaries.Chips.Select(c => c.Label));
        Assert.Null(ActivitySummaries.Chips[0].Value);
        Assert.Equal("errors", ActivitySummaries.Chips[^1].Value);
    }

    // covers: AC-1
    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(2 * 86400, "2 d ago")]
    public void Relative_time_counts_back_from_now(int secondsAgo, string expected)
    {
        Assert.Equal(expected, ActivitySummaries.Relative(Now.AddSeconds(-secondsAgo), Now, TimeZoneInfo.Utc));
    }

    // covers: AC-1
    [Fact]
    public void Relative_time_older_than_a_week_is_the_date_in_the_viewers_zone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var occurred = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal("Sep 2, 2026", ActivitySummaries.Relative(occurred, Now, tokyo));
    }

    // covers: AC-1
    [Fact]
    public void Exact_time_is_in_the_viewers_zone_with_its_name()
    {
        var losAngeles = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        Assert.Equal("Oct 3, 2026, 05:00:00 (America/Los_Angeles)", ActivitySummaries.Exact(Now, losAngeles));
    }

    // covers: AC-4
    [Fact]
    public void An_object_payload_becomes_key_and_value_pairs_with_nested_values_as_indented_json()
    {
        var view = ActivitySummaries.Evidence("""{"reason":"PlanParseFailed","attempts":2,"ok":true,"note":null,"detail":{"model":"fake"},"errors":["a"]}""")!;

        Assert.Null(view.Text);
        Assert.Equal(["reason", "attempts", "ok", "note", "detail", "errors"], view.Fields.Select(f => f.Key));
        Assert.Equal(new EvidenceField("reason", "PlanParseFailed", false), view.Fields[0]);
        Assert.Equal("2", view.Fields[1].Value);
        Assert.Equal("true", view.Fields[2].Value);
        Assert.Equal("null", view.Fields[3].Value);
        Assert.True(view.Fields[4].IsJson);
        Assert.Contains("\n", view.Fields[4].Value);
        Assert.Contains("\"model\": \"fake\"", view.Fields[4].Value);
        Assert.True(view.Fields[5].IsJson);
    }

    // covers: AC-4
    [Fact]
    public void A_json_value_that_is_not_an_object_shows_as_indented_text()
    {
        var view = ActivitySummaries.Evidence("[1,2]")!;

        Assert.Empty(view.Fields);
        Assert.True(view.TextIsJson);
        Assert.Contains("\n", view.Text);
    }

    // covers: AC-4
    [Fact]
    public void A_payload_that_is_not_json_shows_as_plain_text()
    {
        var view = ActivitySummaries.Evidence("plain words {")!;

        Assert.Equal("plain words {", view.Text);
        Assert.False(view.TextIsJson);
    }

    // covers: AC-4
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_payload_means_no_evidence(string? payload)
    {
        Assert.Null(ActivitySummaries.Evidence(payload));
    }

    // covers: AC-4
    [Fact]
    public void A_payload_at_the_limit_is_not_large_and_one_byte_more_is()
    {
        Assert.False(ActivitySummaries.IsLarge(new string('x', ActivitySummaries.EvidenceLimitBytes)));
        Assert.True(ActivitySummaries.IsLarge(new string('x', ActivitySummaries.EvidenceLimitBytes + 1)));
        Assert.False(ActivitySummaries.IsLarge(null));
    }

    // covers: AC-4
    [Fact]
    public void Truncate_keeps_the_first_ten_kilobytes()
    {
        var truncated = ActivitySummaries.Truncate(new string('x', 12000));

        Assert.Equal(ActivitySummaries.EvidenceLimitBytes, truncated.Length);
    }

    // covers: AC-4
    [Fact]
    public void Truncate_never_splits_a_multibyte_character()
    {
        // One ASCII byte, then three byte characters: the 10 KB mark falls inside one of them.
        var payload = "x" + new string('€', 4000);

        var truncated = ActivitySummaries.Truncate(payload);

        Assert.DoesNotContain('�', truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(truncated) <= ActivitySummaries.EvidenceLimitBytes);
        Assert.Equal(payload[..truncated.Length], truncated);
    }

    // covers: AC-4
    [Fact]
    public void Truncate_leaves_a_small_payload_alone()
    {
        Assert.Equal("""{"a":1}""", ActivitySummaries.Truncate("""{"a":1}"""));
    }
}
