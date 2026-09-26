using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddJobDeduplication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Spec 0017: a job's identity moves from (JobSourceId, ExternalId) on the
        // job to one row per sighting in job_source_links. New columns and the
        // links table first, then a 1:1 backfill, then the old columns go.
        migrationBuilder.AddColumn<string>(
            name: "DedupKey",
            schema: "app",
            table: "jobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DedupRuleVersion",
            schema: "app",
            table: "jobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            name: "PrimaryLinkId",
            schema: "app",
            table: "jobs",
            type: "uuid",
            nullable: true);

        // The row's xmin as a concurrency token: Npgsql knows it is a system
        // column and adds nothing to the table.
        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "app",
            table: "jobs",
            type: "xid",
            rowVersion: true,
            nullable: false,
            defaultValue: 0u);

        migrationBuilder.AddColumn<string>(
            name: "CompanyName",
            schema: "app",
            table: "job_sources",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "job_source_links",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                JobSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SourceUrl = table.Column<string>(type: "text", nullable: false),
                FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Confidence = table.Column<decimal>(type: "numeric(3,2)", nullable: false),
                SplitAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_source_links", x => x.Id);
                table.ForeignKey(
                    name: "FK_job_source_links_job_sources_JobSourceId",
                    column: x => x.JobSourceId,
                    principalSchema: "app",
                    principalTable: "job_sources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_job_source_links_jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "app",
                    principalTable: "jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_jobs_DedupKey",
            schema: "app",
            table: "jobs",
            column: "DedupKey");

        migrationBuilder.CreateIndex(
            name: "IX_job_source_links_JobId",
            schema: "app",
            table: "job_source_links",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_job_source_links_JobSourceId_ExternalId",
            schema: "app",
            table: "job_source_links",
            columns: new[] { "JobSourceId", "ExternalId" },
            unique: true);

        // One link per existing job, from its own source pair and provenance.
        // Nothing merges here: the keys stay null and DedupRuleVersion 0, so
        // the startup reconcile job computes them and merges duplicates (AC-9).
        migrationBuilder.Sql(
            """
            INSERT INTO app.job_source_links ("Id", "JobId", "JobSourceId", "ExternalId", "SourceUrl", "FirstSeenAt", "LastSeenAt", "Confidence", "SplitAt")
            SELECT gen_random_uuid(), j."Id", j."JobSourceId", j."ExternalId", j."Provenance_SourceUrl",
                   j."Provenance_RetrievedAt", COALESCE(j."Provenance_VerifiedAt", j."Provenance_RetrievedAt"),
                   COALESCE(j."Provenance_Confidence", 1.0), NULL
            FROM app.jobs j;

            UPDATE app.jobs j SET "PrimaryLinkId" = l."Id"
            FROM app.job_source_links l WHERE l."JobId" = j."Id";
            """);

        // Hand written, keep if this migration is regenerated: a new job and its
        // first link point at each other, a cycle EF Core can't order inside one
        // SaveChanges, so this foreign key is checked at commit instead. Not in
        // the EF model on purpose (see JobConfiguration).
        migrationBuilder.Sql(
            """
            ALTER TABLE app.jobs ADD CONSTRAINT "FK_jobs_job_source_links_PrimaryLinkId"
                FOREIGN KEY ("PrimaryLinkId") REFERENCES app.job_source_links ("Id")
                ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED;
            """);


        migrationBuilder.DropIndex(
            name: "IX_jobs_JobSourceId_ExternalId",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "ExternalId",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "JobSourceId",
            schema: "app",
            table: "jobs");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restores each job's source pair from its primary link. Jobs merged by
        // the reconcile stay merged; the other links' ids live on only in snapshots.
        migrationBuilder.AddColumn<string>(
            name: "ExternalId",
            schema: "app",
            table: "jobs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "JobSourceId",
            schema: "app",
            table: "jobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE app.jobs j SET "JobSourceId" = l."JobSourceId", "ExternalId" = l."ExternalId"
            FROM app.job_source_links l WHERE l."Id" = j."PrimaryLinkId";

            ALTER TABLE app.jobs DROP CONSTRAINT "FK_jobs_job_source_links_PrimaryLinkId";
            ALTER TABLE app.jobs ALTER COLUMN "ExternalId" SET NOT NULL;
            ALTER TABLE app.jobs ALTER COLUMN "JobSourceId" SET NOT NULL;
            """);

        migrationBuilder.DropTable(
            name: "job_source_links",
            schema: "app");

        migrationBuilder.DropIndex(
            name: "IX_jobs_DedupKey",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "DedupKey",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "DedupRuleVersion",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "PrimaryLinkId",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "xmin",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "CompanyName",
            schema: "app",
            table: "job_sources");

        migrationBuilder.CreateIndex(
            name: "IX_jobs_JobSourceId_ExternalId",
            schema: "app",
            table: "jobs",
            columns: new[] { "JobSourceId", "ExternalId" },
            unique: true);
    }
}
