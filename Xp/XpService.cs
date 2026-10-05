using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// One member's line on a leaderboard, already resolved for rendering.
/// </summary>
public sealed record XpStanding(
    int Place,
    ulong UserId,
    string Username,
    int Xp,
    int Level);

/// <summary>
/// A member's full XP picture for the <c>/xp</c> card.
/// </summary>
public sealed record XpProfile(
    ulong UserId,
    string Username,
    XpSeason? Season,
    int SeasonXp,
    int Level,
    int XpIntoLevel,
    int XpForThisLevel,
    int Place,
    int TotalRanked,
    long AllTimeXp,
    int SeasonsPlayed,
    int BestSeasonXp,
    int BestSeasonNumber,
    int BestSeasonPlace,
    IReadOnlyDictionary<XpSource, int> Breakdown);

/// <summary>
/// The XP ladder's rules engine: level maths, season lifecycle, and the
/// idempotent award ledger. Deliberately holds no state and touches no Discord
/// API — it takes a <see cref="BotDbContext"/> per call so background services,
/// slash commands, and the leaderboard can all share one implementation of "what
/// does this member actually have".
///
/// ── This does NOT affect promotions ──
/// The clan decided (Proposal B, NCO review) that XP is a recognition and
/// engagement layer only. Rank promotions keep running off event-attendance
/// credit via EventAttendanceHelper/AutoPromotionService and know nothing about
/// this file. Nothing here writes RankHistory, and nothing here should ever be
/// consulted by PromotionService.
///
/// ── The level curve ──
/// Cost of the step from level L to L+1 is linear:
///
///     step(L) = XpLevelBase + XpLevelStep * (L - 1)          (default 150 + 40·(L−1))
///
/// so the cumulative XP needed to BE level n is the arithmetic series
///
///     cum(n) = base·(n−1) + step·(n−1)(n−2)/2
///
/// A linear step (rather than the geometric curve the old promotion-model
/// spreadsheet used) is the right shape here: geometric growth makes the last few
/// levels of a season unreachable and the ladder stops motivating anyone once the
/// leaders are clear. Linear keeps every level roughly a fixed number of ops
/// apart, so a member who joins in week eight of a season is still visibly
/// climbing rather than staring at a wall.
/// </summary>
public sealed class XpService
{
    /// <summary>
    /// How far in the future a requested start has to be before the season is
    /// created as Scheduled rather than opened immediately. Without it, "start now"
    /// and a start time a few seconds out (clock skew, or an officer typing an
    /// explicit time that has just passed) would produce a pending season that sits
    /// idle until the next scheduler tick.
    /// </summary>
    private static readonly TimeSpan ScheduleGrace = TimeSpan.FromMinutes(1);

    private readonly BotConfig _config;
    private readonly ILogger<XpService> _logger;

