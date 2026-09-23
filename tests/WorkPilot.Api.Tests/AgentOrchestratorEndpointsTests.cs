using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// Integration tests for the Agent orchestrator core's internal endpoints
// (spec 0005): POST /internal/agent/runs, GET /internal/agent/runs/{id}, and
// POST /internal/agent/approvals/{id}/decide. Hangfire's own worker server is
// disabled here (see CreateFactory), so these exercise the synchronous parts
// of each endpoint directly rather than a full run to completion; the live
// end to end pipeline (trigger -> Planning -> Executing -> Completed, and the
// approval suspend/resume path) was proven manually against the real stack
// during /develop (see the feature's scope entry). Needs a reachable
// Postgres; set WORKPILOTDB_CONNECTION (see supabase/.env).
public class AgentOrchestratorEndpointsTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("WORKPILOTDB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set WORKPILOTDB_CONNECTION to a reachable Postgres connection string before running these tests " +
                "(see supabase/.env for the local self hosted Supabase stack's credentials).");

        Environment.SetEnvironmentVariable("ConnectionStrings__workpilotdb", connectionString);
        Environment.SetEnvironmentVariable("Hangfire__DisableServer", "true");

        return new WebApplicationFactory<Program>();
    }

    private static async Task<Guid> CreateProfileAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Test Founder" };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    [Fact]
    public async Task TriggerRun_WithAKnownProfile_CreatesAPlanningRunAndReturns202()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync(factory);

        try
        {
            var response = await client.PostAsJsonAsync("/internal/agent/runs", new { Goal = "list my profile", ProfileId = profileId });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<TriggerBody>();
            Assert.NotNull(body);
            Assert.Equal("Planning", body!.Status);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            var run = await db.AgentRuns.SingleAsync(r => r.Id == body.AgentRunId);
            Assert.Equal(profileId, run.ProfileId);
            Assert.Equal("list my profile", run.Goal);
            Assert.Equal(AgentRunStatus.Planning, run.Status);

            var workflow = await db.WorkflowInstances.SingleAsync(w => w.Id == run.WorkflowInstanceId);
            Assert.Equal("Planning", workflow.Status);
        }
        finally
        {
            await CleanupAsync(factory, profileId);
        }
    }

    [Fact]
    public async Task TriggerRun_WithAnEmptyGoal_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync(factory);

        try
        {
            var response = await client.PostAsJsonAsync("/internal/agent/runs", new { Goal = "", ProfileId = profileId });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await CleanupAsync(factory, profileId);
        }
    }

    [Fact]
    public async Task TriggerRun_WithAnUnknownProfile_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/agent/runs", new { Goal = "list my profile", ProfileId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRun_ForAnUnknownId_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/agent/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DecideApproval_Approve_ResumesTheStepAndRunSynchronously()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync(factory);
        var (runId, stepId, approvalId) = await SeedAwaitingApprovalRunAsync(factory, profileId);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/internal/agent/approvals/{approvalId}/decide",
                new { Decision = "Approve", DecidedBy = profileId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<DecideBody>();
            Assert.NotNull(body);
            Assert.Equal("Approved", body!.Status);
            Assert.True(body.Resumed);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            Assert.Equal(AgentStepStatus.Running, (await db.AgentSteps.SingleAsync(s => s.Id == stepId)).Status);
            Assert.Equal(AgentRunStatus.Executing, (await db.AgentRuns.SingleAsync(r => r.Id == runId)).Status);
        }
        finally
        {
            await CleanupAsync(factory, profileId);
        }
    }

    [Fact]
    public async Task DecideApproval_Reject_FailsTheStepAndRun()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync(factory);
        var (runId, stepId, approvalId) = await SeedAwaitingApprovalRunAsync(factory, profileId);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/internal/agent/approvals/{approvalId}/decide",
                new { Decision = "Reject", DecidedBy = profileId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<DecideBody>();
            Assert.False(body!.Resumed);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            Assert.Equal(AgentStepStatus.Skipped, (await db.AgentSteps.SingleAsync(s => s.Id == stepId)).Status);
            Assert.Equal(AgentRunStatus.Failed, (await db.AgentRuns.SingleAsync(r => r.Id == runId)).Status);
        }
        finally
        {
            await CleanupAsync(factory, profileId);
        }
    }

    [Fact]
    public async Task DecideApproval_DecidedTwice_ReturnsConflictOnTheSecondCall()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var profileId = await CreateProfileAsync(factory);
        var (_, _, approvalId) = await SeedAwaitingApprovalRunAsync(factory, profileId);

        try
        {
            var first = await client.PostAsJsonAsync($"/internal/agent/approvals/{approvalId}/decide", new { Decision = "Approve", DecidedBy = profileId });
            var second = await client.PostAsJsonAsync($"/internal/agent/approvals/{approvalId}/decide", new { Decision = "Approve", DecidedBy = profileId });

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }
        finally
        {
            await CleanupAsync(factory, profileId);
        }
    }

    [Fact]
    public async Task DecideApproval_ForAnUnknownApproval_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/internal/agent/approvals/{Guid.NewGuid()}/decide",
            new { Decision = "Approve", DecidedBy = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Seeds a run already suspended at AwaitingApproval (the shape AdvanceRunJob
    // leaves behind), directly via EF Core: Hangfire's worker is disabled in
    // this test host, so the job that would normally produce this state never
    // runs (the pipeline itself was proven live, see class remarks above).
    private static async Task<(Guid RunId, Guid StepId, Guid ApprovalId)> SeedAwaitingApprovalRunAsync(WebApplicationFactory<Program> factory, Guid profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();

        var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.AwaitingApproval.ToString() };
        db.WorkflowInstances.Add(workflow);

        var run = new AgentRun { WorkflowInstanceId = workflow.Id, ProfileId = profileId, Goal = "approval required demo" };
        db.AgentRuns.Add(run);

        var step = new AgentStep { AgentRunId = run.Id, Ordinal = 0, ToolName = "approval_required_demo" };
        db.AgentSteps.Add(step);

        await db.SaveChangesAsync(); // Ids assigned before the transitions below

        run.TransitionTo(AgentRunStatus.PolicyCheck);
        run.TransitionTo(AgentRunStatus.Executing);
        run.TransitionTo(AgentRunStatus.AwaitingApproval);
        step.TransitionTo(AgentStepStatus.AwaitingApproval);

        var approval = new Approval { TargetType = ApprovalTargets.AgentStep, TargetId = step.Id, RiskTier = ToolRiskTier.ApprovalRequired.ToString() };
        db.Approvals.Add(approval);

        await db.SaveChangesAsync();

        return (run.Id, step.Id, approval.Id);
    }

    private static async Task CleanupAsync(WebApplicationFactory<Program> factory, Guid profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();

        var runIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.Id).ToListAsync();
        var stepIds = await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).Select(s => s.Id).ToListAsync();
        var workflowIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.WorkflowInstanceId).ToListAsync();

        await db.ToolCalls.Where(t => stepIds.Contains(t.AgentStepId)).ExecuteDeleteAsync();
        await db.Approvals.Where(a => a.TargetType == ApprovalTargets.AgentStep && stepIds.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).ExecuteDeleteAsync();
        await db.AgentRuns.Where(r => r.ProfileId == profileId).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(w => workflowIds.Contains(w.Id)).ExecuteDeleteAsync();
        await db.Profiles.Where(p => p.Id == profileId).ExecuteDeleteAsync();
    }

    private sealed record TriggerBody(Guid AgentRunId, string Status);

    private sealed record DecideBody(Guid ApprovalId, string Status, bool Resumed);
}
