using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WorkPilot.AI.Providers;

/// <summary>DI registration for the configured AI providers (docs/specs/0006-ai-provider-abstraction).</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates the <c>Ai</c> section at startup (the host refuses
    /// to start on a bad setting, AC-4), then registers, for every purpose in
    /// <see cref="AiPurposes.All"/>, a keyed <see cref="ResolvedAiPurpose"/>
    /// and a keyed <see cref="IChatClient"/> built for it. Consumers inject
    /// <c>[FromKeyedServices(AiPurposes.Planner)] IChatClient</c>.
    /// </summary>
    public static IServiceCollection AddWorkPilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();

        // Resolved lazily from options (never read here), so configuration
        // layered on after registration (tests, env vars) still applies.
        foreach (var purpose in AiPurposes.All)
        {
            services.AddKeyedSingleton(purpose, (sp, _) =>
                ResolvedAiPurpose.Resolve(sp.GetRequiredService<IOptions<AiOptions>>().Value, purpose));

            services.AddKeyedSingleton(purpose, (sp, _) => AiChatClientFactory.Create(
                sp.GetRequiredKeyedService<ResolvedAiPurpose>(purpose),
                sp.GetRequiredService<IOptions<AiOptions>>().Value,
                sp));
        }

        services.AddHostedService<AiStartupReporter>();
        return services;
    }
}
