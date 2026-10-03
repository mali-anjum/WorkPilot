using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Notifications;
using WorkPilot.Contracts.Notifications;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Notifications;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Notifications;

/// <inheritdoc cref="INotificationRepository" />
public sealed class NotificationRepository(WorkPilotDbContext db) : INotificationRepository
{
    /// <inheritdoc />
    public Task<ApprovalNotificationSource?> FindApprovalSourceAsync(Guid approvalId, CancellationToken cancellationToken) =>
        (from approval in db.Approvals.AsNoTracking()
         join step in db.AgentSteps.AsNoTracking() on approval.TargetId equals step.Id
         join run in db.AgentRuns.AsNoTracking() on step.AgentRunId equals run.Id
         where approval.Id == approvalId && approval.TargetType == ApprovalTargets.AgentStep
         select new ApprovalNotificationSource(run.ProfileId, step.ToolName, run.Goal))
        .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<RunNotificationSource?> FindRunAsync(Guid agentRunId, CancellationToken cancellationToken) =>
        db.AgentRuns.AsNoTracking()
            .Where(r => r.Id == agentRunId)
            .Select(r => new RunNotificationSource(r.ProfileId, r.Goal))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<JobNotificationSource?> FindJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new JobNotificationSource(j.Title, j.Company))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int?> FindStrongMatchThresholdAsync(Guid profileId, CancellationToken cancellationToken) =>
        db.Profiles.AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => (int?)p.StrongMatchThreshold)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Notification?> LockOpenDigestAsync(Guid profileId, string groupKey, CancellationToken cancellationToken) =>
        db.Notifications
            .FromSql($"""
                SELECT * FROM app.notifications
                WHERE "ProfileId" = {profileId} AND "GroupKey" = {groupKey}
                  AND "ReadAt" IS NULL AND "IsDeleted" = false
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> HasOtherOpenDigestAsync(Guid profileId, string groupKey, Guid exceptId, CancellationToken cancellationToken) =>
        db.Notifications.AnyAsync(n => n.ProfileId == profileId && n.GroupKey == groupKey && n.ReadAt == null && n.Id != exceptId, cancellationToken);

    /// <inheritdoc />
    public Task<Notification?> FindAsync(Guid profileId, Guid notificationId, CancellationToken cancellationToken) =>
        db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.ProfileId == profileId, cancellationToken);

    /// <inheritdoc />
    public void Add(Notification notification) => db.Notifications.Add(notification);

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<NotificationPageDto> GetPageAsync(Guid profileId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = db.Notifications.AsNoTracking().Where(n => n.ProfileId == profileId);
        if (unreadOnly)
        {
            rows = rows.Where(n => n.ReadAt == null);
        }

        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new NotificationPageDto(items.Select(NotificationsService.ToDto).ToList(), total, page, pageSize);
    }

    /// <inheritdoc />
    public Task<int> CountUnreadAsync(Guid profileId, CancellationToken cancellationToken) =>
        db.Notifications.CountAsync(n => n.ProfileId == profileId && n.ReadAt == null, cancellationToken);

    /// <inheritdoc />
    public Task<int> MarkAllReadAsync(Guid profileId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Notifications
            .Where(n => n.ProfileId == profileId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);

    /// <inheritdoc />
    public Task<int> DismissReadBeforeAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Notifications
            .Where(n => n.ReadAt != null && n.ReadAt < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.IsDeleted, true).SetProperty(n => n.DeletedAt, now),
                cancellationToken);
}
