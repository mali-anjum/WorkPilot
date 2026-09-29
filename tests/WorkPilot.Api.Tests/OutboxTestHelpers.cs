using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

/// <summary>Reads and clears the outbox rows a test caused, found by an id inside the payload (spec 0018).</summary>
internal static class OutboxTestHelpers
{
    /// <summary>Every outbox message whose payload mentions <paramref name="id"/>, oldest first.</summary>
    public static Task<List<OutboxMessage>> EventsMentioningAsync(WorkPilotDbContext db, Guid id)
    {
        var pattern = $"%{id}%";
        return db.OutboxMessages
            .FromSql($"""SELECT * FROM app.outbox_messages WHERE "Payload"::text LIKE {pattern}""")
            .AsNoTracking()
            .OrderBy(m => m.OccurredAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
    }

    /// <summary>Deletes every outbox message whose payload mentions <paramref name="id"/>.</summary>
    public static Task DeleteEventsMentioningAsync(WorkPilotDbContext db, Guid id)
    {
        var pattern = $"%{id}%";
        return db.Database.ExecuteSqlAsync($"""DELETE FROM app.outbox_messages WHERE "Payload"::text LIKE {pattern}""");
    }

    /// <summary>A payload property as a string, for asserting on an event's content.</summary>
    public static string? Property(OutboxMessage message, string name)
    {
        using var doc = JsonDocument.Parse(message.Payload);
        return doc.RootElement.GetProperty(name).GetString();
    }
}
