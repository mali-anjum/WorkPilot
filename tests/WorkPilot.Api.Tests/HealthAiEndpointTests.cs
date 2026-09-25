using System.Net;
using System.Text.Json;

namespace WorkPilot.Api.Tests;

// GET /health/ai through the real Api host (spec 0006, AC-8). The shared
// test host pins Default to the Fake provider (SharedApiFactory), so this
// proves the wiring and the response shape; the stub backed healthy and
// unhealthy cases live in AiProviderTests, real keys in /check verify.
[Collection("Api")]
public class HealthAiEndpointTests(SharedApiFactory factory)
{
    [Fact]
    public async Task HealthAi_OnTheFakeProvider_ReturnsOkWithTheResolvedTarget()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ai");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Default", body.RootElement.GetProperty("purpose").GetString());
        Assert.Equal("Fake", body.RootElement.GetProperty("provider").GetString());
        Assert.True(body.RootElement.GetProperty("latencyMs").GetInt64() >= 0);
    }
}
