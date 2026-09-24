using Hangfire;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.Workers.Agent;

/// <inheritdoc cref="IAgentRunScheduler" />
public sealed class HangfireAgentRunScheduler(IBackgroundJobClient jobs) : IAgentRunScheduler
{
    public void EnqueueAdvance(Guid agentRunId) => jobs.Enqueue<AdvanceRunJob>(j => j.RunAsync(agentRunId));
}
