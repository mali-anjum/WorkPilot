using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Infrastructure.Modules.Jobs;

namespace WorkPilot.Workers.Jobs;

/// <summary>The Jobs module: job source ingestion and deduplication (specs 0008, 0017).</summary>
public static class JobsModule
{
    /// <summary>Registers the sources, repository and use cases, plus the ingestion and reconcile jobs.</summary>
    public static IServiceCollection AddJobsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobIngestionInfrastructure(configuration);
        services.AddScoped<IngestJobsJob>();
        services.AddScoped<ReconcileJobsJob>();
        return services;
    }
}
