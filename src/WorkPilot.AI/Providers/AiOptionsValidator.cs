using Microsoft.Extensions.Options;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Startup validation for the <c>Ai</c> section (spec 0006, AC-4). Collects
/// every problem into one failure, each naming the configuration key to fix.
/// Only providers some purpose uses are checked, so a declared but unused
/// preset may have no key. Never echoes an API key value.
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
        var errors = new List<string>();
        var usedProviders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!options.Purposes.ContainsKey(AiPurposes.Default))
        {
            errors.Add($"{section}:Purposes:{AiPurposes.Default} is not set. Map it to a provider and model (or Provider \"{AiOptions.FakeProvider}\").");
        }

        if (options.Providers.ContainsKey(AiOptions.FakeProvider))
        {
            errors.Add($"{section}:Providers:{AiOptions.FakeProvider} is reserved for the built in fake client; remove that entry.");
        }

        foreach (var (purpose, mapping) in options.Purposes)
        {
            var prefix = $"{section}:Purposes:{purpose}";

            if (!AiPurposes.All.Contains(purpose, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{prefix} is not a known purpose (known: {string.Join(", ", AiPurposes.All)}).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.Provider))
            {
                errors.Add($"{prefix}:Provider is not set.");
                continue;
            }

            if (string.Equals(mapping.Provider.Trim(), AiOptions.FakeProvider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!options.Providers.ContainsKey(mapping.Provider.Trim()))
            {
                var declared = options.Providers.Count == 0 ? "none" : string.Join(", ", options.Providers.Keys);
                errors.Add($"{prefix}:Provider is '{mapping.Provider}', but {section}:Providers has no entry with that name (declared: {declared}).");
                continue;
            }

            usedProviders.Add(mapping.Provider.Trim());

            if (string.IsNullOrWhiteSpace(mapping.Model))
            {
                errors.Add($"{prefix}:Model is not set.");
            }
        }

        foreach (var name in usedProviders)
        {
            var provider = options.Providers[name];
            var prefix = $"{section}:Providers:{name}";

            if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add($"{prefix}:Endpoint must be an absolute http or https URL (e.g. https://api.openai.com/v1).");
            }

            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                // Only ever say where the key goes, never what it is.
                errors.Add($"{prefix}:ApiKey is not set. Set it with `dotnet user-secrets set \"{prefix}:ApiKey\" <key> --project src/WorkPilot.Api`, " +
                    $"or the {prefix.Replace(":", "__")}__ApiKey env var (or set RequiresApiKey to false for a keyless provider like Ollama). Never commit it.");
            }

            if (provider.TimeoutSeconds is < 1 or > 600)
            {
                errors.Add($"{prefix}:TimeoutSeconds is {provider.TimeoutSeconds}; it must be between 1 and 600.");
            }
        }

        return errors;
    }
}
