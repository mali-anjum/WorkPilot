using System.Text.Json;
using WorkPilot.Application.Common;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Audit.Outbox;

/// <inheritdoc cref="IEventPublisher" />
public sealed class EventPublisher(WorkPilotDbContext db, TimeProvider time) : IEventPublisher
{
    /// <summary>
    /// The outbox payload's own serializer settings (System.Text.Json web defaults), separate from
    /// Hangfire's argument serializer (spec 0018, section 2).
    /// </summary>
    public static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void Publish<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventName = TEvent.EventName,
            Payload = JsonSerializer.Serialize(domainEvent, PayloadJson),
            OccurredAt = time.GetUtcNow(),
        });
    }
}
