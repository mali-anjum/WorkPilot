using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Workers.Audit;

/// <summary>Outbox settings (<c>Outbox</c> configuration section, spec 0018, section 3).</summary>
public sealed class OutboxOptions
{
    /// <summary>How long a dispatched message is kept before the sweep deletes it. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Hands undispatched outbox messages to their handlers (spec 0018, section 3). In one
/// transaction per batch it claims the oldest rows with <c>FOR UPDATE SKIP LOCKED</c>, enqueues one
/// <see cref="HandleEventJob"/> per registered handler, and marks the rows dispatched. A crash
/// after enqueuing but before the commit enqueues those handlers again later; the delivery
/// records make the repeat a no op.
/// </summary>
public sealed class DispatchOutboxJob(
    WorkPilotDbContext db,
    EventRegistry registry,
    IBackgroundJobClient jobs,
    TimeProvider time,
    ILogger<DispatchOutboxJob> logger)
{
    /// <summary>How many messages one transaction claims.</summary>
    public const int BatchSize = 100;

    /// <summary>Dispatches every undispatched message, one batch at a time.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        while (await DispatchBatchAsync(CancellationToken.None) == BatchSize)
        {
        }
    }

    /// <summary>Claims and dispatches one batch; returns how many messages it claimed.</summary>
    public Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            (object?)null,
            async (_, _, ct) =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var batch = await db.OutboxMessages
                    .FromSql($"""
                        SELECT * FROM app.outbox_messages
                        WHERE "DispatchedAt" IS NULL
                        ORDER BY "OccurredAt", "Id"
                        LIMIT {BatchSize}
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(ct);

                var now = time.GetUtcNow();
                foreach (var message in batch)
                {
                    if (registry.FindType(message.EventName) is null)
                    {
                        logger.LogWarning("Outbox message {MessageId} has unknown event {EventName}; no handler can run it.", message.Id, message.EventName);
                    }

                    foreach (var handler in registry.HandlersFor(message.EventName))
                    {
                        var messageId = message.Id;
                        var handlerKey = handler.HandlerKey;
                        jobs.Enqueue<HandleEventJob>(j => j.RunAsync(messageId, handlerKey));
                    }

                    message.MarkDispatched(now);
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return batch.Count;
            },
            verifySucceeded: null,
            cancellationToken);
    }
}

/// <summary>
/// Runs one handler for one outbox message (spec 0018, section 3). In one transaction it records
/// the delivery, runs the handler and commits both, so a handler's writes and its delivery record
/// land together or not at all. When the delivery is already recorded the handler does not run
/// again. Each handler retries on its own through Hangfire, without rerunning the others.
/// Handlers run inside this transaction, so they must not open one of their own.
/// </summary>
public sealed class HandleEventJob(
    WorkPilotDbContext db,
    EventRegistry registry,
    IServiceProvider services,
    TimeProvider time,
    ILogger<HandleEventJob> logger)
{
    /// <summary>Delivers message <paramref name="messageId"/> to the handler registered as <paramref name="handlerKey"/>.</summary>
    [AutomaticRetry(Attempts = 10)]
    public async Task RunAsync(Guid messageId, string handlerKey)
    {
        var handler = registry.FindHandler(handlerKey);
        if (handler is null)
        {
            // The handler was removed by a deploy after this job was queued.
            logger.LogWarning("Event handler {HandlerKey} is no longer registered; outbox message {MessageId} skipped for it.", handlerKey, messageId);
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            (object?)null,
            async (_, _, ct) =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var message = await db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
                if (message is null)
                {
                    logger.LogWarning("Outbox message {MessageId} no longer exists; handler {HandlerKey} skipped.", messageId, handlerKey);
                    return false;
                }

                var eventType = registry.FindType(message.EventName);
                if (eventType is null)
                {
                    logger.LogWarning("Outbox message {MessageId} has unknown event {EventName}; handler {HandlerKey} skipped.", messageId, message.EventName, handlerKey);
                    return false;
                }

                // The unique key waits for a concurrent delivery of the same pair to finish, then
                // reports it: 0 rows means this handler already handled this message.
                var inserted = await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO app.outbox_deliveries ("MessageId", "HandlerKey", "HandledAt")
                    VALUES ({messageId}, {handlerKey}, {time.GetUtcNow()})
                    ON CONFLICT DO NOTHING
                    """,
                    ct);
                if (inserted == 0)
                {
                    return false;
                }

                var domainEvent = JsonSerializer.Deserialize(message.Payload, eventType, EventPublisher.PayloadJson)
                    ?? throw new InvalidOperationException($"Outbox message {messageId} has an empty payload.");
                await handler.Invoker.HandleAsync(services, domainEvent, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return true;
            },
            verifySucceeded: null,
            CancellationToken.None);
    }
}

/// <summary>
/// The safety sweep (spec 0018, section 3), every minute: dispatches anything a crash between
/// commit and enqueue left behind, then deletes dispatched messages past the retention window
/// (their delivery records go with them).
/// </summary>
public sealed class OutboxSweepJob(DispatchOutboxJob dispatch, WorkPilotDbContext db, IOptions<OutboxOptions> options, TimeProvider time)
{
    /// <summary>Recurring job id.</summary>
    public const string RecurringJobId = "audit.outbox-sweep";

    /// <summary>Runs one sweep.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync()
    {
        await dispatch.RunAsync();
        var cutoff = time.GetUtcNow().AddDays(-options.Value.RetentionDays);
        await db.OutboxMessages
            .Where(m => m.DispatchedAt != null && m.DispatchedAt < cutoff)
            .ExecuteDeleteAsync();
    }
}

/// <summary>Enqueues a <see cref="DispatchOutboxJob"/> after a save that published an event.</summary>
public sealed class HangfireOutboxDispatchTrigger(IBackgroundJobClient jobs, ILogger<HangfireOutboxDispatchTrigger> logger) : IOutboxDispatchTrigger
{
    public void RequestDispatch()
    {
        try
        {
            jobs.Enqueue<DispatchOutboxJob>(j => j.RunAsync());
        }
        catch (Exception ex)
        {
            // The caller's save already committed; failing it now would misreport it. The every
            // minute sweep dispatches the message instead.
            logger.LogWarning(ex, "Could not enqueue an outbox dispatch; the sweep will deliver the event.");
        }
    }
}
