using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Common;

namespace WorkPilot.Workers.Audit;

/// <summary>The Audit module: the audit log, its activity feed, and the domain event outbox (specs 0005, 0011, 0018).</summary>
public static class AuditModule
{
    /// <summary>
    /// Registers <see cref="IAuditService"/>, <see cref="IActivityQuery"/>, <see cref="IEventPublisher"/>, the event registry,
    /// the outbox jobs and their every minute sweep, and validates the <c>Outbox</c> settings.
    /// </summary>
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IActivityQuery, ActivityQuery>();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddSingleton<EventRegistry>();
        services.AddSingleton<IOutboxDispatchTrigger, HangfireOutboxDispatchTrigger>();
        services.AddScoped<DispatchOutboxJob>();
        services.AddScoped<HandleEventJob>();
        services.AddScoped<OutboxSweepJob>();
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection("Outbox"))
            .Validate(o => o.RetentionDays > 0, "Outbox:RetentionDays must be a positive number of days.")
            .ValidateOnStart();
        services.AddRecurringJob<OutboxSweepJob>(OutboxSweepJob.RecurringJobId, Cron.Minutely(), j => j.RunAsync());
        services.AddHostedService<OutboxStartupCheck>();
        return services;
    }

    /// <summary>
    /// Registers an event handler (spec 0018, section 2), called from the subscribing module's own
    /// <c>Add&lt;Module&gt;Module</c>.
    /// </summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : WorkPilot.Domain.Common.IDomainEvent
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new EventHandlerRegistration(typeof(TEvent), THandler.HandlerKey, new EventHandlerInvoker<TEvent, THandler>()));
        return services;
    }
}

/// <summary>
/// Fails startup when the registry is inconsistent (a duplicate event name or handler key) or when
/// an undispatched outbox message names an event no type declares, so such a message is never
/// silently dropped (spec 0018, section 2).
/// </summary>
internal sealed class OutboxStartupCheck(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<EventRegistry>();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
        var stored = await db.OutboxMessages
            .Where(m => m.DispatchedAt == null)
            .Select(m => m.EventName)
            .Distinct()
            .ToListAsync(cancellationToken);
        var unknown = stored.Where(name => registry.FindType(name) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The outbox holds undispatched events with no matching event type: {string.Join(", ", unknown)}. Restore the event types or remove those rows.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
