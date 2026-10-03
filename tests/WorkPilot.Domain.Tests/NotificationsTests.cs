using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Notifications;

namespace WorkPilot.Domain.Tests;

// Spec 0020: the Notification entity's rules (trimming, app relative links, read state, dismiss,
// digest revision), the strong match digest (count, top 3, no double counting, UTC day key) and
// the readable run failure reasons.
public class NotificationTests
{
    private static readonly Guid Profile = Guid.Parse("01a10275-c7cd-722f-9a13-c2b3c51c9259");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static Notification Create(string title = "Hello", string? body = null, string? link = "/approvals", string? groupKey = null) =>
        Notification.Create(Profile, "tests.kind", NotificationPriority.Info, title, body, link, Now, groupKey);

    [Fact]
    public void A_new_notification_is_unread_and_keeps_its_fields()
    {
        var n = Notification.Create(Profile, "approvals.requested", NotificationPriority.ActionRequired, "  Approval needed  ", " Goal ", "/approvals", Now, payload: "{\"a\":1}");

        Assert.Equal(Profile, n.ProfileId);
        Assert.Equal("approvals.requested", n.Type);
        Assert.Equal(NotificationPriority.ActionRequired, n.Priority);
        Assert.Equal("Approval needed", n.Title);
        Assert.Equal("Goal", n.Body);
        Assert.Equal("/approvals", n.Link);
        Assert.Equal(Now, n.CreatedAt);
        Assert.Equal("{\"a\":1}", n.Payload);
        Assert.Null(n.ReadAt);
        Assert.False(n.IsRead);
        Assert.False(n.IsDeleted);
    }

    [Fact]
    public void A_long_title_and_body_are_cut_to_their_limits_with_an_ellipsis()
    {
        var n = Create(new string('t', 500), new string('b', 5000));

        Assert.Equal(Notification.TitleMaxLength, n.Title.Length);
        Assert.EndsWith("…", n.Title);
        Assert.Equal(Notification.BodyMaxLength, n.Body!.Length);
        Assert.EndsWith("…", n.Body);
    }

    [Fact]
    public void A_blank_body_is_stored_as_null()
    {
        Assert.Null(Create(body: "   ").Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_title_is_refused(string title)
    {
        Assert.Throws<ArgumentException>(() => Create(title));
    }

    [Fact]
    public void An_empty_profile_or_type_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Notification.Create(Guid.Empty, "t", NotificationPriority.Info, "x", null, null, Now));
        Assert.Throws<ArgumentException>(() => Notification.Create(Profile, " ", NotificationPriority.Info, "x", null, null, Now));
    }

    // Key invariant: a link is always an app relative path, so a notification cannot navigate off site.
    [Theory]
    [InlineData("/approvals")]
    [InlineData("/jobs?sort=score&minScore=70")]
    public void An_app_relative_link_is_kept(string link)
    {
        Assert.Equal(link, Create(link: link).Link);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("approvals")]
    [InlineData("")]
    [InlineData("/a\nb")]
    public void A_link_that_could_leave_the_app_is_refused(string link)
    {
        Assert.Throws<ArgumentException>(() => Create(link: link));
    }

    [Fact]
    public void A_link_longer_than_the_limit_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Create(link: "/" + new string('a', Notification.LinkMaxLength)));
    }

    [Fact]
    public void No_link_is_allowed()
    {
        Assert.Null(Create(link: null).Link);
    }

    [Fact]
    public void Marking_read_twice_keeps_the_first_read_time()
    {
        var n = Create();

        n.MarkRead(Now.AddMinutes(1));
        n.MarkRead(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(1), n.ReadAt);
        Assert.True(n.IsRead);
    }

    [Fact]
    public void Marking_unread_clears_the_read_time()
    {
        var n = Create();
        n.MarkRead(Now);

        n.MarkUnread();

        Assert.Null(n.ReadAt);
    }

    [Fact]
    public void Dismissing_soft_deletes_it()
    {
        var n = Create();

        n.Dismiss(Now);

        Assert.True(n.IsDeleted);
        Assert.Equal(Now, n.DeletedAt);
    }

    [Fact]
    public void An_open_digest_can_be_revised()
    {
        var n = Create(groupKey: "jobs.strong-matches:2026-10-03");

        n.ReviseDigest("2 new strong matches today", "{\"count\":2}");

        Assert.Equal("2 new strong matches today", n.Title);
        Assert.Equal("{\"count\":2}", n.Payload);
    }

    [Fact]
    public void A_notification_that_is_not_a_digest_cannot_be_revised()
    {
        Assert.Throws<InvalidOperationException>(() => Create().ReviseDigest("x", "{}"));
    }

    // State transitions: a digest stops gathering jobs once read or dismissed.
    [Fact]
    public void A_read_or_dismissed_digest_cannot_be_revised()
    {
        var read = Create(groupKey: "g");
        read.MarkRead(Now);
        var dismissed = Create(groupKey: "g");
        dismissed.Dismiss(Now);

        Assert.Throws<InvalidOperationException>(() => read.ReviseDigest("x", "{}"));
        Assert.Throws<InvalidOperationException>(() => dismissed.ReviseDigest("x", "{}"));
    }

    [Fact]
    public void A_blank_group_key_means_no_digest()
    {
        Assert.Null(Create(groupKey: " ").GroupKey);
    }
}

