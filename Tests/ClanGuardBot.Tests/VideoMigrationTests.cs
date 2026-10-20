using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ClanGuardBot.Tests;

/// <summary>
/// The real migrations on an in-memory SQLite database: rows written before
/// clips were forwarded must come out of the video rework's migrations usable.
/// </summary>
public class VideoMigrationTests
{
    private const string BeforeForwarding = "20260929221008_AddVideoSubmissionKeptOriginal";

    [Fact]
    public async Task Oversized_clips_kept_in_place_stay_uploadable_and_old_reposts_do_not()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeForwarding);

        // As the bot wrote them before forwarding: a repost (the bot's own message in
        // the clip channel) and an oversized clip answered in place under the member's
        // post, which is still there.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO VideoSubmissions
                (GuildId, ChannelId, MessageId, AttachmentIndex, FileName, OriginalMessageId, PosterUserId,
                 PosterName, OriginalContent, PostedUtc, Status, KeptOriginal)
            VALUES
                (1, 600, 10, 0, 'repost.mp4', 100, 42, 'Syrup', '', '2026-10-01 12:00:00', 0, 0),
                (1, 600, 20, 0, 'big.mp4',    200, 42, 'Syrup', '', '2026-10-01 12:00:00', 0, 1);
            """);

        await migrator.MigrateAsync();

        var rows = (await db.VideoSubmissions.AsNoTracking().ToListAsync()).OrderBy(v => v.MessageId).ToList();
        Assert.Null(rows[0].OriginalChannelId);          // repost: still an old message
        Assert.Equal(600ul, rows[1].OriginalChannelId);  // in place: the member's post is in the clip channel
        Assert.All(rows, r => Assert.Equal(VideoSubmissionStatus.Pending, r.Status));
    }
}
