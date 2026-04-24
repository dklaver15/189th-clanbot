using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRankHistorySeedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventsAttendedAtRankBeforeBot",
                table: "RankHistories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeedAppliedAt",
                table: "RankHistories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventsAttendedAtRankBeforeBot",
                table: "RankHistories");

            migrationBuilder.DropColumn(
                name: "SeedAppliedAt",
                table: "RankHistories");
        }
    }
}
