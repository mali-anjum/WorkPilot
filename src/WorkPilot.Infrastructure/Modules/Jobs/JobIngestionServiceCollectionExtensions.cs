using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Applications;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Applications;
using WorkPilot.Infrastructure.Modules.Jobs.Sources;

namespace WorkPilot.Infrastructure.Modules.Jobs;

/// <summary>DI registration for job source ingestion and deduplication (docs/specs/0008-job-source-ingestion, 0017-job-deduplication).</summary>
public static class JobIngestionServiceCollectionExtensions
{
    /// <summary>Configuration key for the Greenhouse Job Board API base URL.</summary>
    public const string GreenhouseBaseUrlKey = "Jobs:Greenhouse:BaseUrl";

    /// <summary>Default Greenhouse Job Board API base URL.</summary>
    public const string DefaultGreenhouseBaseUrl = "https://boards-api.greenhouse.io/v1/";

    /// <summary>Configuration key for the Lever postings API base URL (spec 0017).</summary>
    public const string LeverBaseUrlKey = "Jobs:Lever:BaseUrl";

    /// <summary>Default Lever postings API base URL (Lever's EU instance is <c>https://api.eu.lever.co/v0/</c>).</summary>
    public const string DefaultLeverBaseUrl = "https://api.lever.co/v0/";

    /// <summary>
    /// Registers the ingestion and deduplication use cases, their repository,
    /// and every <see cref="IJobSource"/>. Validates source configuration now,
    /// so a bad value fails startup instead of the first ingestion.
    /// </summary>
    public static IServiceCollection AddJobIngestionInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var greenhouseBaseUrl = ValidateBaseUrl(configuration[GreenhouseBaseUrlKey] ?? DefaultGreenhouseBaseUrl, GreenhouseBaseUrlKey);
        var leverBaseUrl = ValidateBaseUrl(configuration[LeverBaseUrlKey] ?? DefaultLeverBaseUrl, LeverBaseUrlKey);

        // IAuditService is registered by the Api's agent orchestrator setup (spec 0005).
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IJobApplicationReassigner, JobApplicationReassigner>();
        services.AddScoped<JobMerger>();
        services.AddScoped<JobIngestionService>();
        services.AddScoped<JobDedupService>();

        // Every source is one IJobSource registration (AC-8), resolved as
        // IEnumerable<IJobSource> by the use cases.
        AddSourceClient(services, GreenhouseJobSource.ClientName, greenhouseBaseUrl);
        AddSourceClient(services, LeverJobSource.ClientName, leverBaseUrl);
        services.AddTransient<IJobSource>(sp =>
            new GreenhouseJobSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient(GreenhouseJobSource.ClientName)));
        services.AddTransient<IJobSource>(sp =>
            new LeverJobSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient(LeverJobSource.ClientName)));

        return services;
    }

    // A large board is several MB and can take ~10s, right at the
    // ServiceDefaults standard resilience handler's 10s per attempt timeout
    // (and that default pipeline ignores per client named options), so each
    // source client swaps it for its own, roomier one.
    // RemoveAllResilienceHandlers is still marked experimental (EXTEXP0001)
    // but is Microsoft's documented way to override the default pipeline.
    private static void AddSourceClient(IServiceCollection services, string name, Uri baseUrl)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseUrl;
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
