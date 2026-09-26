using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs;

namespace WorkPilot.Workers.Jobs;

/// <summary>
/// Runs one job source ingestion off the request thread (specs 0008, 0017).
/// Safe to retry: the ingestion is an idempotent upsert keyed on the source's
/// own external ids, and a failure stores nothing (spec 0008, AC-4, AC-6).
/// </summary>
public sealed class IngestJobsJob(JobIngestionService ingestion, IBackgroundJobClient jobs, ILogger<IngestJobsJob> logger)
{
    /// <summary>
    /// Ingests <paramref name="jobSourceId"/>'s board, keeping titles matching
    /// <paramref name="keywords"/>. A <paramref name="companyName"/> different
    /// from the stored one renames the source and rematches its jobs first
    /// (spec 0017, AC-7). Enqueues the reconcile when the run left a job stale.
    /// </summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid jobSourceId, string? keywords, string? companyName)
    {
        var summary = await ingestion.IngestAsync(jobSourceId, keywords, companyName, CancellationToken.None);
        if (summary is null)
        {
            logger.LogWarning("Job source {JobSourceId} no longer exists; nothing ingested.", jobSourceId);
            return;
        }

        logger.LogInformation(
            "Ingested job source {JobSourceId}: fetched {Fetched}, matched {Matched}, created {Created}, updated {Updated}, unchanged {Unchanged}, skipped {Skipped}, merged {Merged}.",
            summary.JobSourceId, summary.Fetched, summary.Matched, summary.Created, summary.Updated, summary.Unchanged, summary.Skipped, summary.Merged);

        if (summary.ReconcileNeeded)
        {
            jobs.Enqueue<ReconcileJobsJob>(j => j.RunAsync());
        }
    }
}

/// <summary>
/// Recomputes stale match keys and merges the jobs that now collide (spec
/// 0017, AC-9). Enqueued on Api start when any job is stale, and after an
/// ingestion run that left one stale. Idempotent: with nothing stale it does
/// nothing, and each key group commits on its own.
/// </summary>
public sealed class ReconcileJobsJob(JobDedupService dedup, ILogger<ReconcileJobsJob> logger)
{
    /// <summary>Runs one reconcile pass over every stale job.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync()
    {
        var merged = await dedup.ReconcileAsync(CancellationToken.None);
        logger.LogInformation("Job reconcile finished: merged {Merged} jobs.", merged);
    }
}

/// <summary>DI entry point for job source ingestion (specs 0008, 0017): the infrastructure plus its background jobs.</summary>
public static class JobIngestionRegistration
{
    /// <summary>Registers job ingestion and deduplication (sources, repository, use cases) and their background jobs.</summary>
    public static IServiceCollection AddJobIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobIngestionInfrastructure(configuration);
        services.AddScoped<IngestJobsJob>();
        services.AddScoped<ReconcileJobsJob>();
        return services;
    }
}
