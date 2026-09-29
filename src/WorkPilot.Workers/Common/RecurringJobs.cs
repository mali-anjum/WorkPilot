using System.Linq.Expressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WorkPilot.Workers.Common;

/// <summary>One recurring Hangfire job a module declares (spec 0018, section 5).</summary>
public interface IRecurringJobDefinition
{
    /// <summary>The stable recurring job id, prefixed with the module (<c>audit.outbox-sweep</c>).</summary>
    string Id { get; }

    /// <summary>Adds or updates the job in Hangfire.</summary>
    void Apply(IRecurringJobManager manager);
}

/// <summary>A recurring call to <typeparamref name="TJob"/> on a cron schedule.</summary>
public sealed record RecurringJobDefinition<TJob>(string Id, string Cron, Expression<Func<TJob, Task>> Call) : IRecurringJobDefinition
{
    public void Apply(IRecurringJobManager manager) => manager.AddOrUpdate(Id, Call, Cron);
}

/// <summary>
/// Recurring job registration without touching the Hangfire setup (spec 0018, section 5): a
/// module registers its jobs with <see cref="AddRecurringJob{TJob}"/> in its own module file, and
/// the host applies them all with one <see cref="ApplyRecurringJobs"/> call.
/// </summary>
public static class RecurringJobs
{
    /// <summary>Declares a recurring job; applied by <see cref="ApplyRecurringJobs"/> at startup.</summary>
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services, string id, string cron, Expression<Func<TJob, Task>> call) =>
        services.AddSingleton<IRecurringJobDefinition>(new RecurringJobDefinition<TJob>(id, cron, call));

    /// <summary>Adds or updates every declared recurring job in Hangfire. Throws on a duplicate id.</summary>
    public static IHost ApplyRecurringJobs(this IHost host)
    {
        var definitions = host.Services.GetServices<IRecurringJobDefinition>().ToList();
        var duplicate = definitions.GroupBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Recurring job id \"{duplicate.Key}\" is registered more than once.");
        }

        var manager = host.Services.GetRequiredService<IRecurringJobManager>();
        foreach (var definition in definitions)
        {
            definition.Apply(manager);
        }

        return host;
    }
}
