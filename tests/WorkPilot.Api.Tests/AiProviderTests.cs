using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.Api.Tests;

// Spec 0006: the real AddWorkPilotAi registration and the real OpenAI adapter
// driven against StubOpenAiServer (no Postgres, no network beyond loopback,
// no paid calls). Live providers are proven in /check verify, not here.
public class AiProviderTests
{
    private const string ValidPlan = """{"steps":[{"tool":"list_my_profile","arguments":{}}]}""";

    private static readonly ToolDescriptor[] Tools = [new("list_my_profile", "Lists the profile.", [])];

    [Fact]
    public async Task Planner_UsesThePlannerPurposesProviderModelAndKey()
    {
        // covers AC-1, AC-2: the Planner purpose routes to its own provider, model, and key.
        await using var a = await StubOpenAiServer.StartAsync(ValidPlan);
        await using var b = await StubOpenAiServer.StartAsync(ValidPlan);
        using var services = AiTestServices.Build(new Dictionary<string, string?>
        {
            ["Ai:Providers:a:Endpoint"] = a.Endpoint,
            ["Ai:Providers:a:ApiKey"] = "sk-test-a",
            ["Ai:Providers:b:Endpoint"] = b.Endpoint,
            ["Ai:Providers:b:ApiKey"] = "sk-test-b",
            ["Ai:Purposes:Default:Provider"] = "a",
            ["Ai:Purposes:Default:Model"] = "m1",
            ["Ai:Purposes:Planner:Provider"] = "b",
            ["Ai:Purposes:Planner:Model"] = "m2",
        });

        var plan = await PlanAsync(services);

        Assert.Equal("list_my_profile", Assert.Single(plan.Steps).Tool);
        Assert.Empty(a.Requests);
        var request = Assert.Single(b.Requests);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("m2", request.Model);
        Assert.Equal("Bearer sk-test-b", request.Authorization);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task Planner_SwitchesProviderOnConfigChangeAlone(string active)
    {
        // covers AC-1: the same code, two configs, two different providers.
        await using var first = await StubOpenAiServer.StartAsync(ValidPlan);
        await using var second = await StubOpenAiServer.StartAsync(ValidPlan);
        var config = new Dictionary<string, string?>
        {
            ["Ai:Providers:first:Endpoint"] = first.Endpoint,
            ["Ai:Providers:first:ApiKey"] = "sk-test-first",
            ["Ai:Providers:second:Endpoint"] = second.Endpoint,
            ["Ai:Providers:second:ApiKey"] = "sk-test-second",
            ["Ai:Purposes:Default:Provider"] = active,
            ["Ai:Purposes:Default:Model"] = $"{active}-model",
        };
        using var services = AiTestServices.Build(config);

        await PlanAsync(services);

        var (hit, missed) = active == "first" ? (first, second) : (second, first);
        Assert.Equal($"{active}-model", Assert.Single(hit.Requests).Model);
        Assert.Empty(missed.Requests);
    }

    [Fact]
    public async Task Planner_WithNoPlannerMapping_FallsBackToDefault()
    {
        // covers AC-1: an unmapped purpose uses Default.
        await using var stub = await StubOpenAiServer.StartAsync(ValidPlan);
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("deepseek", stub.Endpoint, "deepseek-chat"));

        await PlanAsync(services);

        Assert.Equal("deepseek-chat", Assert.Single(stub.Requests).Model);
        var target = services.GetRequiredKeyedService<ResolvedAiPurpose>(AiPurposes.Planner);
        Assert.Equal(new ResolvedAiPurpose(AiPurposes.Planner, "deepseek", "deepseek-chat"), target);
    }

    [Fact]
    public async Task KeylessProvider_CallsWithoutAConfiguredKey()
    {
        // covers AC-2: an Ollama style provider (RequiresApiKey false) needs no key.
        await using var stub = await StubOpenAiServer.StartAsync(ValidPlan);
        var config = AiTestServices.SingleProvider("ollama", stub.Endpoint, "qwen2.5:7b", apiKey: null);
        config["Ai:Providers:ollama:RequiresApiKey"] = "false";
        using var services = AiTestServices.Build(config);

        await PlanAsync(services);

        Assert.Equal("qwen2.5:7b", Assert.Single(stub.Requests).Model);
    }

    [Fact]
    public async Task FakeProvider_PlansWithNoNetworkAndNoKey()
    {
        // covers AC-3: Provider "Fake" is the deterministic FakeChatClient.
        using var services = AiTestServices.Build(new Dictionary<string, string?> { ["Ai:Purposes:Default:Provider"] = "Fake" });

        var plan = await PlanAsync(services);

        Assert.Equal("list_my_profile", Assert.Single(plan.Steps).Tool);
        Assert.True(services.GetRequiredKeyedService<ResolvedAiPurpose>(AiPurposes.Planner).IsFake);
    }

