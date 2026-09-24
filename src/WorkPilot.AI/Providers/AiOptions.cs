namespace WorkPilot.AI.Providers;

/// <summary>
/// The <c>Ai</c> configuration section (docs/specs/0006-ai-provider-abstraction):
/// named OpenAI compatible providers, and a map from each purpose to a
/// provider and model. Switching a model is a config change plus a restart,
/// never a code change.
/// </summary>
public sealed class AiOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// The reserved provider name for the deterministic, no network
    /// <see cref="Agent.FakeChatClient"/>. Never declared under <see cref="Providers"/>.
    /// </summary>
    public const string FakeProvider = "Fake";

    /// <summary>When true, prompt and response text is recorded in telemetry and logs (AC-7). Off by default: prompts carry personal data.</summary>
    public bool LogSensitiveData { get; set; }

    /// <summary>Every declared provider, keyed by name (e.g. <c>openai</c>, <c>gemini</c>, <c>deepseek</c>, <c>ollama</c>).</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which provider and model answers each purpose, keyed by a name from <see cref="AiPurposes.All"/>.</summary>
    public Dictionary<string, AiPurposeOptions> Purposes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One OpenAI compatible provider under <c>Ai:Providers:&lt;name&gt;</c>.</summary>
public sealed class AiProviderOptions
{
    /// <summary>Default for <see cref="TimeoutSeconds"/>.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>Absolute base URL of the provider's OpenAI compatible API, e.g. <c>https://api.deepseek.com/v1</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The provider's API key. Only ever from user secrets or env vars, never a tracked file; never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>False for a provider that needs no key (Ollama). Defaults to true.</summary>
    public bool RequiresApiKey { get; set; } = true;

    /// <summary>Cutoff for each HTTP attempt, 1 to 600 seconds (AC-5).</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}

/// <summary>One purpose's mapping under <c>Ai:Purposes:&lt;purpose&gt;</c>.</summary>
public sealed class AiPurposeOptions
{
    /// <summary>A key of <see cref="AiOptions.Providers"/>, or <see cref="AiOptions.FakeProvider"/>.</summary>
    public string? Provider { get; set; }

    /// <summary>The model id to ask for, e.g. <c>deepseek-chat</c>. Not used by the Fake provider.</summary>
    public string? Model { get; set; }
}
