using WorkPilot.Domain.Modules.Audit;

namespace WorkPilot.Domain.Tests;

// The activity feed's category rule (spec 0011, AC-3): first match wins, every entry gets one,
// and a failure lands under Errors whatever its target. TryParse backs the ?category= filter (AC-2).
public class AuditCategoriesTests
{
    // covers: AC-3
    [Theory]
    [InlineData("PlanningFailed", "AgentRun")]
    [InlineData("ApprovalGateRefused", "AgentStep")]
    [InlineData("SendFailed", "OutreachMessage")]
    [InlineData("SyncFailed", "CalendarEvent")]
    [InlineData("ImportFailed", "Job")]
    public void A_failure_action_is_an_error_before_its_target_type_is_considered(string action, string targetType)
    {
        Assert.Equal(ActivityCategory.Errors, AuditCategories.For("Agent", action, targetType));
    }

    // covers: AC-3
    [Theory]
    [InlineData("list_my_profile", "Profile")]
    [InlineData("send_email", "OutreachMessage")]
    [InlineData("always_fails", "AgentStep")]
    public void A_call_marked_failed_is_an_error_even_with_a_plain_action_name(string action, string targetType)
    {
        Assert.Equal(ActivityCategory.Errors, AuditCategories.For("Agent", action, targetType, failed: true));
    }

    // covers: AC-3
    [Fact]
    public void A_call_not_marked_failed_keeps_its_usual_category()
    {
        Assert.Equal(ActivityCategory.Agent, AuditCategories.For("Agent", "always_fails", "AgentStep", failed: false));
    }

    // covers: AC-3
    [Theory]
    [InlineData("Job", ActivityCategory.Jobs)]
    [InlineData("JobSource", ActivityCategory.Jobs)]
    [InlineData("JobMatch", ActivityCategory.Jobs)]
    [InlineData("AgentRun", ActivityCategory.Agent)]
    [InlineData("AgentStep", ActivityCategory.Agent)]
    [InlineData("Approval", ActivityCategory.Agent)]
    [InlineData("OutreachMessage", ActivityCategory.Email)]
    [InlineData("EmailThread", ActivityCategory.Email)]
    [InlineData("OutreachContact", ActivityCategory.Email)]
    [InlineData("CalendarEvent", ActivityCategory.Calendar)]
    public void A_known_target_type_picks_its_domain_whoever_acted(string targetType, ActivityCategory expected)
    {
        Assert.Equal(expected, AuditCategories.For("01a0da9c-0d42-7000-8000-000000000001", "Anything", targetType));
    }

    // covers: AC-3
    [Fact]
    public void An_unknown_target_by_the_agent_is_agent()
    {
        Assert.Equal(ActivityCategory.Agent, AuditCategories.For("Agent", "list_my_profile", "Profile"));
    }

    // covers: AC-3
    [Fact]
    public void An_unknown_target_by_anyone_else_is_system()
    {
        Assert.Equal(ActivityCategory.System, AuditCategories.For("01a0da9c-0d42-7000-8000-000000000001", "ProfileUpdated", "Profile"));
    }

    // covers: AC-3
    [Theory]
    [InlineData("failed")]
    [InlineData("FailedToStart")]
    [InlineData("planningfailed")]
    public void Only_an_exact_failed_suffix_counts_as_a_failure(string action)
    {
        Assert.Equal(ActivityCategory.Agent, AuditCategories.For("Agent", action, "Profile"));
    }

    // covers: AC-3
    [Theory]
    [InlineData("job")]
    [InlineData("JOBSOURCE")]
    public void Target_types_match_by_exact_case(string targetType)
    {
        Assert.Equal(ActivityCategory.Agent, AuditCategories.For("Agent", "Anything", targetType));
    }

    // covers: AC-2
    [Theory]
    [InlineData("errors", ActivityCategory.Errors)]
    [InlineData("JoBs", ActivityCategory.Jobs)]
    [InlineData("SYSTEM", ActivityCategory.System)]
    [InlineData("Calendar", ActivityCategory.Calendar)]
    public void TryParse_reads_a_name_ignoring_case(string value, ActivityCategory expected)
    {
        Assert.True(AuditCategories.TryParse(value, out var category));
        Assert.Equal(expected, category);
    }

    // covers: AC-2, AC-7
    [Theory]
    [InlineData("nope")]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("Errors,Jobs")]
    public void TryParse_rejects_numbers_and_unknown_names(string value)
    {
        Assert.False(AuditCategories.TryParse(value, out _));
    }
}
