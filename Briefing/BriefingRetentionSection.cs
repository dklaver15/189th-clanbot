using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Briefing;

/// <summary>
/// Computes the Retention section for the weekly briefing. Pulled into its own
/// static helper for the same reason as BriefingInviteSection — it queries a
/// table (MemberDeparture) the core collector doesn't otherwise touch and
/// shares no intermediate state with the other sections, so a standalone seam
/// keeps the collector focused and makes the section easy to disable.
///
/// Returns null when the week saw zero departures; the prompt treats null as
/// "skip the section," matching the Spotlight / RedditLeads convention.
///
/// A departure "belongs to" the week if DepartedAt is in [weekStart, weekEnd).
/// Tenure aggregation runs in memory over the week's rows (dozens at most),
/// avoiding EF Core SQLite date-math — same reasoning as the joins-by-day
/// query already in BriefingDataCollector.
/// </summary>
internal static class BriefingRetentionSection
{
    /// <summary>Top N named departures surfaced in the briefing.</summary>
    public const int MaxNotableDepartures = 6;

    /// <summary>Top N invite-source labels surfaced for churn-by-source.</summary>
    public const int MaxChurnBySource = 6;

    public static async Task<RetentionSnapshot?> CollectAsync(
        BotDbContext db,
        ulong guildId,
        DateTime weekStart,
        DateTime weekEnd,
        int newRecruitsThisWeek,
        CancellationToken ct = default)
    {
        var rows = await db.MemberDepartures
            .Where(d => d.GuildId == guildId
                     && d.DepartedAt >= weekStart
                     && d.DepartedAt < weekEnd)
            .ToListAsync(ct);

        if (rows.Count == 0)
            return null;

        int Count(params string[] classes) =>
            rows.Count(r => classes.Contains(r.Classification, StringComparer.OrdinalIgnoreCase));

        var voluntary = Count("Left");
        var kickedManual = Count("Kicked");
        var kickedByBot = Count("KickedAwol", "KickedAccountAge");
        var banned = Count("Banned");
        // Anything still "Pending" at briefing time (rare — captured within the
        // grace window of this run) is folded into voluntary for the headline
        // so the counts always sum to TotalDepartures.
        voluntary += Count("Pending");

        // ── Tenure aggregation over the known-tenure subset ──
        var withTenure = rows.Where(r => r.TenureDays is not null)
                             .Select(r => r.TenureDays!.Value)
                             .OrderBy(t => t)
                             .ToList();

        double? medianTenure = withTenure.Count == 0
            ? null
            : Math.Round(Median(withTenure), 1);

        var buckets = BuildTenureBuckets(withTenure);

        // ── Instant churn: joined AND left inside the week ──
        var sameWeekChurn = rows.Count(r =>
            r.JoinedAt is not null
            && r.JoinedAt.Value >= weekStart
            && r.JoinedAt.Value < weekEnd);

        var rejoiners = rows.Count(r => r.IsRejoin);
        var guestChurn = rows.Count(r => r.WasGuest);
        var engagedChurn = rows.Count(r => r.EventsAttendedLifetime > 0);
        var reconciled = rows.Count(r =>
            r.DepartureDetection.Equals("Reconciled", StringComparison.OrdinalIgnoreCase));

        // ── Notable departures: rank >= CPL, OR tenure >= 90d, OR any events attended ──
        // (CPL is the first event-tracked tier; see spec §11 Q4 to adjust the floor.)
        var notableRanks = new[]
        {
            "CPL","SGT","SSG","SFC","MSG","1SG","SGM","CSM","SMA",
            "2ndLT","1stLT","CPT","MAJ","LTC","COL","BG","MG","LTG","GEN","GA"
        };
        bool IsNotable(MemberDeparture r) =>
            notableRanks.Contains(r.RankAtDeparture, StringComparer.OrdinalIgnoreCase)
            || (r.TenureDays is not null && r.TenureDays.Value >= 90)
            || r.EventsAttendedLifetime > 0;

        var notable = rows.Where(IsNotable)
            .OrderByDescending(r => r.EventsAttendedLifetime)
            .ThenByDescending(r => r.TenureDays ?? 0)
            .Take(MaxNotableDepartures)
            .Select(r => new NotableDeparture(
                DisplayName: string.IsNullOrWhiteSpace(r.DisplayName) ? r.Username : r.DisplayName,
                RankAtDeparture: r.RankAtDeparture,
                TenureDays: r.TenureDays,
                Classification: r.Classification,
                EventsAttendedLifetime: r.EventsAttendedLifetime))
            .ToList();

        // ── Churn by original invite source ──
        var churnBySource = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.JoinSourceLabel))
            .GroupBy(r => r.JoinSourceLabel!)
            .Select(g => new ChurnBySourceItem(g.Key, g.Count()))
            .OrderByDescending(c => c.Departures)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxChurnBySource)
            .ToList();

        return new RetentionSnapshot(
            TotalDepartures: rows.Count,
            Voluntary: voluntary,
            KickedManual: kickedManual,
            KickedByBot: kickedByBot,
            Banned: banned,
            NetMembershipChange: newRecruitsThisWeek - rows.Count,
            DeparturesWithKnownTenure: withTenure.Count,
            MedianTenureDays: medianTenure,
            TenureBuckets: buckets,
            SameWeekChurn: sameWeekChurn,
            Rejoiners: rejoiners,
            GuestChurn: guestChurn,
            EngagedChurn: engagedChurn,
            ReconciledDepartures: reconciled,
            NotableDepartures: notable,
            ChurnBySource: churnBySource);
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }

    private static IReadOnlyList<TenureBucket> BuildTenureBuckets(IReadOnlyList<double> tenuresDays)
    {
        int lt1 = 0, d1to7 = 0, d8to30 = 0, d31to90 = 0, d90plus = 0;
        foreach (var t in tenuresDays)
        {
            if (t < 1) lt1++;
            else if (t < 8) d1to7++;
            else if (t <= 30) d8to30++;
            else if (t <= 90) d31to90++;
            else d90plus++;
        }

        // Only emit non-empty buckets — keeps the prompt payload tight.
        var list = new List<TenureBucket>(5);
        if (lt1 > 0) list.Add(new TenureBucket("<24h", lt1));
        if (d1to7 > 0) list.Add(new TenureBucket("1-7d", d1to7));
        if (d8to30 > 0) list.Add(new TenureBucket("8-30d", d8to30));
        if (d31to90 > 0) list.Add(new TenureBucket("31-90d", d31to90));
        if (d90plus > 0) list.Add(new TenureBucket("90d+", d90plus));
        return list;
    }
}
