using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkPilot.Application.Modules.Notifications;
using WorkPilot.Application.Modules.Notifications.Handlers;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Notifications;
using WorkPilot.Workers.Audit;
using WorkPilot.Workers.Common;

namespace WorkPilot.Workers.Notifications;

/// <summary>The Notifications module: in app notifications written by event handlers (spec 0020).</summary>
public static class NotificationsModule
{
    /// <summary>
    /// Registers the repository and use cases, the three event handlers, the daily cleanup, and
    /// validates the <c>Notifications</c> settings at startup.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<NotificationsService>();
        services.AddScoped<NotificationsCleanup>();
        services.AddScoped<NotificationsCleanupJob>();
        services.AddOptions<NotificationsOptions>()
            .Bind(configuration.GetSection(NotificationsOptions.SectionName))
            .Validate(o => o.ReadRetentionDays is >= 1 and <= 3650, "Notifications:ReadRetentionDays must be 1 to 3650 days.")
            .ValidateOnStart();
        services.AddRecurringJob<NotificationsCleanupJob>(NotificationsCleanupJob.RecurringJobId, Cron.Daily(3), j => j.RunAsync());
        services.AddEventHandler<ApprovalRequested, NotifyOnApprovalRequested>();
        services.AddEventHandler<AgentRunFailed, NotifyOnAgentRunFailed>();
        services.AddEventHandler<JobMatched, NotifyOnJobMatched>();
        return services;
    }
}

/// <summary>Daily at 03:00 UTC: dismisses read notifications past the retention window (spec 0020, AC-7).</summary>
public sealed class NotificationsCleanupJob(NotificationsCleanup cleanup, IOptions<NotificationsOptions> options, ILogger<NotificationsCleanupJob> logger)
{
    /// <summary>Recurring job id.</summary>
    public const string RecurringJobId = "notifications.cleanup";

    /// <summary>Runs one cleanup.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var removed = await cleanup.RunAsync(options.Value.ReadRetentionDays, CancellationToken.None);
        if (removed > 0)
        {
            logger.LogInformation("Notifications cleanup dismissed {Count} read notifications.", removed);
        }
    }
}
