using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs.Sources;

namespace WorkPilot.Infrastructure.Modules.Jobs;

/// <summary>DI registration for job source ingestion (docs/specs/0008-job-source-ingestion).</summary>
public static class JobIngestionServiceCollectionExtensions
{
    /// <summary>Configuration key for the Greenhouse Job Board API base URL.</summary>
    public const string GreenhouseBaseUrlKey = "Jobs:Greenhouse:BaseUrl";

    /// <summary>Default Greenhouse Job Board API base URL.</summary>
    public const string DefaultGreenhouseBaseUrl = "https://boards-api.greenhouse.io/v1/";

    /// <summary>
    /// Registers the ingestion use case, its repository, and every
    /// <see cref="IJobSource"/>. Validates source configuration now, so a bad
    /// value fails startup instead of the first ingestion.
    /// </summary>
    public static IServiceCollection AddJobIngestionInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var greenhouseBaseUrl = ValidateBaseUrl(configuration[GreenhouseBaseUrlKey] ?? DefaultGreenhouseBaseUrl, GreenhouseBaseUrlKey);

        // IAuditService is registered by the Api's agent orchestrator setup (spec 0005).
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IJobIngestionRepository, JobIngestionRepository>();
        services.AddScoped<JobIngestionService>();

        // Every source is one IJobSource registration (AC-8), resolved as
        // IEnumerable<IJobSource> by JobIngestionService.
        // A large board with content=true is several MB and can take ~10s,
        // right at the ServiceDefaults standard resilience handler's 10s per
        // attempt timeout (and that default pipeline ignores per client named
        // options), so this client swaps it for its own, roomier one.
        // RemoveAllResilienceHandlers is still marked experimental (EXTEXP0001)
        // but is Microsoft's documented way to override the default pipeline.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(GreenhouseJobSource.ClientName, client =>
            {
                client.BaseAddress = greenhouseBaseUrl;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkPilot/1.0 (job ingestion)");
            })
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(180);
            });
#pragma warning restore EXTEXP0001
        services.AddTransient<IJobSource>(sp =>
            new GreenhouseJobSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient(GreenhouseJobSource.ClientName)));

        return services;
    }

    private static Uri ValidateBaseUrl(string value, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.AbsolutePath.EndsWith('/'))
        {
            throw new InvalidOperationException($"{key} must be an absolute https URL ending in '/', got '{value}'.");
        }

        return uri;
    }
}
