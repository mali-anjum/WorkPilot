using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent.Tools;

/// <summary>
/// Auto-allowed, read-only: returns the calling profile's own summary. The
/// trivial end to end tool call spec 0005's milestone proves the pipeline
/// with.
/// </summary>
public sealed class ListMyProfileTool(WorkPilotDbContext db) : ITool
{
    public string Name => "list_my_profile";
    public string Description => "Lists the founder's own profile summary: name, headline, and location. Takes no arguments.";
    public IReadOnlyList<string> RequiredArguments => [];
    public IReadOnlyList<string> ExpectedOutputFields => ["name", "headline", "location"];
    public ToolRiskTier RiskTier => ToolRiskTier.AutoAllowed;
    public bool IsIdempotent => true;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public int MaxRetries => 2;
    public string? TargetType => "Profile";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var profile = await db.Profiles
            .Where(p => p.Id == context.ProfileId)
            .Select(p => new { p.Name, p.Headline, p.Location })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            return ToolExecutionResult.Fail($"No profile found for {context.ProfileId}.");
        }

        // camelCase so the field names match ExpectedOutputFields exactly (JsonElement.TryGetProperty is case sensitive).
        var json = JsonSerializer.Serialize(profile, JsonSerializerOptions.Web);
        return ToolExecutionResult.Ok(json, context.ProfileId);
    }
}
