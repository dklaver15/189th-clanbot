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
    /// Opens a new season. Returns null if one is already active — the caller is
    /// expected to surface that rather than silently stacking two, because
    /// "exactly one active season per guild" is an invariant the rollup tables and
    /// the leaderboard both assume.
    ///
    /// <paramref name="plannedDays"/> sets a DISPLAY-ONLY target end date shown on
    /// the board ("12 days to go"). Nothing acts on it; the season still only ends
    /// when an officer ends it, and the board says "past its planned end" rather
    /// than pretending it closed itself.
    /// </summary>
    public async Task<XpSeason?> StartSeasonAsync(
        BotDbContext db, ulong guildId, string? name, int? plannedDays, CancellationToken ct)
    {
        if (await GetActiveSeasonAsync(db, guildId, ct) is not null) return null;

        var lastNumber = await db.XpSeasons
            .Where(s => s.GuildId == guildId)
            .Select(s => (int?)s.Number)
            .MaxAsync(ct) ?? 0;

        var days = plannedDays ?? _config.XpSeasonLengthDays;
        var now = DateTime.UtcNow;

        var season = new XpSeason
        {
            GuildId   = guildId,
            Number    = lastNumber + 1,
            Name      = (name ?? string.Empty).Trim(),
            StartUtc  = now,
            EndUtc    = days > 0 ? now.AddDays(days) : null,
            Status    = XpSeasonStatus.Active,
            CreatedAt = now,
        };

        db.XpSeasons.Add(season);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("XP: opened Season {Number} for guild {Guild} (planned end {End})",
            season.Number, guildId, season.EndUtc?.ToString("u") ?? "none");

        return season;
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
