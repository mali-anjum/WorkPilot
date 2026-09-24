using System.Net;

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
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"profiles\":", body);
    }

    [Fact]
    public async Task HangfireDashboard_IsReachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
