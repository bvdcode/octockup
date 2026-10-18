using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octockup.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class QueueMetadataDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "deletion_error",
                table: "snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at",
                table: "snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deletion_error",
                table: "backups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at",
                table: "backups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deletion_error",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "deletion_error",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at",
                table: "backups");
        }
    }
}
