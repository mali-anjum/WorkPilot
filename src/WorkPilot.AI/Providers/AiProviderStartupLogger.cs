using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Builds the chat client once at startup, so a construction failure stops
/// the host rather than a background job, and logs which provider is active
/// (spec 0006, AC-4, AC-5). Never logs the API key.
/// </summary>
internal sealed class AiProviderStartupLogger(IChatClient chatClient, IOptions<AiOptions> options, ILogger<AiProviderStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var (name, provider) = options.Value.FindActive()!.Value;

        if (string.Equals(provider.Kind, AiProviderKinds.Fake, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("AI provider: {Provider} (Fake). Agent runs are planned by a deterministic fake, not a real model.", name);
            return Task.CompletedTask;
        }

        var metadata = chatClient.GetService<ChatClientMetadata>();
        logger.LogInformation(
            "AI provider: {Provider} ({Kind}), endpoint {Endpoint}, model {Model}, timeout {TimeoutSeconds}s",
            name,
            AiProviderKinds.OpenAICompatible,
            metadata?.ProviderUri ?? new Uri(provider.Endpoint!),
            metadata?.DefaultModelId ?? provider.Model,
            provider.TimeoutSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
