namespace WorkPilot.AI.Providers;

/// <summary>
/// The known AI purposes (spec 0006). Each one gets its own keyed
/// <c>IChatClient</c>; a purpose with no <c>Ai:Purposes</c> entry falls back
/// to <see cref="Default"/>. Adding a purpose is one constant here plus,
/// optionally, one config entry.
/// </summary>
public static class AiPurposes
{
    /// <summary>The fallback every unmapped purpose uses. Must be configured.</summary>
    public const string Default = "Default";

    /// <summary>The agent orchestrator's Planner (spec 0005).</summary>
    public const string Planner = "Planner";

    /// <summary>Every known purpose; config naming any other purpose fails startup (AC-4).</summary>
    public static IReadOnlyList<string> All { get; } = [Default, Planner];
}