    public XpService(IOptions<BotConfig> config, ILogger<XpService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    // ─── Level maths ─────────────────────────────────────────────────────────

    /// <summary>XP cost of the single step from <paramref name="level"/> to level+1.</summary>
    public int StepCost(int level)
    {
        if (level < 1) level = 1;
        return _config.XpLevelBase + _config.XpLevelStep * (level - 1);
    }

    /// <summary>
    /// Total XP needed to have reached <paramref name="level"/>. Level 1 is 0.
    /// Uses long internally — at level 100 with the default curve this is ~213k,
    /// nowhere near overflow, but the multiply of two ints is the kind of thing
    /// that bites later when someone raises XpLevelStep.
    /// </summary>
    public int CumulativeXpForLevel(int level)
    {
        if (level <= 1) return 0;
        long k = level - 1;
        long total = (long)_config.XpLevelBase * k + (long)_config.XpLevelStep * k * (k - 1) / 2;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// The level a given season XP total corresponds to, clamped to XpMaxLevel.
    /// Loops rather than solving the quadratic — at a hundred-odd iterations it's
    /// free, and it can't drift out of sync with <see cref="CumulativeXpForLevel"/>
    /// the way a hand-derived inverse can.
    /// </summary>
    public int LevelForXp(int xp)
    {
        if (xp <= 0) return 1;
        var max = Math.Max(1, _config.XpMaxLevel);
        var level = 1;
        while (level < max && CumulativeXpForLevel(level + 1) <= xp)
            level++;
        return level;
    }

    /// <summary>Progress within the current level: how far in, and how wide the level is.</summary>
    public (int Into, int Width) LevelProgress(int xp)
    {
        var level = LevelForXp(xp);
        if (level >= Math.Max(1, _config.XpMaxLevel))
            return (0, 0);   // maxed — no partial progress to show

        var floor = CumulativeXpForLevel(level);
        return (Math.Max(0, xp - floor), StepCost(level));
    }

    // ─── Season lifecycle ────────────────────────────────────────────────────
    //
    // Seasons are 100% MANUAL. Nothing opens a season, closes one, or rolls one
    // over on its own — an officer runs /xp-season start and /xp-season end, and
    // that is the only way the state changes.
    //
    // The trade-off is deliberate and worth stating: with no season running, NO
    // XP ACCRUES AT ALL. XpAccrualService idles rather than banking activity
    // against some implicit season, so anything that happens before the first
    // /xp-season start is gone once it falls outside the accrual lookback window.
    // That's the price of not having the bot make scheduling decisions on the
    // clan's behalf, and both the board and /xp say plainly when no season is
    // running so it can't sit idle unnoticed.

    public Task<XpSeason?> GetActiveSeasonAsync(BotDbContext db, ulong guildId, CancellationToken ct) =>
        db.XpSeasons
            .Where(s => s.GuildId == guildId && s.Status == XpSeasonStatus.Active)
            .OrderByDescending(s => s.Number)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Creates a season, either open right now or lined up to open later.
    ///
    /// Returns null if a season is already Active OR already Scheduled. The caller
    /// is expected to surface that rather than silently stacking two, because
    /// "exactly one season per guild that is not Ended" is an invariant the rollup
    /// tables and the leaderboard both assume.
    ///
    /// <paramref name="startUtc"/> null means open immediately, which is how every
    /// season worked before scheduling existed. A time far enough in the future
    /// creates the season as <see cref="XpSeasonStatus.Scheduled"/>: nothing accrues
    /// and nothing is announced until XpSeasonSchedulerService opens it.
    ///
    /// <paramref name="plannedDays"/> still only sets a DISPLAY target end date on
    /// the board ("12 days to go"); nothing acts on it. <paramref name="autoEndUtc"/>
    /// is the one that binds. When it is given it overrides the days-derived date,
    /// so the countdown on the board and the instant the season actually closes are
    /// always the same date.
    /// </summary>
    public async Task<XpSeason?> StartSeasonAsync(
        BotDbContext db, ulong guildId, string? name, int? plannedDays,
        DateTime? startUtc, DateTime? autoEndUtc, CancellationToken ct)
    {
        if (await GetActiveSeasonAsync(db, guildId, ct) is not null) return null;
        if (await GetScheduledSeasonAsync(db, guildId, ct) is not null) return null;

        var lastNumber = await db.XpSeasons
            .Where(s => s.GuildId == guildId)
            .Select(s => (int?)s.Number)
            .MaxAsync(ct) ?? 0;

        var days = plannedDays ?? _config.XpSeasonLengthDays;
        var now = DateTime.UtcNow;
        var start = startUtc ?? now;

        // The grace window stops a start time of "now" (or a few seconds of clock
        // skew) creating a Scheduled season that then has to wait for a scheduler
        // tick before anything happens. Anything further out than this was clearly
        // meant to be scheduled.
        var scheduled = start > now + ScheduleGrace;
        if (!scheduled) start = now;

        // A planned length counts from when the season OPENS, not from when the
        // command was typed. Scheduling a 30 day season on the 20th for the 1st and
        // getting an end date of the 20th would be nonsense.
        var end = autoEndUtc ?? (days > 0 ? start.AddDays(days) : (DateTime?)null);

        var season = new XpSeason
        {
            GuildId    = guildId,
            Number     = lastNumber + 1,
            Name       = (name ?? string.Empty).Trim(),
            StartUtc   = start,
            EndUtc     = end,
            AutoEndUtc = autoEndUtc,
            Status     = scheduled ? XpSeasonStatus.Scheduled : XpSeasonStatus.Active,
            CreatedAt  = now,
        };

        db.XpSeasons.Add(season);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "XP: created Season {Number} for guild {Guild} as {Status} (starts {Start}, planned end {End}, auto-close {Auto})",
            season.Number, guildId, season.Status, season.StartUtc.ToString("u"),
            season.EndUtc?.ToString("u") ?? "none", season.AutoEndUtc?.ToString("u") ?? "no");

        return season;
    }

    /// <summary>
    /// The season lined up to open later, if there is one. Deliberately NOT returned
    /// by <see cref="GetActiveSeasonAsync"/>: a scheduled season must be invisible to
    /// accrual, standings and announcements, or "scheduled" would just mean "active
    /// with a confusing label".
    /// </summary>
    public Task<XpSeason?> GetScheduledSeasonAsync(BotDbContext db, ulong guildId, CancellationToken ct) =>
        db.XpSeasons
            .Where(s => s.GuildId == guildId && s.Status == XpSeasonStatus.Scheduled)
            .OrderBy(s => s.StartUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Opens a scheduled season whose start time has arrived. Returns null when
    /// there is nothing due, which is the overwhelmingly common case.
    ///
    /// StartUtc is left at the SCHEDULED instant rather than being reset to now.
    /// That matters: XpAccrualService clamps its lookback window to StartUtc, so a
    /// season set for midnight still pays for everything from midnight even though
    /// the tick that opened it landed a few seconds later. It also means a season
    /// that opens late (bot restarting, host down) back-pays what the accrual
    /// lookback can still see rather than silently losing it.
    ///
    /// Refuses to open while another season is Active. That state should be
    /// unreachable, since a scheduled season cannot be created while one is running,
    /// but opening a second Active season would corrupt every rollup in the ladder,
    /// so it is checked rather than assumed.
    /// </summary>
    public async Task<XpSeason?> ActivateDueSeasonAsync(BotDbContext db, ulong guildId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var due = await db.XpSeasons
            .Where(s => s.GuildId == guildId
                        && s.Status == XpSeasonStatus.Scheduled
                        && s.StartUtc <= now)
            .OrderBy(s => s.StartUtc)
            .FirstOrDefaultAsync(ct);

        if (due is null) return null;

        if (await GetActiveSeasonAsync(db, guildId, ct) is { } running)
        {
            _logger.LogError(
                "XP: Season {Scheduled} was due to open but Season {Running} is still Active. " +
                "Leaving it scheduled. End the running season and it will open on the next tick.",
                due.Number, running.Number);
            return null;
        }

        due.Status = XpSeasonStatus.Active;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: Season {Number} opened on schedule for guild {Guild} (start {Start})",
            due.Number, guildId, due.StartUtc.ToString("u"));

        return due;
    }

    /// <summary>
    /// Works out whether a pre-start reminder is due for the Scheduled season and,
    /// if so, records it as handled BEFORE returning it, so the caller posts at most
    /// once: a failed post is logged rather than retried, because a duplicate @here
    /// is worse than a missing reminder. Returns the season and reminder stage (0 =
    /// one week, 1 = one day, 2 = six hours) to post, or null when there is nothing
    /// to send.
    ///
    /// Only the most recent due reminder is ever sent. One whose time had already
    /// passed when the season was scheduled is skipped (the officer scheduling it on
    /// short notice announces it themselves), and one overtaken by a later reminder
    /// while the bot was down is skipped in favor of the later one.
    /// </summary>
    public async Task<(XpSeason Season, int Stage)?> ClaimDueReminderAsync(
        BotDbContext db, ulong guildId, TimeZoneInfo zone, int eveningHour, CancellationToken ct)
    {
        var season = await GetScheduledSeasonAsync(db, guildId, ct);
        if (season is null) return null;

        var now = DateTime.UtcNow;
        if (now >= season.StartUtc) return null; // opening is due; the start card takes over

        var times = XpSeasonReminders.TimesUtc(season.StartUtc, zone, eveningHour);
        var due = times.Count(t => t <= now);
        if (due <= season.PreStartRemindersSent) return null;

        var skippedEarlier = due - 1 - season.PreStartRemindersSent;
        var stage = due - 1;
        season.PreStartRemindersSent = due;
        await db.SaveChangesAsync(ct);

        if (skippedEarlier > 0)
            _logger.LogInformation("XP: skipped {Count} earlier reminder(s) for Season {Number}; a later one is already due",
                skippedEarlier, season.Number);

        if (times[stage] < season.CreatedAt)
        {
            _logger.LogInformation(
                "XP: skipped reminder {Stage} for Season {Number}; its time ({Time}) passed before the season was scheduled",
                stage, season.Number, times[stage].ToString("u"));
            return null;
        }

        return (season, stage);
    }

    /// <summary>
    /// The season that has run past its scheduled close, if any. Only ever returns
    /// an Active season with a non-null <see cref="XpSeason.AutoEndUtc"/>, so a
    /// season with a display-only planned end is never touched.
    /// </summary>
    public Task<XpSeason?> GetSeasonDueToCloseAsync(BotDbContext db, ulong guildId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        return db.XpSeasons
            .Where(s => s.GuildId == guildId
                        && s.Status == XpSeasonStatus.Active
                        && s.AutoEndUtc != null
                        && s.AutoEndUtc <= now)
            .OrderBy(s => s.AutoEndUtc)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Sets (or moves) the automatic close on whichever season is Active or
    /// Scheduled. Writes EndUtc alongside AutoEndUtc so the board counts down to the
    /// instant it will actually close. Returns null if there is no season to set it
    /// on, or the requested instant is not after the season's start.
    /// </summary>
    public async Task<XpSeason?> ScheduleEndAsync(
        BotDbContext db, ulong guildId, DateTime endUtc, CancellationToken ct)
    {
        var season = await GetActiveSeasonAsync(db, guildId, ct)
                     ?? await GetScheduledSeasonAsync(db, guildId, ct);

        if (season is null) return null;
        if (endUtc <= season.StartUtc) return null;

        season.EndUtc     = endUtc;
        season.AutoEndUtc = endUtc;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: Season {Number} will close automatically at {End}",
            season.Number, endUtc.ToString("u"));

        return season;
    }

    /// <summary>
    /// Turns the automatic close back off, leaving the season running until an
    /// officer ends it by hand. EndUtc is left alone on purpose: the date is still a
    /// useful target on the board, it just stops being binding. Returns null when
    /// there was no automatic close to clear.
    /// </summary>
    public async Task<XpSeason?> ClearScheduledEndAsync(BotDbContext db, ulong guildId, CancellationToken ct)
    {
        var season = await GetActiveSeasonAsync(db, guildId, ct)
                     ?? await GetScheduledSeasonAsync(db, guildId, ct);

        if (season?.AutoEndUtc is null) return null;

        season.AutoEndUtc = null;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: automatic close cleared on Season {Number}", season.Number);
        return season;
    }

    /// <summary>
    /// Deletes a season that was lined up but has not opened yet, freeing its season
    /// number for the next one. Safe to delete outright rather than mark Ended: a
    /// Scheduled season has never accrued anything, so there are no awards, rollups
    /// or historical placements pointing at it. Returns null if nothing was pending.
    /// </summary>
    public async Task<XpSeason?> CancelScheduledStartAsync(BotDbContext db, ulong guildId, CancellationToken ct)
    {
        var pending = await GetScheduledSeasonAsync(db, guildId, ct);
        if (pending is null) return null;

        db.XpSeasons.Remove(pending);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: scheduled Season {Number} cancelled for guild {Guild}",
            pending.Number, guildId);

        return pending;
    }

