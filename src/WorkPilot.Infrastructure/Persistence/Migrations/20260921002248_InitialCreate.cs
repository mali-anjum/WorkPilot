using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "app");

        migrationBuilder.CreateTable(
            name: "agent_runs",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                Goal = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_runs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "approvals",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                RiskTier = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DecidedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_approvals", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "audit_logs",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_logs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "calendar_events",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalCalendarId = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendar_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "cover_letters",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cover_letters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "integrations",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_integrations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "job_applications",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                ResumeVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                CoverLetterVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_applications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "job_matches",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Score = table.Column<decimal>(type: "numeric", nullable: false),
                MatchedSkills = table.Column<string>(type: "jsonb", nullable: true),
                RankedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_matches", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "job_sources",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Type = table.Column<string>(type: "text", nullable: false),
                Config = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_sources", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "jobs",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Location = table.Column<string>(type: "text", nullable: true),
                RemoteType = table.Column<string>(type: "text", nullable: true),
                SalaryRangeMin = table.Column<decimal>(type: "numeric", nullable: true),
                SalaryRangeMax = table.Column<decimal>(type: "numeric", nullable: true),
                ExternalId = table.Column<string>(type: "text", nullable: false),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_jobs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "notifications",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: true),
                ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notifications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "oauth_connections",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                AccessToken = table.Column<string>(type: "text", nullable: false),
                RefreshToken = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oauth_connections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "outreach_contacts",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfessorId = table.Column<Guid>(type: "uuid", nullable: true),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outreach_contacts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "profiles",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Headline = table.Column<string>(type: "text", nullable: true),
                TargetRoles = table.Column<List<string>>(type: "text[]", nullable: false),
                Location = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_profiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "research_areas",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_research_areas", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "resumes",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resumes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "skills",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Category = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_skills", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "tasks",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                LinkedApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                LinkedOutreachMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tasks", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "universities",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Website = table.Column<string>(type: "text", nullable: true),
                Location = table.Column<string>(type: "text", nullable: true),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_universities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "workflow_events",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                PayloadUrl = table.Column<string>(type: "text", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "workflow_instances",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DefinitionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_instances", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "workflow_steps",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                StepName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                PayloadUrl = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_steps", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "agent_steps",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentRunId = table.Column<Guid>(type: "uuid", nullable: false),
                ReasoningUrl = table.Column<string>(type: "text", nullable: true),
                Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_steps", x => x.Id);
                table.ForeignKey(
                    name: "FK_agent_steps_agent_runs_AgentRunId",
                    column: x => x.AgentRunId,
                    principalSchema: "app",
                    principalTable: "agent_runs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "cover_letter_versions",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CoverLetterId = table.Column<Guid>(type: "uuid", nullable: false),
                StorageUrl = table.Column<string>(type: "text", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                GeneratedForApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cover_letter_versions", x => x.Id);
                table.ForeignKey(
                    name: "FK_cover_letter_versions_cover_letters_CoverLetterId",
                    column: x => x.CoverLetterId,
                    principalSchema: "app",
                    principalTable: "cover_letters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "application_answers",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                Question = table.Column<string>(type: "text", nullable: false),
                Answer = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_answers", x => x.Id);
                table.ForeignKey(
                    name: "FK_application_answers_job_applications_JobApplicationId",
                    column: x => x.JobApplicationId,
                    principalSchema: "app",
                    principalTable: "job_applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "application_events",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_events", x => x.Id);
                table.ForeignKey(
                    name: "FK_application_events_job_applications_JobApplicationId",
                    column: x => x.JobApplicationId,
                    principalSchema: "app",
                    principalTable: "job_applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "job_snapshots",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                RawContent = table.Column<string>(type: "text", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_snapshots", x => x.Id);
                table.ForeignKey(
                    name: "FK_job_snapshots_jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "app",
                    principalTable: "jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "outreach_messages",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OutreachContactId = table.Column<Guid>(type: "uuid", nullable: false),
                Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outreach_messages", x => x.Id);
                table.ForeignKey(
                    name: "FK_outreach_messages_outreach_contacts_OutreachContactId",
                    column: x => x.OutreachContactId,
                    principalSchema: "app",
                    principalTable: "outreach_contacts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "education",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Institution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Degree = table.Column<string>(type: "text", nullable: false),
                Field = table.Column<string>(type: "text", nullable: false),
                StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_education", x => x.Id);
                table.ForeignKey(
                    name: "FK_education_profiles_ProfileId",
                    column: x => x.ProfileId,
                    principalSchema: "app",
                    principalTable: "profiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "experiences",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                Company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_experiences", x => x.Id);
                table.ForeignKey(
                    name: "FK_experiences_profiles_ProfileId",
                    column: x => x.ProfileId,
                    principalSchema: "app",
                    principalTable: "profiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "resume_versions",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ResumeId = table.Column<Guid>(type: "uuid", nullable: false),
                StorageUrl = table.Column<string>(type: "text", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                ParsedContent = table.Column<string>(type: "jsonb", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resume_versions", x => x.Id);
                table.ForeignKey(
                    name: "FK_resume_versions_resumes_ResumeId",
                    column: x => x.ResumeId,
                    principalSchema: "app",
                    principalTable: "resumes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "profile_skills",
            schema: "app",
            columns: table => new
            {
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                SkillId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_profile_skills", x => new { x.ProfileId, x.SkillId });
                table.ForeignKey(
                    name: "FK_profile_skills_profiles_ProfileId",
                    column: x => x.ProfileId,
                    principalSchema: "app",
                    principalTable: "profiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_profile_skills_skills_SkillId",
                    column: x => x.SkillId,
                    principalSchema: "app",
                    principalTable: "skills",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "professors",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UniversityId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                ProfileUrl = table.Column<string>(type: "text", nullable: true),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_professors", x => x.Id);
                table.ForeignKey(
                    name: "FK_professors_universities_UniversityId",
                    column: x => x.UniversityId,
                    principalSchema: "app",
                    principalTable: "universities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "programs",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UniversityId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Degree = table.Column<string>(type: "text", nullable: false),
                Field = table.Column<string>(type: "text", nullable: false),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_programs", x => x.Id);
                table.ForeignKey(
                    name: "FK_programs_universities_UniversityId",
                    column: x => x.UniversityId,
                    principalSchema: "app",
                    principalTable: "universities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "scholarships",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UniversityId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                AmountRange = table.Column<string>(type: "text", nullable: true),
                DeadlineDate = table.Column<DateOnly>(type: "date", nullable: true),
                Provenance_SourceUrl = table.Column<string>(type: "text", nullable: false),
                Provenance_RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Provenance_VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Provenance_Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scholarships", x => x.Id);
                table.ForeignKey(
                    name: "FK_scholarships_universities_UniversityId",
                    column: x => x.UniversityId,
                    principalSchema: "app",
                    principalTable: "universities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "tool_calls",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentStepId = table.Column<Guid>(type: "uuid", nullable: false),
                ToolName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                InputPayloadUrl = table.Column<string>(type: "text", nullable: true),
                OutputPayloadUrl = table.Column<string>(type: "text", nullable: true),
                Success = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tool_calls", x => x.Id);
                table.ForeignKey(
                    name: "FK_tool_calls_agent_steps_AgentStepId",
                    column: x => x.AgentStepId,
                    principalSchema: "app",
                    principalTable: "agent_steps",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "email_threads",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OutreachMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                GmailThreadId = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_email_threads", x => x.Id);
                table.ForeignKey(
                    name: "FK_email_threads_outreach_messages_OutreachMessageId",
                    column: x => x.OutreachMessageId,
                    principalSchema: "app",
                    principalTable: "outreach_messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "follow_ups",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OutreachMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_follow_ups", x => x.Id);
                table.ForeignKey(
                    name: "FK_follow_ups_outreach_messages_OutreachMessageId",
                    column: x => x.OutreachMessageId,
                    principalSchema: "app",
                    principalTable: "outreach_messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "professor_research_areas",
            schema: "app",
            columns: table => new
            {
                ProfessorId = table.Column<Guid>(type: "uuid", nullable: false),
                ResearchAreaId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_professor_research_areas", x => new { x.ProfessorId, x.ResearchAreaId });
                table.ForeignKey(
                    name: "FK_professor_research_areas_professors_ProfessorId",
                    column: x => x.ProfessorId,
                    principalSchema: "app",
                    principalTable: "professors",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_professor_research_areas_research_areas_ResearchAreaId",
                    column: x => x.ResearchAreaId,
                    principalSchema: "app",
                    principalTable: "research_areas",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_agent_runs_WorkflowInstanceId",
            schema: "app",
            table: "agent_runs",
            column: "WorkflowInstanceId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_steps_AgentRunId",
            schema: "app",
            table: "agent_steps",
            column: "AgentRunId");

        migrationBuilder.CreateIndex(
            name: "IX_agent_steps_Timestamp",
            schema: "app",
            table: "agent_steps",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_application_answers_JobApplicationId",
            schema: "app",
            table: "application_answers",
            column: "JobApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_application_events_JobApplicationId",
            schema: "app",
            table: "application_events",
            column: "JobApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_approvals_TargetType_TargetId",
            schema: "app",
            table: "approvals",
            columns: new[] { "TargetType", "TargetId" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_OccurredAt",
            schema: "app",
            table: "audit_logs",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_cover_letter_versions_CoverLetterId_VersionNumber",
            schema: "app",
            table: "cover_letter_versions",
            columns: new[] { "CoverLetterId", "VersionNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_education_ProfileId",
            schema: "app",
            table: "education",
            column: "ProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_email_threads_GmailThreadId",
            schema: "app",
            table: "email_threads",
            column: "GmailThreadId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_email_threads_OutreachMessageId",
            schema: "app",
            table: "email_threads",
            column: "OutreachMessageId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_experiences_ProfileId",
            schema: "app",
            table: "experiences",
            column: "ProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_follow_ups_OutreachMessageId",
            schema: "app",
            table: "follow_ups",
            column: "OutreachMessageId");

        migrationBuilder.CreateIndex(
            name: "IX_job_matches_JobId_ProfileId",
            schema: "app",
            table: "job_matches",
            columns: new[] { "JobId", "ProfileId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_job_snapshots_ContentHash",
            schema: "app",
            table: "job_snapshots",
            column: "ContentHash");

        migrationBuilder.CreateIndex(
            name: "IX_job_snapshots_JobId",
            schema: "app",
            table: "job_snapshots",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_jobs_JobSourceId_ExternalId",
            schema: "app",
            table: "jobs",
            columns: new[] { "JobSourceId", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_outreach_messages_OutreachContactId",
            schema: "app",
            table: "outreach_messages",
            column: "OutreachContactId");

        migrationBuilder.CreateIndex(
            name: "IX_professor_research_areas_ResearchAreaId",
            schema: "app",
            table: "professor_research_areas",
            column: "ResearchAreaId");

        migrationBuilder.CreateIndex(
            name: "IX_professors_UniversityId",
            schema: "app",
            table: "professors",
            column: "UniversityId");

        migrationBuilder.CreateIndex(
            name: "IX_profile_skills_SkillId",
            schema: "app",
            table: "profile_skills",
            column: "SkillId");

        migrationBuilder.CreateIndex(
            name: "IX_profiles_AuthUserId",
            schema: "app",
            table: "profiles",
            column: "AuthUserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_programs_UniversityId",
            schema: "app",
            table: "programs",
            column: "UniversityId");

        migrationBuilder.CreateIndex(
            name: "IX_research_areas_Name",
            schema: "app",
            table: "research_areas",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_resume_versions_ResumeId_VersionNumber",
            schema: "app",
            table: "resume_versions",
            columns: new[] { "ResumeId", "VersionNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_scholarships_UniversityId",
            schema: "app",
            table: "scholarships",
            column: "UniversityId");

        migrationBuilder.CreateIndex(
            name: "IX_skills_Name",
            schema: "app",
            table: "skills",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tool_calls_AgentStepId",
            schema: "app",
            table: "tool_calls",
            column: "AgentStepId");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_events_OccurredAt",
            schema: "app",
            table: "workflow_events",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_steps_WorkflowInstanceId",
            schema: "app",
            table: "workflow_steps",
            column: "WorkflowInstanceId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "application_answers",
            schema: "app");

        migrationBuilder.DropTable(
            name: "application_events",
            schema: "app");

        migrationBuilder.DropTable(
            name: "approvals",
            schema: "app");

        migrationBuilder.DropTable(
            name: "audit_logs",
            schema: "app");

        migrationBuilder.DropTable(
            name: "calendar_events",
            schema: "app");

        migrationBuilder.DropTable(
            name: "cover_letter_versions",
            schema: "app");

        migrationBuilder.DropTable(
            name: "education",
            schema: "app");

        migrationBuilder.DropTable(
            name: "email_threads",
            schema: "app");

        migrationBuilder.DropTable(
            name: "experiences",
            schema: "app");

        migrationBuilder.DropTable(
            name: "follow_ups",
            schema: "app");

        migrationBuilder.DropTable(
            name: "integrations",
            schema: "app");

        migrationBuilder.DropTable(
            name: "job_matches",
            schema: "app");

        migrationBuilder.DropTable(
            name: "job_snapshots",
            schema: "app");

        migrationBuilder.DropTable(
            name: "job_sources",
            schema: "app");

        migrationBuilder.DropTable(
            name: "notifications",
            schema: "app");

        migrationBuilder.DropTable(
            name: "oauth_connections",
            schema: "app");

        migrationBuilder.DropTable(
            name: "professor_research_areas",
            schema: "app");

        migrationBuilder.DropTable(
            name: "profile_skills",
            schema: "app");

        migrationBuilder.DropTable(
            name: "programs",
            schema: "app");

        migrationBuilder.DropTable(
            name: "resume_versions",
            schema: "app");

        migrationBuilder.DropTable(
            name: "scholarships",
            schema: "app");

        migrationBuilder.DropTable(
            name: "tasks",
            schema: "app");

        migrationBuilder.DropTable(
            name: "tool_calls",
            schema: "app");

        migrationBuilder.DropTable(
            name: "workflow_events",
            schema: "app");

        migrationBuilder.DropTable(
            name: "workflow_instances",
            schema: "app");

        migrationBuilder.DropTable(
            name: "workflow_steps",
            schema: "app");

        migrationBuilder.DropTable(
            name: "job_applications",
            schema: "app");

        migrationBuilder.DropTable(
            name: "cover_letters",
            schema: "app");

        migrationBuilder.DropTable(
            name: "outreach_messages",
            schema: "app");

        migrationBuilder.DropTable(
            name: "jobs",
            schema: "app");

        migrationBuilder.DropTable(
            name: "professors",
            schema: "app");

        migrationBuilder.DropTable(
            name: "research_areas",
            schema: "app");

        migrationBuilder.DropTable(
            name: "profiles",
            schema: "app");

        migrationBuilder.DropTable(
            name: "skills",
            schema: "app");

        migrationBuilder.DropTable(
            name: "resumes",
            schema: "app");

        migrationBuilder.DropTable(
            name: "agent_steps",
            schema: "app");

        migrationBuilder.DropTable(
            name: "outreach_contacts",
            schema: "app");

        migrationBuilder.DropTable(
            name: "universities",
            schema: "app");

        migrationBuilder.DropTable(
            name: "agent_runs",
            schema: "app");
    }
}
