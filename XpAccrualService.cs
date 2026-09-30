using System.Collections.Concurrent;
using System.Text;
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

    /// <summary>Hard cap on a single level-up DM attempt (open channel + send). See TrySendLevelUpDmAsync.</summary>
    private static readonly TimeSpan DmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Hard cap on a single public announcement send. Same rate-limit rationale as DmTimeout.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When the "no season running" warning was last logged. Throttles what would
    /// otherwise be one warning per cycle, forever, for a state that is perfectly
    /// legitimate between seasons.
    /// </summary>
    /// <summary>
    /// Per guild, because a single shared field does not throttle anything once the
    /// bot is in more than one. A guild WITH a season reset it to null on every
    /// cycle, so the guild without one warned every ten minutes instead of every
    /// six hours: 89 lines in a day, which is how you teach someone to ignore the
    /// warning that matters.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _noSeasonWarnedAt = new();

    /// <summary>
    /// Serialises accrual for a guild. Two passes over the same window are safe on
    /// their own (every award is idempotent), but the season scheduler now triggers
    /// an out-of-band pass right before a close, and that one racing the timer pass
    /// would have both computing standings from a half-written rollup.
    /// </summary>
    private readonly SemaphoreSlim _accrualLock = new(1, 1);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly XpService _xp;
    private readonly BotConfig _config;
    private readonly ILogger<XpAccrualService> _logger;

    /// <summary>
    /// The clan's time zone (EventDefaultTimeZone). Daily caps reset at its
    /// midnight, not UTC's, which lands at 6–7 pm Central.
    /// </summary>
    private readonly TimeZoneInfo _dayZone;

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

        if (TimeZoneInfo.TryFindSystemTimeZoneById(_config.EventDefaultTimeZone, out var tz) && tz is not null)
        {
            _dayZone = tz;
        }
        else
        {
            _dayZone = TimeZoneInfo.Utc;
            _logger.LogWarning(
                "XP: EventDefaultTimeZone '{Tz}' is not a valid timezone id; daily caps will use UTC days.",
                _config.EventDefaultTimeZone);
        }
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
                try { await RunGuildLockedAsync(guild, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogError(ex, "XP accrual failed for guild {Guild}", guild.Id); }
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs one accrual pass NOW and waits for it, so the caller can rely on the
    /// rollups being current when it returns.
    ///
    /// This exists for exactly one caller: XpSeasonSchedulerService, immediately
    /// before it closes a season. Final standings are read from XpMemberSeasons,
    /// which only this service writes, and the timer only fires every
    /// XpAccrualIntervalMinutes. Without this, everything earned between the last
    /// tick and the close instant is never credited: the season ends, accrual sees
    /// no active season and returns early, and that activity is lost permanently
    /// from a results card that is the season's only permanent record.
    /// </summary>
    public async Task RunFinalSweepAsync(SocketGuild guild, CancellationToken ct)
    {
        if (!_config.XpEnabled) return;

        _logger.LogInformation("XP: running a final accrual pass for guild {Guild} before the season closes", guild.Id);
        await RunGuildLockedAsync(guild, ct);
    }

    private async Task RunGuildLockedAsync(SocketGuild guild, CancellationToken ct)
    {
        await _accrualLock.WaitAsync(ct);
        try { await RunGuildAsync(guild, ct); }
        finally { _accrualLock.Release(); }
    }

    private async Task RunGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // A season is only ever opened by an officer, whether they typed the command
        // now or scheduled it earlier. No season running means no XP accrues —
        // the accrual does NOT bank activity against an implicit season, because
        // that would quietly decide something the officers asked to decide
        // themselves. Both the board and /xp state this plainly so it can't sit
        // idle unnoticed; the warning is throttled so it doesn't fill the log at
        // one line per cycle forever.
        var season = await _xp.GetActiveSeasonAsync(db, guild.Id, ct);
        if (season is null)
        {
            if (!_noSeasonWarnedAt.TryGetValue(guild.Id, out var lastWarn)
                || DateTime.UtcNow - lastWarn > NoSeasonWarnInterval)
            {
                _noSeasonWarnedAt[guild.Id] = DateTime.UtcNow;

                // A season lined up to open is not a problem, it is the plan. Warning
                // about it would teach whoever reads the log to ignore the warning
                // that matters, which is the ladder sitting idle with nothing planned.
                var pending = await _xp.GetScheduledSeasonAsync(db, guild.Id, ct);
                if (pending is not null)
                {
                    _logger.LogInformation(
                        "XP: nothing accruing in guild {Guild} yet. Season {Number} opens at {Start}.",
                        guild.Id, pending.Number, pending.StartUtc.ToString("u"));
                }
                else
                {
                    _logger.LogWarning(
                        "XP: no active season for guild {Guild}, nothing is accruing and nothing is scheduled. " +
                        "An officer starts one with /xp-season start.",
                        guild.Id);
                }
            }
            return;
        }

        _noSeasonWarnedAt.TryRemove(guild.Id, out _);

        var now = DateTime.UtcNow;
        var lookbackDays = Math.Max(1, _config.XpAccrualLookbackDays);

        // Starts at a local midnight so the oldest day in the window is rebuilt
        // whole. Never look back past the season start — activity from a previous
        // season belongs to that season's ledger and must not be re-paid into this one.
        var windowStart = LocalMidnightUtc(LocalDay(now).AddDays(-(lookbackDays - 1)));
        if (windowStart < season.StartUtc) windowStart = season.StartUtc;

        var changed = new HashSet<ulong>();

        await AccrueEventsAsync(db, guild, season, windowStart, changed, ct);
        await AccrueMeetingsAsync(db, guild, season, windowStart, changed, ct);
        await AccrueVoiceAsync(db, guild, season, windowStart, now, changed, ct);
        await AccrueMessagesAsync(db, guild, season, windowStart, now, changed, ct);

        // MUST flush before recomputing. RecomputeMemberAsync sums XpAwards with a
        // server-side aggregate, and EF Core does NOT include pending Added entities
        // in one — verified: a tracked-but-unsaved 500 XP award returns SUM = 0.
        // Recomputing first wrote a rollup that ignored everything this cycle earned,
        // and because awards are idempotent the next cycle saw no change, skipped the
        // recompute entirely, and the wrong total stuck permanently.
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

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

        // Every participant is re-walked, not just those with attendance in the
        // lookback window. An event whose snapshot lands late is INSERTED into the
        // middle of the timeline, which can break a run for somebody who has no
        // recent attendance at all — and they'd otherwise keep a bonus they no
        // longer qualify for. Tens of events × tens of members is trivial work.
        var existing = await db.XpAwards
            .Where(a => a.GuildId == guild.Id && a.SeasonId == season.Id && a.Source == XpSource.EventStreak)
            .ToListAsync(ct);

        var existingByUser = existing
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var userId in attendedBy.Keys)
        {
            if (IsBot(guild, userId)) continue;

            var attended = attendedBy[userId];

            // Recompute the member's ENTIRE set of earned streak keys from the current
            // timeline, then make the ledger match it exactly.
            //
            // Reconciling rather than only adding is what makes late snapshots safe.
            // The timeline is built from attendance rows, so an event that snapshots
            // hours late slots into the middle and shifts every later position by one.
            // Add-only, that re-points each bonus at a different event id while the old
            // rows stay — a 15-event run re-indexed once could pay out ~1,400 XP of
            // phantom bonus. Reconciling also correctly REVOKES a bonus when a
            // late-arriving event turns out to have broken the run.
            var desired = new Dictionary<string, (int Amount, string Note, DateTime EarnedAt)>();
            var run = 0;

            foreach (var evt in timeline)
            {
                if (!attended.Contains(evt.CalendarEventId)) { run = 0; continue; }

                run++;

                if (_config.XpStreak3Bonus > 0 && run % 3 == 0)
                    desired[$"streak3:{evt.CalendarEventId}"] =
                        (_config.XpStreak3Bonus, $"{run} events in a row", evt.EndUtc);

                if (_config.XpStreak5Bonus > 0 && run % 5 == 0)
                    desired[$"streak5:{evt.CalendarEventId}"] =
                        (_config.XpStreak5Bonus, $"{run} events in a row", evt.EndUtc);
            }

            foreach (var (key, v) in desired)
            {
                if (await _xp.AwardAsync(db, guild.Id, season.Id, userId, XpSource.EventStreak,
                        key, v.Amount, v.Note, v.EarnedAt, ct))
                    changed.Add(userId);
            }

            if (!existingByUser.TryGetValue(userId, out var mine)) continue;

            foreach (var stale in mine.Where(a => !desired.ContainsKey(a.SourceKey)))
            {
                db.XpAwards.Remove(stale);
                changed.Add(userId);
                _logger.LogInformation(
                    "XP: revoked stale streak award {Key} from {User} — the event timeline changed",
                    stale.SourceKey, userId);
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
    /// Hanging out in voice, rolled up one row per member per clan-local day so the daily
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

        // (user, local day) → credited seconds
        var perDay = new Dictionary<(ulong UserId, DateOnly Day), double>();

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

            for (var day = LocalDay(start); day <= LocalDay(end); day = day.AddDays(1))
            {
                var dayBegin = LocalMidnightUtc(day);
                var dayEnd = LocalMidnightUtc(day.AddDays(1));
                if (dayEnd <= windowStart) continue;

                // Clamp to windowStart itself, not windowStart.Date. On the day a
                // season opens, windowStart carries a time of day; truncating it to
                // midnight credited voice time from BEFORE the season existed, while
                // AccrueMessagesAsync (which filters on the full timestamp) did not —
                // so voice and chat disagreed about when the season started.
                var dayStart = dayBegin > windowStart ? dayBegin : windowStart;
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
            var dayEndUtc = LocalMidnightUtc(day.AddDays(1));
            var earnedAt = dayEndUtc > now ? now : dayEndUtc.AddSeconds(-1);

            if (await _xp.AwardAsync(db, guild.Id, season.Id, userId, XpSource.Voice,
                    $"voice:{season.Id}:{day:yyyy-MM-dd}", amount,
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
    /// Chat XP, rolled up per member per clan-local day and capped hard.
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
            .GroupBy(m => (m.UserId, Day: LocalDay(m.Timestamp)))
            .Select(g => new { g.Key.UserId, g.Key.Day, Count = g.Count() });

        foreach (var d in perDay)
        {
            if (IsBot(guild, d.UserId)) continue;

            var counted = _config.XpMessageDailyCap > 0
                ? Math.Min(d.Count, _config.XpMessageDailyCap)
                : d.Count;

            var amount = counted * _config.XpPerMessage;
            if (amount <= 0) continue;

            var dayEndUtc = LocalMidnightUtc(d.Day.AddDays(1));
            var earnedAt = dayEndUtc > now ? now : dayEndUtc.AddSeconds(-1);

            if (await _xp.AwardAsync(db, guild.Id, season.Id, d.UserId, XpSource.Message,
                    $"msg:{season.Id}:{d.Day:yyyy-MM-dd}", amount,
                    $"{counted} message(s) counted", earnedAt, ct))
                changed.Add(d.UserId);
        }
    }

    // ─── Clan-local days ─────────────────────────────────────────────────────

    /// <summary>The clan-local date a UTC instant falls on.</summary>
    private DateOnly LocalDay(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _dayZone));

    /// <summary>
    /// UTC instant of the local midnight that starts <paramref name="day"/>.
    /// Mirrors SatisfactoryFactoryService.LocalMidnightToUtc: a skipped midnight
    /// steps forward, and a repeated one resolves to standard time.
    /// </summary>
    private DateTime LocalMidnightUtc(DateOnly day)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (_dayZone.IsInvalidTime(local))
            local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, _dayZone);
    }

    // ─── Announcements ───────────────────────────────────────────────────────

    /// <summary>
    /// Handles both level-up surfaces:
    ///
    ///   • a DM to the member on EVERY level they gain (private, opt-outable by
    ///     the member simply having DMs closed);
    ///   • a public post to XpLevelUpAnnounceChannelId only when they cross a
    ///     multiple of XpLevelUpAnnounceEveryLevels (default 10).
    ///
    /// ── Why the split ──
    /// Mid-season a level costs roughly 900\u20131,200 XP and an event is worth ~650
    /// with bonuses, so an active member levels about once per event. Posting all
    /// of those publicly is ~80 messages a week across a clan of forty, which
    /// turns a celebration into a tax on the channel. But the member themselves
    /// does want to know every time \u2014 that is the feedback loop the whole ladder
    /// runs on. So the frequent signal goes privately and only the rare one is
    /// public.
    ///
    /// Only the HIGHEST milestone crossed is posted publicly, and a multi-level
    /// jump produces a single DM naming the level they landed on, so neither
    /// surface can burst.
    ///
    /// The public post intentionally never goes to the leaderboard channel: the
    /// board is meant to sit alone as the newest message there.
    /// </summary>
    private async Task AnnounceLevelUpsAsync(
        BotDbContext db, SocketGuild guild, XpSeason season, HashSet<ulong> changed, CancellationToken ct)
    {
        var channelId = _config.XpLevelUpAnnounceChannelId;

        var channel = channelId != 0 && channelId != _config.XpBoardChannelId
            ? guild.GetChannel(channelId) as SocketTextChannel
            : null;

        if (channelId != 0 && channelId == _config.XpBoardChannelId)
            _logger.LogWarning(
                "XP: XpLevelUpAnnounceChannelId matches XpBoardChannelId \u2014 public level-ups suppressed so they don't bury the leaderboard.");

        // Nothing to do at all if neither surface is available.
        if (channel is null && !_config.XpLevelUpDmEnabled) return;

        var userIds = changed.ToList();
        var rows = await db.XpMemberSeasons
            .Where(m => m.GuildId == guild.Id
                        && m.SeasonId == season.Id
                        && userIds.Contains(m.UserId)
                        && m.Level > m.LastAnnouncedLevel)
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        // One indexed read for the whole cycle. The table only holds people who
        // have actively opted out, so this is a handful of rows at most.
        var optedOut = _config.XpLevelUpDmEnabled
            ? (await db.XpDmOptOuts
                .Where(o => o.GuildId == guild.Id)
                .Select(o => o.UserId)
                .ToListAsync(ct)).ToHashSet()
            : new HashSet<ulong>();

        var every = Math.Max(1, _config.XpLevelUpAnnounceEveryLevels);

        // try/finally so the raised LastAnnouncedLevel marks are persisted even if the
        // batch is cut short by host shutdown. Without it, a restart part-way through
        // twenty level-ups discards every mark raised so far and re-DMs (and re-posts)
        // all of them later — the exact retry storm the marks exist to prevent.
        try
        {
        foreach (var row in rows)
        {
            var newLevel = row.Level;
            var previousLevel = row.LastAnnouncedLevel;

            // Integer division floors each level to the milestone at or below it,
            // so crossing from 19 to 23 moves the milestone 10 \u2192 20 and fires
            // once. Levels that don't cross one move the mark silently.
            var previousMilestone = previousLevel / every * every;
            var newMilestone = newLevel / every * every;

            // Raise the high-water mark regardless of whether either send
            // succeeds, so a permissions problem or a member with closed DMs
            // can't turn into a retry storm the moment it's fixed.
            row.LastAnnouncedLevel = newLevel;

            if (_config.XpLevelUpDmEnabled && !optedOut.Contains(row.UserId))
                await TrySendLevelUpDmAsync(guild, row, season, previousLevel, ct);

            if (channel is null) continue;
            if (newMilestone <= previousMilestone || newMilestone < every) continue;

            var embed = new EmbedBuilder()
                .WithTitle("\u2b06\ufe0f Level Up")
                .WithColor(new Color(0x57F287))
                .WithDescription($"<@{row.UserId}> reached **Level {newMilestone}** \u2014 {row.Xp:N0} XP this season.")
                .WithFooter(XpService.SeasonLabel(season))
                .Build();

            // Same guard as the DM path, for the same reason: several members
            // crossing a milestone in one cycle hits the per-channel rate limit, and
            // under Discord.NET's default RetryMode a 429 is silently AWAITED rather
            // than thrown — parking the accrual loop on one line with the try/catch
            // none the wiser. This is what froze /kick-awols on 2026-06-19.
            using (var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                postCts.CancelAfter(SendTimeout);
                var postOptions = new RequestOptions
                {
                    RetryMode   = RetryMode.AlwaysFail,
                    CancelToken = postCts.Token,
                };

                try
                {
                    await channel.SendMessageAsync(
                        text: $"<@{row.UserId}>",
                        embed: embed,
                        allowedMentions: new AllowedMentions { UserIds = new List<ulong> { row.UserId } },
                        options: postOptions);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "XP: failed to announce level-up for {User}", row.UserId);
                }
            }
        }
        }
        finally
        {
            // CancellationToken.None: this save must survive the very cancellation
            // that interrupted the loop, otherwise the marks are lost.
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Best-effort private level-up notice. Never throws, never blocks the
    /// accrual cycle.
    ///
    /// \u2500\u2500 Why the two guards \u2500\u2500
    /// This is the exact shape that froze /kick-awols on 2026-06-19. Opening a DM
    /// channel (POST /users/@me/channels) is a heavily rate-limited route in its
    /// own bucket, and under Discord.NET's default RetryMode.RetryRateLimit a 429
    /// is silently AWAITED rather than thrown \u2014 parking the whole loop on one
    /// await, with the try/catch none the wiser because a 429-wait isn't an error.
    /// A cycle where twenty members level at once would hit exactly that.
    ///
    /// So: RetryMode.AlwaysFail makes a 429 throw immediately, and a
    /// CancellationTokenSource caps the total attempt. A cancellation that ISN'T
    /// ours is host shutdown and is re-thrown so the service stops cleanly.
    /// </summary>
    private async Task TrySendLevelUpDmAsync(
        SocketGuild guild, XpMemberSeason row, XpSeason season, int previousLevel, CancellationToken ct)
    {
        var member = guild.GetUser(row.UserId);
        if (member is null || member.IsBot) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DmTimeout);

        var dmOptions = new RequestOptions
        {
            RetryMode   = RetryMode.AlwaysFail,
            CancelToken = cts.Token,
        };

        var gained = row.Level - previousLevel;
        var (into, width) = _xp.LevelProgress(row.Xp);

        var body = new StringBuilder();
        body.Append(gained > 1
            ? $"You gained **{gained} levels** and are now **Level {row.Level}**."
            : $"You're now **Level {row.Level}**.");
        body.Append($"\n\n**{row.Xp:N0} XP** this season.");
        if (width > 0)
            body.Append($"\n**{width - into:N0} XP** to Level {row.Level + 1}.");

        var embed = new EmbedBuilder()
            .WithTitle("\u2b06\ufe0f Level Up")
            .WithColor(new Color(0x57F287))
            .WithDescription(body.ToString())
            .WithFooter($"{XpService.SeasonLabel(season)} \u2022 /xp for your full card \u2022 XP doesn't affect promotions")
            .Build();

        // The guild id has to travel in the custom id: a button clicked inside a
        // DM has no guild context, so SocketMessageComponent.GuildId is null and
        // the handler would otherwise have nothing to scope the opt-out to.
        var components = new ComponentBuilder()
            .WithButton("Stop these DMs", $"xp:dmoff:{guild.Id}", ButtonStyle.Secondary)
            .Build();

        try
        {
            var dm = await member.CreateDMChannelAsync(dmOptions);
            await dm.SendMessageAsync(embed: embed, components: components, options: dmOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown, not our timeout \u2014 let the service stop cleanly.
            throw;
        }
        catch (Exception ex)
        {
            // Closed DMs, blocked bot, rate-limited, timed out. All expected and
            // all fine: the member simply doesn't get the notice. Debug level so
            // a clan full of closed DMs doesn't fill the log.
            _logger.LogDebug(ex, "XP: skipped level-up DM to {User} \u2014 {Reason}", row.UserId, ex.Message);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsBot(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId)?.IsBot == true;

    private static string? ResolveDisplayName(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId)?.DisplayName;
}
