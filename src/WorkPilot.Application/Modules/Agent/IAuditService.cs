namespace WorkPilot.Application.Modules.Agent;

/// <summary>Records one audit entry. Adds it to the current unit of work without saving; the caller's own SaveChanges commits it atomically alongside the state change it's auditing (spec 0005, AC-5).</summary>
public interface IAuditService
{
    void Record(string actor, string action, string targetType, Guid targetId, string? payload);
}
