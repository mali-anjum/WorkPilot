using WorkPilot.Contracts.Notifications;
using WorkPilot.Domain.Modules.Notifications;

namespace WorkPilot.Application.Modules.Notifications;

/// <summary>What an approval notification needs: whose run it gates, the tool, and the run's goal.</summary>
public sealed record ApprovalNotificationSource(Guid ProfileId, string ToolName, string Goal);

/// <summary>What a run failed notification needs: whose run it is and its goal.</summary>
public sealed record RunNotificationSource(Guid ProfileId, string Goal);

/// <summary>A job as a digest names it.</summary>
public sealed record JobNotificationSource(string Title, string Company);

/// <summary>
/// The Notifications module's storage (spec 0020). It is the only writer of <c>notifications</c>;
/// the <c>Find*</c> reads look at other modules' tables, read only. Writes join the caller's unit of
/// work: an event handler's changes are saved by <c>HandleEventJob</c>, an endpoint's by
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface INotificationRepository
{
    /// <summary>The run behind an approval that gates an agent step, or null when any of them is gone.</summary>
    Task<ApprovalNotificationSource?> FindApprovalSourceAsync(Guid approvalId, CancellationToken cancellationToken);

    /// <summary>An agent run's profile and goal, or null when it is gone.</summary>
    Task<RunNotificationSource?> FindRunAsync(Guid agentRunId, CancellationToken cancellationToken);

    /// <summary>A job's title and company, or null when it is gone.</summary>
    Task<JobNotificationSource?> FindJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>A profile's strong match threshold, or null when the profile is gone.</summary>
    Task<int?> FindStrongMatchThresholdAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>
    /// The unread, non dismissed digest with <paramref name="groupKey"/>, row locked for the rest of
    /// the caller's transaction (<c>FOR UPDATE</c>), or null when there is none.
    /// </summary>
    Task<Notification?> LockOpenDigestAsync(Guid profileId, string groupKey, CancellationToken cancellationToken);

    /// <summary>True when the profile has an unread, non dismissed digest with <paramref name="groupKey"/> other than <paramref name="exceptId"/>.</summary>
    Task<bool> HasOtherOpenDigestAsync(Guid profileId, string groupKey, Guid exceptId, CancellationToken cancellationToken);

    /// <summary>A notification of <paramref name="profileId"/>, tracked, or null when missing, dismissed or someone else's.</summary>
    Task<Notification?> FindAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken);

    /// <summary>Adds a new notification to the unit of work.</summary>
    void Add(Notification notification);

    /// <summary>Saves the tracked changes.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>One page of a profile's notifications, newest first, and the total that match.</summary>
    Task<NotificationPageDto> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>How many unread, non dismissed notifications a profile has.</summary>
    Task<int> CountUnreadAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Marks every unread notification of a profile read at <paramref name="now"/>, in one statement; returns how many.</summary>
    Task<int> MarkAllReadAsync(Guid profileId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Soft deletes every read notification read before <paramref name="cutoff"/>, in one statement; returns how many.</summary>
    Task<int> DismissReadBeforeAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken);
}
