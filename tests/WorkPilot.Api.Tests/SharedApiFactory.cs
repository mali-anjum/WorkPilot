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

/// <summary>
/// One <see cref="WebApplicationFactory{Program}"/> shared across every
/// Api.Tests class via <see cref="ApiTestCollection"/>, instead of each test
/// method building and disposing its own.
///
/// Root cause this fixes (found debugging spec 0005's flaky
/// AgentOrchestratorEndpointsTests): Hangfire.AspNetCore bridges its
/// internal, PROCESS WIDE static logging (`Hangfire.GlobalJobFilters`, whose
/// type initializer runs exactly once per process, lazily, on first real use
/// of `IBackgroundJobClient`) to whichever host's `ILoggerFactory` last
/// called `AddHangfire()`. When every test method created and disposed its
/// own factory, whichever host happened to be alive at that one-time global
/// init decided the outcome for the rest of the process: if the winning
/// host's factory was already disposed by the time anything first touched
/// `IBackgroundJobClient`, `GlobalJobFilters`'s static constructor captured a
/// disposed `LoggerFactory`, threw `ObjectDisposedException` once, and .NET
/// then rethrows that same cached `TypeInitializationException` on every
/// later attempt for the rest of the process, no matter which host asks.
/// A single long lived factory, alive for the whole test run, means Hangfire
/// only ever sees one `ILoggerFactory`, which is never disposed mid run.
/// </summary>
public sealed class SharedApiFactory : WebApplicationFactory<Program>
{
    public SharedApiFactory()
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
        // threads); the dashboard and IBackgroundJobClient still work
        // without it, and it only drags out teardown.
        Environment.SetEnvironmentVariable("Hangfire__DisableServer", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackRemoteIpStartupFilter>());
    }
}

/// <summary>Groups every Api.Tests class onto the one <see cref="SharedApiFactory"/> (see its remarks for why).</summary>
[CollectionDefinition("Api")]
public sealed class ApiTestCollection : ICollectionFixture<SharedApiFactory>;
