using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddInviteTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InviteJoins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserDiscordId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    InviteCode = table.Column<string>(type: "TEXT", nullable: true),
                    LabelSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    InviterDiscordId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsAmbiguous = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InviteJoins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InviteSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    IsVanity = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByDiscordId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DiscordCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaxUses = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    DeactivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InviteSources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InviteJoins_GuildId_InviteCode",
                table: "InviteJoins",
                columns: new[] { "GuildId", "InviteCode" });

            migrationBuilder.CreateIndex(
                name: "IX_InviteJoins_GuildId_JoinedAt",
                table: "InviteJoins",
                columns: new[] { "GuildId", "JoinedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InviteJoins_UserDiscordId",
                table: "InviteJoins",
                column: "UserDiscordId");

            migrationBuilder.CreateIndex(
                name: "IX_InviteSources_CreatedByDiscordId",
                table: "InviteSources",
                column: "CreatedByDiscordId");

            migrationBuilder.CreateIndex(
                name: "IX_InviteSources_GuildId_Code",
                table: "InviteSources",
                columns: new[] { "GuildId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InviteSources_GuildId_IsActive",
                table: "InviteSources",
                columns: new[] { "GuildId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InviteJoins");

            migrationBuilder.DropTable(
                name: "InviteSources");
        }
    }
}
