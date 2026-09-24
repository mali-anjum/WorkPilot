using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WorkPilot.AI.Providers;

/// <summary>DI registration for the configured AI provider (docs/specs/0006-ai-provider-abstraction).</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IChatClient"/> as the provider named by
    /// <c>AI:ActiveProvider</c>, with the <c>AI</c> section validated at
    /// startup (the host refuses to start on a bad or missing setting).
    /// </summary>
    public static IServiceCollection AddWorkPilotChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();

        // Resolved lazily from options (not read here), so configuration
        // layered on after registration (tests, env vars) still applies.
        services.AddChatClient(sp => AiChatClientFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value))
            .UseLogging();

        services.AddHostedService<AiProviderStartupLogger>();
        return services;
    }
}
