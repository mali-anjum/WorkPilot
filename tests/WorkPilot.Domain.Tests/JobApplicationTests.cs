using WorkPilot.Domain.Modules.Applications;

namespace WorkPilot.Domain.Tests;

// Covers AC-4 (spec 0002): the JobApplication state machine is enforced in
// the entity itself, with no database involved.
public class JobApplicationTests
{
    private static JobApplication NewApplication() => new()
    {
        JobId = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        ResumeVersionId = Guid.NewGuid(),
    };

    [Fact]
    public void NewApplication_StartsAsDiscovered()
    {
        var application = NewApplication();

        Assert.Equal(ApplicationStatus.Discovered, application.Status);
    }

    [Theory]
    [InlineData(ApplicationStatus.Discovered, ApplicationStatus.Matched)]
    [InlineData(ApplicationStatus.Matched, ApplicationStatus.Preparing)]
    [InlineData(ApplicationStatus.Preparing, ApplicationStatus.PendingApproval)]
    [InlineData(ApplicationStatus.PendingApproval, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Interviewing)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Offered)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Discovered, ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Withdrawn)]
    public void TransitionTo_AllowsEachValidStep(ApplicationStatus from, ApplicationStatus to)
    {
        var application = NewApplication();
        DriveTo(application, from);

        application.TransitionTo(to);

        Assert.Equal(to, application.Status);
    }

    [Theory]
    [InlineData(ApplicationStatus.Discovered, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Discovered, ApplicationStatus.Interviewing)]
    [InlineData(ApplicationStatus.Discovered, ApplicationStatus.Offered)]
    [InlineData(ApplicationStatus.Matched, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Offered, ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Matched)]
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Matched)]
    public void TransitionTo_RejectsSkippedOrTerminalTransitions(ApplicationStatus from, ApplicationStatus to)
    {
        var application = NewApplication();
        DriveTo(application, from);

        var ex = Assert.Throws<InvalidOperationException>(() => application.TransitionTo(to));

        Assert.Contains(from.ToString(), ex.Message);
        Assert.Contains(to.ToString(), ex.Message);
        Assert.Equal(from, application.Status); // rejected transition never mutates state
    }

    // Drives a fresh application to exactly `target` via real, valid transitions
    // (the same ones the "allows" theory proves), so no test presumes a shortcut
    // the state machine doesn't actually offer.
    private static readonly Dictionary<ApplicationStatus, ApplicationStatus[]> PathTo = new()
    {
        [ApplicationStatus.Discovered] = [],
        [ApplicationStatus.Matched] = [ApplicationStatus.Matched],
        [ApplicationStatus.Preparing] = [ApplicationStatus.Matched, ApplicationStatus.Preparing],
        [ApplicationStatus.PendingApproval] =
            [ApplicationStatus.Matched, ApplicationStatus.Preparing, ApplicationStatus.PendingApproval],
        [ApplicationStatus.Submitted] =
        [
            ApplicationStatus.Matched, ApplicationStatus.Preparing,
            ApplicationStatus.PendingApproval, ApplicationStatus.Submitted,
        ],
        [ApplicationStatus.Interviewing] =
        [
            ApplicationStatus.Matched, ApplicationStatus.Preparing, ApplicationStatus.PendingApproval,
            ApplicationStatus.Submitted, ApplicationStatus.Interviewing,
        ],
        [ApplicationStatus.Offered] =
        [
            ApplicationStatus.Matched, ApplicationStatus.Preparing, ApplicationStatus.PendingApproval,
            ApplicationStatus.Submitted, ApplicationStatus.Interviewing, ApplicationStatus.Offered,
        ],
        [ApplicationStatus.Rejected] =
        [
            ApplicationStatus.Matched, ApplicationStatus.Preparing, ApplicationStatus.PendingApproval,
            ApplicationStatus.Submitted, ApplicationStatus.Rejected,
        ],
        [ApplicationStatus.Withdrawn] = [ApplicationStatus.Withdrawn],
    };

    private static void DriveTo(JobApplication application, ApplicationStatus target)
    {
        foreach (var step in PathTo[target])
        {
            application.TransitionTo(step);
        }
    }
}
