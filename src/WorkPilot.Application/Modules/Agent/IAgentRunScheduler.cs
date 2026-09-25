namespace WorkPilot.Application.Modules.Agent;

/// <summary>Schedules the Execution Engine to advance a run in the background (implemented over Hangfire).</summary>
public interface IAgentRunScheduler
{
    /// <summary>Enqueues one <c>AdvanceRun</c> job for <paramref name="agentRunId"/>.</summary>
    void EnqueueAdvance(Guid agentRunId);
}
