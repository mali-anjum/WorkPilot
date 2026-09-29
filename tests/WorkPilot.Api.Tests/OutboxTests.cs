using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Audit;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Audit;

namespace WorkPilot.Api.Tests;

// The transactional outbox (spec 0018, section 3) against the real Postgres:
// publishing, dispatching, delivering to a handler exactly once, and the
// retention sweep. Hangfire's worker is off in SharedApiFactory, so the jobs
// are driven directly and enqueues are recorded instead of stored.
[Collection("Api")]
public class OutboxTests(SharedApiFactory factory) : IAsyncLifetime
{
    private readonly List<Guid> _messageIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Test events are unknown to the app's registry, so a leftover
        // undispatched one would stop the next test host from starting.
        await using var db = CreateDbContext();
        await db.OutboxMessages.Where(m => _messageIds.Contains(m.Id) || m.EventName.StartsWith("tests.")).ExecuteDeleteAsync();
        await db.AuditLogs.IgnoreQueryFilters().Where(a => a.Actor == PingHandler.Actor).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Publish_AddsTheEventToTheUnitOfWork_AndItCommitsWithTheCallersSave()
    {
        await using var db = CreateDbContext();
        var publisher = new EventPublisher(db, TimeProvider.System);
        var ping = new Pinged(Guid.NewGuid(), "hello");

        publisher.Publish(ping);
        await using (var before = CreateDbContext())
        {
            Assert.Empty(await OutboxTestHelpers.EventsMentioningAsync(before, ping.Id)); // nothing until the caller saves
        }

        await db.SaveChangesAsync();

        await using var verify = CreateDbContext();
        var stored = Assert.Single(await OutboxTestHelpers.EventsMentioningAsync(verify, ping.Id));
        _messageIds.Add(stored.Id);
        Assert.Equal("tests.pinged.v1", stored.EventName);
        Assert.Null(stored.DispatchedAt);
        Assert.Equal("hello", OutboxTestHelpers.Property(stored, "note")); // camelCase web JSON
        Assert.Equal(ping.Id.ToString(), OutboxTestHelpers.Property(stored, "id"));
    }

    [Fact]
    public async Task Publish_WithoutASave_LeavesNothingBehind()
    {
        var ping = new Pinged(Guid.NewGuid(), "dropped");
        await using (var db = CreateDbContext())
        {
            new EventPublisher(db, TimeProvider.System).Publish(ping);
        }

        await using var verify = CreateDbContext();
        Assert.Empty(await OutboxTestHelpers.EventsMentioningAsync(verify, ping.Id));
    }

    [Fact]
    public async Task Dispatch_EnqueuesOneHandlerJobPerRegisteredHandler_AndMarksTheMessageDispatched()
    {
        var messageId = await StoreAsync(new Pinged(Guid.NewGuid(), "dispatch me"));
        var jobs = new RecordingJobClient();
        await using var db = CreateDbContext();
        var dispatch = new DispatchOutboxJob(db, Registry(), jobs, TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance);

        await dispatch.RunAsync();

        var handlerJobs = jobs.Created.Where(j => j.Args.Count == 2 && (Guid)j.Args[0]! == messageId).ToList();
        Assert.Equal([PingHandler.Key, FailingPingHandler.Key, SecondPingHandler.Key], handlerJobs.Select(j => (string)j.Args[1]!).Order());
        Assert.All(handlerJobs, j => Assert.Equal(typeof(HandleEventJob), j.Type));
        await using var verify = CreateDbContext();
        Assert.NotNull((await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).DispatchedAt);
    }

    [Fact]
    public async Task Dispatch_NeverEnqueuesAnAlreadyDispatchedMessageAgain()
    {
        var messageId = await StoreAsync(new Pinged(Guid.NewGuid(), "once"));
        var registry = Registry();
        await using (var db = CreateDbContext())
        {
            await new DispatchOutboxJob(db, registry, new RecordingJobClient(), TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance).RunAsync();
        }

        var jobs = new RecordingJobClient();
        await using (var db = CreateDbContext())
        {
            await new DispatchOutboxJob(db, registry, jobs, TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance).RunAsync();
        }

        Assert.DoesNotContain(jobs.Created, j => j.Args.Count == 2 && (Guid)j.Args[0]! == messageId);
    }

