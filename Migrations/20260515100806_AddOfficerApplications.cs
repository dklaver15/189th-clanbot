using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddOfficerApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "OfficerAppButtonMessageId",
                table: "BotStates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OfficerApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Q1AreaOfInterest = table.Column<string>(type: "TEXT", nullable: false),
                    Q2Ideas = table.Column<string>(type: "TEXT", nullable: false),
                    Q3Conflict = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewedByUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ReviewedByUsername = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewNotes = table.Column<string>(type: "TEXT", nullable: true),
                    DossierMessageId = table.Column<ulong>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficerApplications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfficerApplications_GuildId_UserId_Status",
                table: "OfficerApplications",
                columns: new[] { "GuildId", "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficerApplications");

            migrationBuilder.DropColumn(
                name: "OfficerAppButtonMessageId",
                table: "BotStates");
        }
    }
}
