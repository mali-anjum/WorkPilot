using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using WorkPilot.AI.Agent;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Builds the <see cref="IChatClient"/> pipeline for one resolved purpose
/// (spec 0006). The only place an OpenAI SDK type is touched; everything
/// else in the app asks for a purpose and sees <see cref="IChatClient"/> alone.
/// </summary>
public static class AiChatClientFactory
{
    /// <summary>The OpenTelemetry source and meter name every AI call reports under (AC-7).</summary>
    public const string TelemetrySourceName = "WorkPilot.AI";

    // Up to 2 more attempts on 408, 429, 5xx and network failures, with the
    // SDK's exponential backoff honoring Retry-After (AC-5). Retries live
    // here rather than in an IHttpClientFactory resilience handler, whose
    // Aspire default 10s attempt timeout would cut off slow model calls.
    private const int MaxRetries = 2;

    /// <summary>Creates the client for <paramref name="target"/>. Assumes <paramref name="options"/> already passed <see cref="AiOptionsValidator"/>.</summary>
    public static IChatClient Create(ResolvedAiPurpose target, AiOptions options, IServiceProvider services)
    {
        AiProviderOptions? provider = null;
        IChatClient inner;

        if (target.IsFake)
        {
            inner = new FakeChatClient();
        }
        else
        {
            provider = options.Providers[target.Provider];
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.Endpoint!),
                NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),
                RetryPolicy = new ClientRetryPolicy(MaxRetries),
            };

            // The SDK insists on a credential; a keyless provider (Ollama) ignores it.
            var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(provider.ApiKey) ? "unused" : provider.ApiKey);
            inner = new OpenAIClient(credential, clientOptions).GetChatClient(target.Model!).AsIChatClient();
        }

        // Outermost first: telemetry sees every call, including ones that end
        // in AiProviderException; the error translation sits innermost so it
        // only ever sees the final failure after the SDK's retries.
        var builder = new ChatClientBuilder(inner)
            .UseOpenTelemetry(sourceName: TelemetrySourceName, configure: c => c.EnableSensitiveData = options.LogSensitiveData);

        if (options.LogSensitiveData)
        {
            // LoggingChatClient writes full prompts and responses at Trace;
            // only wired in when capture was switched on deliberately (AC-7).
            builder = builder.UseLogging();
        }

        return builder
            .Use(client => new ProviderErrorChatClient(client, target, provider?.ApiKey))
            .Build(services);
    }
}
