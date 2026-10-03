using WorkPilot.Application.Modules.Audit;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Audit;

/// <inheritdoc cref="IAuditService" />
public sealed class AuditService(WorkPilotDbContext db) : IAuditService
{
    public void Record(string actor, string action, string targetType, Guid targetId, string? payload, bool failed = false)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Payload = payload,
            Category = AuditCategories.For(actor, action, targetType, failed),
        });
    }
}
