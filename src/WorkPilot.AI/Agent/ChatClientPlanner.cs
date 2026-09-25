using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.AI.Agent;

/// <summary>
/// Turns a goal into a plan via a single upfront <see cref="IChatClient"/>
/// call (spec 0005's plan-then-execute shape). Provider agnostic: which
/// provider and model answer is configuration only, via the Planner purpose
/// (spec 0006, Ai:Purposes:Planner, else Ai:Purposes:Default).
/// </summary>
public sealed class ChatClientPlanner(
    [FromKeyedServices(AiPurposes.Planner)] IChatClient chatClient,
    [FromKeyedServices(AiPurposes.Planner)] ResolvedAiPurpose target) : IPlanner
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
            // A provider failure surfaces as AiProviderException from the
            // client pipeline (spec 0006, AC-6); nothing to translate here.
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

        throw new PlanParseException($"Planner produced no valid plan after one retry: {lastError}")
        {
            Purpose = target.Purpose,
            Provider = target.Provider,
            Model = target.Model,
        };
    }

    private static List<PlanStep>? TryParseSteps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlanResponse>(StripCodeFence(text), JsonSerializerOptions.Web)?.Steps;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Real models often wrap JSON in a markdown fence even in JSON mode
    // (```json ... ```); strip exactly one surrounding fence (spec 0006, AC-9).
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal) || trimmed.Length < 6)
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd >= 0)
        {
            return trimmed[(firstLineEnd + 1)..^3].Trim();
        }

        // A one line fence (```json{...}```): drop the fence and any language tag.
        var inner = trimmed[3..^3].TrimStart();
        return inner.TrimStart("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray()).Trim();
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
