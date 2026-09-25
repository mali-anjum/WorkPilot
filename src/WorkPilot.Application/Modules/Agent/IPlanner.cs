namespace WorkPilot.Application.Modules.Agent;

/// <summary>A tool as the Planner sees it: enough to decide whether and how to call it.</summary>
public sealed record ToolDescriptor(string Name, string Description, IReadOnlyList<string> RequiredArguments);

public sealed record PlannedToolCall(string Tool, IReadOnlyDictionary<string, string> Arguments);

public sealed record AgentPlan(IReadOnlyList<PlannedToolCall> Steps);

/// <summary>The Planner's output failed to parse into a valid, non-empty plan (spec 0005, AC-7).</summary>
public sealed class PlanParseException(string message) : Exception(message)
{
    /// <summary>The AI purpose whose model produced the unparseable plan (spec 0006, AC-6).</summary>
    public string? Purpose { get; init; }

    /// <summary>The configured provider name behind <see cref="Purpose"/>, e.g. <c>deepseek</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>The model behind <see cref="Purpose"/>; null for the Fake provider.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Turns a goal into an ordered plan of tool calls via a single upfront model
/// call (spec 0005's chosen plan-then-execute shape, not a re-planning loop).
/// </summary>
public interface IPlanner
{
    /// <exception cref="PlanParseException">The model's output didn't parse into a valid, non-empty plan even after one retry.</exception>
    /// <exception cref="AiProviderException">The AI provider call itself failed, after its own retries.</exception>
    Task<AgentPlan> PlanAsync(string goal, IReadOnlyList<ToolDescriptor> availableTools, CancellationToken cancellationToken);
}