    [Fact]
    public async Task TransientErrors_AreRetriedHonoringRetryAfter_ThenSucceed()
    {
        // covers AC-5: 429 twice, then success, within the 2 extra attempts.
        await using var stub = await StubOpenAiServer.StartAsync(
            [new StubReply(429, RetryAfter: "0"), new StubReply(429, RetryAfter: "0")],
            new StubReply(Content: ValidPlan));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", stub.Endpoint, "gpt-4o-mini"));

        var plan = await PlanAsync(services);

        Assert.Single(plan.Steps);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task PersistentServerErrors_GiveUpAfterTwoRetries_AsAiProviderException()
    {
        // covers AC-5, AC-6: 3 attempts total, then a provider failure naming purpose, provider, and model.
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(500, RetryAfter: "0"));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("deepseek", stub.Endpoint, "deepseek-chat"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(AiPurposes.Planner, ex.Purpose);
        Assert.Equal("deepseek", ex.Provider);
        Assert.Equal("deepseek-chat", ex.Model);
    }

    [Fact]
    public async Task SlowProvider_IsCutOffByTimeoutSeconds_AsAiProviderException()
    {
        // covers AC-5: each attempt stops after TimeoutSeconds; the final failure is a provider error, not a hang.
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(Content: ValidPlan, Delay: TimeSpan.FromSeconds(30)));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("gemini", stub.Endpoint, "gemini-2.5-flash", timeoutSeconds: 1));

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.Equal("gemini", ex.Provider);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ProviderError_NeverCarriesTheApiKey()
    {
        // covers AC-6 and the key invariant: providers echo keys back in 401 bodies.
        const string key = "sk-test-SUPERSECRET-1234567890";
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(401,
            ErrorBody: $$$"""{"error":{"message":"Incorrect API key provided: {{{key}}} and sk-proj-****7890","type":"invalid_request_error"}}"""));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", stub.Endpoint, "gpt-4o-mini", apiKey: key));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.DoesNotContain(key, ex.Message);
        Assert.DoesNotContain("sk-proj", ex.Message);
        Assert.DoesNotContain("SUPERSECRET", ex.Message);
        Assert.True(ex.Message.Length <= 500);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(404)]
    public async Task NonTransientErrors_FailOnTheFirstAttempt_WithoutRetrying(int status)
    {
        // covers AC-5: only transient failures are retried; a bad key, no credit,
        // or a retired model fails at once (seen live: OpenAI 401, DeepSeek 402, Gemini 404).
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(status));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("deepseek", stub.Endpoint, "deepseek-chat"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.Single(stub.Requests);
        Assert.Contains(status.ToString(), ex.Message);
    }

    [Fact]
    public async Task GeminiStyleArrayErrorBody_IsStillAProviderFailure()
    {
        // covers AC-6: Gemini wraps its error in a JSON array, which the SDK can't
        // parse; the failure must still name the provider, model, and status.
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(404,
            ErrorBody: """[{"error":{"code":404,"message":"This model is no longer available to new users.","status":"NOT_FOUND"}}]"""));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("gemini", stub.Endpoint, "gemini-2.5-flash"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.Equal("gemini", ex.Provider);
        Assert.Equal("gemini-2.5-flash", ex.Model);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task ProviderError_NeverCarriesAGeminiStyleKey()
    {
        // covers the key invariant for keys the pattern doesn't know (Gemini's "AQ." format):
        // the configured key itself is always scrubbed.
        const string key = "AQ.Ab8-test-GEMINISECRET-0123456789";
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(400,
            ErrorBody: $$$"""{"error":{"message":"API key not valid: {{{key}}}","type":"invalid_request_error"}}"""));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("gemini", stub.Endpoint, "gemini-3.8-flash", apiKey: key));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => PlanAsync(services));

        Assert.DoesNotContain("GEMINISECRET", ex.Message);
    }

    [Fact]
    public async Task StartupReporter_WarnsForEachFakePurpose()
    {
        // covers AC-3: a deployment still on the Fake provider is obvious in the startup log.
        var logs = new CapturingLoggerProvider();
        using var services = AiTestServices.Build(new Dictionary<string, string?> { ["Ai:Purposes:Default:Provider"] = "Fake" }, logs);

        await StartHostedServicesAsync(services);

        Assert.Contains(logs.Messages, m => m.Contains("AI purpose Default uses the Fake provider"));
        Assert.Contains(logs.Messages, m => m.Contains("AI purpose Planner uses the Fake provider"));
    }

    [Fact]
    public async Task StartupReporter_NamesTheRealProviderAndModel_ButNeverTheKey()
    {
        // covers AC-3, AC-4: the resolved target per purpose is logged; the key is not.
        const string key = "sk-test-STARTUPSECRET-0123456789";
        var logs = new CapturingLoggerProvider();
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", "https://api.openai.com/v1", "gpt-4o-mini", apiKey: key), logs);

        await StartHostedServicesAsync(services);

        Assert.Contains(logs.Messages, m => m.Contains("AI purpose Planner: provider openai, model gpt-4o-mini"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Fake provider"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("STARTUPSECRET"));
    }

    [Fact]
    public async Task CallerCancellation_PassesThroughUntranslated()
    {
        // A cancellation the caller asked for is not a provider outage.
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(Content: ValidPlan, Delay: TimeSpan.FromSeconds(30)));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", stub.Endpoint, "gpt-4o-mini"));
        var client = services.GetRequiredKeyedService<IChatClient>(AiPurposes.Default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hi", cancellationToken: cts.Token));

        Assert.IsNotType<AiProviderException>(ex);
    }

    [Fact]
    public async Task Telemetry_ReportsModelAndTokens_WithoutPromptTextByDefault()
    {
        // covers AC-7: spans carry provider, model, and token counts; no prompt text unless LogSensitiveData.
        await using var stub = await StubOpenAiServer.StartAsync("OK");
        var logs = new CapturingLoggerProvider();
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("deepseek", stub.Endpoint, "deepseek-chat"), logs);
        var activities = CaptureActivities();

        await services.GetRequiredKeyedService<IChatClient>(AiPurposes.Default).GetResponseAsync("PROMPT-MARKER-123");

        var span = Assert.Single(activities);
        Assert.Equal("deepseek-chat", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(11, Convert.ToInt32(span.GetTagItem("gen_ai.usage.input_tokens")));
        Assert.Equal(7, Convert.ToInt32(span.GetTagItem("gen_ai.usage.output_tokens")));
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("PROMPT-MARKER-123") == true);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("PROMPT-MARKER-123"));
    }

    [Fact]
    public async Task LogSensitiveData_WhenOn_RecordsThePromptText()
    {
        // covers AC-7: capture is available on purpose, for debugging.
        await using var stub = await StubOpenAiServer.StartAsync("OK");
        var logs = new CapturingLoggerProvider();
        var config = AiTestServices.SingleProvider("deepseek", stub.Endpoint, "deepseek-chat");
        config["Ai:LogSensitiveData"] = "true";
        using var services = AiTestServices.Build(config, logs);

        await services.GetRequiredKeyedService<IChatClient>(AiPurposes.Default).GetResponseAsync("PROMPT-MARKER-456");

        Assert.Contains(logs.Messages, m => m.Contains("PROMPT-MARKER-456"));
    }

    [Fact]
    public async Task HealthProbe_ReportsHealthyWithLatency_AgainstAWorkingProvider()
    {
        // covers AC-8.
        await using var stub = await StubOpenAiServer.StartAsync("OK");
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", stub.Endpoint, "gpt-4o-mini"));

        var result = await ProbeAsync(services);

        Assert.True(result.Healthy);
        Assert.Equal(new ResolvedAiPurpose(AiPurposes.Default, "openai", "gpt-4o-mini"), result.Target);
        Assert.Null(result.Error);
        Assert.Contains("\"max_completion_tokens\":5", Assert.Single(stub.Requests).Body.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthProbe_ReportsUnhealthyWithTheError_AgainstAFailingProvider()
    {
        // covers AC-8.
        await using var stub = await StubOpenAiServer.StartAsync([], new StubReply(503, RetryAfter: "0"));
        using var services = AiTestServices.Build(AiTestServices.SingleProvider("openai", stub.Endpoint, "gpt-4o-mini"));

        var result = await ProbeAsync(services);

        Assert.False(result.Healthy);
        Assert.Contains("openai", result.Error);
    }

    private static async Task<AgentPlan> PlanAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPlanner>().PlanAsync("list my profile", Tools, CancellationToken.None);
    }

    private static async Task StartHostedServicesAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static Task<AiHealthResult> ProbeAsync(ServiceProvider services) => AiHealthProbe.RunAsync(
        services.GetRequiredKeyedService<IChatClient>(AiPurposes.Default),
        services.GetRequiredKeyedService<ResolvedAiPurpose>(AiPurposes.Default),
        CancellationToken.None);

    // Listens only to spans started on this test's async flow, so parallel
    // test classes calling the same source don't leak into the assertion.
    private static List<Activity> CaptureActivities()
    {
        var captured = new List<Activity>();
        var parent = new Activity("test-root").Start();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiChatClientFactory.TelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == parent.TraceId)
                {
                    lock (captured)
                    {
                        captured.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return captured;
    }
}
