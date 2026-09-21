using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace WorkPilot.Api.Tests;

// TestServer never populates HttpContext.Connection.RemoteIpAddress, so
// Hangfire's default dashboard filter (which correctly restricts /hangfire to
// local requests) always sees a non-local caller. This simulates what every
// real local/loopback request actually looks like, rather than loosening the
// dashboard's real authorization for the sake of the test.
internal sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await nextMiddleware();
        });
        next(app);
    };
}

// Integration test for the scaffold's one real, non-boilerplate endpoint:
// GET /health/db, which proves EF Core round-trips against a live Postgres
// (docs/specs/0001-stack-architecture.md AC-5). Needs a reachable Postgres;
// set WORKPILOTDB_CONNECTION to point at it (for local runs, the self hosted
// Supabase stack's connection string is in supabase/.env, gitignored - never
// hardcode it here).
public class HealthDbEndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("WORKPILOTDB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set WORKPILOTDB_CONNECTION to a reachable Postgres connection string before running these tests " +
                "(see supabase/.env for the local self hosted Supabase stack's credentials).");

        // Both of Program.cs's builder.Configuration reads (the connection
        // string, consumed by Aspire's AddNpgsqlDbContext, and the Hangfire
        // server toggle) happen before WebApplicationFactory's own
        // ConfigureAppConfiguration customization is layered on, so overrides
        // have to arrive as env vars (picked up by WebApplication.CreateBuilder
        // itself), not in-memory config added via WithWebHostBuilder.
        Environment.SetEnvironmentVariable("ConnectionStrings__workpilotdb", connectionString);
        // Skips the Hangfire background worker server (polling/watchdog
        // threads), which only drags out WebApplicationFactory teardown and
        // isn't needed by these tests; the dashboard still works without it.
        Environment.SetEnvironmentVariable("Hangfire__DisableServer", "true");

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, LoopbackRemoteIpStartupFilter>());
        });
    }

    [Fact]
    public async Task HealthDb_ReturnsOkWithProfileCount()
    {
        // Updated for the real data model (spec 0002): ScaffoldPing is gone,
        // /health/db now round-trips against the `profiles` table instead.
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
