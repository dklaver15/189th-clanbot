using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupAndExportTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRosterExportCompletedUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSqliteBackupCompletedUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSqliteBackupError",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastSqliteBackupSizeBytes",
                table: "BotStates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRosterExportCompletedUtc",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "LastSqliteBackupCompletedUtc",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "LastSqliteBackupError",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "LastSqliteBackupSizeBytes",
                table: "BotStates");
        }
    }
}
