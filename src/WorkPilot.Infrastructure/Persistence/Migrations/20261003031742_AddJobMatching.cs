using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddJobMatching : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Matches are derived data and the table had no writer before spec 0019; clearing it
        // lets the new required columns go in without placeholder values (the sweep rescores).
        migrationBuilder.Sql("DELETE FROM app.job_matches;");

        migrationBuilder.DropColumn(
            name: "MatchedSkills",
            schema: "app",
            table: "job_matches");

        migrationBuilder.AddColumn<List<string>>(
            name: "AuthorizedCountries",
            schema: "app",
            table: "profiles",
            type: "text[]",
            nullable: false,
            defaultValueSql: "'{}'");

        migrationBuilder.AddColumn<string[]>(
            name: "JobTypes",
            schema: "app",
            table: "profiles",
            type: "text[]",
            nullable: false,
            defaultValueSql: "'{}'");

        migrationBuilder.AddColumn<decimal>(
            name: "MinSalary",
            schema: "app",
            table: "profiles",
            type: "numeric(12,2)",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "NeedsSponsorshipElsewhere",
            schema: "app",
            table: "profiles",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<string>(
            name: "PreferredLocations",
            schema: "app",
            table: "profiles",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.AddColumn<string>(
            name: "RemotePreference",
            schema: "app",
            table: "profiles",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Any");

        migrationBuilder.AddColumn<string>(
            name: "SalaryCurrency",
            schema: "app",
            table: "profiles",
            type: "char(3)",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "StrongMatchThreshold",
            schema: "app",
            table: "profiles",
            type: "integer",
            nullable: false,
            defaultValue: 70);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "UpdatedAt",
            schema: "app",
            table: "profiles",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "now()");

        // The row's xmin as the match profile ETag: Npgsql knows it is a system column and adds
        // nothing to the table.
        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "app",
            table: "profiles",
            type: "xid",
            rowVersion: true,
            nullable: false,
            defaultValue: 0u);

        migrationBuilder.AlterColumn<int>(
            name: "Score",
            schema: "app",
            table: "job_matches",
            type: "integer",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric");

        migrationBuilder.AddColumn<string>(
            name: "Confidence",
            schema: "app",
            table: "job_matches",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false);

        migrationBuilder.AddColumn<string>(
            name: "Explanation",
            schema: "app",
            table: "job_matches",
            type: "jsonb",
            nullable: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasBlocker",
            schema: "app",
            table: "job_matches",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "InputsFingerprint",
            schema: "app",
            table: "job_matches",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false);

        migrationBuilder.AddColumn<int>(
            name: "ScoringVersion",
            schema: "app",
            table: "job_matches",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "DegreeLevel",
            schema: "app",
            table: "education",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.CreateTable(
            name: "job_requirements",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ExtractorVersion = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Requirements = table.Column<string>(type: "jsonb", nullable: true),
                Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                PendingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_requirements", x => x.Id);
                table.ForeignKey(
                    name: "FK_job_requirements_jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "app",
                    principalTable: "jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddCheckConstraint(
            name: "ck_profiles_salary_currency",
            schema: "app",
            table: "profiles",
            sql: "(\"MinSalary\" IS NULL) = (\"SalaryCurrency\" IS NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_profiles_strong_match_threshold",
            schema: "app",
            table: "profiles",
            sql: "\"StrongMatchThreshold\" BETWEEN 0 AND 100");

        migrationBuilder.CreateIndex(
            name: "IX_job_matches_ProfileId_HasBlocker_Score",
            schema: "app",
            table: "job_matches",
            columns: new[] { "ProfileId", "HasBlocker", "Score" },
            descending: new[] { false, false, true });

        migrationBuilder.AddCheckConstraint(
            name: "ck_job_matches_score",
            schema: "app",
            table: "job_matches",
            sql: "\"Score\" IS NULL OR \"Score\" BETWEEN 0 AND 100");

        migrationBuilder.CreateIndex(
            name: "IX_job_requirements_JobId",
            schema: "app",
            table: "job_requirements",
            column: "JobId",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_job_matches_profiles_ProfileId",
            schema: "app",
            table: "job_matches",
            column: "ProfileId",
            principalSchema: "app",
            principalTable: "profiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_job_matches_profiles_ProfileId",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropTable(
            name: "job_requirements",
            schema: "app");

        migrationBuilder.DropCheckConstraint(
            name: "ck_profiles_salary_currency",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropCheckConstraint(
            name: "ck_profiles_strong_match_threshold",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropIndex(
            name: "IX_job_matches_ProfileId_HasBlocker_Score",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropCheckConstraint(
            name: "ck_job_matches_score",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "AuthorizedCountries",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "JobTypes",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "MinSalary",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "NeedsSponsorshipElsewhere",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "PreferredLocations",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "RemotePreference",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "SalaryCurrency",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "StrongMatchThreshold",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "UpdatedAt",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "xmin",
            schema: "app",
            table: "profiles");

        migrationBuilder.DropColumn(
            name: "Confidence",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "Explanation",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "HasBlocker",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "InputsFingerprint",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "ScoringVersion",
            schema: "app",
            table: "job_matches");

        migrationBuilder.DropColumn(
            name: "DegreeLevel",
            schema: "app",
            table: "education");

        migrationBuilder.AlterColumn<decimal>(
            name: "Score",
            schema: "app",
            table: "job_matches",
            type: "numeric",
            nullable: false,
            defaultValue: 0m,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MatchedSkills",
            schema: "app",
            table: "job_matches",
            type: "jsonb",
            nullable: true);
    }
}
