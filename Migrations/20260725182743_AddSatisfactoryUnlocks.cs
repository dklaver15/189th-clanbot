using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactoryUnlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryUnlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    UnlockId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TechTier = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryUnlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryUnlocks_Seed_Kind",
                table: "SatisfactoryUnlocks",
                columns: new[] { "Seed", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryUnlocks_Seed_Kind_UnlockId",
                table: "SatisfactoryUnlocks",
                columns: new[] { "Seed", "Kind", "UnlockId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryUnlocks");
        }
    }
}
