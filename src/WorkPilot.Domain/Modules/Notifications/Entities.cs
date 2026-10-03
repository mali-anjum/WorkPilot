using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Notifications;

/// <summary>How urgent a notification is (spec 0020); stored as its name.</summary>
public enum NotificationPriority
{
    Info,
    Success,
    Warning,
    ActionRequired,
    Error,
}

/// <summary>
/// Something the founder should know about, written only by the Notifications module's event
/// handlers (spec 0020). Unread (<see cref="ReadAt"/> null) and read switch freely; dismissing soft
/// deletes it for good. A digest (<see cref="GroupKey"/> set) gathers several events into one row
/// while it is unread.
/// </summary>
public class Notification : SoftDeletableEntity
{
    /// <summary>The longest title kept; longer ones are cut.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>The longest body kept; longer ones are cut.</summary>
    public const int BodyMaxLength = 1000;

    /// <summary>The longest link allowed.</summary>
    public const int LinkMaxLength = 500;

    private Notification()
    {
    }

    public Guid ProfileId { get; private init; }
    public string Type { get; private init; } = string.Empty;
    public NotificationPriority Priority { get; private init; }
    public string Title { get; private set; } = string.Empty;
    public string? Body { get; private set; }

    /// <summary>An app relative path (starts with <c>/</c>), so a notification never navigates off site.</summary>
    public string? Link { get; private set; }

    /// <summary>The digest key; at most one unread digest per (profile, key) exists.</summary>
    public string? GroupKey { get; private init; }

    /// <summary>Extra data as JSON (ids, titles, scores; never secrets), or null.</summary>
    public string? Payload { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }

    public bool IsRead => ReadAt is not null;

    /// <summary>
    /// A new unread notification. Title and body are trimmed and cut to their limits. Throws when
    /// the title is empty or the link is not an app relative path.
    /// </summary>
    public static Notification Create(
        Guid profileId,
        string type,
        NotificationPriority priority,
        string title,
        string? body,
        string? link,
        DateTimeOffset now,
        string? groupKey = null,
        string? payload = null)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A notification needs a profile.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("A notification needs a type.", nameof(type));
        }

        return new Notification
        {
            ProfileId = profileId,
            Type = type,
            Priority = priority,
            Title = CleanTitle(title),
            Body = Cut(body, BodyMaxLength),
            Link = CheckLink(link),
            GroupKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey,
            Payload = payload,
            CreatedAt = now,
        };
    }

    /// <summary>Marks it read; a notification already read keeps its first read time.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    /// <summary>Marks it unread again.</summary>
    public void MarkUnread() => ReadAt = null;

    /// <summary>Dismisses it: soft deleted, final.</summary>
    public void Dismiss(DateTimeOffset now) => SoftDelete(now);

    /// <summary>
    /// Rewrites an open digest's title and payload. Throws for a notification that is not a
    /// digest, or one that is already read or dismissed (a digest stops gathering then).
    /// </summary>
    public void ReviseDigest(string title, string payload)
    {
        if (GroupKey is null)
        {
            throw new InvalidOperationException("Only a digest notification can be revised.");
        }

        if (IsRead || IsDeleted)
        {
            throw new InvalidOperationException("A read or dismissed digest no longer changes.");
        }

        Title = CleanTitle(title);
        Payload = payload;
    }

    /// <summary>True when <paramref name="link"/> is a path inside the app: starts with one <c>/</c>, no scheme, no host.</summary>
    public static bool IsAppRelative(string link) =>
        link.Length is > 0 and <= LinkMaxLength
        && link[0] == '/'
        && !link.StartsWith("//", StringComparison.Ordinal)
        && !link.StartsWith("/\\", StringComparison.Ordinal)
        && !link.Any(char.IsControl);

    /// <summary>Trims <paramref name="text"/> and cuts it to <paramref name="max"/> characters, ending in an ellipsis; null for blank.</summary>
    public static string? Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : string.Concat(trimmed.AsSpan(0, max - 1).TrimEnd(), "…");
    }

    private static string CleanTitle(string title) =>
        Cut(title, TitleMaxLength) ?? throw new ArgumentException("A notification needs a title.", nameof(title));

    private static string? CheckLink(string? link)
    {
        if (link is null)
        {
            return null;
        }

        return IsAppRelative(link) ? link : throw new ArgumentException($"A notification link must be an app relative path, not \"{link}\".", nameof(link));
    }
}
