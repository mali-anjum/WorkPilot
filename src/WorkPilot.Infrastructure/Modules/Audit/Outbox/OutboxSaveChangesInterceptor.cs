using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Domain.Modules.Audit;

namespace WorkPilot.Infrastructure.Modules.Audit.Outbox;

/// <summary>Asks for an outbox dispatch soon (implemented in Workers as a Hangfire enqueue).</summary>
public interface IOutboxDispatchTrigger
{
    void RequestDispatch();
}

/// <summary>
/// Requests a dispatch right after a save that added an <see cref="OutboxMessage"/> (spec 0018,
/// section 3), so events are usually handled within a second. Stateless apart from a per context
/// flag, so one instance serves every context. The trigger is looked up from the context's
/// application services when the save completes; without one (a context built by hand, as some
/// tests do) nothing is requested and the every minute sweep delivers the message instead.
/// Inside an explicit transaction the request can run before the commit; that is harmless,
/// because the dispatcher only sees committed rows and the sweep catches anything it missed.
/// </summary>
public sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>The one instance every <c>WorkPilotDbContext</c> uses.</summary>
    public static readonly OutboxSaveChangesInterceptor Instance = new();

    private readonly ConditionalWeakTable<DbContext, object> _pending = [];

    private OutboxSaveChangesInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Fire(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Fire(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Clear(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Clear(eventData.Context);
        return Task.CompletedTask;
    }

    private void Note(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
        {
            _pending.AddOrUpdate(context, this);
        }
    }

    private void Fire(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out _))
        {
            return;
        }

        _pending.Remove(context);
        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        services?.GetService<IOutboxDispatchTrigger>()?.RequestDispatch();
    }

    private void Clear(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }
}
