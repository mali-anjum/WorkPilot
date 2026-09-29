using WorkPilot.Application.Common;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;

namespace WorkPilot.Api.Tests;

// The event registry (spec 0018, section 2): stored names to types and
// handlers, with duplicates refused at startup. Pure, no database.
public class EventRegistryTests
{
    [Fact]
    public void KnowsEveryCatalogEventTheDomainDeclares_EvenWithNoHandlers()
    {
        var registry = new EventRegistry([]);

        Assert.Equal(typeof(ApprovalRequested), registry.FindType("approvals.approval-requested.v1"));
        Assert.Equal(typeof(ApprovalDecided), registry.FindType("approvals.approval-decided.v1"));
        Assert.Equal(typeof(AgentRunFailed), registry.FindType("agent.agent-run-failed.v1"));
        Assert.Empty(registry.HandlersFor("agent.agent-run-failed.v1"));
    }

    [Fact]
    public void AnUnknownNameOrKey_FindsNothing()
    {
        var registry = new EventRegistry([]);

        Assert.Null(registry.FindType("nobody.declared-this.v1"));
        Assert.Null(registry.FindHandler("nobody.handles-this"));
        Assert.Empty(registry.HandlersFor("nobody.declared-this.v1"));
    }

    [Fact]
    public void AHandlersEventType_IsKnownEvenOutsideTheDomainAssembly()
    {
        var registry = new EventRegistry([Registration<Probe, ProbeHandler>()]);

        Assert.Equal(typeof(Probe), registry.FindType(Probe.EventName));
        Assert.Equal(ProbeHandler.HandlerKey, Assert.Single(registry.HandlersFor(Probe.EventName)).HandlerKey);
        Assert.NotNull(registry.FindHandler(ProbeHandler.HandlerKey));
    }

    [Fact]
    public void TheSameHandlerKeyTwice_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EventRegistry([Registration<Probe, ProbeHandler>(), Registration<Probe, ProbeHandler>()]));

        Assert.Contains(ProbeHandler.HandlerKey, ex.Message);
    }

    [Fact]
    public void TwoTypesDeclaringOneEventName_AreRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EventRegistry([Registration<Probe, ProbeHandler>(), Registration<ProbeClash, ProbeClashHandler>()]));

        Assert.Contains(Probe.EventName, ex.Message);
    }

    [Fact]
    public void NameOf_ReadsTheStableNameFromTheType()
    {
        Assert.Equal("agent.agent-run-failed.v1", EventRegistry.NameOf(typeof(AgentRunFailed)));
        Assert.Throws<ArgumentException>(() => EventRegistry.NameOf(typeof(string)));
    }

    private static EventHandlerRegistration Registration<TEvent, THandler>()
        where TEvent : IDomainEvent
        where THandler : class, IEventHandler<TEvent> =>
        new(typeof(TEvent), THandler.HandlerKey, new EventHandlerInvoker<TEvent, THandler>());

    public sealed record Probe(Guid Id) : IDomainEvent
    {
        public static string EventName => "tests.probe.v1";
    }

    public sealed record ProbeClash(Guid Id) : IDomainEvent
    {
        public static string EventName => "tests.probe.v1";
    }

    public sealed class ProbeHandler : IEventHandler<Probe>
    {
        public static string HandlerKey => "tests.on-probe";

        public Task HandleAsync(Probe domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class ProbeClashHandler : IEventHandler<ProbeClash>
    {
        public static string HandlerKey => "tests.on-probe-clash";

        public Task HandleAsync(ProbeClash domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
