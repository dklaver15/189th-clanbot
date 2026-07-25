using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Turns activity the bot already records into XP.
///
/// ── Why derive instead of instrument ──
/// The obvious way to build XP is to hook the gateway and grant points as things
/// happen. This service deliberately doesn't. Every signal the ladder needs is
/// ALREADY captured, durably and with indexes, by machinery that has been running
/// and proving itself for months:
///
///   EventAttendances    — voice presence in the events VC, ≥30 min (EventAttendanceSnapshotService)
///   MeetingAttendances  — voice presence in the meeting VC, ≥15 min (MeetingAttendanceSnapshotService)
///   VoiceSessions       — every join→leave, with channel + category (ActivityTrackingHandler)
///   MessageEvents       — one row per message (ActivityTrackingHandler)
///
/// Hooking the gateway a second time would mean a second set of edge cases to get
/// wrong (restarts mid-session, the AFK channel, events that never got snapshotted)
/// and two sources of truth that would quietly disagree. Instead this runs on a
/// timer, re-derives XP over a rolling lookback window, and writes idempotent
/// ledger rows. Consequences worth knowing:
///
///   • XP is eventually-consistent, not instant — a member sees an op's XP within
///     an accrual cycle or two of the attendance snapshot landing, not the second
///     they leave voice. For a leaderboard, that is completely fine.
///   • The bot being down costs nothing. When it comes back the same window is
///     re-derived and everything missing is filled in.
///   • A mis-tuned XP value can be corrected in config; the daily rollups
///     retroactively repair themselves for anything still inside the lookback.
///
/// ── What is NOT paid for ──
/// Manual event credit (EventCredit rows carry the sentinel CalendarEventId = 0)
/// is skipped on purpose. Those rows exist so officers can fix promotion credit,
/// the unique index collapses all of a member's manual credit into a single row,
/// and paying XP off it would be both unbounded and un-auditable. Officers adjust
/// XP with /xp-adjust instead, which writes a visible ledger entry with a reason.
/// </summary>
public sealed class XpAccrualService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NoSeasonWarnInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// When the "no season running" warning was last logged. Throttles what would
    /// otherwise be one warning per cycle, forever, for a state that is perfectly
    /// legitimate between seasons.
    /// </summary>
    private DateTime? _noSeasonWarnedAt;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly XpService _xp;
    private readonly BotConfig _config;
    private readonly ILogger<XpAccrualService> _logger;

    public XpAccrualService(
        IServiceProvider services,
        DiscordSocketClient client,
        XpService xp,
        IOptions<BotConfig> config,
        ILogger<XpAccrualService> logger)
    {
        _services = services;
        _client = client;
        _xp = xp;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.XpEnabled)
        {
            _logger.LogInformation("XpAccrualService: XpEnabled is false — service idle.");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.XpAccrualIntervalMinutes));
        _logger.LogInformation("XpAccrualService started — every {Min}min, {Days}-day lookback",
            interval.TotalMinutes, _config.XpAccrualLookbackDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var guild in _client.Guilds.ToList())
            {
                try { await RunGuildAsync(guild, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogError(ex, "XP accrual failed for guild {Guild}", guild.Id); }
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Seasons are entirely manual. No season running means no XP accrues —
        // the accrual does NOT bank activity against an implicit season, because
        // that would quietly decide something the officers asked to decide
        // themselves. Both the board and /xp state this plainly so it can't sit
        // idle unnoticed; the warning is throttled so it doesn't fill the log at
        // one line per cycle forever.
        var season = await _xp.GetActiveSeasonAsync(db, guild.Id, ct);
        if (season is null)
        {
            if (_noSeasonWarnedAt is null || DateTime.UtcNow - _noSeasonWarnedAt > NoSeasonWarnInterval)
            {
                _noSeasonWarnedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "XP: no active season for guild {Guild} — nothing is accruing. An officer starts one with /xp-season start.",
                    guild.Id);
            }
            return;
        }

        _noSeasonWarnedAt = null;

        var now = DateTime.UtcNow;
        var lookbackDays = Math.Max(1, _config.XpAccrualLookbackDays);

        // Never look back past the season start — activity from a previous season
        // belongs to that season's ledger and must not be re-paid into this one.
        var windowStart = now.Date.AddDays(-(lookbackDays - 1));
        if (windowStart < season.StartUtc) windowStart = season.StartUtc;

        var changed = new HashSet<ulong>();

        await AccrueEventsAsync(db, guild, season, windowStart, changed, ct);
        await AccrueMeetingsAsync(db, guild, season, windowStart, changed, ct);
        await AccrueVoiceAsync(db, guild, season, windowStart, now, changed, ct);
        await AccrueMessagesAsync(db, guild, season, windowStart, now, changed, ct);

        if (changed.Count == 0)
        {
            // Still persist anything the season-open path may have added.
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var userId in changed)
        {
            var name = ResolveDisplayName(guild, userId);
            await _xp.RecomputeMemberAsync(db, guild.Id, season.Id, userId, name, ct);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("XP accrual: {Count} member(s) updated in guild {Guild}", changed.Count, guild.Id);

        await AnnounceLevelUpsAsync(db, guild, season, changed, ct);
    }

    // ─── Events ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Pays for attended events, the honoured-RSVP bonus, and attendance streaks.
    ///
    /// The RSVP bonus is the one place XP touches RSVPs at all. RSVPs are otherwise
    /// explicitly cosmetic (see EventRsvp) — but "said Going AND showed up" is worth
    /// paying for, because accurate RSVP counts are what let organisers plan an op.
    /// Note it only ever ADDS: nobody is penalised for RSVPing Going and then
    /// missing, since real life happens and a penalty would just teach people to
    /// stop RSVPing at all.
    /// </summary>
    private async Task AccrueEventsAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, DateTime windowStart,
        HashSet<ulong> changed, CancellationToken ct)
    {
        // CalendarEventId 0 is the manual-credit sentinel — see the class remarks.
        var recent = await db.EventAttendances
            .Where(a => a.GuildId == guild.Id && a.CalendarEventId != 0 && a.EventEndUtc >= windowStart)
            .ToListAsync(ct);

        if (recent.Count == 0) return;

        foreach (var a in recent)
        {
            if (IsBot(guild, a.UserId)) continue;

            if (await _xp.AwardAsync(db, guild.Id, season.Id, a.UserId, XpSource.Event,
                    $"evt:{a.CalendarEventId}", _config.XpPerEvent,
                    $"{a.AttendedMinutes} min", a.EventEndUtc, ct))
                changed.Add(a.UserId);
        }

        if (_config.XpRsvpHonoredBonus > 0)
            await AccrueRsvpBonusAsync(db, guild, season, recent, changed, ct);

        if (_config.XpStreak3Bonus > 0 || _config.XpStreak5Bonus > 0)
            await AccrueStreaksAsync(db, guild, season, recent, changed, ct);
    }

    private async Task AccrueRsvpBonusAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, List<EventAttendance> recent,
        HashSet<ulong> changed, CancellationToken ct)
    {
        var calendarIds = recent.Select(a => a.CalendarEventId).Distinct().ToList();

        // EventRsvp hangs off ClanEvent.Id, while attendance is keyed by
        // CalendarEvent.Id — hop through ClanEvent to bridge the two.
        var clanEvents = await db.ClanEvents
            .Where(e => e.GuildId == guild.Id && calendarIds.Contains(e.CalendarEventId))
            .Select(e => new { e.Id, e.CalendarEventId })
            .ToListAsync(ct);

        if (clanEvents.Count == 0) return;

        var clanEventIds = clanEvents.Select(e => e.Id).ToList();
        var calendarIdByClanEventId = clanEvents.ToDictionary(e => e.Id, e => e.CalendarEventId);

        var goingRsvps = await db.EventRsvps
            .Where(r => clanEventIds.Contains(r.ClanEventId) && r.Status == EventRsvpStatus.Going)
            .Select(r => new { r.ClanEventId, r.UserId })
            .ToListAsync(ct);

        var goingPairs = goingRsvps
            .Where(r => calendarIdByClanEventId.ContainsKey(r.ClanEventId))
            .Select(r => (CalendarEventId: calendarIdByClanEventId[r.ClanEventId], r.UserId))
            .ToHashSet();

        foreach (var a in recent)
        {
            if (IsBot(guild, a.UserId)) continue;
            if (!goingPairs.Contains((a.CalendarEventId, a.UserId))) continue;

            if (await _xp.AwardAsync(db, guild.Id, season.Id, a.UserId, XpSource.RsvpHonored,
                    $"rsvp:{a.CalendarEventId}", _config.XpRsvpHonoredBonus,
                    "RSVP'd Going and showed up", a.EventEndUtc, ct))
                changed.Add(a.UserId);
        }
    }

    /// <summary>
    /// Streak bonuses for consecutive attendance.
    ///
    /// "Consecutive" is measured against the events that actually HAPPENED this
    /// season — the distinct events that produced any attendance at all, in
    /// chronological order. An event nobody turned up to isn't counted as a miss
    /// against anyone (it evidently wasn't a real op), and an event a member
    /// attended can't be a miss either, so the run length is exactly "how many
    /// scheduled ops in a row did you make".
    ///
    /// Every 3rd consecutive op pays XpStreak3Bonus and every 5th pays
    /// XpStreak5Bonus, so a 15-op run collects both at 15. The alternative —
    /// paying once at 3 and once at 5 and then nothing forever — stops rewarding
    /// exactly the members the ladder most wants to keep.
    ///
    /// A miss resets the run to zero. Bonuses key off the event that COMPLETED the
    /// run, so they're insert-once and replay-safe like every other award.
    /// </summary>
    private async Task AccrueStreaksAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, List<EventAttendance> recent,
        HashSet<ulong> changed, CancellationToken ct)
    {
        // The season's event timeline. Small (tens of rows), so pulling the whole
        // thing is cheaper than trying to be clever about incremental streaks.
        var seasonRows = await db.EventAttendances
            .Where(a => a.GuildId == guild.Id && a.CalendarEventId != 0 && a.EventEndUtc >= season.StartUtc)
            .Select(a => new { a.CalendarEventId, a.UserId, a.EventEndUtc })
            .ToListAsync(ct);

        if (seasonRows.Count == 0) return;

        var timeline = seasonRows
            .GroupBy(r => r.CalendarEventId)
            .Select(g => new { CalendarEventId = g.Key, EndUtc = g.Min(x => x.EventEndUtc) })
            .OrderBy(e => e.EndUtc)
            .ThenBy(e => e.CalendarEventId)
            .ToList();

        var attendedBy = seasonRows
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.CalendarEventId).ToHashSet());

        // Only members who attended something inside the lookback window can have a
        // newly-completed run, so there's no need to re-walk everyone every cycle.
        var candidates = recent.Select(a => a.UserId).Distinct();

        foreach (var userId in candidates)
        {
            if (IsBot(guild, userId)) continue;
            if (!attendedBy.TryGetValue(userId, out var attended)) continue;

            var run = 0;
            foreach (var evt in timeline)
            {
                if (!attended.Contains(evt.CalendarEventId)) { run = 0; continue; }

                run++;

                if (_config.XpStreak3Bonus > 0 && run % 3 == 0)
                {
                    if (await _xp.AwardAsync(db, guild.Id, season.Id, userId, XpSource.EventStreak,
                            $"streak3:{evt.CalendarEventId}", _config.XpStreak3Bonus,
                            $"{run} events in a row", evt.EndUtc, ct))
                        changed.Add(userId);
                }

                if (_config.XpStreak5Bonus > 0 && run % 5 == 0)
                {
                    if (await _xp.AwardAsync(db, guild.Id, season.Id, userId, XpSource.EventStreak,
                            $"streak5:{evt.CalendarEventId}", _config.XpStreak5Bonus,
                            $"{run} events in a row", evt.EndUtc, ct))
                        changed.Add(userId);
                }
            }
        }
    }

    private async Task AccrueMeetingsAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, DateTime windowStart,
        HashSet<ulong> changed, CancellationToken ct)
    {
        var recent = await db.MeetingAttendances
            .Where(a => a.GuildId == guild.Id && a.CalendarEventId != 0 && a.EventEndUtc >= windowStart)
            .ToListAsync(ct);

        foreach (var a in recent)
        {
            if (IsBot(guild, a.UserId)) continue;

            if (await _xp.AwardAsync(db, guild.Id, season.Id, a.UserId, XpSource.Meeting,
                    $"meet:{a.CalendarEventId}", _config.XpPerMeeting,
                    $"{a.AttendedMinutes} min", a.EventEndUtc, ct))
                changed.Add(a.UserId);
        }
    }

    // ─── Voice ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Hanging out in voice, rolled up one row per member per UTC day so the daily
    /// cap is enforced by construction rather than by remembering what was already
    /// paid.
    ///
    /// Three exclusions keep this honest:
    ///   • the guild's AFK channel (idling is not participating);
    ///   • event and meeting VCs, when XpVoiceExcludeEventChannels is on — those
    ///     hours already pay 500/250 as attendance, and paying twice would blur
    ///     the "an op is worth ~7 hours of hanging out" ratio the economy is
    ///     built around;
    ///   • sessions shorter than XpVoiceMinSessionMinutes, which kills the
    ///     join/leave-repeatedly exploit.
    ///
    /// Long sessions are capped at MaxSingleSessionHours before being split across
    /// days, the same clamp VoiceActivityHelper and the attendance snapshots use,
    /// so someone who leaves a headset connected overnight doesn't out-earn a
    /// member who shows up to ops.
    /// </summary>
    private async Task AccrueVoiceAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, DateTime windowStart, DateTime now,
        HashSet<ulong> changed, CancellationToken ct)
    {
        if (_config.XpVoicePer15Minutes <= 0) return;

        var excluded = BuildExcludedVoiceChannels(guild);
        var excludedCategories = new HashSet<ulong>();
        if (_config.XpVoiceExcludeEventChannels && _config.EventsCategoryId != 0)
            excludedCategories.Add(_config.EventsCategoryId);

        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guild.Id
                        && v.JoinedAt < now
                        && (v.LeftAt == null || v.LeftAt > windowStart))
            .Select(v => new { v.UserId, v.JoinedAt, v.LeftAt, v.ChannelId, v.CategoryId })
            .ToListAsync(ct);

        if (sessions.Count == 0) return;

        var capSeconds = Math.Max(1.0, _config.MaxSingleSessionHours) * 3600.0;
        var minSeconds = Math.Max(0, _config.XpVoiceMinSessionMinutes) * 60.0;

        // (user, UTC day) → credited seconds
        var perDay = new Dictionary<(ulong UserId, DateTime Day), double>();

        foreach (var s in sessions)
        {
            if (s.ChannelId is { } chan && excluded.Contains(chan)) continue;
            if (s.CategoryId is { } cat && excludedCategories.Contains(cat)) continue;

            var start = s.JoinedAt;
            var end = s.LeftAt ?? now;
            if (end <= start) continue;

            // Clamp before splitting, so an unclosed marathon session can't
            // manufacture credited time on every day it spans.
            if ((end - start).TotalSeconds > capSeconds) end = start.AddSeconds(capSeconds);
            if ((end - start).TotalSeconds < minSeconds) continue;

            for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
            {
                if (day < windowStart.Date) continue;

                var dayStart = day;
                var dayEnd = day.AddDays(1);
                var from = start > dayStart ? start : dayStart;
                var to = end < dayEnd ? end : dayEnd;
                var seconds = (to - from).TotalSeconds;
                if (seconds <= 0) continue;

                perDay.TryGetValue((s.UserId, day), out var running);
                perDay[(s.UserId, day)] = running + seconds;
            }
        }

        foreach (var ((userId, day), seconds) in perDay)
        {
            if (IsBot(guild, userId)) continue;

            var blocks = (int)(seconds / 900.0);                       // whole 15-minute blocks
            var amount = blocks * _config.XpVoicePer15Minutes;
            if (_config.XpVoiceDailyCap > 0)
                amount = Math.Min(amount, _config.XpVoiceDailyCap);
            if (amount <= 0) continue;

            // Stamp the award at the END of the day (or now, for today) so season
            // attribution and the "reached it first" tiebreak stay sensible.
            var earnedAt = day.AddDays(1) > now ? now : day.AddDays(1).AddSeconds(-1);

            if (await _xp.AwardAsync(db, guild.Id, season.Id, userId, XpSource.Voice,
                    $"voice:{day:yyyy-MM-dd}", amount,
                    $"{(int)(seconds / 60)} min in voice", earnedAt, ct))
                changed.Add(userId);
        }
    }

    private HashSet<ulong> BuildExcludedVoiceChannels(SocketGuild guild)
    {
        var excluded = new HashSet<ulong>(_config.GetXpVoiceExcludedChannelIdsList());

        // Idling in AFK is the single most obvious way to farm a voice-time
        // metric, and Discord hands us the channel id directly.
        if (guild.AFKChannel is not null) excluded.Add(guild.AFKChannel.Id);

        if (_config.XpVoiceExcludeEventChannels)
        {
            if (_config.EventsVoiceChannelId != 0) excluded.Add(_config.EventsVoiceChannelId);
            if (_config.MeetingVoiceChannelId != 0) excluded.Add(_config.MeetingVoiceChannelId);
        }

        return excluded;
    }

    // ─── Chat ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Chat XP, rolled up per member per UTC day and capped hard.
    ///
    /// Deliberately near-worthless: at the default 2 XP × 10 messages the ceiling
    /// is 20 XP/day, so a full week of maxed-out chatting is worth about 28% of
    /// showing up to one op. That ratio is the point. Chat XP exists so quiet
    /// members see the number move, not as a route up the board — and because a
    /// leaderboard that rewards message volume turns a Discord into a spam
    /// contest.
    /// </summary>
    private async Task AccrueMessagesAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, DateTime windowStart, DateTime now,
        HashSet<ulong> changed, CancellationToken ct)
    {
        if (_config.XpPerMessage <= 0) return;

        var events = await db.MessageEvents
            .Where(m => m.GuildId == guild.Id && m.Timestamp >= windowStart && m.Timestamp <= now)
            .Select(m => new { m.UserId, m.Timestamp })
            .ToListAsync(ct);

        if (events.Count == 0) return;

        var perDay = events
            .GroupBy(m => (m.UserId, Day: m.Timestamp.Date))
            .Select(g => new { g.Key.UserId, g.Key.Day, Count = g.Count() });

        foreach (var d in perDay)
        {
            if (IsBot(guild, d.UserId)) continue;

            var counted = _config.XpMessageDailyCap > 0
                ? Math.Min(d.Count, _config.XpMessageDailyCap)
                : d.Count;

            var amount = counted * _config.XpPerMessage;
            if (amount <= 0) continue;

            var earnedAt = d.Day.AddDays(1) > now ? now : d.Day.AddDays(1).AddSeconds(-1);

            if (await _xp.AwardAsync(db, guild.Id, season.Id, d.UserId, XpSource.Message,
                    $"msg:{d.Day:yyyy-MM-dd}", amount,
                    $"{counted} message(s) counted", earnedAt, ct))
                changed.Add(d.UserId);
        }
    }

    // ─── Announcements ───────────────────────────────────────────────────────

    /// <summary>
    /// Posts MILESTONE level-ups to XpLevelUpAnnounceChannelId.
    ///
    /// ── Why milestones and not every level ──
    /// Mid-season a level costs roughly 900–1,200 XP and an op is worth ~650 with
    /// bonuses, so an active member levels up about once per op they attend. Across
    /// a clan of forty that is ~80 pings a week, which turns a celebration into a
    /// tax on the channel. Announcing only on multiples of
    /// XpLevelUpAnnounceEveryLevels (default 10) reduces that to roughly three
    /// posts per member per season — rare enough that each one still means
    /// something. Set the interval to 1 to announce every level.
    ///
    /// Only the HIGHEST milestone crossed is announced. If a member somehow clears
    /// two at once, they get one post rather than a burst.
    ///
    /// This intentionally never posts to the leaderboard channel: the board is
    /// meant to sit alone as the newest message there, and a stream of level-up
    /// pings would bury it.
    /// </summary>
    private async Task AnnounceLevelUpsAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, HashSet<ulong> changed, CancellationToken ct)
    {
        var channelId = _config.XpLevelUpAnnounceChannelId;
        if (channelId == 0) return;
        if (channelId == _config.XpBoardChannelId)
        {
            _logger.LogWarning(
                "XP: XpLevelUpAnnounceChannelId matches XpBoardChannelId — level-ups suppressed so they don't bury the leaderboard.");
            return;
        }
        if (guild.GetChannel(channelId) is not SocketTextChannel channel) return;

        var userIds = changed.ToList();
        var rows = await db.XpMemberSeasons
            .Where(m => m.GuildId == guild.Id
                        && m.SeasonId == season.Id
                        && userIds.Contains(m.UserId)
                        && m.Level > m.LastAnnouncedLevel)
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        var every = Math.Max(1, _config.XpLevelUpAnnounceEveryLevels);

        foreach (var row in rows)
        {
            var newLevel = row.Level;

            // Integer division floors each level to the milestone it sits at or
            // above, so crossing from 19 to 23 moves the milestone 10 → 20 and
            // fires once. Levels that don't cross one move the mark silently.
            var previousMilestone = row.LastAnnouncedLevel / every * every;
            var newMilestone = newLevel / every * every;

            // Raise the high-water mark regardless of whether the post succeeds,
            // so a permissions problem doesn't turn into a retry storm that spams
            // the channel the moment it's fixed.
            row.LastAnnouncedLevel = newLevel;

            if (newMilestone <= previousMilestone || newMilestone < every) continue;

            var embed = new EmbedBuilder()
                .WithTitle("⬆️ Level Up")
                .WithColor(new Color(0x57F287))
                .WithDescription($"<@{row.UserId}> reached **Level {newMilestone}** — {row.Xp:N0} XP this season.")
                .WithFooter(XpService.SeasonLabel(season))
                .Build();

            try
            {
                await channel.SendMessageAsync(
                    text: $"<@{row.UserId}>",
                    embed: embed,
                    allowedMentions: new AllowedMentions { UserIds = new List<ulong> { row.UserId } });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "XP: failed to announce level-up for {User}", row.UserId);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsBot(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId)?.IsBot == true;

    private static string? ResolveDisplayName(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId)?.DisplayName;
}
