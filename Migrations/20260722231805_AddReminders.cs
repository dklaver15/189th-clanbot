using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClanReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    CreatorId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", nullable: false),
                    PingRoleIdsCsv = table.Column<string>(type: "TEXT", nullable: false),
                    PingUserIdsCsv = table.Column<string>(type: "TEXT", nullable: false),
                    PingEveryone = table.Column<bool>(type: "INTEGER", nullable: false),
                    PingHere = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextFireUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstFireLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextOccurrenceIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ImageFileName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFiredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClanReminders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClanReminders_GuildId_Status",
                table: "ClanReminders",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClanReminders_Status_NextFireUtc",
                table: "ClanReminders",
                columns: new[] { "Status", "NextFireUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClanReminders");
        }
    }
}
