using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Approvals;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// Integration tests for the approval engine's internal endpoints (spec 0007):
// GET /internal/approvals and the strengthened
// POST /internal/agent/approvals/{id}/decide, against the real Postgres
// (WORKPILOTDB_CONNECTION, see supabase/.env). Runs are seeded straight into
// the suspended shape AdvanceRunJob leaves behind, because Hangfire's worker
// is disabled in SharedApiFactory; the suspension itself is covered by
// AdvanceRunJobTests and was proven live (docs/specs/0007-approval-engine-center/verify.md).
[Collection("Api")]
public class ApprovalEndpointsTests(SharedApiFactory factory)
{
    private const string ApprovalTool = "approval_required_demo";
    private const string ExplicitTool = "explicit_confirmation_demo";

    // covers: AC-4
    [Fact]
    public async Task ListApprovals_ReturnsTheProfilesPendingApprovalsOldestFirstWithTheirEvidence()
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var evidence = new ApprovalEvidence("Sends your application", new ApprovalTarget("Job", Guid.NewGuid(), "Acme, Backend Engineer"),
            [new DocumentVersionEvidence("Resume", Guid.NewGuid(), "Main CV", 2, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))]);
        var older = await SeedAwaitingApprovalRunAsync(profileId, ExplicitTool, ToolRiskTier.ExplicitConfirmation,
            requestedAt: DateTimeOffset.UtcNow.AddMinutes(-10), goal: "delete my old data");
        var newer = await SeedAwaitingApprovalRunAsync(profileId, ApprovalTool, ToolRiskTier.ApprovalRequired,
            requestedAt: DateTimeOffset.UtcNow.AddMinutes(-1), evidence: evidence, argumentsJson: """{"jobId":"123"}""");

        try
        {
            var view = await client.GetFromJsonAsync<ApprovalCenterDto>($"/internal/approvals?profileId={profileId}");

            Assert.NotNull(view);
            Assert.Equal([older.ApprovalId, newer.ApprovalId], view!.Pending.Select(p => p.ApprovalId));
            Assert.Empty(view.RecentlyDecided);

            var first = view.Pending[0];
            Assert.Equal("ExplicitConfirmation", first.RiskTier);
            Assert.Equal(older.RunId, first.AgentRunId);
            Assert.Equal("delete my old data", first.Goal);
            Assert.Equal(0, first.StepOrdinal);
            Assert.Equal(ExplicitTool, first.ToolName);
            Assert.False(string.IsNullOrWhiteSpace(first.ToolDescription)); // from the live Tool Registry
            Assert.Equal(ExplicitTool, first.ConfirmationPhrase);
            Assert.Null(first.Evidence);

            var second = view.Pending[1];
            Assert.Null(second.ConfirmationPhrase); // only the explicit tier asks for one
            Assert.Equal("123", second.Arguments["jobId"]);
            Assert.NotNull(second.Evidence);
            Assert.Equal("Sends your application", second.Evidence!.Summary);
            Assert.Equal("Acme, Backend Engineer", second.Evidence.Target!.Label);
            var document = Assert.Single(second.Evidence.Documents);
            Assert.Equal(("Resume", "Main CV", 2), (document.Kind, document.Name, document.VersionNumber));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-4
    [Fact]
    public async Task ListApprovals_NeverShowsAnotherProfilesApprovals()
    {
        using var client = factory.CreateClient();
        var owner = await CreateProfileAsync();
        var other = await CreateProfileAsync();
        await SeedAwaitingApprovalRunAsync(owner, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            var view = await client.GetFromJsonAsync<ApprovalCenterDto>($"/internal/approvals?profileId={other}");

            Assert.NotNull(view);
            Assert.Empty(view!.Pending);
            Assert.Empty(view.RecentlyDecided);
        }
        finally
        {
            await CleanupAsync(owner);
            await CleanupAsync(other);
        }
    }

    // covers: AC-4
    [Theory]
    [InlineData("/internal/approvals")]
    [InlineData("/internal/approvals?profileId=00000000-0000-0000-0000-000000000000")]
    public async Task ListApprovals_WithoutAProfileId_Returns400(string url)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // covers: AC-4
    [Fact]
    public async Task ListApprovals_ListsDecidedApprovalsNewestFirstWithWhoDecided()
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var approved = await SeedAwaitingApprovalRunAsync(profileId, ExplicitTool, ToolRiskTier.ExplicitConfirmation);
        var rejected = await SeedAwaitingApprovalRunAsync(profileId, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            await DecideAsync(client, approved.ApprovalId, "Approve", profileId, ExplicitTool);
            await DecideAsync(client, rejected.ApprovalId, "Reject", profileId);

            var view = await client.GetFromJsonAsync<ApprovalCenterDto>($"/internal/approvals?profileId={profileId}");

            Assert.Empty(view!.Pending);
            Assert.Equal([rejected.ApprovalId, approved.ApprovalId], view.RecentlyDecided.Select(d => d.ApprovalId));
            Assert.Equal(("Rejected", false), (view.RecentlyDecided[0].Status, view.RecentlyDecided[0].ExplicitlyConfirmed));
            Assert.Equal(("Approved", true), (view.RecentlyDecided[1].Status, view.RecentlyDecided[1].ExplicitlyConfirmed));
            Assert.All(view.RecentlyDecided, d => Assert.Equal(profileId, d.DecidedBy));
            Assert.All(view.RecentlyDecided, d => Assert.NotNull(d.DecidedAt));
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-7
    [Fact]
    public async Task Decide_ByAProfileThatDoesNotOwnTheRun_Returns403AndChangesNothing()
    {
        using var client = factory.CreateClient();
        var owner = await CreateProfileAsync();
        var intruder = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(owner, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            var response = await DecideAsync(client, seeded.ApprovalId, "Approve", intruder);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertStillPendingAsync(seeded);
        }
        finally
        {
            await CleanupAsync(owner);
            await CleanupAsync(intruder);
        }
    }

    // covers: AC-8
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("approval_required_demo")]
    [InlineData("Explicit_Confirmation_Demo")]
    public async Task Decide_ApprovingTheExplicitTierWithoutTheExactPhrase_Returns422AndStaysPending(string? confirmation)
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ExplicitTool, ToolRiskTier.ExplicitConfirmation);

        try
        {
            var response = await DecideAsync(client, seeded.ApprovalId, "Approve", profileId, confirmation);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertStillPendingAsync(seeded);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-8, AC-10
    [Fact]
    public async Task Decide_ApprovingTheExplicitTierWithThePhrase_RecordsAnExplicitConfirmation()
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ExplicitTool, ToolRiskTier.ExplicitConfirmation);

        try
        {
            var response = await DecideAsync(client, seeded.ApprovalId, "Approve", profileId, $"  {ExplicitTool} ");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var db = CreateDbContext();
            var approval = await db.Approvals.SingleAsync(a => a.Id == seeded.ApprovalId);
            Assert.Equal(ApprovalStatus.Approved, approval.Status);
            Assert.True(approval.ExplicitlyConfirmed);
            Assert.Equal(profileId, approval.DecidedBy);
            Assert.Equal(AgentRunStatus.Executing, (await db.AgentRuns.SingleAsync(r => r.Id == seeded.RunId)).Status);

            var audit = await db.AuditLogs.SingleAsync(a => a.TargetId == seeded.StepId && a.Action == "ApprovalApproved");
            using var payload = JsonDocument.Parse(audit.Payload!);
            Assert.True(payload.RootElement.GetProperty("explicitlyConfirmed").GetBoolean());
            Assert.Equal("ExplicitConfirmation", payload.RootElement.GetProperty("riskTier").GetString());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-8
    [Fact]
    public async Task Decide_RejectingTheExplicitTier_NeedsNoConfirmation()
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ExplicitTool, ToolRiskTier.ExplicitConfirmation);

        try
        {
            var response = await DecideAsync(client, seeded.ApprovalId, "Reject", profileId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var db = CreateDbContext();
            var approval = await db.Approvals.SingleAsync(a => a.Id == seeded.ApprovalId);
            Assert.Equal(ApprovalStatus.Rejected, approval.Status);
            Assert.False(approval.ExplicitlyConfirmed);
            Assert.Equal(AgentStepStatus.Skipped, (await db.AgentSteps.SingleAsync(s => s.Id == seeded.StepId)).Status);
            Assert.Equal(AgentRunStatus.Failed, (await db.AgentRuns.SingleAsync(r => r.Id == seeded.RunId)).Status);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("")]
    [InlineData("approve")]
    public async Task Decide_WithAnUnknownDecision_Returns400AndStaysPending(string decision)
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            var response = await DecideAsync(client, seeded.ApprovalId, decision, profileId);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertStillPendingAsync(seeded);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-10
    [Theory]
    [InlineData("Approve", "ApprovalApproved")]
    [InlineData("Reject", "ApprovalRejected")]
    public async Task Decide_WritesExactlyOneAuditRowWithTheFullPayload(string decision, string expectedAction)
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            await DecideAsync(client, seeded.ApprovalId, decision, profileId);
            await DecideAsync(client, seeded.ApprovalId, decision, profileId); // a replay must not audit again

            await using var db = CreateDbContext();
            var audit = await db.AuditLogs.SingleAsync(a => a.TargetId == seeded.StepId && a.Action.StartsWith("Approval") && a.Action != "ApprovalRequested");
            Assert.Equal(expectedAction, audit.Action);
            Assert.Equal(profileId.ToString(), audit.Actor);
            Assert.Equal(ApprovalTargets.AgentStep, audit.TargetType);

            using var payload = JsonDocument.Parse(audit.Payload!);
            var root = payload.RootElement;
            Assert.Equal(seeded.ApprovalId, root.GetProperty("approvalId").GetGuid());
            Assert.Equal(seeded.RunId, root.GetProperty("agentRunId").GetGuid());
            Assert.Equal(ApprovalTool, root.GetProperty("toolName").GetString());
            Assert.Equal("ApprovalRequired", root.GetProperty("riskTier").GetString());
            Assert.Equal(decision, root.GetProperty("decision").GetString());
            Assert.False(root.GetProperty("explicitlyConfirmed").GetBoolean());
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    // covers: AC-9
    [Fact]
    public async Task Decide_WhileAnotherTransactionChangesTheRun_Returns409AndLeavesNoPartialState()
    {
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync();
        var seeded = await SeedAwaitingApprovalRunAsync(profileId, ApprovalTool, ToolRiskTier.ApprovalRequired);

        try
        {
            HttpResponseMessage response;
            await using (var lockDb = CreateDbContext())
            {
                // Hold an uncommitted change to the run row: the decide reads the
                // old xmin, then its guarded UPDATE waits for this commit and
                // finds the row changed underneath it.
                await using var transaction = await lockDb.Database.BeginTransactionAsync();
                await lockDb.Database.ExecuteSqlAsync($"""update app.agent_runs set "Goal" = "Goal" where "Id" = {seeded.RunId}""");

                var decide = DecideAsync(client, seeded.ApprovalId, "Approve", profileId);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                await transaction.CommitAsync();
                response = await decide;
            }

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertStillPendingAsync(seeded);
            await using var db = CreateDbContext();
            Assert.False(await db.AuditLogs.AnyAsync(a => a.TargetId == seeded.StepId && a.Action == "ApprovalApproved"));

            // Nothing was left half done: a fresh decision still goes through.
            var retry = await DecideAsync(client, seeded.ApprovalId, "Approve", profileId);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid approvalId, string decision, Guid decidedBy, string? confirmation = null) =>
        client.PostAsJsonAsync($"/internal/agent/approvals/{approvalId}/decide", new DecideApprovalRequest(decision, decidedBy, confirmation));

    private async Task AssertStillPendingAsync(Seeded seeded)
    {
        await using var db = CreateDbContext();
        var approval = await db.Approvals.SingleAsync(a => a.Id == seeded.ApprovalId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Null(approval.DecidedBy);
        Assert.False(approval.ExplicitlyConfirmed);
        Assert.Equal(AgentStepStatus.AwaitingApproval, (await db.AgentSteps.SingleAsync(s => s.Id == seeded.StepId)).Status);
        Assert.Equal(AgentRunStatus.AwaitingApproval, (await db.AgentRuns.SingleAsync(r => r.Id == seeded.RunId)).Status);
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<Guid> CreateProfileAsync()
    {
        await using var db = CreateDbContext();
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Test Founder" };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    // The shape AdvanceRunJob's suspension leaves behind: run and step at
    // AwaitingApproval, one Pending approval at the tool's tier.
    private async Task<Seeded> SeedAwaitingApprovalRunAsync(
        Guid profileId,
        string toolName,
        ToolRiskTier tier,
        DateTimeOffset? requestedAt = null,
        ApprovalEvidence? evidence = null,
        string? argumentsJson = null,
        string goal = "approval required demo")
    {
        await using var db = CreateDbContext();
        var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.AwaitingApproval.ToString() };
        db.WorkflowInstances.Add(workflow);
        var run = new AgentRun { WorkflowInstanceId = workflow.Id, ProfileId = profileId, Goal = goal };
        db.AgentRuns.Add(run);
        var step = new AgentStep { AgentRunId = run.Id, Ordinal = 0, ToolName = toolName, ArgumentsJson = argumentsJson };
        db.AgentSteps.Add(step);
        await db.SaveChangesAsync();

        run.TransitionTo(AgentRunStatus.PolicyCheck);
        run.TransitionTo(AgentRunStatus.Executing);
        run.TransitionTo(AgentRunStatus.AwaitingApproval);
        step.TransitionTo(AgentStepStatus.AwaitingApproval);

        var approval = new Approval
        {
            TargetType = ApprovalTargets.AgentStep,
            TargetId = step.Id,
            RiskTier = tier.ToString(),
            RequestedAt = requestedAt ?? DateTimeOffset.UtcNow,
            EvidenceJson = evidence is null ? null : JsonSerializer.Serialize(evidence, JsonSerializerOptions.Web),
        };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();

        return new Seeded(run.Id, step.Id, approval.Id);
    }

    private async Task CleanupAsync(Guid profileId)
    {
        await using var db = CreateDbContext();
        var runIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.Id).ToListAsync();
        var stepIds = await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).Select(s => s.Id).ToListAsync();
        var workflowIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.WorkflowInstanceId).ToListAsync();

        // AuditLog is append only in the product, but these rows are test fixtures, so they are hard deleted here.
        await db.AuditLogs.IgnoreQueryFilters().Where(a => stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.ToolCalls.Where(t => stepIds.Contains(t.AgentStepId)).ExecuteDeleteAsync();
        await db.Approvals.Where(a => a.TargetType == ApprovalTargets.AgentStep && stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).ExecuteDeleteAsync();
        await db.AgentRuns.Where(r => r.ProfileId == profileId).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(w => workflowIds.Contains(w.Id)).ExecuteDeleteAsync();
        await db.Profiles.Where(p => p.Id == profileId).ExecuteDeleteAsync();
    }

    private sealed record Seeded(Guid RunId, Guid StepId, Guid ApprovalId);
}
