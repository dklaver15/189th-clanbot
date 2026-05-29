using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddApolloSortRebindTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DeleteOnCancelTimeout",
                table: "CalendarEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingCancelUntil",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_GuildId_ContentHash",
                table: "CalendarEvents",
                columns: new[] { "GuildId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_PendingCancelUntil",
                table: "CalendarEvents",
                column: "PendingCancelUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_GuildId_ContentHash",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_PendingCancelUntil",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "DeleteOnCancelTimeout",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "PendingCancelUntil",
                table: "CalendarEvents");
        }
    }
}
