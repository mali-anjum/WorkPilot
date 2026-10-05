using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddJobsList : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "job_dismissals",
            schema: "app",
            columns: table => new
            {
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_dismissals", x => new { x.ProfileId, x.JobId });
                table.ForeignKey(
                    name: "FK_job_dismissals_jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "app",
                    principalTable: "jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_job_dismissals_profiles_ProfileId",
                    column: x => x.ProfileId,
                    principalSchema: "app",
                    principalTable: "profiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_jobs_PostedAt_listed",
            schema: "app",
            table: "jobs",
            column: "PostedAt",
            descending: new bool[0],
            filter: "\"IsDeleted\" = false");

        migrationBuilder.CreateIndex(
            name: "IX_job_dismissals_JobId",
            schema: "app",
            table: "job_dismissals",
            column: "JobId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "job_dismissals",
            schema: "app");

        migrationBuilder.DropIndex(
            name: "IX_jobs_PostedAt_listed",
            schema: "app",
            table: "jobs");
    }
}
