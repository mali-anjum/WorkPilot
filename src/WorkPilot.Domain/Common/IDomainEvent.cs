namespace WorkPilot.Domain.Common;

/// <summary>
/// A fact one module publishes about its own data, for other modules to react to (spec 0018,
/// section 2). An event is a small <c>sealed record</c> named in the past tense, carrying ids and
/// small values only, never an entity.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// The stable name the outbox stores, <c>&lt;module&gt;.&lt;event-kebab&gt;.v1</c>. Renaming the
    /// class never changes it; changing the payload shape bumps the version.
    /// </summary>
    static abstract string EventName { get; }
}
