using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRosterTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "ChannelId",
                table: "VoiceSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChannelName",
                table: "VoiceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RankHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RankName = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceSessions_GuildId_UserId_ChannelId",
                table: "VoiceSessions",
                columns: new[] { "GuildId", "UserId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_RankHistories_GuildId_UserId",
                table: "RankHistories",
                columns: new[] { "GuildId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RankHistories");

            migrationBuilder.DropIndex(
                name: "IX_VoiceSessions_GuildId_UserId_ChannelId",
                table: "VoiceSessions");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "VoiceSessions");

            migrationBuilder.DropColumn(
                name: "ChannelName",
                table: "VoiceSessions");
        }
    }
}
