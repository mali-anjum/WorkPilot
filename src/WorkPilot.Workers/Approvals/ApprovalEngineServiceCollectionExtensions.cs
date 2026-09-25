using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Application.Modules.Approvals;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent.Tools;
using WorkPilot.Infrastructure.Modules.Approvals;
using WorkPilot.Workers.Agent;

namespace WorkPilot.Workers.Approvals;

/// <summary>DI registration for the approval engine (spec 0007).</summary>
public static class ApprovalEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the decide and Approval center use cases, the approval
    /// repository, the Hangfire backed run scheduler, and the explicit
    /// confirmation demo tool. Expects the agent orchestrator core (spec 0005)
    /// and <c>WorkPilotDbContext</c> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddApprovalEngine(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<IAgentRunScheduler, HangfireAgentRunScheduler>();
        services.AddScoped<DecideApprovalHandler>();
        services.AddScoped<GetApprovalCenterHandler>();
        services.AddScoped<ITool, ExplicitConfirmationDemoTool>();
        return services;
    }
}
