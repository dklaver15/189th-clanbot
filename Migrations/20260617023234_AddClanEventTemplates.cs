using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddClanEventTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClanEventTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxParticipants = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ImageFileName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClanEventTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClanEventTemplates_GuildId",
                table: "ClanEventTemplates",
                column: "GuildId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClanEventTemplates");
        }
    }
}
