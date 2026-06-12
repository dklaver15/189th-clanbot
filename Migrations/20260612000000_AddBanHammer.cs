using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddBanHammer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BanHammerLastBanUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "BanHammerMessageId",
                table: "BotStates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BanHammerRecordDays",
                table: "BotStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BanHammerTotalBans",
                table: "BotStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BanHammerLastBanUtc",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "BanHammerMessageId",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "BanHammerRecordDays",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "BanHammerTotalBans",
                table: "BotStates");
        }
    }
}
