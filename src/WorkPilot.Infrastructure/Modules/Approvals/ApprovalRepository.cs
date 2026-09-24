using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Approvals;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Approvals;

/// <inheritdoc cref="IApprovalRepository" />
public sealed class ApprovalRepository(WorkPilotDbContext db) : IApprovalRepository
{
    public async Task<ApprovalDecisionContext?> LoadForDecisionAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(
            a => a.Id == approvalId && a.TargetType == ApprovalTargets.AgentStep, cancellationToken);
        if (approval is null)
        {
            return null;
        }

        var step = await db.AgentSteps.FirstOrDefaultAsync(s => s.Id == approval.TargetId, cancellationToken);
        if (step is null)
        {
            return null; // the step was swept by retention; nothing left to decide on
        }

        var run = await db.AgentRuns.FirstAsync(r => r.Id == step.AgentRunId, cancellationToken);
        return new ApprovalDecisionContext(approval, step, run);
    }

    public async Task<SaveDecisionOutcome> SaveDecisionAsync(ApprovalDecisionContext context, CancellationToken cancellationToken)
    {
        await WorkflowMirror.SyncAsync(db, context.Run, cancellationToken);

        try
        {
            // One SaveChanges is one transaction: the approval (guarded by
            // WHERE Status = 'Pending'), the step/run (guarded by the run's
            // xmin), the workflow mirror, and the audit row all commit, or none do.
            await db.SaveChangesAsync(cancellationToken);
            return SaveDecisionOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Leave the context clean for anything else in this scope.
            db.ChangeTracker.Clear();
            return ex.Entries.Any(e => e.Entity is Approval)
                ? SaveDecisionOutcome.AlreadyDecided
                : SaveDecisionOutcome.ConcurrentChange;
        }
    }

    public async Task<IReadOnlyList<PendingApprovalRow>> ListPendingAsync(Guid profileId, CancellationToken cancellationToken) =>
        await (
            from a in db.Approvals.AsNoTracking()
            join s in db.AgentSteps on a.TargetId equals s.Id
            join r in db.AgentRuns on s.AgentRunId equals r.Id
            where a.TargetType == ApprovalTargets.AgentStep && a.Status == ApprovalStatus.Pending && r.ProfileId == profileId
            orderby a.RequestedAt, a.Id
            select new PendingApprovalRow(a.Id, a.RiskTier, a.RequestedAt, r.Id, r.Goal, s.Ordinal, s.ToolName, s.ArgumentsJson, a.EvidenceJson))
        .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DecidedApprovalRow>> ListRecentlyDecidedAsync(Guid profileId, int limit, CancellationToken cancellationToken) =>
        await (
            from a in db.Approvals.AsNoTracking()
            join s in db.AgentSteps on a.TargetId equals s.Id
            join r in db.AgentRuns on s.AgentRunId equals r.Id
            where a.TargetType == ApprovalTargets.AgentStep && a.Status != ApprovalStatus.Pending && r.ProfileId == profileId
            orderby a.DecidedAt descending, a.Id descending
            select new DecidedApprovalRow(a.Id, s.ToolName, r.Goal, a.RiskTier, a.Status, a.DecidedAt, a.DecidedBy, a.ExplicitlyConfirmed))
        .Take(limit)
        .ToListAsync(cancellationToken);
}
