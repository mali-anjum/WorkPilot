using WorkPilot.Application.Modules.Agent;
using WorkPilot.Domain.Modules.Agent;

namespace WorkPilot.Api.Tests;

// The Verification Engine's structural check (spec 0005): a tool call only
// counts as a success when the tool said so AND its output carries every field
// the tool declares. Pure unit tests, no database.
public class VerificationEngineTests
{
    private readonly VerificationEngine _engine = new();

    // covers: spec 0005 AC-3
    [Fact]
    public void A_tool_that_reported_failure_never_verifies()
    {
        Assert.False(_engine.Verify(new DeclaredTool([]), ToolExecutionResult.Fail("boom")));
    }

    // covers: spec 0005 AC-3
    [Fact]
    public void A_tool_that_declares_no_output_verifies_on_success_alone()
    {
        Assert.True(_engine.Verify(new DeclaredTool([]), ToolExecutionResult.Ok()));
    }

    // covers: spec 0005 AC-3
    [Fact]
    public void Output_with_every_declared_field_verifies()
    {
        Assert.True(_engine.Verify(new DeclaredTool(["name", "email"]), ToolExecutionResult.Ok("""{"name":"A","email":"a@b.c","extra":1}""")));
    }

    // covers: spec 0005 AC-3
    [Theory]
    [InlineData("""{"name":"A"}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void Output_missing_a_declared_field_or_not_json_fails(string? output)
    {
        Assert.False(_engine.Verify(new DeclaredTool(["name", "email"]), ToolExecutionResult.Ok(output)));
    }

    private sealed class DeclaredTool(IReadOnlyList<string> expectedOutputFields) : ITool
    {
        public string Name => "declared";
        public string Description => "test tool";
        public IReadOnlyList<string> RequiredArguments => [];
        public IReadOnlyList<string> ExpectedOutputFields { get; } = expectedOutputFields;
        public ToolRiskTier RiskTier => ToolRiskTier.AutoAllowed;
        public bool IsIdempotent => true;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public int MaxRetries => 0;
        public string? TargetType => null;

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
