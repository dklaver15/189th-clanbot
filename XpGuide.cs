using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Builds the pinned "how XP works" post that sits directly above the
/// leaderboard.
///
/// ── Why this exists as its own message ──
/// The rules cannot live on the leaderboard itself. The board is a graphic that
/// changes every few minutes and gets paged; the rules are static prose that
/// people need to be able to read, search and quote. Splitting them means the
/// board can stay purely visual and the rules can stay properly explained.
///
/// ── Where the numbers come from ──
/// Every rate here is read from live config, and most of them are rendered into
/// the attached card by XpLeaderboardRenderer. Nothing about the economy is
/// hardcoded in this copy, so retuning a value in appsettings.json updates the
/// pinned guide on the next refresh instead of leaving it quietly wrong — which
/// is exactly how a "the bot is rigged" argument starts. For the same reason
/// nothing here promises an end-of-season level: an earlier draft said "around
/// Level 28 over a three-month season", which silently became false the moment
/// the season length changed.
///
/// ── Tone ──
/// Plain language, no callsign flavour. The copy leads with what XP is NOT,
/// because the single most likely way this feature causes a problem is a member
/// assuming a high level entitles them to a promotion. Everything else is kept as
/// short as it can be while still answering the questions that would otherwise
/// get asked in chat.
/// </summary>
public static class XpGuide
{
    /// <summary>
    /// Title of the guide embed. Doubles as the marker XpLeaderboardService uses
    /// to find its own message again after a restart, so it must stay stable —
    /// changing it orphans the existing pinned post and the service will post a
    /// fresh one (the old copy has to be deleted by hand).
    /// </summary>
    public const string GuideTitle = "📖 XP & Levels";

    public const string RatesImageFileName = "xp-rates.png";

