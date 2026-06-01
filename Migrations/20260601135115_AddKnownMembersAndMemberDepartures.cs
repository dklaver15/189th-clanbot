using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownMembersAndMemberDepartures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnownMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    JoinedAtCached = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RankCached = table.Column<string>(type: "TEXT", nullable: false),
                    RolesCached = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemberDepartures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    DepartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    JoinedAtSource = table.Column<string>(type: "TEXT", nullable: false),
                    TenureDays = table.Column<double>(type: "REAL", nullable: true),
                    Classification = table.Column<string>(type: "TEXT", nullable: false),
                    DepartureDetection = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    RankAtDeparture = table.Column<string>(type: "TEXT", nullable: false),
                    RolesAtDeparture = table.Column<string>(type: "TEXT", nullable: false),
                    WasGuest = table.Column<bool>(type: "INTEGER", nullable: false),
                    HadReserve = table.Column<bool>(type: "INTEGER", nullable: false),
                    MessagesLifetime = table.Column<int>(type: "INTEGER", nullable: false),
                    EventsAttendedLifetime = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRejoin = table.Column<bool>(type: "INTEGER", nullable: false),
                    JoinSourceLabel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClassifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberDepartures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownMembers_GuildId_UserId",
                table: "KnownMembers",
                columns: new[] { "GuildId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberDepartures_GuildId_Classification_DepartedAt",
                table: "MemberDepartures",
                columns: new[] { "GuildId", "Classification", "DepartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemberDepartures_GuildId_DepartedAt",
                table: "MemberDepartures",
                columns: new[] { "GuildId", "DepartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemberDepartures_GuildId_UserId_DepartedAt",
                table: "MemberDepartures",
                columns: new[] { "GuildId", "UserId", "DepartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownMembers");

            migrationBuilder.DropTable(
                name: "MemberDepartures");
        }
    }
}
