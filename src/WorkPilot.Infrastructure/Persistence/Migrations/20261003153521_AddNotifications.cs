using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Body",
                schema: "app",
                table: "notifications",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "app",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                schema: "app",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Link",
                schema: "app",
                table: "notifications",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "app",
                table: "notifications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                schema: "app",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Hand written (spec 0020): rows from before this feature get readable values, and a row
            // whose profile is gone is removed so the new foreign key can be added. Keep this SQL if
            // the migration is ever regenerated.
            migrationBuilder.Sql("""
                UPDATE app.notifications
                SET "CreatedAt" = CASE WHEN "CreatedAt" = '-infinity' OR "CreatedAt" < '1900-01-01' THEN now() ELSE "CreatedAt" END,
                    "Priority" = CASE WHEN "Priority" = '' THEN 'Info' ELSE "Priority" END,
                    "Title" = CASE WHEN "Title" = '' THEN left("Type", 200) ELSE "Title" END;
                DELETE FROM app.notifications n
                WHERE NOT EXISTS (SELECT 1 FROM app.profiles p WHERE p."Id" = n."ProfileId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_list",
                schema: "app",
                table: "notifications",
                columns: new[] { "ProfileId", "CreatedAt" },
                descending: new[] { false, true },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_unread",
                schema: "app",
                table: "notifications",
                column: "ProfileId",
                filter: "\"ReadAt\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "UX_notifications_open_digest",
                schema: "app",
                table: "notifications",
                columns: new[] { "ProfileId", "GroupKey" },
                unique: true,
                filter: "\"GroupKey\" IS NOT NULL AND \"ReadAt\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_profiles_ProfileId",
                schema: "app",
                table: "notifications",
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
                name: "FK_notifications_profiles_ProfileId",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_list",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_unread",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "UX_notifications_open_digest",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Body",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Link",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "app",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Title",
                schema: "app",
                table: "notifications");
        }
    }
}
