using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Briefing;

/// <summary>
/// Computes the recruit-leads section for the weekly briefing. Mirrors the
/// BriefingInviteSection pattern: a standalone static helper, kept out of
/// BriefingDataCollector so the existing collector logic stays untouched.
///
/// ── What this surfaces vs. RecruitmentSources ──
/// RecruitmentSources (BriefingInviteSection) covers who actually JOINED
/// Discord this week, attributed by invite label. This section covers the
/// upstream funnel: who we found, who officers engaged, who converted.
/// The two are complementary — a healthy week shows leads surfacing AND
/// converting through to the recruit log.
///
/// ── Time scopes ──
/// The funnel counts (surfaced/claimed/contacted/etc) are scoped to leads
/// DISCOVERED in the briefing week — that's the "what happened this week"
/// frame the briefing uses everywhere else.
///
/// StaleClaimed is the one exception: it's all-time, because a claimed
/// lead from two weeks ago that never progressed is exactly the kind of
/// thing that gets forgotten without a periodic reminder. The weekly
/// briefing is the right place for that nudge.
/// </summary>
internal static class BriefingRedditLeadsSection
{
    /// <summary>
    /// Top N subreddits surfaced in the snapshot. Bounds prompt token cost
    /// and matches the cap pattern used elsewhere in the briefing context.
    /// </summary>
    public const int MaxSubredditEntries = 5;

    /// <summary>
    /// A claimed lead that hasn't progressed past Claimed for this many
    /// days is considered "stale" — officer probably lost the thread.
    /// Tunable here rather than in BotConfig because this is briefing-
    /// internal heuristic, not operational policy.
    /// </summary>
    public const int StaleClaimedDaysThreshold = 3;

    /// <summary>
    /// Returns null when the briefing window saw zero leads surfaced.
    /// The prompt's section guidance treats null as "skip the section
    /// entirely" — same convention as Spotlight.
    ///
    /// Why null instead of an empty record: a week with zero surfaced
    /// leads is operationally identical to "feature disabled" or "feature
    /// not yet shipped" — the briefing has no useful story to tell, so
    /// we suppress the section rather than print "None this week" boilerplate.
    /// </summary>
    public static async Task<RedditLeadsSnapshot?> CollectAsync(
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default)
    {
        // ── Briefing-window leads ──────────────────────────────────
        // Pull only the columns we aggregate over. Cheaper than materializing
        // full RedditLead entities (Title/Excerpt are large strings we don't need).
        var weekLeads = await db.RedditLeads
            .Where(l => l.DiscoveredAtUtc >= weekStart && l.DiscoveredAtUtc < weekEnd)
            .Select(l => new { l.Status, l.Subreddit })
            .ToListAsync(ct);

        if (weekLeads.Count == 0) return null;

        // Status breakdown via single dictionary materialization.
        var byStatus = weekLeads
            .GroupBy(l => l.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        int Count(LeadStatus s) => byStatus.GetValueOrDefault(s, 0);

        // Subreddit breakdown — top N by surfaced count.
        var bySub = weekLeads
            .GroupBy(l => l.Subreddit, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RedditLeadSubredditBreakdown(g.Key, g.Count()))
            .OrderByDescending(s => s.SurfacedCount)
            .ThenBy(s => s.Subreddit, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSubredditEntries)
            .ToList();

        // ── All-time stale claimed (separate query, separate scope) ──
        // "Claimed > N days ago and still Claimed" — officer started but
        // didn't follow through. This is the one cross-week signal the
        // section surfaces; everything else is briefing-window scoped.
        var staleThreshold = DateTime.UtcNow.AddDays(-StaleClaimedDaysThreshold);
        var staleClaimed = await db.RedditLeads
            .CountAsync(
                l => l.Status == LeadStatus.Claimed
                  && l.DiscoveredAtUtc < staleThreshold,
                ct);

        return new RedditLeadsSnapshot(
            TotalSurfaced:   weekLeads.Count,
            StillNew:        Count(LeadStatus.New),
            Claimed:         Count(LeadStatus.Claimed),
            Contacted:       Count(LeadStatus.Contacted),
            Joined:          Count(LeadStatus.Joined),
            Declined:        Count(LeadStatus.Declined),
            NoResponse:      Count(LeadStatus.NoResponse),
            Skipped:         Count(LeadStatus.Skipped),
            BySubreddit:     bySub,
            StaleClaimedAllTime: staleClaimed);
    }
}
