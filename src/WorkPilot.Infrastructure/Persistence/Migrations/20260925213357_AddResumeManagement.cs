using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPilot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddResumeManagement : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CreatedAt",
            schema: "app",
            table: "resumes",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "now()");

        migrationBuilder.AddColumn<string>(
            name: "Kind",
            schema: "app",
            table: "resumes",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Base");

        migrationBuilder.AddColumn<Guid>(
            name: "SourceVersionId",
            schema: "app",
            table: "resumes",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TargetCompany",
            schema: "app",
            table: "resumes",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "StorageUrl",
            schema: "app",
            table: "resume_versions",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AddColumn<string>(
            name: "Content",
            schema: "app",
            table: "resume_versions",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "FileContentType",
            schema: "app",
            table: "resume_versions",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FileName",
            schema: "app",
            table: "resume_versions",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "FileSizeBytes",
            schema: "app",
            table: "resume_versions",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LockedAt",
            schema: "app",
            table: "resume_versions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "LockedByApplicationId",
            schema: "app",
            table: "resume_versions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Note",
            schema: "app",
            table: "resume_versions",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "UpdatedAt",
            schema: "app",
            table: "resume_versions",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "now()");

        migrationBuilder.CreateTable(
            name: "resume_files",
            schema: "app",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Data = table.Column<byte[]>(type: "bytea", nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resume_files", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_resumes_ProfileId",
            schema: "app",
            table: "resumes",
            column: "ProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_resumes_SourceVersionId",
            schema: "app",
            table: "resumes",
            column: "SourceVersionId");

        migrationBuilder.AddForeignKey(
            name: "FK_resumes_resume_versions_SourceVersionId",
            schema: "app",
            table: "resumes",
            column: "SourceVersionId",
            principalSchema: "app",
            principalTable: "resume_versions",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        // Hand written (keep it if this migration is ever regenerated): a version an application
        // used can never change or disappear, even through a racing request or raw SQL
        // (spec 0009, AC-5). SQLSTATE WP409 is mapped to HTTP 409 by ResumeService.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION app.resume_versions_block_locked_changes() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."LockedAt" IS NOT NULL THEN
                    RAISE EXCEPTION 'resume version % is locked (used by an application) and can never be changed', OLD."Id"
                        USING ERRCODE = 'WP409';
                END IF;
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER resume_versions_block_locked_changes
                BEFORE UPDATE OR DELETE ON app.resume_versions
                FOR EACH ROW EXECUTE FUNCTION app.resume_versions_block_locked_changes();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS resume_versions_block_locked_changes ON app.resume_versions;
            DROP FUNCTION IF EXISTS app.resume_versions_block_locked_changes();
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_resumes_resume_versions_SourceVersionId",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropTable(
            name: "resume_files",
            schema: "app");

        migrationBuilder.DropIndex(
            name: "IX_resumes_ProfileId",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropIndex(
            name: "IX_resumes_SourceVersionId",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "CreatedAt",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "Kind",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "SourceVersionId",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "TargetCompany",
            schema: "app",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "Content",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "FileContentType",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "FileName",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "FileSizeBytes",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "LockedAt",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "LockedByApplicationId",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "Note",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.DropColumn(
            name: "UpdatedAt",
            schema: "app",
            table: "resume_versions");

        migrationBuilder.AlterColumn<string>(
            name: "StorageUrl",
            schema: "app",
            table: "resume_versions",
            type: "text",
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
    }
}
