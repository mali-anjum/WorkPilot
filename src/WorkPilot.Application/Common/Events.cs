using WorkPilot.Domain.Common;

namespace WorkPilot.Application.Common;

/// <summary>
/// Publishes a domain event through the transactional outbox (spec 0018, sections 2 and 3). Like
/// <c>IAuditService.Record</c>, it only adds an outbox row to the current unit of work: the
/// caller's own <c>SaveChangesAsync</c> commits the change and the event together, or neither.
/// </summary>
public interface IEventPublisher
{
    void Publish<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent;
}

/// <summary>
/// Reacts to one domain event published by another module (spec 0018, section 2). A handler writes
/// only its own module's tables and never has a real world side effect (sending, submitting,
/// calling a provider): those go through an agent tool and the approval engine. Delivery is at
/// least once; the outbox makes a repeat a no op, so a handler does not need its own dedup.
/// Register it with <c>AddEventHandler&lt;TEvent, THandler&gt;()</c>.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    /// <summary>The stable key the outbox records deliveries under, <c>&lt;module&gt;.&lt;handler-kebab&gt;</c>. Never change it once shipped.</summary>
    static abstract string HandlerKey { get; }

    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