    [Fact]
    public async Task Dispatch_MarksAnEventWithNoHandlersDispatched_WithoutEnqueuingAnything()
    {
        var messageId = await StoreAsync(new Pinged(Guid.NewGuid(), "nobody listens"));
        var jobs = new RecordingJobClient();
        await using var db = CreateDbContext();

        await new DispatchOutboxJob(db, new EventRegistry([]), jobs, TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance).RunAsync();

        Assert.DoesNotContain(jobs.Created, j => j.Args.Count == 2 && (Guid)j.Args[0]! == messageId);
        await using var verify = CreateDbContext();
        Assert.NotNull((await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).DispatchedAt);
    }

    [Fact]
    public async Task Handle_RunsTheHandler_AndCommitsItsWritesWithTheDeliveryRecord()
    {
        var ping = new Pinged(Guid.NewGuid(), "handle me");
        var messageId = await StoreAsync(ping);

        await HandleAsync(messageId, PingHandler.Key);

        await using var verify = CreateDbContext();
        var written = Assert.Single(await verify.AuditLogs.Where(a => a.TargetId == ping.Id).ToListAsync());
        Assert.Equal(PingHandler.Actor, written.Actor);
        Assert.True(await verify.OutboxDeliveries.AnyAsync(d => d.MessageId == messageId && d.HandlerKey == PingHandler.Key));
    }

