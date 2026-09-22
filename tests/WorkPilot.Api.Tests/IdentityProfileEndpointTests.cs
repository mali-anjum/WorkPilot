using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// Integration tests for POST /internal/identity/profile (spec 0004, auth & app
// shell): the internal-only endpoint Web calls after a successful GoTrue sign
// in to resolve or create the founder's Profile row. Never externally exposed
// (Api carries no external endpoint in AppHost), so no auth is asserted here,
// only the get-or-create behavior itself (spec 0004, Value sourcing: "profileId
// from looking up, or on a first ever sign in creating, the Profile row whose
// AuthUserId matches sub"). Needs a reachable Postgres; set
// WORKPILOTDB_CONNECTION (see supabase/.env for the local stack's credentials).
public class IdentityProfileEndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("WORKPILOTDB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set WORKPILOTDB_CONNECTION to a reachable Postgres connection string before running these tests " +
                "(see supabase/.env for the local self hosted Supabase stack's credentials).");

        // Same env-var-before-host-build constraint as HealthDbEndpointTests:
        // Program.cs reads the connection string before WebApplicationFactory's
        // own configuration hook runs.
        Environment.SetEnvironmentVariable("ConnectionStrings__workpilotdb", connectionString);
        Environment.SetEnvironmentVariable("Hangfire__DisableServer", "true");

        return new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task ResolveProfile_OnFirstSignIn_CreatesAProfileRowLinkedToTheAuthUser()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var authUserId = Guid.NewGuid();
        var email = $"test-{authUserId:N}@example.com";

        var response = await client.PostAsJsonAsync(
            "/internal/identity/profile",
            new { AuthUserId = authUserId, Email = email });

        try
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ProfileIdBody>();
            Assert.NotNull(body);
            Assert.NotEqual(Guid.Empty, body!.ProfileId);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            var profile = await db.Profiles.SingleAsync(p => p.Id == body.ProfileId);
            Assert.Equal(authUserId, profile.AuthUserId);
            // spec 0004 API surface: name is derived from the email's local
            // part on first sign in, not left blank.
            Assert.Equal(email.Split('@')[0], profile.Name);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            await db.Profiles.Where(p => p.AuthUserId == authUserId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ResolveProfile_CalledTwiceForTheSameAuthUser_ReturnsTheSameProfileIdWithoutCreatingASecondRow()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var authUserId = Guid.NewGuid();
        var email = $"test-{authUserId:N}@example.com";

        try
        {
            var first = await client.PostAsJsonAsync(
                "/internal/identity/profile",
                new { AuthUserId = authUserId, Email = email });
            var firstBody = await first.Content.ReadFromJsonAsync<ProfileIdBody>();

            var second = await client.PostAsJsonAsync(
                "/internal/identity/profile",
                new { AuthUserId = authUserId, Email = email });
            var secondBody = await second.Content.ReadFromJsonAsync<ProfileIdBody>();

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(firstBody!.ProfileId, secondBody!.ProfileId);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            var count = await db.Profiles.CountAsync(p => p.AuthUserId == authUserId);
            Assert.Equal(1, count);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
            await db.Profiles.Where(p => p.AuthUserId == authUserId).ExecuteDeleteAsync();
        }
    }

    private sealed record ProfileIdBody(Guid ProfileId);
}
