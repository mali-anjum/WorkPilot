using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddModuleContracts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outbox_messages",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_messages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "outbox_deliveries",
            schema: "app",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                HandlerKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                HandledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_deliveries", x => new { x.MessageId, x.HandlerKey });
                table.ForeignKey(
                    name: "FK_outbox_deliveries_outbox_messages_MessageId",
                    column: x => x.MessageId,
                    principalSchema: "app",
                    principalTable: "outbox_messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_undispatched",
            schema: "app",
            table: "outbox_messages",
            column: "OccurredAt",
            filter: "\"DispatchedAt\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox_deliveries",
            schema: "app");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "app");
    }
}
