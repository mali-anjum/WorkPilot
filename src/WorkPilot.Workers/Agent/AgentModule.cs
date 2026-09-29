using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.AI.Agent;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Agent.Tools;

namespace WorkPilot.Workers.Agent;

/// <summary>
/// The Agent module (spec 0005): Planner, Policy Engine, Tool Registry, Execution Engine
/// (<see cref="AdvanceRunJob"/>) and Verification Engine. The AI providers themselves are host
/// plumbing (<c>AddWorkPilotAi</c>), registered outside the modules block.
/// </summary>
public static class AgentModule
{
    /// <summary>Registers the orchestrator pieces, its tools, its background jobs and the run scheduler.</summary>
    public static IServiceCollection AddAgentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPlanner, ChatClientPlanner>();
        services.AddScoped<IPolicyEngine, PolicyEngine>();
        services.AddScoped<IVerificationEngine, VerificationEngine>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddScoped<ITool, ListMyProfileTool>();
        services.AddScoped<ITool, ApprovalRequiredDemoTool>();
        services.AddScoped<IAgentRunScheduler, HangfireAgentRunScheduler>();
        services.AddScoped<PlanRunJob>();
        services.AddScoped<AdvanceRunJob>();
        return services;
    }
}
