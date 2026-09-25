using System.Diagnostics;
using Microsoft.Extensions.AI;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.AI.Providers;

/// <summary>The outcome of one <see cref="AiHealthProbe"/> call.</summary>
/// <param name="Healthy">True when the provider answered.</param>
/// <param name="Target">The purpose, provider, and model that were probed.</param>
/// <param name="LatencyMs">Wall clock time of the one call, retries included.</param>
/// <param name="Error">The provider failure message (no key), when <paramref name="Healthy"/> is false.</param>
public sealed record AiHealthResult(bool Healthy, ResolvedAiPurpose Target, long LatencyMs, string? Error);

/// <summary>
/// Sends one tiny prompt through a purpose's client to prove its provider and
/// key work, on demand only (spec 0006, AC-8). Never part of <c>/health</c>,
/// so container probes never spend tokens.
/// </summary>
public static class AiHealthProbe
{
    private const string Prompt = "Reply with the single word OK.";

    private static readonly ChatOptions ProbeOptions = new() { MaxOutputTokens = 5 };

    /// <summary>Calls <paramref name="client"/> once; a provider failure becomes an unhealthy result, not an exception.</summary>
    public static async Task<AiHealthResult> RunAsync(IChatClient client, ResolvedAiPurpose target, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.GetResponseAsync(Prompt, ProbeOptions, cancellationToken);
            return new AiHealthResult(true, target, stopwatch.ElapsedMilliseconds, null);
        }
        catch (AiProviderException ex)
        {
            return new AiHealthResult(false, target, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
}
