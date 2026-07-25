using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddXpLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "XpAwards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    EarnedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AwardedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpAwards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "XpMemberSeasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAnnouncedLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEarnedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpMemberSeasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "XpMemberTotals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    AllTimeXp = table.Column<long>(type: "INTEGER", nullable: false),
                    SeasonsPlayed = table.Column<int>(type: "INTEGER", nullable: false),
                    BestSeasonXp = table.Column<int>(type: "INTEGER", nullable: false),
                    BestSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BestSeasonPlace = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpMemberTotals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "XpSeasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpSeasons", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XpAwards_GuildId_SeasonId_UserId",
                table: "XpAwards",
                columns: new[] { "GuildId", "SeasonId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_XpAwards_GuildId_UserId_Source_SourceKey",
                table: "XpAwards",
                columns: new[] { "GuildId", "UserId", "Source", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XpMemberSeasons_GuildId_SeasonId_UserId",
                table: "XpMemberSeasons",
                columns: new[] { "GuildId", "SeasonId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XpMemberSeasons_GuildId_SeasonId_Xp",
                table: "XpMemberSeasons",
                columns: new[] { "GuildId", "SeasonId", "Xp" });

            migrationBuilder.CreateIndex(
                name: "IX_XpMemberTotals_GuildId_UserId",
                table: "XpMemberTotals",
                columns: new[] { "GuildId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XpSeasons_GuildId_Number",
                table: "XpSeasons",
                columns: new[] { "GuildId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XpSeasons_GuildId_Status",
                table: "XpSeasons",
                columns: new[] { "GuildId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XpAwards");

            migrationBuilder.DropTable(
                name: "XpMemberSeasons");

            migrationBuilder.DropTable(
                name: "XpMemberTotals");

            migrationBuilder.DropTable(
                name: "XpSeasons");
        }
    }
}
