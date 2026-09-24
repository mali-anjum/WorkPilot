using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddApprovalEngine : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EvidenceJson",
            schema: "app",
            table: "approvals",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ExplicitlyConfirmed",
            schema: "app",
            table: "approvals",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "RequestedAt",
            schema: "app",
            table: "approvals",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "now()");

        migrationBuilder.CreateIndex(
            name: "IX_approvals_Status_RequestedAt",
            schema: "app",
            table: "approvals",
            columns: new[] { "Status", "RequestedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_approvals_Status_RequestedAt",
            schema: "app",
            table: "approvals");

        migrationBuilder.DropColumn(
            name: "EvidenceJson",
            schema: "app",
            table: "approvals");

        migrationBuilder.DropColumn(
            name: "ExplicitlyConfirmed",
            schema: "app",
            table: "approvals");

        migrationBuilder.DropColumn(
            name: "RequestedAt",
            schema: "app",
            table: "approvals");
    }
}
