namespace WorkPilot.Application.Modules.Applications;

/// <summary>
/// The Applications module's port for another module to move applications
/// between jobs. Job deduplication (spec 0017) calls it when it merges a
/// newer job into an older one, so one role keeps one set of applications.
/// </summary>
public interface IJobApplicationReassigner
{
    /// <summary>
    /// Moves every application of <paramref name="fromJobId"/> (soft deleted
    /// ones too) to <paramref name="toJobId"/>. Runs immediately, inside the
    /// caller's current transaction.
    /// </summary>
    Task ReassignJobAsync(Guid fromJobId, Guid toJobId, CancellationToken cancellationToken);
}
