using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Aspire cross-cutting concerns: OpenTelemetry, health checks, service
// discovery, resilient HttpClient defaults.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// EF Core, pointed at the "workpilotdb" connection supplied by Aspire
// (the AppHost wires this to the same Postgres instance/database Supabase's
// own Postgres container exposes; see AppHost.cs and docker-compose.yml).
// EF Core owns only the product schema here, never `auth.*`/`storage.*`.
builder.AddNpgsqlDbContext<WorkPilotDbContext>("workpilotdb");

// Hangfire, storage in the same Postgres database as EF Core (per spec:
// "Background jobs / workflows | Hangfire, storage in the same Postgres
// database").
var connectionString = builder.Configuration.GetConnectionString("workpilotdb");
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints(); // Aspire health/liveness endpoints from ServiceDefaults

// Hangfire dashboard (AC-6: dashboard must be reachable). No auth filter
// yet — this is a local scaffold; add authorization before any non-local
// deployment.
app.UseHangfireDashboard("/hangfire");

app.MapGet("/health/db", async (WorkPilotDbContext db) =>
{
    await db.Database.EnsureCreatedAsync();
    var count = await db.ScaffoldPings.CountAsync();
    return Results.Ok(new { status = "ok", scaffoldPings = count });
});

app.Run();
