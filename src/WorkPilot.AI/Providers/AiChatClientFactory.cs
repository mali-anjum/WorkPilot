using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using WorkPilot.AI.Agent;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Builds the one <see cref="IChatClient"/> the active provider describes
/// (spec 0006). The only place an OpenAI SDK type is touched; everything
/// else in the app sees <see cref="IChatClient"/> alone.
/// </summary>
public static class AiChatClientFactory
{
    /// <summary>Creates the client for <paramref name="options"/>' active provider. Assumes the options already passed <see cref="AiOptionsValidator"/>.</summary>
    /// <exception cref="InvalidOperationException">The active provider is missing or invalid (validation was skipped).</exception>
    public static IChatClient Create(AiOptions options)
    {
        var errors = AiOptionsValidator.Collect(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        var provider = options.FindActive()!.Value.Value;

        if (string.Equals(provider.Kind, AiProviderKinds.Fake, StringComparison.OrdinalIgnoreCase))
        {
            return new FakeChatClient();
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(provider.ApiKey!),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint!),
                NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),
            });

        return client.GetChatClient(provider.Model!).AsIChatClient();
    }
}
