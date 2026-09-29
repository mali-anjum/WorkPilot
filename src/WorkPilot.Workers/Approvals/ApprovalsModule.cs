using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Approvals;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent.Tools;
using WorkPilot.Infrastructure.Modules.Approvals;

namespace WorkPilot.Workers.Approvals;

/// <summary>The Approvals module: the approval engine and Approval center (spec 0007).</summary>
public static class ApprovalsModule
{
    /// <summary>
    /// Registers the decide and Approval center use cases, the approval repository, and the
    /// explicit confirmation demo tool.
    /// </summary>
    public static IServiceCollection AddApprovalsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<DecideApprovalHandler>();
        services.AddScoped<GetApprovalCenterHandler>();
        services.AddScoped<ITool, ExplicitConfirmationDemoTool>();
        return services;
    }
}
