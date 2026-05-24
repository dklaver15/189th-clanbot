using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRankChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RankChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    FromRank = table.Column<string>(type: "TEXT", nullable: true),
                    ToRank = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RankChanges_GuildId_ChangedAt",
                table: "RankChanges",
                columns: new[] { "GuildId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RankChanges_GuildId_UserId_ChangedAt",
                table: "RankChanges",
                columns: new[] { "GuildId", "UserId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RankChanges");
        }
    }
}
