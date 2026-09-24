using Microsoft.Extensions.Options;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Startup validation for the <c>AI</c> section (spec 0006, AC-3): every
/// failure names the configuration key to fix. Only the active provider is
/// checked, so a declared but unused provider may have no key.
/// </summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var errors = Collect(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>Every problem with <paramref name="options"/>, empty when it is valid.</summary>
    public static IReadOnlyList<string> Collect(AiOptions options)
    {
        const string section = AiOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.ActiveProvider))
        {
            return [$"{section}:ActiveProvider is not set. Set it to one of the names under {section}:Providers (e.g. OpenAI, DeepSeek)."];
        }

        if (options.FindActive() is not { } active)
        {
            var declared = options.Providers.Count == 0 ? "none" : string.Join(", ", options.Providers.Keys);
            return [$"{section}:ActiveProvider is '{options.ActiveProvider}', but {section}:Providers has no entry with that name (declared: {declared})."];
        }

        var prefix = $"{section}:Providers:{active.Key}";
        var provider = active.Value;
        var errors = new List<string>();

        if (provider.TimeoutSeconds is < 1 or > 600)
        {
            errors.Add($"{prefix}:TimeoutSeconds is {provider.TimeoutSeconds}; it must be between 1 and 600.");
        }

        if (string.Equals(provider.Kind, AiProviderKinds.Fake, StringComparison.OrdinalIgnoreCase))
        {
            return errors;
        }

        if (!string.Equals(provider.Kind, AiProviderKinds.OpenAICompatible, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{prefix}:Kind is '{provider.Kind}'; it must be {AiProviderKinds.OpenAICompatible} or {AiProviderKinds.Fake}.");
            return errors;
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"{prefix}:Endpoint must be an absolute http or https URL (e.g. https://api.openai.com/v1).");
        }

        if (string.IsNullOrWhiteSpace(provider.Model))
        {
            errors.Add($"{prefix}:Model is not set.");
        }

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            // Never echo the value, only where to put it.
            errors.Add($"{prefix}:ApiKey is not set. Set it with `dotnet user-secrets set \"{prefix}:ApiKey\" <key>` in src/WorkPilot.Api, or the {prefix.Replace(":", "__")}__ApiKey env var. Never commit it.");
        }

        return errors;
    }
}