    /// <summary>
    /// Closes the active season and does NOT open another — the ladder sits idle
    /// until an officer starts the next one.
    ///
    /// The close pass folds every participant's result into their
    /// <see cref="XpMemberTotal"/>: final placement, best-season records, seasons
    /// played. Those are the things that must survive the reset. Season XP itself
    /// is not copied anywhere — the next season genuinely starts everyone at zero,
    /// which is the entire point of having seasons. The ledger is never touched,
    /// so an ended season can always be recomputed or audited.
    ///
    /// Returns the closed season and its final standings, or null if nothing was
    /// running.
    /// </summary>
    public async Task<(XpSeason Season, List<XpStanding> Standings)?> EndSeasonAsync(
        BotDbContext db, ulong guildId, CancellationToken ct)
    {
        var active = await GetActiveSeasonAsync(db, guildId, ct);
        if (active is null) return null;

        // Standings have to be computed BEFORE anything is marked ended, since
        // final placement is what feeds the permanent best-finish record.
        var standings = await GetSeasonStandingsAsync(db, guildId, active.Id, int.MaxValue, ct);
        var now = DateTime.UtcNow;

        foreach (var s in standings)
        {
            var total = await GetOrCreateTotalAsync(db, guildId, s.UserId, s.Username, ct);
            total.SeasonsPlayed += 1;

            if (s.Xp > total.BestSeasonXp)
            {
                total.BestSeasonXp     = s.Xp;
                total.BestSeasonNumber = active.Number;
            }

            // Lower place number is better; 0 means "never placed".
            if (total.BestSeasonPlace == 0 || s.Place < total.BestSeasonPlace)
                total.BestSeasonPlace = s.Place;

            total.UpdatedAt = now;
        }

        active.Status    = XpSeasonStatus.Ended;
        active.ClosedUtc = now;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: closed Season {Number} for guild {Guild} — {Count} participant(s)",
            active.Number, guildId, standings.Count);

