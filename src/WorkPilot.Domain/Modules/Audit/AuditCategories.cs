namespace WorkPilot.Domain.Modules.Audit;

/// <summary>The domain an audit entry belongs to, the activity feed's filter (spec 0011). Stored as its name.</summary>
public enum ActivityCategory
{
    Agent,
    Jobs,
    Email,
    Calendar,
    System,
    Errors,
}

/// <summary>
/// Picks an audit entry's <see cref="ActivityCategory"/> when it is written (spec 0011, AC-3).
/// First match wins, and every entry gets one, so a new action is never hidden from the feed.
/// The <c>AddActivityFeed</c> migration's backfill mirrors these rules in SQL: change both together.
/// </summary>
public static class AuditCategories
{
    /// <summary>Actions that are failures even though their names do not end with <c>Failed</c>.</summary>
    private static readonly HashSet<string> ErrorActions = new(StringComparer.Ordinal)
    {
        "PlanningFailed",
        "ApprovalGateRefused",
    };

    // Later modules add their target types here; old rows keep the category they were stored with.
    private static readonly Dictionary<string, ActivityCategory> ByTargetType = new(StringComparer.Ordinal)
    {
        ["Job"] = ActivityCategory.Jobs,
        ["JobSource"] = ActivityCategory.Jobs,
        ["JobMatch"] = ActivityCategory.Jobs,
        ["AgentRun"] = ActivityCategory.Agent,
        ["AgentStep"] = ActivityCategory.Agent,
        ["Approval"] = ActivityCategory.Agent,
        ["OutreachMessage"] = ActivityCategory.Email,
        ["EmailThread"] = ActivityCategory.Email,
        ["OutreachContact"] = ActivityCategory.Email,
        ["CalendarEvent"] = ActivityCategory.Calendar,
    };

    /// <summary>
    /// 1. <see cref="ActivityCategory.Errors"/> for a failure action; 2. by target type; 3.
    /// <see cref="ActivityCategory.Agent"/> when the Agent acted; 4. <see cref="ActivityCategory.System"/>.
    /// </summary>
    public static ActivityCategory For(string actor, string action, string targetType)
    {
        if (ErrorActions.Contains(action) || action.EndsWith("Failed", StringComparison.Ordinal))
        {
            return ActivityCategory.Errors;
        }

        if (ByTargetType.TryGetValue(targetType, out var category))
        {
            return category;
        }

        return actor == "Agent" ? ActivityCategory.Agent : ActivityCategory.System;
    }

    /// <summary>Reads a category by its name, ignoring case; numbers and unknown names are rejected.</summary>
    public static bool TryParse(string value, out ActivityCategory category)
    {
        foreach (var candidate in Enum.GetValues<ActivityCategory>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                category = candidate;
                return true;
            }
        }

        category = default;
        return false;
    }
}