    /// <summary>
    /// <paramref name="scheduled"/> is the season lined up to open later, if any.
    /// It is only ever non-null when <paramref name="season"/> is null: a season
    /// cannot be scheduled while another is running. The guide needs it so the
    /// pinned rules do not sit there saying nobody has started a season when one
    /// starts on Saturday.
    /// </summary>
    public static Embed Build(BotConfig config, XpSeason? season, XpSeason? scheduled, bool hasRatesImage)
    {
        var embed = new EmbedBuilder()
            .WithTitle(GuideTitle)
            .WithColor(new Color(0xF1C40F))
            .WithDescription(
                "**XP has nothing to do with promotions.** Rank is still earned the same way — by turning up, " +
                "and by your officers' judgement. This is just a scoreboard, so it's visible who's been " +
                "putting the work in.\n\n" +
                "Events are worth far more than everything else put together. That's deliberate. " +
                "Full rates below.");

        // The countdown goes in a FIELD, not the footer. Discord renders <t:...>
        // timestamp markdown in descriptions and field values but not in footers,
        // where it would show up as raw text.
        if (season is null && scheduled is not null)
        {
            var stamp = new DateTimeOffset(scheduled.StartUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            embed.AddField($"⏳ {XpService.SeasonLabel(scheduled)} starts",
                $"<t:{stamp}:F> (<t:{stamp}:R>)\nNothing counts until then. Everyone starts at zero.");
        }

        if (hasRatesImage)
            embed.WithImageUrl($"attachment://{RatesImageFileName}");
        else
            embed.AddField("How to earn XP", BuildRatesFallback(config));

        embed.AddField("How it works", BuildHowItWorks(config));
        embed.AddField("Levels", BuildLevels(config), inline: true);
        embed.AddField("Seasons", BuildSeasons(), inline: true);
        embed.AddField("Commands", BuildCommands());

        embed.WithFooter(season is not null
            ? $"{XpService.SeasonLabel(season)} • these rates update automatically if they're changed"
            : scheduled is not null
                ? "These rates update automatically if they're changed."
                : "No season is running right now. An officer will start one.");

        return embed.Build();
    }

    /// <summary>
    /// The rules people actually need, at the shortest length that still answers
    /// the questions that would otherwise get asked in chat. Five bullets rather
    /// than the original six: the AFK exclusion, the minimum session length and
    /// the events-channel exclusion all answer the same question — "why didn't my
    /// voice time count?" — so they belong on one line, not three.
    /// </summary>
    private static string BuildHowItWorks(BotConfig config) =>
        $"• **Event and meeting XP is automatic** — it comes from being in the voice channel during the " +
        $"event, {config.AutoPromotionMinEventAttendanceMinutes} minutes for an event and " +
        $"{config.AutoPromotionMinMeetingAttendanceMinutes} for a meeting.\n" +
        $"• **RSVPs don't decide attendance** — voice presence does. The bonus is only for saying Going and " +
        $"then turning up; you're never docked for missing one.\n" +
        $"• **Voice needs {config.XpVoiceMinSessionMinutes} unbroken minutes** to earn anything. AFK never " +
        $"counts, and neither does the events channel during an event — that already paid you " +
        $"{config.XpPerEvent:N0}.\n" +
        $"• **Chat is capped at {config.XpMessageDailyCap} messages a day**, on purpose.\n" +
        $"• **Streaks reset** if you miss an event other people made.";

    /// <summary>
    /// Anchors expectations with a figure derived from live config rather than a
    /// fixed claim about where you'll finish. See the class remarks for why.
    /// </summary>
    private static string BuildLevels(BotConfig config)
    {
        var perEvent = config.XpPerEvent + config.XpRsvpHonoredBonus;
        return
            $"1–{config.XpMaxLevel}, each costing a bit more than the last.\n\n" +
            $"Two events in a week is about **{perEvent * 2:N0} XP** — roughly a level a week.";
    }

    private static string BuildSeasons() =>
        "Season XP resets to **zero** when a season ends, so nobody's locked out for joining late.\n\n" +
        "Your **all-time total** and your best finish are kept.";

    private static string BuildCommands() =>
        "`/xp` — your card, and exactly where your XP came from\n" +
        "`/xp member:@someone` — someone else's\n" +
        "`/xp-leaderboard` — the full standings, privately\n" +
        "`/xp-dms` — turn level-up DMs on or off\n\n" +
        "Think something's off? Every point is logged — run `/xp`, check the breakdown, then grab an officer.";

    /// <summary>
    /// Text version of the rates, used only when the rendered card failed. Ugly
    /// next to the graphic, but a pinned guide with no rates on it would be
    /// useless, and render failures (missing font, missing native lib) are exactly
    /// the kind of thing that shows up first in production.
    /// </summary>
    private static string BuildRatesFallback(BotConfig config)
    {
        var lines = new List<string>();
        if (config.XpPerEvent > 0) lines.Add($"**{config.XpPerEvent:N0} XP** — attend an event");
        if (config.XpPerMeeting > 0) lines.Add($"**{config.XpPerMeeting:N0} XP** — attend a clan meeting");
        if (config.XpRsvpHonoredBonus > 0) lines.Add($"**+{config.XpRsvpHonoredBonus:N0} XP** — RSVP \"Going\" and show up");
        if (config.XpStreak3Bonus > 0) lines.Add($"**+{config.XpStreak3Bonus:N0} XP** — every 3 events in a row");
        if (config.XpStreak5Bonus > 0) lines.Add($"**+{config.XpStreak5Bonus:N0} XP** — every 5 events in a row");
        if (config.XpVoicePer15Minutes > 0)
            lines.Add($"**{config.XpVoicePer15Minutes} XP** per 15 min in voice _(max {config.XpVoiceDailyCap}/day)_");
        if (config.XpPerMessage > 0)
            lines.Add($"**{config.XpPerMessage} XP** per message _(max {config.XpPerMessage * config.XpMessageDailyCap}/day)_");

        return string.Join('\n', lines);
    }
}
