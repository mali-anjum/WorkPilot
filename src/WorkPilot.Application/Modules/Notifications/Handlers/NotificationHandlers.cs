using System.Text.Json;
using WorkPilot.Application.Common;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Notifications;

namespace WorkPilot.Application.Modules.Notifications.Handlers;

/// <summary>The notification types the handlers write (spec 0020).</summary>
public static class NotificationTypes
{
    public const string ApprovalRequested = "approvals.requested";
    public const string AgentRunFailed = "agent.run-failed";
    public const string StrongMatches = StrongMatchDigest.Type;
}

/// <summary>
/// Tells you an approval waits (spec 0020, AC-1): Action Required, the tool in the title, the
/// run's goal as the body, linking to <c>/approvals</c>. Skips an approval whose run is gone.
/// </summary>
public sealed class NotifyOnApprovalRequested(INotificationRepository notifications, TimeProvider time) : IEventHandler<ApprovalRequested>
{
    /// <summary>How much of the goal the body shows.</summary>
    public const int GoalMaxLength = 300;

    /// <inheritdoc />
    public static string HandlerKey => "notifications.on-approval-requested";

    /// <summary>Adds the notification to the delivery's unit of work.</summary>
    public async Task HandleAsync(ApprovalRequested domainEvent, CancellationToken cancellationToken)
    {
        if (await notifications.FindApprovalSourceAsync(domainEvent.ApprovalId, cancellationToken) is not { } source)
        {
            return;
        }

        notifications.Add(Notification.Create(
            source.ProfileId,
            NotificationTypes.ApprovalRequested,
            NotificationPriority.ActionRequired,
            $"Approval needed: {source.ToolName}",
            Notification.Cut(source.Goal, GoalMaxLength),
            "/approvals",
            time.GetUtcNow(),
            payload: NotificationPayloads.Serialize(new { approvalId = domainEvent.ApprovalId })));
    }
}

/// <summary>
/// Tells you an agent run failed (spec 0020, AC-2): Error, the readable reason and the goal,
/// linking to <c>/agent/runs</c>. Skips a run that is gone.
/// </summary>
public sealed class NotifyOnAgentRunFailed(INotificationRepository notifications, TimeProvider time) : IEventHandler<AgentRunFailed>
{
    /// <inheritdoc />
    public static string HandlerKey => "notifications.on-agent-run-failed";

    /// <summary>Adds the notification to the delivery's unit of work.</summary>
    public async Task HandleAsync(AgentRunFailed domainEvent, CancellationToken cancellationToken)
    {
        if (await notifications.FindRunAsync(domainEvent.AgentRunId, cancellationToken) is not { } run)
        {
            return;
        }

        notifications.Add(Notification.Create(
            run.ProfileId,
            NotificationTypes.AgentRunFailed,
            NotificationPriority.Error,
            "Agent run failed",
            $"{AgentRunFailureReasons.Describe(domainEvent.Reason)}. Goal: {run.Goal}",
            "/agent/runs",
            time.GetUtcNow(),
            payload: NotificationPayloads.Serialize(new { agentRunId = domainEvent.AgentRunId, reason = domainEvent.Reason })));
    }
}

/// <summary>
/// Gathers strong matches into one notification per profile and UTC day (spec 0020, AC-3): the
/// open digest is locked and updated, or a new one starts. A job already counted is not counted
/// again (AC-6). Two deliveries racing to start the same digest collide on the partial unique
/// index; the loser's retry finds the winner's row and updates it. Skips a job or profile that is gone.
/// </summary>
public sealed class NotifyOnJobMatched(INotificationRepository notifications, TimeProvider time) : IEventHandler<JobMatched>
{
    /// <inheritdoc />
    public static string HandlerKey => "notifications.on-job-matched";

    /// <summary>Adds or revises the digest in the delivery's unit of work.</summary>
    public async Task HandleAsync(JobMatched domainEvent, CancellationToken cancellationToken)
    {
        if (await notifications.FindJobAsync(domainEvent.JobId, cancellationToken) is not { } job
            || await notifications.FindStrongMatchThresholdAsync(domainEvent.ProfileId, cancellationToken) is not { } threshold)
        {
            return;
        }

        var now = time.GetUtcNow();
        var groupKey = StrongMatchDigest.GroupKeyFor(now);
        var entry = new DigestJob(domainEvent.JobId, job.Title, job.Company, domainEvent.Score);
        var open = await notifications.LockOpenDigestAsync(domainEvent.ProfileId, groupKey, cancellationToken);
        if (open is null)
        {
            var digest = StrongMatchDigest.Empty.Add(entry);
            notifications.Add(Notification.Create(
                domainEvent.ProfileId,
                NotificationTypes.StrongMatches,
                NotificationPriority.Info,
                digest.Title,
                null,
                StrongMatchDigest.LinkFor(threshold),
                now,
                groupKey,
                NotificationPayloads.Serialize(digest)));
            return;
        }

        var current = NotificationPayloads.ReadDigest(open.Payload);
        if (current.Contains(entry.JobId))
        {
            return;
        }

        var updated = current.Add(entry);
        open.ReviseDigest(updated.Title, NotificationPayloads.Serialize(updated));
    }
}

/// <summary>The JSON shape of notification payloads: camelCase web JSON.</summary>
public static class NotificationPayloads
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes a payload.</summary>
    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>Reads a digest payload; an empty or unreadable one is an empty digest.</summary>
    public static StrongMatchDigest ReadDigest(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return StrongMatchDigest.Empty;
        }

        try
        {
            var digest = JsonSerializer.Deserialize<StrongMatchDigest>(payload, Json);
            return digest is null ? StrongMatchDigest.Empty : digest with { Top = digest.Top ?? [], JobIds = digest.JobIds ?? [] };
        }
        catch (JsonException)
        {
            return StrongMatchDigest.Empty;
        }
    }
}
