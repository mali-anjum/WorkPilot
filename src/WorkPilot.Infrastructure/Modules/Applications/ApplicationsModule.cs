using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Applications;

namespace WorkPilot.Infrastructure.Modules.Applications;

/// <summary>The Applications module. Today only the job merge hook (spec 0017); features 16 to 18 and 29 add to it.</summary>
public static class ApplicationsModule
{
    /// <summary>Registers <see cref="IJobApplicationReassigner"/>.</summary>
    public static IServiceCollection AddApplicationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IJobApplicationReassigner, JobApplicationReassigner>();
        return services;
    }
}
