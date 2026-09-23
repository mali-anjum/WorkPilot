using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent;

/// <inheritdoc cref="IAuditService" />
public sealed class AuditService(WorkPilotDbContext db) : IAuditService
{
    public void Record(string actor, string action, string targetType, Guid targetId, string? payload)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Payload = payload,
        });
    }
}
