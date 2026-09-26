using System.Linq.Expressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Agent;
using WorkPilot.Domain.Modules.Applications;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Domain.Modules.Audit;
using WorkPilot.Domain.Modules.Calendar;
using WorkPilot.Domain.Modules.Integrations;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Notifications;
using WorkPilot.Domain.Modules.Outreach;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Domain.Modules.Tasks;
using WorkPilot.Domain.Modules.Universities;
using WorkPilot.Infrastructure.Persistence.Configurations;

namespace WorkPilot.Infrastructure.Persistence;

/// <summary>
/// EF Core owns every table in this DbContext, all in the `app` schema. Per
/// docs/specs/0001-stack-architecture.md, EF Core must never migrate the
/// `auth.*` or `storage.*` schemas, those are owned exclusively by Supabase
/// (GoTrue and Storage respectively). The entity shapes here follow
/// docs/specs/0002-data-model/index.md.
/// </summary>
public class WorkPilotDbContext(
    DbContextOptions<WorkPilotDbContext> options,
    IDataProtectionProvider dataProtectionProvider)
    : DbContext(options)
{
    private readonly IDataProtector _tokenProtector =
        dataProtectionProvider.CreateProtector("WorkPilot.OAuthConnection.Tokens");

    // Profile
    public DbSet<Domain.Modules.Profile.Profile> Profiles => Set<Domain.Modules.Profile.Profile>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<Experience> Experiences => Set<Experience>();
    public DbSet<Education> Education => Set<Education>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeVersion> ResumeVersions => Set<ResumeVersion>();
    public DbSet<CoverLetter> CoverLetters => Set<CoverLetter>();
    public DbSet<CoverLetterVersion> CoverLetterVersions => Set<CoverLetterVersion>();

    // Jobs
    public DbSet<JobSource> JobSources => Set<JobSource>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSnapshot> JobSnapshots => Set<JobSnapshot>();
    public DbSet<JobSourceLink> JobSourceLinks => Set<JobSourceLink>();
    public DbSet<JobMatch> JobMatches => Set<JobMatch>();

    // Applications
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<ApplicationAnswer> ApplicationAnswers => Set<ApplicationAnswer>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();

    // Universities
    public DbSet<University> Universities => Set<University>();
    public DbSet<Domain.Modules.Universities.Program> Programs => Set<Domain.Modules.Universities.Program>();
    public DbSet<Professor> Professors => Set<Professor>();
    public DbSet<ResearchArea> ResearchAreas => Set<ResearchArea>();
    public DbSet<Scholarship> Scholarships => Set<Scholarship>();

    // Outreach
    public DbSet<OutreachContact> OutreachContacts => Set<OutreachContact>();
    public DbSet<OutreachMessage> OutreachMessages => Set<OutreachMessage>();
    public DbSet<EmailThread> EmailThreads => Set<EmailThread>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();

    // Personal
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    // Agent & workflow
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowEvent> WorkflowEvents => Set<WorkflowEvent>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();

    // Approvals & audit
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Integrations & notifications
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<OAuthConnection> OAuthConnections => Set<OAuthConnection>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Without this, EF's tables land in Postgres's default `public`
        // schema, the one PostgREST exposes by default once Kong/PostgREST
        // are added.
        modelBuilder.HasDefaultSchema("app");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkPilotDbContext).Assembly);

        // OAuthConnection needs the injected IDataProtector, so it can't go
        // through an assembly-scanned IEntityTypeConfiguration (those are
        // constructed parameterless). Configured here instead (AC-6).
        modelBuilder.Entity<OAuthConnection>(entity =>
        {
            entity.ToTable("oauth_connections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AccessToken)
                .HasConversion(new EncryptedStringConverter(_tokenProtector))
                .IsRequired();
            entity.Property(e => e.RefreshToken)
                .HasConversion(new EncryptedStringConverter(_tokenProtector))
                .IsRequired();
        });

        // Global soft delete filter: every ISoftDeletable entity is excluded
        // from default queries once IsDeleted is set, without the row ever
        // being physically removed (spec 0002, AC-3).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var property = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
            var notDeleted = Expression.Equal(property, Expression.Constant(false));
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(notDeleted, parameter));
        }
    }
}
