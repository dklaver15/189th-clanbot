using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Tests;

public class TestDbTests
{
    [Fact]
    public async Task Schema_is_created_and_rows_round_trip()
    {
        using var test = new TestDb();
        await using (var db = test.NewContext())
        {
            db.VideoSubmissions.Add(new VideoSubmission { GuildId = 1, ChannelId = 2, MessageId = 3, FileName = "a.mp4" });
            await db.SaveChangesAsync();
        }

        await using (var db = test.NewContext())
        {
            var row = await db.VideoSubmissions.SingleAsync();
            Assert.Equal(3ul, row.MessageId);
            Assert.Equal(VideoSubmissionStatus.Pending, row.Status);
        }
    }
}
