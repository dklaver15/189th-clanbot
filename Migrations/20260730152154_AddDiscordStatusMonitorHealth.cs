using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordStatusMonitorHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DiscordStatusPollAlertedAtUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscordStatusPollConsecutiveFailures",
                table: "BotStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDiscordStatusPollAttemptUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordStatusPollAlertedAtUtc",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "DiscordStatusPollConsecutiveFailures",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "LastDiscordStatusPollAttemptUtc",
                table: "BotStates");
        }
    }
}
