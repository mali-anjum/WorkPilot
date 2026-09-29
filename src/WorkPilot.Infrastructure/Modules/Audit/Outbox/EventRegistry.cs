using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Common;
using WorkPilot.Domain.Common;

namespace WorkPilot.Infrastructure.Modules.Audit.Outbox;

/// <summary>Runs one typed handler for an event the outbox stored as JSON (spec 0018, section 2).</summary>
public interface IEventHandlerInvoker
{
    /// <summary>Resolves the handler from <paramref name="services"/> (the job's scope) and runs it.</summary>
    Task HandleAsync(IServiceProvider services, object domainEvent, CancellationToken cancellationToken);
}

/// <summary>One event handler as registered in DI; the registry is built from these at startup.</summary>
/// <param name="EventType">The domain event the handler reacts to.</param>
/// <param name="HandlerKey">The handler's stable key.</param>
/// <param name="Invoker">Runs the handler without knowing its type.</param>
public sealed record EventHandlerRegistration(Type EventType, string HandlerKey, IEventHandlerInvoker Invoker);

public sealed class EventHandlerInvoker<TEvent, THandler> : IEventHandlerInvoker
    where TEvent : IDomainEvent
    where THandler : class, IEventHandler<TEvent>
{
    public Task HandleAsync(IServiceProvider services, object domainEvent, CancellationToken cancellationToken) =>
        services.GetRequiredService<THandler>().HandleAsync((TEvent)domainEvent, cancellationToken);
}

/// <summary>
/// Maps stored event names to CLR types and to their handlers (spec 0018, section 2). Built once
/// at startup from every <see cref="IDomainEvent"/> in the Domain assembly plus every
/// <see cref="EventHandlerRegistration"/>; throws on a duplicate event name or handler key, so a
/// clash fails startup instead of misrouting an event.
/// </summary>
public sealed class EventRegistry
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventHandlerRegistration>> _handlersByEvent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventHandlerRegistration> _handlersByKey = new(StringComparer.Ordinal);

    public EventRegistry(IEnumerable<EventHandlerRegistration> handlers)
    {
        var handlerList = handlers.ToList();
        var eventTypes = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .Concat(handlerList.Select(h => h.EventType))
            .Distinct();

        foreach (var type in eventTypes)
        {
            var name = NameOf(type);
            if (_types.TryGetValue(name, out var existing) && existing != type)
            {
                throw new InvalidOperationException($"Event name \"{name}\" is declared by both {existing.FullName} and {type.FullName}.");
            }

            _types[name] = type;
        }

        foreach (var handler in handlerList)
        {
            if (!_handlersByKey.TryAdd(handler.HandlerKey, handler))
            {
                throw new InvalidOperationException($"Event handler key \"{handler.HandlerKey}\" is registered more than once.");
            }

            var name = NameOf(handler.EventType);
            if (!_handlersByEvent.TryGetValue(name, out var list))
            {
                _handlersByEvent[name] = list = [];
            }

            list.Add(handler);
        }
    }

    /// <summary>Every known event name.</summary>
    public IReadOnlyCollection<string> EventNames => _types.Keys;

    /// <summary>The CLR type stored under <paramref name="eventName"/>, or null when none is known.</summary>
    public Type? FindType(string eventName) => _types.GetValueOrDefault(eventName);

    /// <summary>Every handler of <paramref name="eventName"/> (none is fine: the event is then just recorded).</summary>
    public IReadOnlyList<EventHandlerRegistration> HandlersFor(string eventName) =>
        _handlersByEvent.TryGetValue(eventName, out var list) ? list : [];

    /// <summary>The handler registered under <paramref name="handlerKey"/>, or null.</summary>
    public EventHandlerRegistration? FindHandler(string handlerKey) => _handlersByKey.GetValueOrDefault(handlerKey);

    /// <summary>The stable <see cref="IDomainEvent.EventName"/> of an event type.</summary>
    public static string NameOf(Type eventType)
    {
        if (!typeof(IDomainEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException($"{eventType.FullName} is not an {nameof(IDomainEvent)}.", nameof(eventType));
        }

        return (string)typeof(EventRegistry)
            .GetMethod(nameof(NameOfGeneric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(eventType)
            .Invoke(null, null)!;
    }

    private static string NameOfGeneric<TEvent>() where TEvent : IDomainEvent => TEvent.EventName;
}
