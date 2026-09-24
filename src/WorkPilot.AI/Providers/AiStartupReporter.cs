using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkPilot.AI.Providers;

/// <summary>
/// Logs, once at startup, which provider and model answers each purpose,
/// with a warning for every purpose left on the Fake provider so a
/// deployment still running on the fake is obvious (spec 0006, AC-3).
/// Never logs an API key.
/// </summary>
internal sealed class AiStartupReporter(IServiceProvider services, ILogger<AiStartupReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var purpose in AiPurposes.All)
        {
            var target = services.GetRequiredKeyedService<ResolvedAiPurpose>(purpose);
            if (target.IsFake)
            {
                logger.LogWarning("AI purpose {Purpose} uses the Fake provider: its calls are answered by a deterministic fake, not a real model.", purpose);
            }
            else
            {
                logger.LogInformation("AI purpose {Purpose}: provider {Provider}, model {Model}", purpose, target.Provider, target.Model);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
