using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Profile.Resumes;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <summary>DI registration for resume management (spec 0009).</summary>
public static class ResumeServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IResumeService"/> and the Postgres backed <see cref="IResumeFileStore"/>.</summary>
    public static IServiceCollection AddResumeManagement(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResumeFileStore, PostgresResumeFileStore>();
        services.AddScoped<IResumeService, ResumeService>();
        return services;
    }
}