    [Fact]
    public async Task Handle_WhenTheSameDeliveryRunsAgain_DoesNotRunTheHandlerTwice()
    {
        var ping = new Pinged(Guid.NewGuid(), "at least once");
        var messageId = await StoreAsync(ping);

        await HandleAsync(messageId, PingHandler.Key);
        await HandleAsync(messageId, PingHandler.Key); // Hangfire redelivered it

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.TargetId == ping.Id));
        Assert.Equal(1, await verify.OutboxDeliveries.CountAsync(d => d.MessageId == messageId));
    }

    [Fact]
    public async Task Handle_EachHandlerOfOneMessageRunsOnItsOwn()
    {
        var ping = new Pinged(Guid.NewGuid(), "two listeners");
        var messageId = await StoreAsync(ping);

        await HandleAsync(messageId, PingHandler.Key);
        await HandleAsync(messageId, SecondPingHandler.Key);

        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.AuditLogs.CountAsync(a => a.TargetId == ping.Id));
        Assert.Equal(2, await verify.OutboxDeliveries.CountAsync(d => d.MessageId == messageId));
    }

    [Fact]
    public async Task Handle_WhenTheHandlerThrows_RecordsNoDeliveryAndNoWrites_SoARetryRunsItAgain()
    {
        var ping = new Pinged(Guid.NewGuid(), FailingPingHandler.Poison);
        var messageId = await StoreAsync(ping);

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(messageId, FailingPingHandler.Key));

        await using var verify = CreateDbContext();
        Assert.False(await verify.OutboxDeliveries.AnyAsync(d => d.MessageId == messageId));
        Assert.False(await verify.AuditLogs.AnyAsync(a => a.TargetId == ping.Id)); // the write before the throw rolled back
    }

    [Fact]
    public async Task Handle_ForAHandlerNoLongerRegistered_DoesNothing()
    {
        var messageId = await StoreAsync(new Pinged(Guid.NewGuid(), "orphan"));

        await HandleAsync(messageId, "tests.removed-handler");

        await using var verify = CreateDbContext();
        Assert.False(await verify.OutboxDeliveries.AnyAsync(d => d.MessageId == messageId));
    }

    [Fact]
    public async Task Handle_ForAMessageTheSweepAlreadyDeleted_DoesNothing()
    {
        await HandleAsync(Guid.CreateVersion7(), PingHandler.Key); // no throw, nothing to deliver
    }

    [Fact]
    public async Task Sweep_DeletesDispatchedMessagesPastRetention_WithTheirDeliveries_AndKeepsTheRest()
    {
        var now = DateTimeOffset.UtcNow;
        var old = await StoreAsync(new Pinged(Guid.NewGuid(), "old"), occurredAt: now.AddDays(-31), dispatchedAt: now.AddDays(-31));
        var recent = await StoreAsync(new Pinged(Guid.NewGuid(), "recent"), occurredAt: now.AddDays(-29), dispatchedAt: now.AddDays(-29));
        await using (var seed = CreateDbContext())
        {
            seed.OutboxDeliveries.Add(new OutboxDelivery { MessageId = old, HandlerKey = PingHandler.Key, HandledAt = now.AddDays(-31) });
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var dispatch = new DispatchOutboxJob(db, new EventRegistry([]), new RecordingJobClient(), TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance);
            await new OutboxSweepJob(dispatch, db, Options.Create(new OutboxOptions { RetentionDays = 30 }), TimeProvider.System).RunAsync();
        }

        await using var verify = CreateDbContext();
        Assert.False(await verify.OutboxMessages.AnyAsync(m => m.Id == old));
        Assert.False(await verify.OutboxDeliveries.AnyAsync(d => d.MessageId == old));
        Assert.True(await verify.OutboxMessages.AnyAsync(m => m.Id == recent));
    }

    [Fact]
    public async Task Sweep_DispatchesWhatACrashLeftBehind_AndNeverDeletesAnUndispatchedMessage()
    {
        var stale = await StoreAsync(new Pinged(Guid.NewGuid(), "left behind"), occurredAt: DateTimeOffset.UtcNow.AddDays(-60));

        await using (var db = CreateDbContext())
        {
            var dispatch = new DispatchOutboxJob(db, new EventRegistry([]), new RecordingJobClient(), TimeProvider.System, NullLogger<DispatchOutboxJob>.Instance);
            await new OutboxSweepJob(dispatch, db, Options.Create(new OutboxOptions()), TimeProvider.System).RunAsync();
        }

        await using var verify = CreateDbContext();
        var message = await verify.OutboxMessages.SingleAsync(m => m.Id == stale);
        Assert.NotNull(message.DispatchedAt); // dispatched just now, so it is inside retention
    }

    [Fact]
    public async Task Interceptor_RequestsADispatch_OnlyAfterASaveThatAddedAnOutboxMessage()
    {
        var trigger = new CountingTrigger();
        await using var db = CreateDbContextWith(trigger);

        db.OutboxMessages.Add(new OutboxMessage { EventName = "tests.pinged.v1", Payload = "{}", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(1, trigger.Count);

        var profile = new Domain.Modules.Profile.Profile { AuthUserId = Guid.NewGuid(), Name = "No events" };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        Assert.Equal(1, trigger.Count); // a save with no event asks for nothing

        await db.Profiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
        _messageIds.AddRange(await db.OutboxMessages.Where(m => m.EventName == "tests.pinged.v1").Select(m => m.Id).ToListAsync());
    }

    [Fact]
    public async Task Interceptor_DoesNotRequestADispatch_WhenTheSaveFails()
    {
        var taken = await StoreAsync(new Pinged(Guid.NewGuid(), "taken"));
        var trigger = new CountingTrigger();
        await using var db = CreateDbContextWith(trigger);
        db.OutboxMessages.Add(new OutboxMessage { Id = taken, EventName = "tests.pinged.v1", Payload = "{}", OccurredAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());

        Assert.Equal(0, trigger.Count);
    }

    [Fact]
    public void TheAppRegistersTheOutboxSweepAsAnEveryMinuteRecurringJob()
    {
        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var sweep = Hangfire.Storage.StorageConnectionExtensions.GetRecurringJobs(connection)
            .SingleOrDefault(j => j.Id == OutboxSweepJob.RecurringJobId);

        Assert.NotNull(sweep);
        Assert.Equal(Cron.Minutely(), sweep.Cron);
        Assert.Equal(typeof(OutboxSweepJob), sweep.Job.Type);
    }

    private async Task HandleAsync(Guid messageId, string handlerKey)
    {
        using var scope = TestScope();
        var job = new HandleEventJob(
            scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>(),
            scope.ServiceProvider.GetRequiredService<EventRegistry>(),
            scope.ServiceProvider,
            TimeProvider.System,
            NullLogger<HandleEventJob>.Instance);
        await job.RunAsync(messageId, handlerKey);
    }

    // A scope over the app's own services plus the test handlers, sharing one
    // DbContext per scope exactly as a Hangfire job scope does.
    private IServiceScope TestScope()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>());
        services.AddScoped<IAuditService>(sp => new WorkPilot.Infrastructure.Modules.Audit.AuditService(sp.GetRequiredService<WorkPilotDbContext>()));
        services.AddSingleton(Registry());
        services.AddScoped<PingHandler>();
        services.AddScoped<SecondPingHandler>();
        services.AddScoped<FailingPingHandler>();
        return services.BuildServiceProvider().CreateScope();
    }

    private static EventRegistry Registry() => new(
    [
        new EventHandlerRegistration(typeof(Pinged), PingHandler.Key, new EventHandlerInvoker<Pinged, PingHandler>()),
        new EventHandlerRegistration(typeof(Pinged), SecondPingHandler.Key, new EventHandlerInvoker<Pinged, SecondPingHandler>()),
        new EventHandlerRegistration(typeof(Pinged), FailingPingHandler.Key, new EventHandlerInvoker<Pinged, FailingPingHandler>()),
    ]);

    private async Task<Guid> StoreAsync(Pinged ping, DateTimeOffset? occurredAt = null, DateTimeOffset? dispatchedAt = null)
    {
        await using var db = CreateDbContext();
        var message = new OutboxMessage
        {
            EventName = Pinged.EventName,
            Payload = System.Text.Json.JsonSerializer.Serialize(ping, EventPublisher.PayloadJson),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
        if (dispatchedAt is { } at)
        {
            message.MarkDispatched(at);
        }

        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        _messageIds.Add(message.Id);
        return message.Id;
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    // A context wired like the app's (the outbox interceptor, application
    // services), but whose dispatch trigger only counts.
    private WorkPilotDbContext CreateDbContextWith(IOutboxDispatchTrigger trigger)
    {
        var appServices = new ServiceCollection().AddSingleton(trigger).BuildServiceProvider();
        var appOptions = factory.Services.CreateScope().ServiceProvider.GetRequiredService<DbContextOptions<WorkPilotDbContext>>();
        var options = new DbContextOptionsBuilder<WorkPilotDbContext>(appOptions)
            .UseApplicationServiceProvider(appServices)
            .Options;
        return new WorkPilotDbContext(options, factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
    }

    public sealed record Pinged(Guid Id, string Note) : IDomainEvent
    {
        public static string EventName => "tests.pinged.v1";
    }

    // Writes one audit row per delivery, so a test can count how often it ran.
    public sealed class PingHandler(IAuditService audit) : IEventHandler<Pinged>
    {
        public const string Key = "tests.on-pinged";
        public const string Actor = "OutboxTests";

        public static string HandlerKey => Key;

        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            audit.Record(Actor, "Pinged", "Test", domainEvent.Id, null);
            return Task.CompletedTask;
        }
    }

    public sealed class SecondPingHandler(IAuditService audit) : IEventHandler<Pinged>
    {
        public const string Key = "tests.on-pinged-second";

        public static string HandlerKey => Key;

        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            audit.Record(PingHandler.Actor, "PingedSecond", "Test", domainEvent.Id, null);
            return Task.CompletedTask;
        }
    }

    // Writes, then throws: the write must not survive.
    public sealed class FailingPingHandler(IAuditService audit) : IEventHandler<Pinged>
    {
        public const string Key = "tests.on-pinged-failing";
        public const string Poison = "poison";

        public static string HandlerKey => Key;

        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            audit.Record(PingHandler.Actor, "PingedThenFailed", "Test", domainEvent.Id, null);
            throw new InvalidOperationException("scripted handler failure");
        }
    }

    private sealed class CountingTrigger : IOutboxDispatchTrigger
    {
        public int Count { get; private set; }

        public void RequestDispatch() => Count++;
    }

    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
