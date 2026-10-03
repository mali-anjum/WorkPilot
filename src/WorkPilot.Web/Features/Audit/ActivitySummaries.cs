using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkPilot.Contracts.Audit;

namespace WorkPilot.Web.Features.Audit;

/// <summary>One filter chip on <c>/activity</c>: its label and its <c>category</c> query value (null for All).</summary>
public sealed record ActivityChip(string Label, string? Value);

/// <summary>One top level evidence property: its value as text, or as indented JSON for an object or array.</summary>
public sealed record EvidenceField(string Key, string Value, bool IsJson);

/// <summary>
/// An entry's evidence, ready to render (spec 0011, AC-4): key and value pairs for a JSON object,
/// otherwise <see cref="Text"/> (indented JSON for other JSON, or the raw text when it is not JSON).
/// </summary>
public sealed record EvidenceView(IReadOnlyList<EvidenceField> Fields, string? Text, bool TextIsJson);

/// <summary>How the activity feed presents an audit entry (spec 0011, AC-1, AC-4 to AC-6). Pure, so the page stays thin.</summary>
public static class ActivitySummaries
{
    /// <summary>Payloads larger than this many UTF-8 bytes show truncated until "Show all" (AC-4).</summary>
    public const int EvidenceLimitBytes = 10 * 1024;

    /// <summary>The chips, in order (AC-2).</summary>
    public static readonly IReadOnlyList<ActivityChip> Chips =
    [
        new("All", null),
        new("Agent", "agent"),
        new("Jobs", "jobs"),
        new("Email", "email"),
        new("Calendar", "calendar"),
        new("System", "system"),
        new("Errors", "errors"),
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A readable line for a known action; the raw action name otherwise, so a new action is never hidden (AC-6).</summary>
    public static string Summary(ActivityEntryDto entry) => entry.Action switch
    {
        "JobsIngested" => IngestedSummary(entry.Payload),
        "JobsMerged" => "Merged duplicate jobs",
        "JobLinkSplit" => "Split a job link",
        "ApprovalRequested" => "Asked for your approval",
        "ApprovalApproved" => "You approved a step",
        "ApprovalRejected" => "You rejected a step",
        "ApprovalGateRefused" => "Blocked a step at the approval gate",
        "PlanningFailed" => "Agent planning failed",
        _ => entry.Action,
    };

    /// <summary><c>You</c> when the actor is the viewer's profile id, <c>Agent</c> for the Agent, otherwise <c>System</c> (AC-1).</summary>
    public static string Who(string actor, Guid? viewerProfileId) =>
        actor == "Agent" ? "Agent"
        : viewerProfileId is { } me && Guid.TryParse(actor, out var id) && id == me ? "You"
        : "System";

    /// <summary>The page a target links to, or null when it has none (AC-5).</summary>
    public static string? TargetHref(string targetType, Guid targetId) => targetType switch
    {
        "Job" => $"/jobs/{targetId}",
        "JobSource" => $"/jobs?source={targetId}",
        "AgentStep" => "/approvals",
        "AgentRun" => "/agent/runs",
        _ => null,
    };

    /// <summary>The target as text: its type and the first eight characters of its id.</summary>
    public static string TargetLabel(string targetType, Guid targetId) => $"{targetType} {targetId.ToString()[..8]}";

    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago", or the date for anything older than a week.</summary>
    public static string Relative(DateTimeOffset occurredAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        var age = now - occurredAt;
        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
            { TotalDays: < 7 } => $"{(int)age.TotalDays} d ago",
            _ => TimeZoneInfo.ConvertTime(occurredAt, zone).ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The exact time in <paramref name="zone"/>, with the zone's id (AC-1).</summary>
    public static string Exact(DateTimeOffset occurredAt, TimeZoneInfo zone) =>
        $"{TimeZoneInfo.ConvertTime(occurredAt, zone).ToString("MMM d, yyyy, HH:mm:ss", CultureInfo.InvariantCulture)} ({zone.Id})";

    /// <summary>Whether <paramref name="payload"/> is over <see cref="EvidenceLimitBytes"/>.</summary>
    public static bool IsLarge(string? payload) => payload is not null && Encoding.UTF8.GetByteCount(payload) > EvidenceLimitBytes;

    /// <summary>The first <see cref="EvidenceLimitBytes"/> of <paramref name="payload"/>, never splitting a character.</summary>
    public static string Truncate(string payload)
    {
        if (!IsLarge(payload))
        {
            return payload;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        var end = EvidenceLimitBytes;

        // Step back off UTF-8 continuation bytes so the cut lands on a character boundary.
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    /// <summary>Parses <paramref name="payload"/> into what the evidence panel shows (AC-4); null when there is none.</summary>
    public static EvidenceView? Evidence(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new EvidenceView([], JsonSerializer.Serialize(root, Indented), true);
            }

            var fields = root.EnumerateObject()
                .Select(p => p.Value.ValueKind switch
                {
                    JsonValueKind.Object or JsonValueKind.Array => new EvidenceField(p.Name, JsonSerializer.Serialize(p.Value, Indented), true),
                    JsonValueKind.String => new EvidenceField(p.Name, p.Value.GetString() ?? "", false),
                    _ => new EvidenceField(p.Name, p.Value.GetRawText(), false),
                })
                .ToList();
            return new EvidenceView(fields, null, false);
        }
        catch (JsonException)
        {
            return new EvidenceView([], payload, false);
        }
    }

    // "Ingested jobs from a source: 12 new, 3 updated", from the ingestion summary's counts.
    private static string IngestedSummary(string? payload)
    {
        const string Base = "Ingested jobs from a source";
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Base;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number && created.TryGetInt32(out var newCount)
                && root.TryGetProperty("updated", out var updated) && updated.ValueKind == JsonValueKind.Number && updated.TryGetInt32(out var updatedCount)
                ? $"{Base}: {newCount} new, {updatedCount} updated"
                : Base;
        }
        catch (JsonException)
        {
            return Base;
        }
    }
}
