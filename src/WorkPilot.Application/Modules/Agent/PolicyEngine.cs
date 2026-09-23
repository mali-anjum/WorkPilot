namespace WorkPilot.Application.Modules.Agent;

/// <summary>One reason a planned step is not permitted (spec 0005, AC-2).</summary>
public sealed record PolicyViolation(int StepOrdinal, string Reason);

/// <summary>Validates a whole plan before any step executes: no partial execution of an invalid plan.</summary>
public interface IPolicyEngine
{
    IReadOnlyList<PolicyViolation> Validate(AgentPlan plan, IToolRegistry registry);
}

public sealed class PolicyEngine : IPolicyEngine
{
    public IReadOnlyList<PolicyViolation> Validate(AgentPlan plan, IToolRegistry registry)
    {
        var violations = new List<PolicyViolation>();

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];

            if (!registry.TryGet(step.Tool, out var tool))
            {
                violations.Add(new PolicyViolation(i, $"Unknown tool \"{step.Tool}\"."));
                continue;
            }

            foreach (var required in tool.RequiredArguments)
            {
                if (!step.Arguments.ContainsKey(required))
                {
                    violations.Add(new PolicyViolation(i, $"Tool \"{step.Tool}\" is missing its required argument \"{required}\"."));
                }
            }
        }

        return violations;
    }
}
