using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Agent;

namespace WorkPilot.Api.Tests;

// Integration tests for PlanRunJob's policy check (spec 0005, AC-2), against
// the real Postgres stack. The live app's FakeChatClient only ever plans
// registered, correctly argued tools, so the rejection path can't be reached
// through HTTP: these drive PlanRunJob directly with a fixed stub IPlanner.
// Needs a reachable Postgres (see WORKPILOTDB_CONNECTION, supabase/.env).
[Collection("Api")]
public class PlanRunJobTests(SharedApiFactory factory)
{
    private static readonly ITool[] Tools = [new StubTool("needs_query", requiredArguments: ["query"])];

    [Fact]
    public async Task RunAsync_WhenThePlanNamesAnUnknownTool_FailsTheRunWithoutPersistingAnyStep()
    {
        // covers AC-2: a step naming a tool that isn't in the Tool Registry fails the whole run up front.
        var plan = new AgentPlan([
            new PlannedToolCall("needs_query", new Dictionary<string, string> { ["query"] = "ok" }),
            new PlannedToolCall("does_not_exist", new Dictionary<string, string>()),
        ]);

        var (runStatus, stepCount) = await PlanAsync(plan);

        Assert.Equal(AgentRunStatus.Failed, runStatus);
        Assert.Equal(0, stepCount); // not even the valid first step: no partial execution of an invalid plan
    }

    [Fact]
    public async Task RunAsync_WhenAStepIsMissingARequiredArgument_FailsTheRunWithoutPersistingAnyStep()
    {
        // covers AC-2: arguments must satisfy the tool's declared schema.
        var plan = new AgentPlan([new PlannedToolCall("needs_query", new Dictionary<string, string>())]);

        var (runStatus, stepCount) = await PlanAsync(plan);

        Assert.Equal(AgentRunStatus.Failed, runStatus);
        Assert.Equal(0, stepCount);
    }

    [Fact]
    public async Task RunAsync_WhenEveryStepPassesPolicy_PersistsTheStepsInOrderAndStartsExecuting()
    {
        // covers AC-1, AC-2: a permitted plan is persisted as ordered AgentStep rows.
        var plan = new AgentPlan([
            new PlannedToolCall("needs_query", new Dictionary<string, string> { ["query"] = "first" }),
            new PlannedToolCall("needs_query", new Dictionary<string, string> { ["query"] = "second" }),
        ]);

        var (runStatus, stepCount) = await PlanAsync(plan);

        Assert.Equal(AgentRunStatus.Executing, runStatus);
        Assert.Equal(2, stepCount);
    }

    private async Task<(AgentRunStatus RunStatus, int StepCount)> PlanAsync(AgentPlan plan)
    {
        await using var db = CreateDbContext();
        var (runId, profileId) = await SeedPlanningRunAsync(db);

        try
        {
            var jobs = factory.Services.CreateScope().ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var job = new PlanRunJob(db, new FixedPlanner(plan), new ToolRegistry(Tools), new PolicyEngine(), jobs);

            await job.RunAsync(runId);

            await using var verifyDb = CreateDbContext();
            var run = await verifyDb.AgentRuns.SingleAsync(r => r.Id == runId);
            var steps = await verifyDb.AgentSteps.Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal(Enumerable.Range(0, steps.Count), steps.Select(s => s.Ordinal));
            return (run.Status, steps.Count);
        }
        finally
        {
            await CleanupAsync(profileId);
        }
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private static async Task<(Guid RunId, Guid ProfileId)> SeedPlanningRunAsync(WorkPilotDbContext db)
    {
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Test Founder" };
        db.Profiles.Add(profile);

        var workflow = new WorkflowInstance { DefinitionName = "AgentRun", Status = AgentRunStatus.Planning.ToString() };
        db.WorkflowInstances.Add(workflow);

        var run = new AgentRun { WorkflowInstanceId = workflow.Id, ProfileId = profile.Id, Goal = "test" };
        db.AgentRuns.Add(run);

        await db.SaveChangesAsync();
        return (run.Id, profile.Id);
    }

    private async Task CleanupAsync(Guid profileId)
    {
        await using var db = CreateDbContext();
        var runIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.Id).ToListAsync();
        var workflowIds = await db.AgentRuns.Where(r => r.ProfileId == profileId).Select(r => r.WorkflowInstanceId).ToListAsync();

        await db.AgentSteps.Where(s => runIds.Contains(s.AgentRunId)).ExecuteDeleteAsync();
        await db.AgentRuns.Where(r => r.ProfileId == profileId).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(w => workflowIds.Contains(w.Id)).ExecuteDeleteAsync();
        await db.Profiles.Where(p => p.Id == profileId).ExecuteDeleteAsync();
    }

    // Stands in for the model: returns the same plan every time, so a test
    // controls exactly what the Policy Engine is asked to validate.
    private sealed class FixedPlanner(AgentPlan plan) : IPlanner
    {
        public Task<AgentPlan> PlanAsync(string goal, IReadOnlyList<ToolDescriptor> availableTools, CancellationToken cancellationToken) =>
            Task.FromResult(plan);
    }

    // A registered tool with one required argument; never executed here,
    // only validated against.
    private sealed class StubTool(string name, IReadOnlyList<string> requiredArguments) : ITool
    {
        public string Name { get; } = name;
        public string Description => "test tool";
        public IReadOnlyList<string> RequiredArguments { get; } = requiredArguments;
        public IReadOnlyList<string> ExpectedOutputFields => [];
        public ToolRiskTier RiskTier => ToolRiskTier.AutoAllowed;
        public bool IsIdempotent => true;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public int MaxRetries => 0;
        public string? TargetType => null;

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolExecutionResult.Ok());
    }
}
