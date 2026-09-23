using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent;

/// <summary>Keeps the 1:1 <see cref="WorkflowInstance.Status"/> mirrored from its <see cref="AgentRun.Status"/> (spec 0005). Call before SaveChanges whenever a run's status changed.</summary>
public static class WorkflowMirror
{
    public static async Task SyncAsync(WorkPilotDbContext db, AgentRun run, CancellationToken cancellationToken)
    {
        var workflow = await db.WorkflowInstances.FirstAsync(w => w.Id == run.WorkflowInstanceId, cancellationToken);
        workflow.Status = run.Status.ToString();
    }
}
