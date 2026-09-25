using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddJobIngestion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Description",
            schema: "app",
            table: "jobs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PostedAt",
            schema: "app",
            table: "jobs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Type",
            schema: "app",
            table: "job_sources",
            type: "character varying(50)",
            maxLength: 50,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AddColumn<string>(
            name: "ExternalId",
            schema: "app",
            table: "job_snapshots",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<Guid>(
            name: "JobSourceId",
            schema: "app",
            table: "job_snapshots",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        // Backfill any pre-existing snapshots from their job, so the new
        // foreign key below never sees the placeholder default.
        migrationBuilder.Sql(
            """
            UPDATE app.job_snapshots AS s
            SET "JobSourceId" = j."JobSourceId", "ExternalId" = left(j."ExternalId", 200)
            FROM app.jobs AS j
            WHERE s."JobId" = j."Id";
            """);

        migrationBuilder.CreateIndex(
            name: "IX_job_sources_Type_Name",
            schema: "app",
            table: "job_sources",
            columns: new[] { "Type", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_job_snapshots_JobSourceId_ExternalId",
            schema: "app",
            table: "job_snapshots",
            columns: new[] { "JobSourceId", "ExternalId" });

        migrationBuilder.AddForeignKey(
            name: "FK_job_snapshots_job_sources_JobSourceId",
            schema: "app",
            table: "job_snapshots",
            column: "JobSourceId",
            principalSchema: "app",
            principalTable: "job_sources",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_job_snapshots_job_sources_JobSourceId",
            schema: "app",
            table: "job_snapshots");

        migrationBuilder.DropIndex(
            name: "IX_job_sources_Type_Name",
            schema: "app",
            table: "job_sources");

        migrationBuilder.DropIndex(
            name: "IX_job_snapshots_JobSourceId_ExternalId",
            schema: "app",
            table: "job_snapshots");

        migrationBuilder.DropColumn(
            name: "Description",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "PostedAt",
            schema: "app",
            table: "jobs");

        migrationBuilder.DropColumn(
            name: "ExternalId",
            schema: "app",
            table: "job_snapshots");

        migrationBuilder.DropColumn(
            name: "JobSourceId",
            schema: "app",
            table: "job_snapshots");

        migrationBuilder.AlterColumn<string>(
            name: "Type",
            schema: "app",
            table: "job_sources",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(50)",
            oldMaxLength: 50);
    }
}
