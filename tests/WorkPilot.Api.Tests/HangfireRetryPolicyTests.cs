using System.Reflection;
using Hangfire;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Workers.Jobs;

namespace WorkPilot.Api.Tests;

// The Hangfire retry counts the specs fix. A retry count is an attribute, so a
// change to it compiles fine and only shows up in production as too many or too
// few attempts; these pin the ones a spec states.
public class HangfireRetryPolicyTests
{
    // covers: spec 0008 AC-6 (a source failure fails the job so it is retried, 2 automatic retries)
    [Fact]
    public void Ingestion_is_retried_twice()
    {
        Assert.Equal(2, RetriesOf(typeof(IngestJobsJob)));
    }

    // covers: spec 0019 AC-8 (three runs in total, matching the requirements row's attempt counter)
    [Fact]
    public void Extraction_runs_as_many_times_as_the_requirements_row_counts()
    {
        Assert.Equal(JobRequirement.MaxAttempts, 1 + RetriesOf(typeof(ExtractJobRequirementsJob)));
    }

    private static int RetriesOf(Type job) =>
        job.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts;
}
