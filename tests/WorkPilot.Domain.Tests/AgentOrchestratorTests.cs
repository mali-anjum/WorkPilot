using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Approvals;

namespace WorkPilot.Domain.Tests;

// Covers the AgentRun/AgentStep/Approval state machines (spec 0005, Feature
// design > State transitions), enforced in the entities themselves, no
// database involved.
public class AgentOrchestratorTests
{
    private static AgentRun NewRun() => new()
    {
        WorkflowInstanceId = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        Goal = "list my profile",
    };

    private static AgentStep NewStep() => new()
    {
        AgentRunId = Guid.NewGuid(),
        Ordinal = 0,
        ToolName = "list_my_profile",
    };

    private static Approval NewApproval() => new()
    {
        TargetType = ApprovalTargets.AgentStep,
        TargetId = Guid.NewGuid(),
        RiskTier = ToolRiskTier.ApprovalRequired.ToString(),
    };

    [Fact]
    public void NewRun_StartsAsPlanning()
    {
        Assert.Equal(AgentRunStatus.Planning, NewRun().Status);
    }

    [Theory]
    [InlineData(AgentRunStatus.Planning, AgentRunStatus.PolicyCheck)]
    [InlineData(AgentRunStatus.Planning, AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.PolicyCheck, AgentRunStatus.Executing)]
    [InlineData(AgentRunStatus.Executing, AgentRunStatus.AwaitingApproval)]
    [InlineData(AgentRunStatus.Executing, AgentRunStatus.Completed)]
    [InlineData(AgentRunStatus.Executing, AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.AwaitingApproval, AgentRunStatus.Executing)]
    [InlineData(AgentRunStatus.AwaitingApproval, AgentRunStatus.Failed)]
    public void Run_TransitionTo_AllowsEachValidStep(AgentRunStatus from, AgentRunStatus to)
    {
        var run = DriveRunTo(from);

        run.TransitionTo(to);

        Assert.Equal(to, run.Status);
    }

    [Theory]
    [InlineData(AgentRunStatus.Planning, AgentRunStatus.Executing)] // skips PolicyCheck
    [InlineData(AgentRunStatus.Planning, AgentRunStatus.Completed)]
    [InlineData(AgentRunStatus.Completed, AgentRunStatus.Executing)] // terminal
    [InlineData(AgentRunStatus.Failed, AgentRunStatus.Executing)] // terminal
    public void Run_TransitionTo_RejectsInvalidTransitions(AgentRunStatus from, AgentRunStatus to)
    {
        var run = DriveRunTo(from);

        var ex = Assert.Throws<InvalidOperationException>(() => run.TransitionTo(to));

        Assert.Contains(from.ToString(), ex.Message);
        Assert.Equal(from, run.Status); // rejected transition never mutates state
    }

    [Fact]
    public void NewStep_StartsAsPending()
    {
        Assert.Equal(AgentStepStatus.Pending, NewStep().Status);
    }

    [Theory]
    [InlineData(AgentStepStatus.Pending, AgentStepStatus.Running)]
    [InlineData(AgentStepStatus.Pending, AgentStepStatus.AwaitingApproval)]
    [InlineData(AgentStepStatus.AwaitingApproval, AgentStepStatus.Running)] // approve
    [InlineData(AgentStepStatus.AwaitingApproval, AgentStepStatus.Skipped)] // reject
    [InlineData(AgentStepStatus.Running, AgentStepStatus.Succeeded)]
    [InlineData(AgentStepStatus.Running, AgentStepStatus.Failed)]
    public void Step_TransitionTo_AllowsEachValidStep(AgentStepStatus from, AgentStepStatus to)
    {
        var step = DriveStepTo(from);

        step.TransitionTo(to);

        Assert.Equal(to, step.Status);
    }

    [Theory]
    [InlineData(AgentStepStatus.Pending, AgentStepStatus.Succeeded)]
    [InlineData(AgentStepStatus.Succeeded, AgentStepStatus.Running)]
    [InlineData(AgentStepStatus.Skipped, AgentStepStatus.Running)]
    public void Step_TransitionTo_RejectsInvalidTransitions(AgentStepStatus from, AgentStepStatus to)
    {
        var step = DriveStepTo(from);

        var ex = Assert.Throws<InvalidOperationException>(() => step.TransitionTo(to));

        Assert.Contains(from.ToString(), ex.Message);
        Assert.Equal(from, step.Status);
    }

    [Fact]
    public void Approval_Decide_RecordsTheDecisionOnce()
    {
        var approval = NewApproval();
        var decidedBy = Guid.NewGuid();
        var decidedAt = DateTimeOffset.UtcNow;

        approval.Decide(ApprovalStatus.Approved, decidedBy, decidedAt);

        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(decidedBy, approval.DecidedBy);
        Assert.Equal(decidedAt, approval.DecidedAt);
    }

    [Fact]
    public void Approval_Decide_ThrowsOnASecondDecision()
    {
        var approval = NewApproval();
        approval.Decide(ApprovalStatus.Rejected, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => approval.Decide(ApprovalStatus.Approved, Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalStatus.Rejected, approval.Status); // the first decision stands
    }

    private static readonly Dictionary<AgentRunStatus, AgentRunStatus[]> RunPathTo = new()
    {
        [AgentRunStatus.Planning] = [],
        [AgentRunStatus.PolicyCheck] = [AgentRunStatus.PolicyCheck],
        [AgentRunStatus.Executing] = [AgentRunStatus.PolicyCheck, AgentRunStatus.Executing],
        [AgentRunStatus.AwaitingApproval] = [AgentRunStatus.PolicyCheck, AgentRunStatus.Executing, AgentRunStatus.AwaitingApproval],
        [AgentRunStatus.Completed] = [AgentRunStatus.PolicyCheck, AgentRunStatus.Executing, AgentRunStatus.Completed],
        [AgentRunStatus.Failed] = [AgentRunStatus.Failed],
    };

    private static AgentRun DriveRunTo(AgentRunStatus target)
    {
        var run = NewRun();
        foreach (var step in RunPathTo[target])
        {
            run.TransitionTo(step);
        }

        return run;
    }

    private static readonly Dictionary<AgentStepStatus, AgentStepStatus[]> StepPathTo = new()
    {
        [AgentStepStatus.Pending] = [],
        [AgentStepStatus.AwaitingApproval] = [AgentStepStatus.AwaitingApproval],
        [AgentStepStatus.Running] = [AgentStepStatus.Running],
        [AgentStepStatus.Succeeded] = [AgentStepStatus.Running, AgentStepStatus.Succeeded],
        [AgentStepStatus.Failed] = [AgentStepStatus.Running, AgentStepStatus.Failed],
        [AgentStepStatus.Skipped] = [AgentStepStatus.AwaitingApproval, AgentStepStatus.Skipped],
    };

    private static AgentStep DriveStepTo(AgentStepStatus target)
    {
        var step = NewStep();
        foreach (var status in StepPathTo[target])
        {
            step.TransitionTo(status);
        }

        return step;
    }
}
