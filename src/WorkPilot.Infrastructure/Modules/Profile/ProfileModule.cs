using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Profile.Resumes;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <summary>The Profile module. Today resume management (spec 0009); cover letters (#15) and onboarding (#33) add to it.</summary>
public static class ProfileModule
{
    /// <summary>Registers <see cref="IResumeService"/> and the Postgres backed <see cref="IResumeFileStore"/>.</summary>
    public static IServiceCollection AddProfileModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResumeFileStore, PostgresResumeFileStore>();
        services.AddScoped<IResumeService, ResumeService>();
        return services;
    }
}
