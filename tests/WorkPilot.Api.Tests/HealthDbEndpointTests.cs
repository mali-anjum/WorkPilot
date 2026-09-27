using System.Net;
using System.Text.Json;

namespace WorkPilot.Api.Tests;

// Integration test for the scaffold's one real, non-boilerplate endpoint:
// GET /health/db, which proves EF Core round-trips against a live Postgres
// (docs/specs/0001-stack-architecture.md AC-5). Needs a reachable Postgres;
// set WORKPILOTDB_CONNECTION to point at it (for local runs, the self hosted
// Supabase stack's connection string is in supabase/.env, gitignored - never
// hardcode it here). Shares SharedApiFactory (see its remarks) instead of
// building its own WebApplicationFactory per test.
[Collection("Api")]
public class HealthDbEndpointTests(SharedApiFactory factory)
{
    [Fact]
    public async Task HealthDb_ReturnsOkWithProfileCount()
    {
        // Updated for the real data model (spec 0002): ScaffoldPing is gone,
        // /health/db now round-trips against the `profiles` table instead.
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/db");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.GetProperty("profiles").GetInt32() >= 0);
    }

    [Fact]
    public async Task HangfireDashboard_IsReachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HangfireDashboard_RefusesARemoteCaller()
    {
        // The dashboard has no login yet, so only its local only filter keeps
        // it private; this guards that the filter is still in place.
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        request.Headers.Add(LoopbackRemoteIpStartupFilter.RemoteIpHeader, "203.0.113.7");

        var response = await client.SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }
}
