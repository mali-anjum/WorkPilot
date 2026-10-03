using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkPilot.AI.Matching;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs.Matching;
using WorkPilot.Workers.Audit;
using WorkPilot.Workers.Common;

namespace WorkPilot.Workers.Jobs;

/// <summary>The Jobs module: job source ingestion, deduplication and matching (specs 0008, 0017, 0019).</summary>
public static class JobsModule
{
    /// <summary>
    /// Registers the sources, repositories and use cases, the ingestion, reconcile and matching
    /// jobs, the hourly match sweep, and the matching event handlers. Validates the <c>Matching</c>
    /// section at startup (AC-16).
    /// </summary>
    public static IServiceCollection AddJobsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobIngestionInfrastructure(configuration);
        services.AddScoped<IngestJobsJob>();
        services.AddScoped<ReconcileJobsJob>();

        services.AddOptions<MatchingSettings>()
            .Bind(configuration.GetSection(MatchingSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MatchingSettings>, MatchingSettingsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MatchingSettings>>().Value);
        services.AddScoped<IMatchRepository, MatchRepository>();
        services.AddScoped<IMatchQueries, MatchQueries>();
        services.AddScoped<IJobRequirementExtractor, ChatClientJobRequirementExtractor>();
        services.AddScoped<IMatchJobScheduler, HangfireMatchJobScheduler>();
        services.AddScoped<JobMatchingService>();
        services.AddScoped<ExtractJobRequirementsJob>();
        services.AddScoped<RescoreProfileJob>();
        services.AddScoped<MatchSweepJob>();
        services.AddRecurringJob<MatchSweepJob>(MatchSweepJob.RecurringJobId, Cron.Hourly(), j => j.RunAsync(false));
        services.AddEventHandler<JobContentChanged, EnqueueRequirementExtraction>();
        services.AddEventHandler<MatchProfileChanged, EnqueueProfileRescore>();
        return services;
    }
}

/// <summary>Names every broken <c>Matching</c> rule in the startup failure (AC-16).</summary>
internal sealed class MatchingSettingsValidator : IValidateOptions<MatchingSettings>
{
    public ValidateOptionsResult Validate(string? name, MatchingSettings options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
