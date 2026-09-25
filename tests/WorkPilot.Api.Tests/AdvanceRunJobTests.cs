using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Agent;

namespace WorkPilot.Api.Tests;

// Integration tests for AdvanceRunJob's retry-exhaustion and restart-survival
// branches (spec 0005, AC-8 and AC-9), against the real Postgres stack.
// /check verify (2026-09-24) found these unexercisable through the live app,
// since both shipped tools always succeed: these drive AdvanceRunJob directly
// with a controllable fake ITool instead of going through HTTP, needs a
// reachable Postgres (see WORKPILOTDB_CONNECTION, supabase/.env). Shares
// SharedApiFactory only to reuse its DbContext wiring, not its HTTP surface.
[Collection("Api")]
public class AdvanceRunJobTests(SharedApiFactory factory)
{
    [Fact]
    public async Task RunAsync_WhenTheToolExhaustsItsRetries_FailsTheStepAndTheRun()
    {
        // covers AC-8
        var tool = new ScriptedTool("always_fails", isIdempotent: true, maxRetries: 1, ScriptedTool.Behavior.AlwaysFail);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var reloadedStep = await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id);
            var reloadedRun = await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id);
            Assert.Equal(AgentStepStatus.Failed, reloadedStep.Status);
            Assert.Equal(AgentRunStatus.Failed, reloadedRun.Status);
            Assert.Equal(1 + tool.MaxRetries, tool.AttemptCount); // exhausted every retry, no more
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenANonIdempotentStepIsFoundRunningOnReentry_FailsItInsteadOfReexecuting()
    {
        // covers AC-9: a step left Running (the process died mid tool call) must not silently re-run a side effect.
        var tool = new ScriptedTool("side_effect", isIdempotent: false, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedRunningRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var reloadedStep = await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id);
            var reloadedRun = await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id);
            Assert.Equal(AgentStepStatus.Failed, reloadedStep.Status);
            Assert.Equal(AgentRunStatus.Failed, reloadedRun.Status);
            Assert.Equal(0, tool.AttemptCount); // never actually invoked again
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenANonIdempotentStepWasJustApproved_ExecutesItOnceInsteadOfFailingIt()
    {
        // covers AC-4, AC-9: an approved step hasn't started yet, so it must not be mistaken for a crashed one.
        var tool = new ScriptedTool("approved_side_effect", isIdempotent: false, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedAwaitingApprovalRunAsync(db, tool.Name, approved: true);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(AgentStepStatus.Succeeded, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(1, tool.AttemptCount);
            Assert.Equal(1, await verifyDb.ToolCalls.CountAsync(t => t.AgentStepId == step.Id));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheStepsApprovalIsStillPending_DoesNotExecuteIt()
    {
        // covers AC-4: a stray job delivery before any decision must not run an approval gated tool.
        var tool = new ScriptedTool("gated", isIdempotent: false, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedAwaitingApprovalRunAsync(db, tool.Name, approved: false);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(AgentStepStatus.AwaitingApproval, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(0, tool.AttemptCount);
            Assert.Equal(0, await verifyDb.ToolCalls.CountAsync(t => t.AgentStepId == step.Id));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnIdempotentStepIsFoundRunningOnReentry_SafelyReexecutesAndSucceeds()
    {
        // covers AC-9: an idempotent tool found Running on restart may re-run to completion.
        var tool = new ScriptedTool("idempotent_op", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedRunningRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var reloadedStep = await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id);
            Assert.Equal(AgentStepStatus.Succeeded, reloadedStep.Status);
            Assert.Equal(1, tool.AttemptCount);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAFailedStepIsFollowedByAnother_NeverExecutesTheLaterStep()
    {
        // covers AC-8: no further planned steps execute after a step exhausts its retries.
        var failing = new ScriptedTool("fails_first", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysFail);
        var later = new ScriptedTool("never_reached", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(failing, later);
        var (run, _, profileId) = await SeedPendingRunAsync(db, failing.Name);
        var secondStep = new AgentStep { AgentRunId = run.Id, Ordinal = 1, ToolName = later.Name };
        db.AgentSteps.Add(secondStep);
        await db.SaveChangesAsync();

        try
        {
            await job.RunAsync(run.Id);
            await job.RunAsync(run.Id); // a stray re-delivery after the failure must also be a no-op

            await using var verifyDb = CreateDbContext();
            var reloadedSecond = await verifyDb.AgentSteps.SingleAsync(s => s.Id == secondStep.Id);
            Assert.Equal(AgentStepStatus.Pending, reloadedSecond.Status);
            Assert.Equal(0, later.AttemptCount);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheToolFails_AuditsTheErrorTextWrappedAsJson()
    {
        // covers AC-5: a failed ToolCall still writes its AuditLog row. The error is
        // plain text, so it must land in the jsonb Payload column as {"error": "..."}.
        var tool = new ScriptedTool("fails_with_text", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysFail);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var audit = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == tool.Name);
            Assert.Equal("Agent", audit.Actor);
            Assert.NotNull(audit.Payload);
            using var payload = JsonDocument.Parse(audit.Payload);
            Assert.Equal("scripted failure", payload.RootElement.GetProperty("error").GetString());

            var toolCall = await verifyDb.ToolCalls.SingleAsync(t => t.AgentStepId == step.Id);
            Assert.False(toolCall.Success);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheErrorTextHasQuotesAndUnicode_StillAuditsItAsValidJson()
    {
        // covers AC-5: an exception message with JSON-hostile characters must not break the jsonb insert.
        const string error = "boom: \"quoted\" \\ back\\slash, newline\n and ünïcødé ✓";
        var tool = new ScriptedTool("fails_awkwardly", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysFail, failureMessage: error);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var audit = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == tool.Name);
            using var payload = JsonDocument.Parse(audit.Payload!);
            Assert.Equal(error, payload.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheToolSucceedsWithOutput_AuditsTheOutputJsonAsIs()
    {
        // covers AC-5: a successful ToolCall's AuditLog Payload is the tool's own output, not an error wrapper.
        var tool = new ScriptedTool("returns_output", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed, outputJson: """{"name":"Test Founder","count":2}""");
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var audit = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == tool.Name);
            using var payload = JsonDocument.Parse(audit.Payload!);
            Assert.Equal("Test Founder", payload.RootElement.GetProperty("name").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("count").GetInt32());
            Assert.False(payload.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheToolSucceedsWithNoOutput_AuditsANullPayload()
    {
        // covers AC-5: no output and no error still writes the AuditLog row, with a null Payload.
        var tool = new ScriptedTool("silent_success", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var audit = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == tool.Name);
            Assert.Null(audit.Payload);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Theory]
    [InlineData(ToolRiskTier.ApprovalRequired)]
    [InlineData(ToolRiskTier.ExplicitConfirmation)]
    public async Task RunAsync_WhenAGatedToolIsNext_SuspendsWithAPendingApprovalAndTheEvidenceSnapshot(ToolRiskTier tier)
    {
        // covers spec 0007 AC-1, AC-3, AC-10
        var documentVersionId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero);
        var tool = new DescribingTool("gated_with_evidence", tier, context => new ApprovalEvidence(
            "Sends your application",
            new ApprovalTarget("Profile", context.ProfileId, "Test Founder"),
            [new DocumentVersionEvidence("Resume", documentVersionId, "Main CV", 3, createdAt)]));
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(AgentStepStatus.AwaitingApproval, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(AgentRunStatus.AwaitingApproval, (await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status);
            Assert.Equal(0, tool.AttemptCount);
            Assert.Equal(0, await verifyDb.ToolCalls.CountAsync(t => t.AgentStepId == step.Id));

            var approval = await verifyDb.Approvals.SingleAsync(a => a.TargetId == step.Id);
            Assert.Equal(ApprovalStatus.Pending, approval.Status);
            Assert.Equal(tier.ToString(), approval.RiskTier);
            Assert.True(approval.RequestedAt >= before);
            Assert.False(approval.ExplicitlyConfirmed);

            var evidence = JsonSerializer.Deserialize<ApprovalEvidence>(approval.EvidenceJson!, JsonSerializerOptions.Web)!;
            Assert.Equal("Sends your application", evidence.Summary);
            Assert.Equal(new ApprovalTarget("Profile", profileId, "Test Founder"), evidence.Target);
            var document = Assert.Single(evidence.Documents);
            Assert.Equal(documentVersionId, document.VersionId);
            Assert.Equal(3, document.VersionNumber);

            var requested = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == "ApprovalRequested");
            Assert.Equal("Agent", requested.Actor);
            using var payload = JsonDocument.Parse(requested.Payload!);
            Assert.Equal("Sends your application", payload.RootElement.GetProperty("summary").GetString());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnAutoAllowedToolIsNext_ExecutesWithNoApprovalRow()
    {
        // covers spec 0007 AC-1
        var tool = new ScriptedTool("auto", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(AgentStepStatus.Succeeded, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(1, tool.AttemptCount);
            Assert.False(await verifyDb.Approvals.AnyAsync(a => a.TargetId == step.Id));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAGatedToolDescribesNoEvidence_StillSuspendsWithANullSnapshot()
    {
        // covers spec 0007 AC-3: IApprovalEvidenceProvider is optional
        var tool = new ScriptedTool("gated_plain", isIdempotent: false, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed, riskTier: ToolRiskTier.ApprovalRequired);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            var approval = await verifyDb.Approvals.SingleAsync(a => a.TargetId == step.Id);
            Assert.Equal(ApprovalStatus.Pending, approval.Status);
            Assert.Null(approval.EvidenceJson);
            Assert.Equal(0, tool.AttemptCount);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenDescribingTheEvidenceThrows_StillSuspendsWithTheErrorAsTheSummary()
    {
        // covers spec 0007 AC-3: evidence is a courtesy, never a reason not to suspend
        var tool = new DescribingTool("gated_broken_evidence", ToolRiskTier.ApprovalRequired, _ => throw new InvalidOperationException("resume store offline"));
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedPendingRunAsync(db, tool.Name);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(AgentRunStatus.AwaitingApproval, (await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status);
            var approval = await verifyDb.Approvals.SingleAsync(a => a.TargetId == step.Id);
            var evidence = JsonSerializer.Deserialize<ApprovalEvidence>(approval.EvidenceJson!, JsonSerializerOptions.Web)!;
            Assert.Contains("resume store offline", evidence.Summary);
            Assert.Null(evidence.Target);
            Assert.Empty(evidence.Documents);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnExplicitToolsApprovalWasNotConfirmed_RefusesAtTheGateAndNeverExecutes()
    {
        // covers spec 0007 AC-2: the approval was recorded at the ApprovalRequired
        // tier and approved without a confirmation, but the tool is explicit tier
        var tool = new ScriptedTool("destructive", isIdempotent: false, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed, riskTier: ToolRiskTier.ExplicitConfirmation);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedAwaitingApprovalRunAsync(db, tool.Name, approved: true);

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(0, tool.AttemptCount);
            Assert.Equal(0, await verifyDb.ToolCalls.CountAsync(t => t.AgentStepId == step.Id));
            Assert.Equal(AgentStepStatus.Skipped, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(AgentRunStatus.Failed, (await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status);

            var refused = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == "ApprovalGateRefused");
            Assert.Equal("Agent", refused.Actor);
            using var payload = JsonDocument.Parse(refused.Payload!);
            Assert.Equal("ExplicitConfirmation", payload.RootElement.GetProperty("toolRiskTier").GetString());
            Assert.False(payload.RootElement.GetProperty("explicitlyConfirmed").GetBoolean());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnIdempotentGatedStepIsRunningWithoutAPermittingApproval_FailsInsteadOfReexecuting()
    {
        // covers spec 0007 AC-2: the gate also holds on the Running re-entry path
        var tool = new ScriptedTool("gated_idempotent", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed, riskTier: ToolRiskTier.ApprovalRequired);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedRunningRunAsync(db, tool.Name); // no Approval row at all

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(0, tool.AttemptCount);
            // Failed, not Skipped: a Running step may have partly run before the crash.
            Assert.Equal(AgentStepStatus.Failed, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(AgentRunStatus.Failed, (await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status);

            // Audited as a gate refusal, so it never looks like an ordinary crash.
            var refused = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == "ApprovalGateRefused");
            Assert.Equal("Agent", refused.Actor);
            using var payload = JsonDocument.Parse(refused.Payload!);
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("approvalId").ValueKind); // no approval row at all
            Assert.Equal("ApprovalRequired", payload.RootElement.GetProperty("toolRiskTier").GetString());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Fact]
    public async Task RunAsync_WhenARunningStepsToolBecameExplicitAfterApproval_RefusesAtTheGateWithTheApprovalInTheAudit()
    {
        // covers spec 0007 AC-2 on the Running re-entry path: approved at the
        // ApprovalRequired tier, then the tool was redeployed as explicit tier
        // before the crashed job came back
        var tool = new ScriptedTool("became_destructive", isIdempotent: true, maxRetries: 0, ScriptedTool.Behavior.AlwaysSucceed, riskTier: ToolRiskTier.ExplicitConfirmation);
        var (db, job) = CreateJob(tool);
        var (run, step, profileId) = await SeedAwaitingApprovalRunAsync(db, tool.Name, approved: true);
        step.TransitionTo(AgentStepStatus.Running);
        await db.SaveChangesAsync();

        try
        {
            await job.RunAsync(run.Id);

            await using var verifyDb = CreateDbContext();
            Assert.Equal(0, tool.AttemptCount);
            Assert.Equal(AgentStepStatus.Failed, (await verifyDb.AgentSteps.SingleAsync(s => s.Id == step.Id)).Status);
            Assert.Equal(AgentRunStatus.Failed, (await verifyDb.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status);

            var approval = await verifyDb.Approvals.SingleAsync(a => a.TargetId == step.Id);
            var refused = await verifyDb.AuditLogs.SingleAsync(a => a.TargetId == step.Id && a.Action == "ApprovalGateRefused");
            using var payload = JsonDocument.Parse(refused.Payload!);
            Assert.Equal(approval.Id, payload.RootElement.GetProperty("approvalId").GetGuid());
            Assert.Equal("ExplicitConfirmation", payload.RootElement.GetProperty("toolRiskTier").GetString());
            Assert.Equal("ApprovalRequired", payload.RootElement.GetProperty("approvalRiskTier").GetString());
            Assert.False(payload.RootElement.GetProperty("explicitlyConfirmed").GetBoolean());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    private (WorkPilotDbContext Db, AdvanceRunJob Job) CreateJob(params ITool[] tools)
    {
        var db = CreateDbContext();
        var registry = new ToolRegistry(tools);
        var verification = new VerificationEngine();
        var audit = new AuditService(db);
        var jobs = factory.Services.CreateScope().ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        return (db, new AdvanceRunJob(db, registry, verification, audit, jobs));
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private static async Task<(AgentRun Run, AgentStep Step, Guid ProfileId)> SeedPendingRunAsync(WorkPilotDbContext db, string toolName)
    {
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Test Founder" };
        db.Profiles.Add(profile);

        var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.Executing.ToString() };
        db.WorkflowInstances.Add(workflow);

        var run = new AgentRun { WorkflowInstanceId = workflow.Id, ProfileId = profile.Id, Goal = "test" };
        db.AgentRuns.Add(run);

        var step = new AgentStep { AgentRunId = run.Id, Ordinal = 0, ToolName = toolName };
        db.AgentSteps.Add(step);

        await db.SaveChangesAsync();

        run.TransitionTo(AgentRunStatus.PolicyCheck);
        run.TransitionTo(AgentRunStatus.Executing);
        await db.SaveChangesAsync();

        return (run, step, profile.Id);
    }

    private static async Task<(AgentRun Run, AgentStep Step, Guid ProfileId)> SeedRunningRunAsync(WorkPilotDbContext db, string toolName)
    {
        var (run, step, profileId) = await SeedPendingRunAsync(db, toolName);
        step.TransitionTo(AgentStepStatus.Running);
        await db.SaveChangesAsync();
        return (run, step, profileId);
    }

    // Mirrors what SuspendForApprovalAsync plus the decide endpoint leave behind:
    // the step at AwaitingApproval with its Approval row, and the run back at
    // Executing once approved (or still AwaitingApproval while pending).
    private static async Task<(AgentRun Run, AgentStep Step, Guid ProfileId)> SeedAwaitingApprovalRunAsync(
        WorkPilotDbContext db, string toolName, bool approved)
    {
        var (run, step, profileId) = await SeedPendingRunAsync(db, toolName);
        step.TransitionTo(AgentStepStatus.AwaitingApproval);
        run.TransitionTo(AgentRunStatus.AwaitingApproval);

        var approval = new Approval { TargetType = ApprovalTargets.AgentStep, TargetId = step.Id, RiskTier = ToolRiskTier.ApprovalRequired.ToString() };
        if (approved)
        {
            approval.Decide(ApprovalStatus.Approved, profileId, DateTimeOffset.UtcNow);
            run.TransitionTo(AgentRunStatus.Executing);
        }

        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        return (run, step, profileId);
    }

    private async Task CleanupAsync(Guid profileId)
    {
        await using var db = CreateDbContext();
        var runIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.Id).ToListAsync();
        var stepIds = await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).Select(s => s.Id).ToListAsync();
        var workflowIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.WorkflowInstanceId).ToListAsync();

        // AuditLog is append only in the product, but these rows are test fixtures, so they are hard deleted here.
        await db.AuditLogs.IgnoreQueryFilters().Where(a => stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.Approvals.Where(a => stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.ToolCalls.Where(t => stepIds.Contains(t.AgentStepId)).ExecuteDeleteAsync();
        await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).ExecuteDeleteAsync();
        await db.AgentRuns.Where(r => r.ProfileId == profileId).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(w => workflowIds.Contains(w.Id)).ExecuteDeleteAsync();
        await db.Profiles.Where(p => p.Id == profileId).ExecuteDeleteAsync();
    }

    // A gated ITool double that also describes its approval evidence (spec
    // 0007, AC-3); counts executions so a test can prove it never ran.
    private sealed class DescribingTool(string name, ToolRiskTier riskTier, Func<ToolExecutionContext, ApprovalEvidence> describe)
        : ITool, IApprovalEvidenceProvider
    {
        public string Name { get; } = name;
        public string Description => "test tool with evidence";
        public IReadOnlyList<string> RequiredArguments => [];
        public IReadOnlyList<string> ExpectedOutputFields => [];
        public ToolRiskTier RiskTier { get; } = riskTier;
        public bool IsIdempotent => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public int MaxRetries => 0;
        public string? TargetType => null;
        public int AttemptCount { get; private set; }

        public Task<ApprovalEvidence> DescribeForApprovalAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(describe(context));

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            AttemptCount++;
            return Task.FromResult(ToolExecutionResult.Ok(null));
        }
    }

    // A controllable ITool double: scripted to always succeed or always fail,
    // with configurable idempotency/retry, and a count of real invocations so
    // a test can assert a non-idempotent step was never called twice.
    private sealed class ScriptedTool(
        string name,
        bool isIdempotent,
        int maxRetries,
        ScriptedTool.Behavior behavior,
        string? outputJson = null,
        string failureMessage = "scripted failure",
        ToolRiskTier riskTier = ToolRiskTier.AutoAllowed) : ITool
    {
        public enum Behavior { AlwaysSucceed, AlwaysFail }

        public string Name { get; } = name;
        public string Description => "test tool";
        public IReadOnlyList<string> RequiredArguments => [];
        public IReadOnlyList<string> ExpectedOutputFields => [];
        public ToolRiskTier RiskTier { get; } = riskTier;
        public bool IsIdempotent { get; } = isIdempotent;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public int MaxRetries { get; } = maxRetries;
        public string? TargetType => null;
        public int AttemptCount { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            AttemptCount++;
            return Task.FromResult(behavior == Behavior.AlwaysSucceed
                ? ToolExecutionResult.Ok(outputJson)
                : ToolExecutionResult.Fail(failureMessage));
        }
    }
}
