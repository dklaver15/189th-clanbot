using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddClanEventMessageIdFilteredIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClanEvents_MessageId",
                table: "ClanEvents");

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_MessageId_Unique",
                table: "ClanEvents",
                column: "MessageId",
                unique: true,
                filter: "\"MessageId\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClanEvents_MessageId_Unique",
                table: "ClanEvents");

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_MessageId",
                table: "ClanEvents",
                column: "MessageId",
                unique: true);
        }
    }
}
