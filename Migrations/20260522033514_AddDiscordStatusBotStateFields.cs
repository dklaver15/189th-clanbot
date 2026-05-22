using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordStatusBotStateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastDiscordStatusPollCompletedUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastDiscordStatusPollError",
                table: "BotStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastDiscordStatusPollCompletedUtc",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "LastDiscordStatusPollError",
                table: "BotStates");
        }
    }
}
