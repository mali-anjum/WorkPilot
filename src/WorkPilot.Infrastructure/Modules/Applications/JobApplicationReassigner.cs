using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Applications;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Applications;

/// <inheritdoc cref="IJobApplicationReassigner" />
public sealed class JobApplicationReassigner(WorkPilotDbContext db) : IJobApplicationReassigner
{
    /// <inheritdoc />
    public Task ReassignJobAsync(Guid fromJobId, Guid toJobId, CancellationToken cancellationToken) =>
        db.JobApplications
            .IgnoreQueryFilters()
            .Where(a => a.JobId == fromJobId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.JobId, toJobId), cancellationToken);
}
