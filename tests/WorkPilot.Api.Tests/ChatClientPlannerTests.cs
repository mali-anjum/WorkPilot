using Microsoft.Extensions.AI;
using WorkPilot.AI.Agent;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.Api.Tests;

// Unit tests for ChatClientPlanner's retry-then-fail behavior (spec 0005,
// AC-7). Pure unit tests, no Postgres needed: a scripted IChatClient stands
// in for a real model, so these cover the parse-failure branch that spec
// 0005's live app (wired to the always-valid FakeChatClient) can't exercise
// through /check verify.
public class ChatClientPlannerTests
{
    private static readonly ToolDescriptor[] Tools = [new("list_my_profile", "Lists the profile.", [])];

    private static readonly ResolvedAiPurpose Target = new(AiPurposes.Planner, "stub", "stub-model");

    [Fact]
    public async Task PlanAsync_WithAValidResponseFirstTry_ReturnsThePlanWithoutRetrying()
    {
        var chat = new ScriptedChatClient("""{"steps":[{"tool":"list_my_profile","arguments":{}}]}""");
        var planner = new ChatClientPlanner(chat, Target);

        var plan = await planner.PlanAsync("list my profile", Tools, CancellationToken.None);

        Assert.Single(plan.Steps);
        Assert.Equal("list_my_profile", plan.Steps[0].Tool);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task PlanAsync_WithInvalidJsonThenAValidResponse_RetriesOnceAndSucceeds()
    {
        var chat = new ScriptedChatClient("not json", """{"steps":[{"tool":"list_my_profile","arguments":{}}]}""");
        var planner = new ChatClientPlanner(chat, Target);

        var plan = await planner.PlanAsync("list my profile", Tools, CancellationToken.None);

        Assert.Single(plan.Steps);
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task PlanAsync_WithInvalidJsonTwice_ThrowsPlanParseExceptionAfterOneRetry()
    {
        var chat = new ScriptedChatClient("not json", "still not json");
        var planner = new ChatClientPlanner(chat, Target);

        await Assert.ThrowsAsync<PlanParseException>(() => planner.PlanAsync("list my profile", Tools, CancellationToken.None));
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task PlanAsync_WithAnEmptyStepsArrayTwice_ThrowsPlanParseException()
    {
        // An empty plan is a parse failure under AC-7, not a valid zero-step run (AC-2).
        var chat = new ScriptedChatClient("""{"steps":[]}""", """{"steps":[]}""");
        var planner = new ChatClientPlanner(chat, Target);

        await Assert.ThrowsAsync<PlanParseException>(() => planner.PlanAsync("list my profile", Tools, CancellationToken.None));
    }

    [Theory]
    [InlineData("```json\n{\"steps\":[{\"tool\":\"list_my_profile\",\"arguments\":{}}]}\n```")]
    [InlineData("```\n{\"steps\":[{\"tool\":\"list_my_profile\",\"arguments\":{}}]}\n```")]
    [InlineData("  ```json\n{\"steps\":[{\"tool\":\"list_my_profile\",\"arguments\":{}}]}\n```  \n")]
    [InlineData("```json {\"steps\":[{\"tool\":\"list_my_profile\",\"arguments\":{}}]}```")]
    [InlineData("```{\"steps\":[{\"tool\":\"list_my_profile\",\"arguments\":{}}]}```")]
    public async Task PlanAsync_WithAPlanWrappedInACodeFence_ParsesItFirstTry(string reply)
    {
        // covers spec 0006 AC-9: real models fence JSON even in JSON mode.
        var chat = new ScriptedChatClient(reply);
        var planner = new ChatClientPlanner(chat, Target);

        var plan = await planner.PlanAsync("list my profile", Tools, CancellationToken.None);

        Assert.Equal("list_my_profile", Assert.Single(plan.Steps).Tool);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task PlanAsync_WhenItGivesUp_NamesThePurposeProviderAndModel_AndNotTheGoal()
    {
        // covers spec 0006 AC-6: the unparseable_plan audit needs these, and must not carry prompt text.
        var chat = new ScriptedChatClient("not json");
        var planner = new ChatClientPlanner(chat, Target);

        var ex = await Assert.ThrowsAsync<PlanParseException>(() => planner.PlanAsync("SECRET-GOAL-TEXT", Tools, CancellationToken.None));

        Assert.Equal(AiPurposes.Planner, ex.Purpose);
        Assert.Equal("stub", ex.Provider);
        Assert.Equal("stub-model", ex.Model);
        Assert.DoesNotContain("SECRET-GOAL-TEXT", ex.Message);
    }

    // A minimal IChatClient stand in that replays a fixed script of raw
    // response bodies, one per call, so a test can pin exactly what the
    // "model" said on each attempt.
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private int _index;

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var text = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
