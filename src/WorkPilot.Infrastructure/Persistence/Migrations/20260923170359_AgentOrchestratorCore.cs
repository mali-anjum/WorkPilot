using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AgentOrchestratorCore : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_agent_steps_AgentRunId",
            schema: "app",
            table: "agent_steps");

        // No Approval rows predate this feature (the Agent orchestrator core
        // is the first thing that ever writes one), so there is no real
        // DecidedBy value to preserve; blank any stray dev data instead of
        // attempting an automatic (and here, impossible) text->uuid cast.
        migrationBuilder.Sql("ALTER TABLE app.approvals ALTER COLUMN \"DecidedBy\" TYPE uuid USING NULL::uuid;");

        migrationBuilder.AddColumn<string>(
            name: "ArgumentsJson",
            schema: "app",
            table: "agent_steps",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Ordinal",
            schema: "app",
            table: "agent_steps",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "Status",
            schema: "app",
            table: "agent_steps",
            type: "character varying(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Pending");

        migrationBuilder.AddColumn<string>(
            name: "ToolName",
            schema: "app",
            table: "agent_steps",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<Guid>(
            name: "ProfileId",
            schema: "app",
            table: "agent_runs",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "app",
            table: "agent_runs",
            type: "xid",
            rowVersion: true,
            nullable: false,
            defaultValue: 0u);

        migrationBuilder.CreateIndex(
            name: "IX_agent_steps_AgentRunId_Ordinal",
            schema: "app",
            table: "agent_steps",
            columns: new[] { "AgentRunId", "Ordinal" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_runs_ProfileId",
            schema: "app",
            table: "agent_runs",
            column: "ProfileId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_agent_steps_AgentRunId_Ordinal",
            schema: "app",
            table: "agent_steps");

        migrationBuilder.DropIndex(
            name: "IX_agent_runs_ProfileId",
            schema: "app",
            table: "agent_runs");

        migrationBuilder.DropColumn(
            name: "ArgumentsJson",
            schema: "app",
            table: "agent_steps");

        migrationBuilder.DropColumn(
            name: "Ordinal",
            schema: "app",
            table: "agent_steps");

        migrationBuilder.DropColumn(
            name: "Status",
            schema: "app",
            table: "agent_steps");

        migrationBuilder.DropColumn(
            name: "ToolName",
            schema: "app",
            table: "agent_steps");

        migrationBuilder.DropColumn(
            name: "ProfileId",
            schema: "app",
            table: "agent_runs");

        migrationBuilder.DropColumn(
            name: "xmin",
            schema: "app",
            table: "agent_runs");

        migrationBuilder.AlterColumn<string>(
            name: "DecidedBy",
            schema: "app",
            table: "approvals",
            type: "text",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_steps_AgentRunId",
            schema: "app",
            table: "agent_steps",
            column: "AgentRunId");
    }
}
