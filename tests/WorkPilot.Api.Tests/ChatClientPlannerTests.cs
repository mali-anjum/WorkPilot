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

    // covers: spec 0005 AC-7 (the retry tells the model what was wrong with its first answer)
    [Fact]
    public async Task PlanAsync_OnRetry_FeedsTheParseErrorBackToTheModel()
    {
        var chat = new ScriptedChatClient("not json", """{"steps":[{"tool":"list_my_profile","arguments":{}}]}""");
        var planner = new ChatClientPlanner(chat, Target);

        await planner.PlanAsync("list my profile", Tools, CancellationToken.None);

        Assert.DoesNotContain("didn't parse", chat.Prompts[0]);
        Assert.Contains("didn't parse", chat.Prompts[1]);
    }

    // covers: spec 0005 AC-1 (a plan is capped at 10 steps)
    [Fact]
    public async Task PlanAsync_WithMoreThanTenSteps_RetriesAndAcceptsAPlanOfTen()
    {
        var chat = new ScriptedChatClient(PlanOf(11), PlanOf(10));
        var planner = new ChatClientPlanner(chat, Target);

        var plan = await planner.PlanAsync("list my profile", Tools, CancellationToken.None);

        Assert.Equal(10, plan.Steps.Count);
        Assert.Equal(2, chat.CallCount);
    }

    // covers: spec 0005 AC-1, AC-7
    [Fact]
    public async Task PlanAsync_WithMoreThanTenStepsTwice_ThrowsPlanParseException()
    {
        var chat = new ScriptedChatClient(PlanOf(11), PlanOf(11));
        var planner = new ChatClientPlanner(chat, Target);

        await Assert.ThrowsAsync<PlanParseException>(() => planner.PlanAsync("list my profile", Tools, CancellationToken.None));
    }

    private static string PlanOf(int steps) =>
        $$"""{"steps":[{{string.Join(",", Enumerable.Repeat("""{"tool":"list_my_profile","arguments":{}}""", steps))}}]}""";

    // A minimal IChatClient stand in that replays a fixed script of raw
    // response bodies, one per call, so a test can pin exactly what the
    // "model" said on each attempt.
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private int _index;

        public int CallCount { get; private set; }

        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
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
