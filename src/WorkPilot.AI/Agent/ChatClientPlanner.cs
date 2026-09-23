using System.Text.Json;
using Microsoft.Extensions.AI;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.AI.Agent;

/// <summary>
/// Turns a goal into a plan via a single upfront <see cref="IChatClient"/>
/// call (spec 0005's plan-then-execute shape). Provider agnostic: which
/// IChatClient is actually wired in is scope item 7's decision, not this
/// class's.
/// </summary>
public sealed class ChatClientPlanner(IChatClient chatClient) : IPlanner
{
    private const int MaxSteps = 10;

    private static readonly ChatOptions PlanOptions = new() { ResponseFormat = ChatResponseFormat.Json };

    public async Task<AgentPlan> PlanAsync(string goal, IReadOnlyList<ToolDescriptor> availableTools, CancellationToken cancellationToken)
    {
        string? lastError = null;

        // One retry on a parse or empty-plan failure, then give up (AC-7).
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = BuildPrompt(goal, availableTools, lastError);
            var response = await chatClient.GetResponseAsync(prompt, PlanOptions, cancellationToken);
            var steps = TryParseSteps(response.Text);

            if (steps is { Count: > 0 } &&
                steps.Count <= MaxSteps &&
                steps.All(s => !string.IsNullOrWhiteSpace(s.Tool)))
            {
                return new AgentPlan(steps
                    .Select(s => new PlannedToolCall(s.Tool, s.Arguments ?? new Dictionary<string, string>()))
                    .ToList());
            }

            lastError = "The plan was empty, exceeded 10 steps, a step named no tool, or the response didn't parse as the expected JSON shape.";
        }

        throw new PlanParseException($"Planner produced no valid plan for goal \"{goal}\" after one retry: {lastError}");
    }

    private static List<PlanStep>? TryParseSteps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlanResponse>(text, JsonSerializerOptions.Web)?.Steps;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildPrompt(string goal, IReadOnlyList<ToolDescriptor> tools, string? retryError)
    {
        var toolList = string.Join("\n", tools.Select(t =>
            $"- {t.Name}: {t.Description} (arguments: {(t.RequiredArguments.Count == 0 ? "none" : string.Join(", ", t.RequiredArguments))})"));
        var retry = retryError is null ? string.Empty : $"\n\nYour previous attempt failed: {retryError} Fix it.";

        const string shape = """{"steps": [{"tool": "<name>", "arguments": {}}]}""";

        return $"""
            You are planning tool calls to achieve a goal. Available tools:
            {toolList}

            Goal: {goal}

            Reply with ONLY a JSON object of the shape {shape},
            an ordered plan of 1 to {MaxSteps} tool calls. Each step names exactly one tool from the list
            above and the arguments it needs. Never invent a tool not in the list.{retry}
            """;
    }

    private sealed record PlanResponse(List<PlanStep>? Steps);

    private sealed record PlanStep(string Tool, Dictionary<string, string>? Arguments);
}
