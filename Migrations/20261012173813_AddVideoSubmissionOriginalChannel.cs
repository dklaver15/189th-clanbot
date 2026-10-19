using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoSubmissionOriginalChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "OriginalChannelId",
                table: "VideoSubmissions",
                type: "INTEGER",
                nullable: true);

            // Oversized clips were answered in place under the member's post, so their
            // row's channel is the post's channel and the post is still there: they stay
            // uploadable. Reposts (the member's post was deleted) stay old messages.
            migrationBuilder.Sql("UPDATE VideoSubmissions SET OriginalChannelId = ChannelId WHERE KeptOriginal = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalChannelId",
                table: "VideoSubmissions");
        }
    }
}
