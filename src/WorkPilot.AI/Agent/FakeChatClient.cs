using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using WorkPilot.AI.Matching;

namespace WorkPilot.AI.Agent;

/// <summary>
/// A deterministic, no-network stand in for a real <see cref="IChatClient"/>.
/// Active when the configured provider's <c>Kind</c> is <c>Fake</c>, the
/// Development default (docs/specs/0006-ai-provider-abstraction). Picks the first tool the
/// goal text mentions by name, falling back to the first tool listed, so the
/// orchestrator's pipeline can be built and tested without a real API key. Job
/// requirements extraction prompts get a deterministic answer too (spec 0019).
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    private static readonly ChatClientMetadata Metadata = new("fake", defaultModelId: "fake");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = string.Concat(messages.Select(m => m.Text));

        // A job requirements extraction prompt (spec 0019, AC-13) gets a deterministic v1
        // document built from its description; anything else is a planning prompt.
        var json = ChatClientJobRequirementExtractor.DescriptionOf(prompt) is { } description
            ? FakeJobRequirements.Build(description)
            : BuildFakePlan(prompt);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)) { ModelId = "fake" });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Text);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? Metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private static string BuildFakePlan(string prompt)
    {
        var toolNames = ToolLinePattern().Matches(prompt).Select(m => m.Groups[1].Value).ToList();
        if (toolNames.Count == 0)
        {
            return """{"steps":[]}""";
        }

        var goalMatch = GoalLinePattern().Match(prompt);
        var goal = goalMatch.Success ? goalMatch.Groups[1].Value : string.Empty;
        var chosen = toolNames.Find(name => goal.Contains(name.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)) ?? toolNames[0];

        return JsonSerializer.Serialize(new
        {
            steps = new[] { new { tool = chosen, arguments = new Dictionary<string, string>() } },
        });
    }

    [GeneratedRegex(@"^- ([\w_]+):", RegexOptions.Multiline)]
    private static partial Regex ToolLinePattern();

    [GeneratedRegex(@"Goal:\s*(.+)")]
    private static partial Regex GoalLinePattern();
}
