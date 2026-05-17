using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookSnapshotsAndBotStateField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastWebhookAuditScanCompletedUtc",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WebhookSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    WebhookId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    CreatorUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    CreatorUsername = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSnapshots_GuildId_ChannelId",
                table: "WebhookSnapshots",
                columns: new[] { "GuildId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSnapshots_GuildId_WebhookId",
                table: "WebhookSnapshots",
                columns: new[] { "GuildId", "WebhookId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookSnapshots");

            migrationBuilder.DropColumn(
                name: "LastWebhookAuditScanCompletedUtc",
                table: "BotStates");
        }
    }
}
