using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AttachmentIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PosterUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PosterName = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalContent = table.Column<string>(type: "TEXT", nullable: false),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UploadedByUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    UploadedByName = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    YouTubeVideoId = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoSubmissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex",
                table: "VideoSubmissions",
                columns: new[] { "MessageId", "AttachmentIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoSubmissions");
        }
    }
}
