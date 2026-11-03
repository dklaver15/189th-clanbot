using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class EventTitleLookupTests
{
    private const ulong Guild = 1;

    [Fact]
    public async Task Titles_come_from_the_calendar_then_the_clan_event()
    {
        using var test = new TestDb();
        int live, cancelled, blank, otherGuild;
        await using (var db = test.NewContext())
        {
            var liveCal = new CalendarEvent { GuildId = Guild, Title = "Helldivers Night" };
            var blankCal = new CalendarEvent { GuildId = Guild, Title = " " };
            var otherCal = new CalendarEvent { GuildId = 2, Title = "Someone else's event" };
            db.CalendarEvents.AddRange(liveCal, blankCal, otherCal);
            await db.SaveChangesAsync();
            (live, blank, otherGuild) = (liveCal.Id, blankCal.Id, otherCal.Id);

            // A cancelled ClanEvent whose CalendarEvent row has been deleted.
            cancelled = otherGuild + 100;
            db.ClanEvents.Add(new ClanEvent { GuildId = Guild, CalendarEventId = cancelled, MessageId = 10, Title = "Officer Meeting" });
            await db.SaveChangesAsync();
        }

        await using (var db = test.NewContext())
        {
            var titles = await EventAttendanceHelper.GetEventTitlesAsync(
                db, Guild, [live, live, cancelled, blank, otherGuild, 9999]);

            Assert.Equal("Helldivers Night", titles[live]);
            Assert.Equal("Officer Meeting", titles[cancelled]);
            Assert.False(titles.ContainsKey(blank));
            Assert.False(titles.ContainsKey(otherGuild));
            Assert.False(titles.ContainsKey(9999));
            Assert.Equal(2, titles.Count);
        }
    }

    [Fact]
    public async Task No_ids_means_no_queries_and_an_empty_result()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        Assert.Empty(await EventAttendanceHelper.GetEventTitlesAsync(db, Guild, []));
    }
}
