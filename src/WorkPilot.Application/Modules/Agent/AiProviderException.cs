namespace WorkPilot.Application.Modules.Agent;

/// <summary>
/// An AI provider call failed after the provider client's own transient
/// retries: an HTTP error status, a network failure, or a per attempt
/// timeout (spec 0006, AC-5, AC-6). Carries which purpose, provider, and
/// model failed so callers can fail and audit without referencing any AI SDK.
/// The message never contains an API key or prompt text.
/// </summary>
public sealed class AiProviderException(string purpose, string provider, string? model, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The AI purpose that was called, e.g. <c>Planner</c>.</summary>
    public string Purpose { get; } = purpose;

    /// <summary>The configured provider name, e.g. <c>deepseek</c>.</summary>
    public string Provider { get; } = provider;

    /// <summary>The model that was asked; null for the Fake provider.</summary>
    public string? Model { get; } = model;
}
