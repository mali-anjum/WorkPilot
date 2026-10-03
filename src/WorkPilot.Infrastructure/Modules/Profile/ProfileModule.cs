using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Application.Modules.Profile.Resumes;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <summary>The Profile module: resume management (spec 0009) and the match profile (spec 0019); cover letters (#15) and onboarding (#33) add to it.</summary>
public static class ProfileModule
{
    /// <summary>Registers <see cref="IResumeService"/>, the Postgres backed <see cref="IResumeFileStore"/> and <see cref="IMatchProfileService"/>.</summary>
    public static IServiceCollection AddProfileModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResumeFileStore, PostgresResumeFileStore>();
        services.AddScoped<IResumeService, ResumeService>();
        services.AddScoped<IMatchProfileService, MatchProfileService>();
        return services;
    }
}
