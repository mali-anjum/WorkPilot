namespace WorkPilot.AI.Providers;

/// <summary>
/// A purpose after the <see cref="AiPurposes.Default"/> fallback: which
/// configured provider and model actually answer it. Registered keyed by
/// purpose next to its <c>IChatClient</c>, so audit rows and the health probe
/// can name the provider without touching the client (spec 0006, Value sourcing).
/// </summary>
/// <param name="Purpose">The purpose that was asked for, e.g. <c>Planner</c>, even when it fell back to Default.</param>
/// <param name="Provider">The configured provider name, e.g. <c>deepseek</c>, or <c>Fake</c>.</param>
/// <param name="Model">The model id; null for the Fake provider.</param>
public sealed record ResolvedAiPurpose(string Purpose, string Provider, string? Model)
{
    /// <summary>True when this purpose is answered by the deterministic Fake client.</summary>
    public bool IsFake => string.Equals(Provider, AiOptions.FakeProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves <paramref name="purpose"/> against validated <paramref name="options"/>.</summary>
    /// <exception cref="InvalidOperationException">Neither the purpose nor Default is mapped (validation was skipped).</exception>
    public static ResolvedAiPurpose Resolve(AiOptions options, string purpose)
    {
        var mapping = options.Purposes.GetValueOrDefault(purpose)
            ?? options.Purposes.GetValueOrDefault(AiPurposes.Default)
            ?? throw new InvalidOperationException($"{AiOptions.SectionName}:Purposes:{AiPurposes.Default} is not configured.");

        var provider = mapping.Provider?.Trim() ?? string.Empty;
        var isFake = string.Equals(provider, AiOptions.FakeProvider, StringComparison.OrdinalIgnoreCase);

        return new ResolvedAiPurpose(purpose, isFake ? AiOptions.FakeProvider : provider, isFake ? null : mapping.Model?.Trim());
    }
}
