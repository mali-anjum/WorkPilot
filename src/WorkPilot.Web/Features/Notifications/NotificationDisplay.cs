using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Features.Notifications;

/// <summary>How the bell and <c>/notifications</c> show a notification (spec 0020).</summary>
public static class NotificationDisplay
{
    /// <summary>How many notifications the drawer shows (AC-4).</summary>
    public const int DrawerSize = 10;

    /// <summary>How many notifications one page of <c>/notifications</c> shows (AC-5).</summary>
    public const int PageSize = 25;

    /// <summary>How often the bell asks for the unread count while the page is open (AC-4).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>The bell's count: empty at 0 (hidden), <c>99+</c> above 99.</summary>
    public static string? CountLabel(int count) => count switch
    {
        <= 0 => null,
        > 99 => "99+",
        _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The bell's accessible name.</summary>
    public static string BellLabel(int count) => count switch
    {
        <= 0 => "Notifications, none unread",
        1 => "Notifications, 1 unread",
        _ => $"Notifications, {CountLabel(count)} unread",
    };

    /// <summary>The badge color of a priority.</summary>
    public static StatusKind PriorityStatus(string priority) => priority switch
    {
        "Error" => StatusKind.Danger,
        "ActionRequired" or "Warning" => StatusKind.Warning,
        "Success" => StatusKind.Success,
        "Info" => StatusKind.Info,
        _ => StatusKind.Neutral,
    };

    /// <summary>A priority in words.</summary>
    public static string PriorityLabel(string priority) => priority switch
    {
        "ActionRequired" => "Action required",
        _ => priority,
    };

    /// <summary>
    /// The link to open, or null when it is not an app relative path (the Api never stores one,
    /// this only guards the page against a bad row).
    /// </summary>
    public static string? SafeLink(string? link) =>
        link is { Length: > 0 } && link[0] == '/' && !link.StartsWith("//", StringComparison.Ordinal) && !link.StartsWith("/\\", StringComparison.Ordinal)
            ? link
            : null;

    /// <summary>The <c>/notifications</c> URL for a page and the unread toggle (AC-5).</summary>
    public static string PageHref(int page, bool unreadOnly)
    {
        var query = new List<string>();
        if (unreadOnly)
        {
            query.Add("unread=true");
        }

        if (page > 1)
        {
            query.Add($"page={page}");
        }

        return query.Count == 0 ? "/notifications" : $"/notifications?{string.Join('&', query)}";
    }
}
