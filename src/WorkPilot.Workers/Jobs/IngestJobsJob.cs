using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs;

namespace WorkPilot.Workers.Jobs;

/// <summary>
/// Runs one job source ingestion off the request thread (spec 0008). Safe to
/// retry: the ingestion is an idempotent upsert keyed on the source's own
/// external ids, and a fetch failure stores nothing (AC-4, AC-6).
/// </summary>
public sealed class IngestJobsJob(JobIngestionService ingestion, ILogger<IngestJobsJob> logger)
{
    /// <summary>Ingests <paramref name="jobSourceId"/>'s board, keeping titles matching <paramref name="keywords"/>.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid jobSourceId, string? keywords)
    {
        var summary = await ingestion.IngestAsync(jobSourceId, keywords, CancellationToken.None);
        if (summary is null)
        {
            logger.LogWarning("Job source {JobSourceId} no longer exists; nothing ingested.", jobSourceId);
            return;
        }

        logger.LogInformation(
            "Ingested job source {JobSourceId}: fetched {Fetched}, matched {Matched}, created {Created}, updated {Updated}, unchanged {Unchanged}, skipped {Skipped}.",
            summary.JobSourceId, summary.Fetched, summary.Matched, summary.Created, summary.Updated, summary.Unchanged, summary.Skipped);
    }
}

/// <summary>DI entry point for job source ingestion (spec 0008): the infrastructure plus its background job.</summary>
public static class JobIngestionRegistration
{
    /// <summary>Registers job ingestion (sources, repository, use case) and <see cref="IngestJobsJob"/>.</summary>
    public static IServiceCollection AddJobIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobIngestionInfrastructure(configuration);
        services.AddScoped<IngestJobsJob>();
        return services;
    }
}
