using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Integrations;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// The schema guarantees of the data model (spec 0002) against the real Postgres the
// Api migrated on startup: which schema EF owns, required provenance, the soft delete
// filter and encrypted OAuth tokens. Needs WORKPILOTDB_CONNECTION (see supabase/.env).
[Collection("Api")]
public class DataModelTests(SharedApiFactory factory)
{
    // covers: spec 0002 AC-1, AC-7
    [Fact]
    public void Every_entity_lives_in_the_app_schema()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();

        var outside = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.GetSchema() != "app")
            .Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .ToList();

        Assert.Empty(outside);
    }

    // covers: spec 0002 AC-7
    [Fact]
    public void No_migration_ever_touches_the_auth_or_storage_schema()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.NotEmpty(script);
        Assert.DoesNotMatch(@"(?i)""?\b(auth|storage)""?\s*\.", script);
    }

    // covers: spec 0002 AC-2 (ResearchArea is left out: AC-2 names it, but the spec's own data model
    // sketch gives it no provenance and the schema follows the sketch, see the test audit)
    [Theory]
    [InlineData("jobs")]
    [InlineData("job_snapshots")]
    [InlineData("universities")]
    [InlineData("programs")]
    [InlineData("professors")]
    [InlineData("scholarships")]
    public async Task Provenance_source_and_retrieval_time_are_required_by_the_database(string table)
    {
        await using var db = CreateDbContext();

        var nullability = await db.Database.SqlQuery<ColumnNullability>($"""
            SELECT column_name AS "Name", is_nullable AS "IsNullable"
            FROM information_schema.columns
            WHERE table_schema = 'app' AND table_name = {table} AND column_name LIKE 'Provenance_%'
            """).ToDictionaryAsync(c => c.Name, c => c.IsNullable);

        Assert.Equal("NO", nullability["Provenance_SourceUrl"]);
        Assert.Equal("NO", nullability["Provenance_RetrievedAt"]);
        Assert.Equal("YES", nullability["Provenance_VerifiedAt"]);
        Assert.Equal("YES", nullability["Provenance_Confidence"]);
    }

    // covers: spec 0002 AC-3
    [Fact]
    public void Every_soft_deletable_entity_has_the_global_query_filter()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();

        var softDeletable = db.Model.GetEntityTypes()
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType))
            .ToList();

        Assert.NotEmpty(softDeletable);
        Assert.All(softDeletable, e => Assert.NotEmpty(e.GetDeclaredQueryFilters()));
    }

    // covers: spec 0002 AC-3 (a soft deleted parent hides from default queries; rows referencing it stay)
    [Fact]
    public async Task Soft_deleting_a_profile_hides_it_but_keeps_the_rows_that_reference_it()
    {
        var (profileId, integrationId, connectionId) = await SeedConnectionAsync("access-token", "refresh-token");
        try
        {
            await using (var db = CreateDbContext())
            {
                var profile = await db.Profiles.SingleAsync(p => p.Id == profileId);
                profile.SoftDelete(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }

            await using var check = CreateDbContext();
            Assert.False(await check.Profiles.AnyAsync(p => p.Id == profileId));
            var stored = await check.Profiles.IgnoreQueryFilters().SingleAsync(p => p.Id == profileId);
            Assert.True(stored.IsDeleted);
            Assert.NotNull(stored.DeletedAt);
            Assert.True(await check.OAuthConnections.AnyAsync(c => c.Id == connectionId && c.ProfileId == profileId));
        }
        finally
        {
            await CleanupAsync(profileId, integrationId);
        }
    }

    // covers: spec 0002 AC-6
    [Fact]
    public async Task OAuth_tokens_are_ciphertext_in_the_column_and_plain_text_through_EF()
    {
        const string accessToken = "ya29.raw-access-token-never-stored";
        const string refreshToken = "1//raw-refresh-token-never-stored";
        var (profileId, integrationId, connectionId) = await SeedConnectionAsync(accessToken, refreshToken);
        try
        {
            await using var db = CreateDbContext();
            var raw = await db.Database.SqlQuery<RawTokens>($"""
                SELECT "AccessToken", "RefreshToken" FROM app.oauth_connections WHERE "Id" = {connectionId}
                """).SingleAsync();

            Assert.DoesNotContain(accessToken, raw.AccessToken);
            Assert.DoesNotContain(refreshToken, raw.RefreshToken);
            Assert.NotEqual(raw.AccessToken, raw.RefreshToken);

            var read = await db.OAuthConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId);
            Assert.Equal(accessToken, read.AccessToken);
            Assert.Equal(refreshToken, read.RefreshToken);
        }
        finally
        {
            await CleanupAsync(profileId, integrationId);
        }
    }

    private async Task<(Guid ProfileId, Guid IntegrationId, Guid ConnectionId)> SeedConnectionAsync(string access, string refresh)
    {
        await using var db = CreateDbContext();
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Data model test" };
        var integration = new Integration { ProviderName = "Gmail", Status = "Connected" };
        db.Profiles.Add(profile);
        db.Integrations.Add(integration);
        var connection = new OAuthConnection
        {
            IntegrationId = integration.Id,
            ProfileId = profile.Id,
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.OAuthConnections.Add(connection);
        await db.SaveChangesAsync();
        return (profile.Id, integration.Id, connection.Id);
    }

    private async Task CleanupAsync(Guid profileId, Guid integrationId)
    {
        await using var db = CreateDbContext();
        await db.OAuthConnections.Where(c => c.ProfileId == profileId).ExecuteDeleteAsync();
        await db.Integrations.Where(i => i.Id == integrationId).ExecuteDeleteAsync();
        await db.Profiles.IgnoreQueryFilters().Where(p => p.Id == profileId).ExecuteDeleteAsync();
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private sealed record ColumnNullability(string Name, string IsNullable);

    private sealed record RawTokens(string AccessToken, string RefreshToken);
}
