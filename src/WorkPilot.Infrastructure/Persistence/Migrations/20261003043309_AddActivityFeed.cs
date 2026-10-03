using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddActivityFeed : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_logs_OccurredAt",
            schema: "app",
            table: "audit_logs");

        migrationBuilder.AddColumn<string>(
            name: "Category",
            schema: "app",
            table: "audit_logs",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "System");

        // HAND WRITTEN (spec 0011): keep when regenerating. Backfills every existing row with
        // the same first match wins rules as AuditCategories.For; change both together. Old rows
        // carry no failed flag, so a failed tool call is found by its payload: AdvanceRunJob
        // wraps a tool's error as the object {"error": ...} and nothing else.
        migrationBuilder.Sql("""
            UPDATE app.audit_logs SET "Category" = CASE
                WHEN "Action" IN ('PlanningFailed', 'ApprovalGateRefused') OR "Action" LIKE '%Failed' THEN 'Errors'
                WHEN "Actor" = 'Agent' AND jsonb_typeof("Payload") = 'object' AND "Payload" ? 'error'
                    AND (SELECT count(*) FROM jsonb_object_keys("Payload")) = 1 THEN 'Errors'
                WHEN "TargetType" IN ('Job', 'JobSource', 'JobMatch') THEN 'Jobs'
                WHEN "TargetType" IN ('AgentRun', 'AgentStep', 'Approval') THEN 'Agent'
                WHEN "TargetType" IN ('OutreachMessage', 'EmailThread', 'OutreachContact') THEN 'Email'
                WHEN "TargetType" = 'CalendarEvent' THEN 'Calendar'
                WHEN "Actor" = 'Agent' THEN 'Agent'
                ELSE 'System'
            END;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_feed",
            schema: "app",
            table: "audit_logs",
            columns: new[] { "OccurredAt", "Id" },
            descending: new bool[0],
            filter: "\"IsDeleted\" = false");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_feed_category",
            schema: "app",
            table: "audit_logs",
            columns: new[] { "Category", "OccurredAt", "Id" },
            descending: new[] { false, true, true },
            filter: "\"IsDeleted\" = false");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_logs_feed",
            schema: "app",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "IX_audit_logs_feed_category",
            schema: "app",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "Category",
            schema: "app",
            table: "audit_logs");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_OccurredAt",
            schema: "app",
            table: "audit_logs",
            column: "OccurredAt");
    }
}
