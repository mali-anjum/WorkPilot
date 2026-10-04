using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Domain.Tests;

// A dismissal is one profile ruling one job out (spec 0021, AC-4). Pure unit tests; the one per
// (profile, job) rule and the merge move live in Postgres and are covered in the Api tests.
public class JobDismissalTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Profile = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000a1");
    private static readonly Guid Job = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000a2");

    // covers: spec 0021 AC-4
    [Fact]
    public void Create_records_the_profile_the_job_and_when()
    {
        var dismissal = JobDismissal.Create(Profile, Job, At);

        Assert.Equal(Profile, dismissal.ProfileId);
        Assert.Equal(Job, dismissal.JobId);
        Assert.Equal(At, dismissal.DismissedAt);
    }

    // covers: spec 0021 AC-4
    [Fact]
    public void A_dismissal_without_a_profile_is_refused()
    {
        Assert.Throws<ArgumentException>(() => JobDismissal.Create(Guid.Empty, Job, At));
    }

    // covers: spec 0021 AC-4
    [Fact]
    public void A_dismissal_without_a_job_is_refused()
    {
        Assert.Throws<ArgumentException>(() => JobDismissal.Create(Profile, Guid.Empty, At));
    }
}