        return (active, standings);
    }

    /// <summary>Renders "Season 3", or "Season 3: Launch" when a name was given.</summary>
    public static string SeasonLabel(XpSeason? season) =>
        season is null
            ? "Preseason"
            : string.IsNullOrWhiteSpace(season.Name)
                ? $"Season {season.Number}"
                : $"Season {season.Number}: {season.Name}";

    // ─── The award ledger ────────────────────────────────────────────────────

    /// <summary>
    /// Records (or corrects) one award. Returns true if anything actually changed,
    /// so callers can skip the rollup recompute on a no-op cycle — which is the
    /// common case, since accrual re-examines the same lookback window every few
    /// minutes.
    ///
    /// Insert-once sources (events, meetings, bonuses) short-circuit if a row with
    /// the same key already exists. Daily rollups (voice, chat) overwrite Amount,
    /// which is how a partial day gets topped up as it fills and how a missed
    /// cycle self-heals.
    ///
    /// Does NOT call SaveChanges — the caller batches a whole cycle into one save.
    /// </summary>
    public async Task<bool> AwardAsync(
        BotDbContext db,
        ulong guildId,
        int seasonId,
        ulong userId,
        XpSource source,
        string sourceKey,
        int amount,
        string? note,
        DateTime earnedAtUtc,
        CancellationToken ct)
    {
        var existing = await db.XpAwards.FirstOrDefaultAsync(
            a => a.GuildId == guildId && a.UserId == userId && a.Source == source && a.SourceKey == sourceKey, ct);

        if (existing is not null)
        {
            // Rollups are recomputed from source each cycle; a changed amount means
            // the member earned more (or a cap/config changed). Anything else is a
            // replay and must not pay twice.
            if (existing.Amount == amount) return false;

            existing.Amount      = amount;
            existing.Note        = note;
            existing.AwardedAtUtc = DateTime.UtcNow;
            return true;
        }

        // Nothing to record for a zero-value rollup (a day with no qualifying
        // activity) — writing it would just bloat the ledger with empty rows.
        if (amount == 0) return false;

        db.XpAwards.Add(new XpAward
        {
            GuildId      = guildId,
            SeasonId     = seasonId,
            UserId       = userId,
            Source       = source,
            SourceKey    = sourceKey,
            Amount       = amount,
            Note         = note,
            EarnedAtUtc  = earnedAtUtc,
            AwardedAtUtc = DateTime.UtcNow,
        });

        return true;
    }

    /// <summary>
    /// Rebuilds a member's season rollup and lifetime total straight from the
    /// ledger. Recompute-from-source rather than increment-in-place, so a bad
    /// cycle can never permanently corrupt a total — the next pass just fixes it.
    ///
    /// Returns the (previousLevel, newLevel) pair so the caller can decide whether
    /// a level-up is worth announcing.
    /// </summary>
    public async Task<(int OldLevel, int NewLevel)> RecomputeMemberAsync(
        BotDbContext db, ulong guildId, int seasonId, ulong userId, string? username, CancellationToken ct)
    {
        var seasonXp = await db.XpAwards
            .Where(a => a.GuildId == guildId && a.SeasonId == seasonId && a.UserId == userId)
            .SumAsync(a => (int?)a.Amount, ct) ?? 0;

        // A manual deduction can in principle exceed what someone has earned;
        // floor at zero rather than showing negative XP on a public board.
        if (seasonXp < 0) seasonXp = 0;

        var lastEarned = await db.XpAwards
            .Where(a => a.GuildId == guildId && a.SeasonId == seasonId && a.UserId == userId)
            .MaxAsync(a => (DateTime?)a.EarnedAtUtc, ct) ?? DateTime.UtcNow;

        var row = await db.XpMemberSeasons.FirstOrDefaultAsync(
            m => m.GuildId == guildId && m.SeasonId == seasonId && m.UserId == userId, ct);

        if (row is null)
        {
            row = new XpMemberSeason
            {
                GuildId  = guildId,
                SeasonId = seasonId,
                UserId   = userId,
                // A brand-new member starts "already announced" at level 1 so
                // their very first award doesn't fire a 1→1 announcement.
                LastAnnouncedLevel = 1,
            };
            db.XpMemberSeasons.Add(row);
        }

        var oldLevel = row.Level < 1 ? 1 : row.Level;

        row.Xp            = seasonXp;
        row.Level         = LevelForXp(seasonXp);
        row.LastEarnedUtc = lastEarned;
        row.UpdatedAt     = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(username)) row.Username = username!;

        // Lifetime total spans every season, so it sums the whole ledger for this
        // member. Indexed on (GuildId, UserId) and scoped to one person, so this
        // stays cheap even years in.
        var allTime = await db.XpAwards
            .Where(a => a.GuildId == guildId && a.UserId == userId)
            .SumAsync(a => (long?)a.Amount, ct) ?? 0L;

        var total = await GetOrCreateTotalAsync(db, guildId, userId, username, ct);
        total.AllTimeXp = allTime < 0 ? 0 : allTime;
        total.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(username)) total.Username = username!;

        return (oldLevel, row.Level);
    }

    private async Task<XpMemberTotal> GetOrCreateTotalAsync(
        BotDbContext db, ulong guildId, ulong userId, string? username, CancellationToken ct)
    {
        var total = await db.XpMemberTotals.FirstOrDefaultAsync(
            t => t.GuildId == guildId && t.UserId == userId, ct);

        if (total is null)
        {
            total = new XpMemberTotal
            {
                GuildId   = guildId,
                UserId    = userId,
                Username  = username ?? string.Empty,
                UpdatedAt = DateTime.UtcNow,
            };
            db.XpMemberTotals.Add(total);
        }

        return total;
    }

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Season standings, best first. Ties break on who reached the total EARLIER
    /// (LastEarnedUtc ascending) — the same convention speedrun boards use, and it
    /// stops two members sitting on identical XP from swapping places on every
    /// board refresh.
    /// </summary>
    public async Task<List<XpStanding>> GetSeasonStandingsAsync(
        BotDbContext db, ulong guildId, int seasonId, int limit, CancellationToken ct)
    {
        var rows = await db.XpMemberSeasons
            .Where(m => m.GuildId == guildId && m.SeasonId == seasonId && m.Xp > 0)
            .OrderByDescending(m => m.Xp)
            .ThenBy(m => m.LastEarnedUtc)
            .Take(limit == int.MaxValue ? int.MaxValue : Math.Max(1, limit))
            .ToListAsync(ct);

        return rows
            .Select((m, i) => new XpStanding(i + 1, m.UserId, m.Username, m.Xp, m.Level))
            .ToList();
    }

    /// <summary>
    /// Everything the <c>/xp</c> card needs for one member, including the
    /// per-source breakdown that makes the number auditable.
    /// </summary>
    public async Task<XpProfile> GetProfileAsync(
        BotDbContext db, ulong guildId, ulong userId, string displayName, CancellationToken ct)
    {
        var season = await GetActiveSeasonAsync(db, guildId, ct);

        var seasonXp = 0;
        var place = 0;
        var ranked = 0;
        var breakdown = new Dictionary<XpSource, int>();

        if (season is not null)
        {
            var mine = await db.XpMemberSeasons.FirstOrDefaultAsync(
                m => m.GuildId == guildId && m.SeasonId == season.Id && m.UserId == userId, ct);

            seasonXp = mine?.Xp ?? 0;

            ranked = await db.XpMemberSeasons
                .CountAsync(m => m.GuildId == guildId && m.SeasonId == season.Id && m.Xp > 0, ct);

            if (mine is not null && mine.Xp > 0)
            {
                // Place = how many people are strictly ahead, +1. Matches the
                // ordering used by GetSeasonStandingsAsync, tiebreak included.
                var ahead = await db.XpMemberSeasons.CountAsync(
                    m => m.GuildId == guildId
                         && m.SeasonId == season.Id
                         && (m.Xp > mine.Xp || (m.Xp == mine.Xp && m.LastEarnedUtc < mine.LastEarnedUtc)), ct);
                place = ahead + 1;
            }

            var awards = await db.XpAwards
                .Where(a => a.GuildId == guildId && a.SeasonId == season.Id && a.UserId == userId)
                .GroupBy(a => a.Source)
                .Select(g => new { Source = g.Key, Total = g.Sum(x => x.Amount) })
                .ToListAsync(ct);

            foreach (var a in awards)
                breakdown[a.Source] = a.Total;
        }

        var total = await db.XpMemberTotals.FirstOrDefaultAsync(
            t => t.GuildId == guildId && t.UserId == userId, ct);

        var (into, width) = LevelProgress(seasonXp);

        return new XpProfile(
            UserId:           userId,
            Username:         displayName,
            Season:           season,
            SeasonXp:         seasonXp,
            Level:            LevelForXp(seasonXp),
            XpIntoLevel:      into,
            XpForThisLevel:   width,
            Place:            place,
            TotalRanked:      ranked,
            AllTimeXp:        total?.AllTimeXp ?? 0,
            SeasonsPlayed:    total?.SeasonsPlayed ?? 0,
            BestSeasonXp:     total?.BestSeasonXp ?? 0,
            BestSeasonNumber: total?.BestSeasonNumber ?? 0,
            BestSeasonPlace:  total?.BestSeasonPlace ?? 0,
            Breakdown:        breakdown);
    }

    /// <summary>Human label for a source, used on the /xp breakdown.</summary>
    public static string SourceLabel(XpSource source) => source switch
    {
        XpSource.Event       => "Events attended",
        XpSource.Meeting     => "Meetings",
        XpSource.RsvpHonored => "RSVP kept",
        XpSource.EventStreak => "Streak bonuses",
        XpSource.Voice       => "Voice time",
        XpSource.Message     => "Chat",
        XpSource.Manual      => "Officer adjustments",
        _                    => source.ToString(),
    };
}