public class StrongMatchDigestTests
{
    private static DigestJob Job(int score, string title = "Engineer") => new(Guid.CreateVersion7(), title, "Acme", score);

    // covers: AC-3
    [Fact]
    public void The_group_key_is_the_utc_day_of_handling()
    {
        var lateInKarachi = new DateTimeOffset(2026, 10, 4, 3, 30, 0, TimeSpan.FromHours(5)); // 22:30 UTC on Oct 3

        Assert.Equal("jobs.strong-matches:2026-10-03", StrongMatchDigest.GroupKeyFor(lateInKarachi));
        Assert.Equal("jobs.strong-matches:2026-10-04", StrongMatchDigest.GroupKeyFor(new DateTimeOffset(2026, 10, 4, 0, 0, 1, TimeSpan.Zero)));
    }

    // covers: AC-3
    [Fact]
    public void The_link_opens_the_jobs_list_best_first_from_the_threshold()
    {
        Assert.Equal("/jobs?sort=score&minScore=70", StrongMatchDigest.LinkFor(70));
        Assert.True(Notification.IsAppRelative(StrongMatchDigest.LinkFor(85)));
    }

    // covers: AC-3
    [Fact]
    public void The_title_is_singular_for_one_job_and_plural_otherwise()
    {
        var one = StrongMatchDigest.Empty.Add(Job(80));
        var two = one.Add(Job(75));

        Assert.Equal("1 new strong match today", one.Title);
        Assert.Equal("2 new strong matches today", two.Title);
    }

    // covers: AC-3
    [Fact]
    public void Five_jobs_count_five_and_keep_the_top_three_by_score()
    {
        var jobs = new[] { Job(71, "a"), Job(95, "b"), Job(80, "c"), Job(99, "d"), Job(72, "e") };

        var digest = jobs.Aggregate(StrongMatchDigest.Empty, (d, j) => d.Add(j));

        Assert.Equal(5, digest.Count);
        Assert.Equal(["d", "b", "c"], digest.Top.Select(j => j.Title));
        Assert.Equal(jobs.Select(j => j.JobId), digest.JobIds);
    }

    // covers: AC-3, AC-6
    [Fact]
    public void A_job_already_counted_is_not_counted_again()
    {
        var job = Job(90);
        var once = StrongMatchDigest.Empty.Add(job);

        var twice = once.Add(job with { Score = 99 });

        Assert.Same(once, twice);
        Assert.Equal(1, twice.Count);
        Assert.True(twice.Contains(job.JobId));
    }

    [Fact]
    public void Equal_scores_keep_the_job_that_arrived_first()
    {
        var digest = new[] { Job(80, "first"), Job(80, "second"), Job(80, "third"), Job(80, "fourth") }
            .Aggregate(StrongMatchDigest.Empty, (d, j) => d.Add(j));

        Assert.Equal(["first", "second", "third"], digest.Top.Select(j => j.Title));
    }

    [Fact]
    public void An_empty_digest_counts_nothing()
    {
        Assert.Equal(0, StrongMatchDigest.Empty.Count);
        Assert.Empty(StrongMatchDigest.Empty.Top);
        Assert.False(StrongMatchDigest.Empty.Contains(Guid.CreateVersion7()));
    }
}

public class AgentRunFailureReasonTextTests
{
    // covers: AC-2
    [Theory]
    [InlineData(AgentRunFailureReasons.ProviderError, "The AI provider failed")]
    [InlineData(AgentRunFailureReasons.UnparseablePlan, "The AI answered with a plan that could not be read")]
    [InlineData(AgentRunFailureReasons.PolicyViolation, "The plan broke a safety policy")]
    [InlineData(AgentRunFailureReasons.StepFailed, "A step failed")]
    [InlineData(AgentRunFailureReasons.ApprovalGateRefused, "The approval gate refused a step")]
    [InlineData(AgentRunFailureReasons.ApprovalRejected, "You rejected an approval")]
    public void Each_known_reason_reads_in_plain_words(string reason, string text)
    {
        Assert.Equal(text, AgentRunFailureReasons.Describe(reason));
    }

    [Fact]
    public void An_unknown_reason_is_shown_as_is_and_a_blank_one_as_unknown()
    {
        Assert.Equal("disk_full", AgentRunFailureReasons.Describe("disk_full"));
        Assert.Equal("Unknown reason", AgentRunFailureReasons.Describe(" "));
    }
}
