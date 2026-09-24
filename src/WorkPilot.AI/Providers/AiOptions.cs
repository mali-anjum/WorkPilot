namespace WorkPilot.AI.Providers;

/// <summary>
/// The <c>AI</c> configuration section: a named list of providers and the
/// one that is active (docs/specs/0006-ai-provider-abstraction). Switching
/// providers is a config change plus a restart, never a code change.
/// </summary>
public sealed class AiOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "AI";

    /// <summary>Name of the entry under <see cref="Providers"/> that answers every <c>IChatClient</c> call.</summary>
    public string? ActiveProvider { get; set; }

    /// <summary>Every declared provider, keyed by name (e.g. <c>OpenAI</c>, <c>DeepSeek</c>, <c>Fake</c>).</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks up the active provider's entry, ignoring case like the rest of
    /// .NET configuration does. Null when <see cref="ActiveProvider"/> is blank or names no entry.
    /// </summary>
    public KeyValuePair<string, AiProviderOptions>? FindActive()
    {
        if (string.IsNullOrWhiteSpace(ActiveProvider))
        {
            return null;
        }

        foreach (var entry in Providers)
        {
            if (string.Equals(entry.Key, ActiveProvider, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>One named provider under <c>AI:Providers:&lt;Name&gt;</c>.</summary>
public sealed class AiProviderOptions
{
    /// <summary>Default for <see cref="TimeoutSeconds"/>.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>Which adapter builds the client: <see cref="AiProviderKinds.OpenAICompatible"/> or <see cref="AiProviderKinds.Fake"/>.</summary>
    public string? Kind { get; set; }

    /// <summary>Absolute base URL of an OpenAI compatible API, e.g. <c>https://api.deepseek.com/v1</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model id sent with every call, e.g. <c>deepseek-chat</c>.</summary>
    public string? Model { get; set; }

    /// <summary>The provider's API key. Only ever from user secrets or env vars, never a tracked file; never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Network timeout per HTTP call, 1 to 600 seconds.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}

/// <summary>The provider kinds <see cref="AiChatClientFactory"/> knows how to build.</summary>
public static class AiProviderKinds
{
    /// <summary>Any endpoint speaking OpenAI's Chat Completions API (OpenAI, DeepSeek, OpenRouter, a local server).</summary>
    public const string OpenAICompatible = "OpenAICompatible";

    /// <summary>The deterministic, no network <see cref="Agent.FakeChatClient"/>, for keyless development and tests.</summary>
    public const string Fake = "Fake";
}
