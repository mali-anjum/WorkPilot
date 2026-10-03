namespace WorkPilot.Application.Modules.Audit;

/// <summary>Records one audit entry. Adds it to the current unit of work without saving; the caller's own SaveChanges commits it atomically alongside the state change it's auditing (spec 0005, AC-5).</summary>
public interface IAuditService
{
    /// <param name="failed">True when the audited action failed, so the entry lands under Errors in the activity feed (spec 0011).</param>
    void Record(string actor, string action, string targetType, Guid targetId, string? payload, bool failed = false);
}
