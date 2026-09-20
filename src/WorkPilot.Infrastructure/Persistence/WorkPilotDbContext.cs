using Microsoft.EntityFrameworkCore;

namespace WorkPilot.Infrastructure.Persistence;

/// <summary>
/// EF Core owns every table in this DbContext. Per
/// docs/specs/0001-stack-architecture.md, EF Core must never migrate the
/// `auth.*` or `storage.*` schemas — those are owned exclusively by
/// Supabase (GoTrue and Storage respectively). This DbContext is scoped to
/// the product's own schema only.
///
/// This is a scaffold-stage placeholder. The real data model (30+ entities:
/// Users, Jobs, JobMatches, Applications, Universities, Professors,
/// AgentRuns, Approvals, AuditLogs, ...) is a separate future feature
/// (docs/scope/foundation.md item 3) — do not add business entities here
/// until that feature is built.
/// </summary>
public class WorkPilotDbContext(DbContextOptions<WorkPilotDbContext> options) : DbContext(options)
{
    public DbSet<ScaffoldPing> ScaffoldPings => Set<ScaffoldPing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Without this, EF's tables land in Postgres's default `public`
        // schema, the one PostgREST exposes by default once Kong/PostgREST
        // are added (docs/specs/0001-stack-architecture.md defers them, but
        // the product schema still needs to be separate from day one, before
        // the real 30+ entity data model lands here).
        modelBuilder.HasDefaultSchema("app");

        modelBuilder.Entity<ScaffoldPing>(entity =>
        {
            entity.ToTable("scaffold_pings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Message).HasMaxLength(200);
        });
    }
}

/// <summary>
/// Trivial placeholder entity that exists only to prove EF Core migrations
/// work end to end against the shared Postgres database. Delete once the
/// real data model feature lands.
/// </summary>
public class ScaffoldPing
{
    public int Id { get; set; }
    public string Message { get; set; } = "WorkPilot scaffold is alive";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
