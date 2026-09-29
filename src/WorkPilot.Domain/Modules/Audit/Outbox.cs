using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Audit;

/// <summary>
/// One published domain event, waiting for (or past) delivery to its handlers (spec 0018,
/// section 3). Written in the same unit of work as the change it describes, so both commit or
/// neither does. Hard deleted after the retention window, never soft deleted.
/// </summary>
public class OutboxMessage : Entity
{
    /// <summary>The event's stable name (<see cref="IDomainEvent.EventName"/>).</summary>
    public required string EventName { get; init; }

    /// <summary>The event serialized as JSON.</summary>
    public required string Payload { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>When the dispatcher enqueued this message's handlers; null until then.</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>Records that every handler of this message has been enqueued.</summary>
    public void MarkDispatched(DateTimeOffset dispatchedAt)
    {
        if (DispatchedAt is not null)
        {
            throw new InvalidOperationException($"Outbox message {Id} was already dispatched.");
        }

        DispatchedAt = dispatchedAt;
    }
}

/// <summary>
/// Proof that one handler already handled one outbox message (spec 0018, section 3). Its key,
/// (<see cref="MessageId"/>, <see cref="HandlerKey"/>), is what makes a repeated delivery a no op.
/// </summary>
public class OutboxDelivery
{
    public required Guid MessageId { get; init; }

    /// <summary>The handler's stable key, <c>&lt;module&gt;.&lt;handler-kebab&gt;</c>.</summary>
    public required string HandlerKey { get; init; }

    public required DateTimeOffset HandledAt { get; init; }
}
