using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Briefing;

public interface IBriefingDataCollector
{
    Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default);
}

/// <summary>
/// Aggregates and caps the weekly snapshot. THIS is where the cost optimization
/// lives — anything that could grow with roster size (members, attendance,
/// rank events) gets filtered/sorted/Take(N) here. The prompt should never
/// see raw EF Core query results.
///
/// ── Design notes ──
/// AWOL risks come from the AwolRecords table (filled by AwolCheckService).
/// Promotion candidates use a *time-in-rank only* check, NOT the full
/// AutoPromotionService activity check. Rationale: AutoPromotionService runs
/// nightly and promotes whoever's eligible, so by Sunday the only members
/// surfacing here are people the auto-promo can't handle (DryRun ranks,
/// ranks above the auto-promo ladder like CSM→SMA, members who failed for
/// non-trivial reasons). That's exactly who officers need to manually review.
///
/// The PromotionTiers map duplicates AutoPromotionService.DefaultTiers in
/// terms of days-in-rank thresholds. If the canonical thresholds change over
/// there, update here too. (Both could be extracted to a shared service later;
/// for V1 this duplication is the lowest-risk path.)
///
/// ── Per-section error isolation ──
/// Each Get*Async block runs in its own try/catch so one failing data source
/// (e.g. Google Sheets API blip while computing roster stats) doesn't kill
/// the whole briefing. Failures are logged and that section returns empty.
/// </summary>
public sealed class BriefingDataCollector : IBriefingDataCollector
{
    /// <summary>
    /// Time-in-rank thresholds, kept in sync with AutoPromotionService.DefaultTiers.
    /// Briefing only checks the time gate; the activity gate is enforced at
    /// promotion time by AutoPromotionService.
    /// </summary>
    private static readonly Dictionary<string, (string ToRank, int DaysInRank)> PromotionTiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RCT"] = ("PVT", 7),
            ["PVT"] = ("PFC", 7),
            ["PFC"] = ("SPC", 7),
            ["SPC"] = ("CPL", 14),
            ["CPL"] = ("SGT", 14),
            ["SGT"] = ("SSG", 21),
            ["SSG"] = ("SFC", 28),
            ["SFC"] = ("MSG", 35),
            ["MSG"] = ("1SG", 35),
            ["1SG"] = ("SGM", 42),
            ["SGM"] = ("CSM", 49),
        };

    // Caps — these bound input token cost. Tune up if briefings consistently
    // leave items out; each bullet is roughly 30–50 input tokens.
    private const int MaxAwolRisks = 8;
    private const int MaxPromotionCandidates = 8;
    private const int MaxNotableEvents = 5;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<BriefingDataCollector> _logger;

    public BriefingDataCollector(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<BriefingDataCollector> logger)
    {
        _services = services;
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Collecting briefing data for {Start:yyyy-MM-dd} → {End:yyyy-MM-dd}",
            weekStart, weekEnd);

        // ClanGuard runs in a single guild. Defensive: log if that's not true,
        // and operate on the first guild either way.
        var guild = _client.Guilds.FirstOrDefault();
        if (guild is null)
        {
            _logger.LogWarning("BriefingDataCollector: no Discord guild connected — returning empty context");
            return EmptyContext(weekStart, weekEnd);
        }
        if (_client.Guilds.Count > 1)
        {
            _logger.LogWarning(
                "BriefingDataCollector: bot is in {Count} guilds; only operating on '{First}'. " +
                "If this is intentional, the briefing pipeline needs multi-guild handling.",
                _client.Guilds.Count, guild.Name);
        }

        await guild.DownloadUsersAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Run independent fetches concurrently. Each one is wrapped in a
        // try/catch so one failure doesn't kill the briefing.
        var awolTask    = SafeAsync(() => GetAwolRisksAsync(guild, db, ct), "AWOL risks", []);
        var promoTask   = SafeAsync(() => GetPromotionCandidatesAsync(guild, db, ct), "promotion candidates", []);
        var eventsTask  = SafeAsync(() => GetNotableEventsAsync(guild, db, weekStart, weekEnd, ct), "notable events", []);
        var topLineTask = SafeAsync(
            () => GetTopLineNumbersAsync(guild, db, weekStart, weekEnd, ct),
            "top-line numbers",
            new TopLineNumbers(0, 0, 0d, 0));

        await Task.WhenAll(awolTask, promoTask, eventsTask, topLineTask);

        var awolRisks = (await awolTask)
            .OrderByDescending(r => r.DaysSinceAwolAssigned)
            .Take(MaxAwolRisks)
            .ToList();

        var promotionCandidates = (await promoTask)
            .OrderByDescending(c => c.DaysInRank)
            .Take(MaxPromotionCandidates)
            .ToList();

        var notableEvents = (await eventsTask)
            .OrderByDescending(e => e.Attendance)
            .Take(MaxNotableEvents)
            .ToList();

        var topLine = await topLineTask;

        var context = new BriefingContext
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            ActiveMemberCount = topLine.ActiveMemberCount,
            EventsHeld = topLine.EventsHeld,
            AverageAttendancePercent = topLine.AverageAttendancePercent,
            NewRecruits = topLine.NewRecruits,
            AwolRisks = awolRisks,
            PromotionCandidates = promotionCandidates,
            NotableEvents = notableEvents,
            Anomalies = []
        };

        _logger.LogInformation(
            "Briefing context: {Awol} AWOL risks, {Promo} promotion candidates, {Events} events, " +
            "{Members} active members, {NewRecruits} new recruits",
            awolRisks.Count, promotionCandidates.Count, notableEvents.Count,
            topLine.ActiveMemberCount, topLine.NewRecruits);

        return context;
    }

    // ── AWOL Risks ──────────────────────────────────────────────────────
    // Members currently AWOL with NotificationSent=false. Sorted by oldest
    // assignment first (longest-overdue = highest risk). Pulls current rank
    // from the live Discord guild user where available.
    private async Task<List<AwolRiskItem>> GetAwolRisksAsync(
        SocketGuild guild,
        BotDbContext db,
        CancellationToken ct)
    {
        var pending = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id && !r.NotificationSent)
            .OrderBy(r => r.AssignedAt)
            .ToListAsync(ct);

        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        var risks = new List<AwolRiskItem>();
        foreach (var record in pending)
        {
            var member = guild.GetUser(record.UserId);
            var daysSince = (int)(now - record.AssignedAt).TotalDays;
            string currentRank;
            string trendNote;

            if (member is null)
            {
                currentRank = "Left Server";
                trendNote = $"flagged {daysSince}d ago — no longer in guild";
            }
            else
            {
                currentRank = GetCurrentRank(member, rankRoles) ?? "No Rank";
                trendNote = daysSince >= _config.AwolGraceDays
                    ? $"flagged {daysSince}d ago, grace period elapsed — ready for officer review"
                    : $"flagged {daysSince}d ago, in {_config.AwolGraceDays}-day grace period";
            }

            risks.Add(new AwolRiskItem(
                Gamertag: member?.DisplayName ?? record.Username,
                CurrentRank: currentRank,
                DaysSinceAwolAssigned: daysSince,
                TrendNote: trendNote));
        }

        return risks;
    }

    // ── Promotion Candidates ────────────────────────────────────────────
    // Members whose time-in-rank has crossed the tier's threshold. Activity
    // checks happen at actual promotion time (AutoPromotionService). Excludes
    // members with the AWOL role. EventsAttendedAtRank uses the same helper
    // AutoPromotionService uses, so the number matches what promotion math sees.
    private async Task<List<PromotionCandidate>> GetPromotionCandidatesAsync(
        SocketGuild guild,
        BotDbContext db,
        CancellationToken ct)
    {
        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        // Pull all rank history rows for this guild in one query to avoid N+1
        var rankHistories = await db.RankHistories
            .Where(r => r.GuildId == guild.Id)
            .ToDictionaryAsync(r => r.UserId, ct);

        var candidates = new List<PromotionCandidate>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id)) continue;

            var currentRank = GetCurrentRank(member, rankRoles);
            if (currentRank is null) continue;

            if (!PromotionTiers.TryGetValue(currentRank, out var tier)) continue;

            // Use RankHistory.AssignedAt if available, fall back to guild-join date
            DateTime assignedAt;
            RankHistory? rh = null;
            if (rankHistories.TryGetValue(member.Id, out rh))
                assignedAt = rh.AssignedAt;
            else if (member.JoinedAt.HasValue)
                assignedAt = member.JoinedAt.Value.UtcDateTime;
            else
                continue;

            var daysInRank = (int)(now - assignedAt).TotalDays;
            if (daysInRank < tier.DaysInRank) continue;

            // Pull events-at-rank using the same helper AutoPromotionService uses
            // so the number matches what promotion math sees. Defaults to 0 if
            // no RankHistory row exists (member fell back to join-date above).
            int eventsAttended = 0;
            if (rh is not null)
            {
                eventsAttended = await EventAttendanceHelper.CountEventsAttendedAsync(
                    db, guild.Id, member.Id,
                    rh.AssignedAt,
                    rh.SeedAppliedAt,
                    rh.EventsAttendedAtRankBeforeBot,
                    ct);
            }

            candidates.Add(new PromotionCandidate(
                Gamertag: member.DisplayName,
                CurrentRank: currentRank.ToUpperInvariant(),
                ProposedRank: tier.ToRank.ToUpperInvariant(),
                EventsAttendedAtRank: eventsAttended,
                DaysInRank: daysInRank));
        }

        return candidates;
    }

    // ── Notable Events ──────────────────────────────────────────────────
    // Calendar events that ended in the past week, with attendance counts.
    // Includes both Clan and CompDiv events; the system prompt sorts by
    // attendance which puts the biggest events first.
    private async Task<List<NotableEvent>> GetNotableEventsAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var events = await db.CalendarEvents
            .Where(c => c.GuildId == guild.Id
                     && c.EndUtc >= weekStart
                     && c.EndUtc <= weekEnd)
            .ToListAsync(ct);

        var notable = new List<NotableEvent>();
        foreach (var ev in events)
        {
            var attendanceCount = await db.EventAttendances
                .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);

            notable.Add(new NotableEvent(
                EventName: ev.Title,
                Date: ev.StartUtc,
                Attendance: attendanceCount,
                Source: ev.Source));
        }

        return notable;
    }

    // ── Top-Line Numbers ────────────────────────────────────────────────
    // ActiveMemberCount excludes bots and exempt-role members (Reserve,
    // Admin, etc.) — same definition AwolCheckService uses for who counts.
    // EventsHeld + average attendance use only sources in
    // BotConfig.AttendanceCountingSources (default "Clan").
    // NewRecruits = RankHistory rows with RankName=RCT in the window.
    private async Task<TopLineNumbers> GetTopLineNumbersAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var exemptRoles = _config.GetExemptRolesList();

        var activeMembers = guild.Users.Count(u =>
            !u.IsBot &&
            !u.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)));

        var attendanceSources = _config.AttendanceCountingSources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var weeklyEvents = await db.CalendarEvents
            .Where(c => c.GuildId == guild.Id
                     && c.EndUtc >= weekStart
                     && c.EndUtc <= weekEnd)
            .ToListAsync(ct);

        var countingEvents = weeklyEvents
            .Where(e => attendanceSources.Contains(e.Source))
            .ToList();
        var eventsHeld = countingEvents.Count;

        double averageAttendancePercent = 0d;
        if (eventsHeld > 0 && activeMembers > 0)
        {
            var totalAttendances = 0;
            foreach (var ev in countingEvents)
            {
                totalAttendances += await db.EventAttendances
                    .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);
            }
            var avgPerEvent = (double)totalAttendances / eventsHeld;
            averageAttendancePercent = Math.Round((avgPerEvent / activeMembers) * 100, 1);
        }

        // EF Core can't translate string.Equals(StringComparison) for SQLite
        // — pull the window-filtered rows and filter in-memory. Window is
        // 1 week so the row count is bounded.
        var windowAssignments = await db.RankHistories
            .Where(r => r.GuildId == guild.Id
                     && r.AssignedAt >= weekStart
                     && r.AssignedAt <= weekEnd)
            .ToListAsync(ct);
        var newRecruits = windowAssignments.Count(r =>
            r.RankName.Equals("RCT", StringComparison.OrdinalIgnoreCase));

        return new TopLineNumbers(activeMembers, eventsHeld, averageAttendancePercent, newRecruits);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the rank list high-to-low and returns the highest rank role the
    /// member holds. Mirrors AutoPromotionService.GetCurrentRank exactly.
    /// </summary>
    private static string? GetCurrentRank(SocketGuildUser member, List<string> rankRoles)
    {
        var memberRoleNames = member.Roles.Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Contains(rankRoles[i]))
                return rankRoles[i];
        }
        return null;
    }

    /// <summary>
    /// Wraps an async data fetch in a try/catch so one failing data source
    /// doesn't kill the whole briefing. Returns the supplied fallback on error.
    /// </summary>
    private async Task<T> SafeAsync<T>(Func<Task<T>> work, string sectionName, T fallback)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Briefing section '{Section}' failed; using empty fallback", sectionName);
            return fallback;
        }
    }

    private static BriefingContext EmptyContext(DateTime weekStart, DateTime weekEnd) => new()
    {
        WeekStart = weekStart,
        WeekEnd = weekEnd,
        ActiveMemberCount = 0,
        EventsHeld = 0,
        AverageAttendancePercent = 0d,
        NewRecruits = 0
    };

    private sealed record TopLineNumbers(
        int ActiveMemberCount,
        int EventsHeld,
        double AverageAttendancePercent,
        int NewRecruits);
}