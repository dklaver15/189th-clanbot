using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Briefing;

/// <summary>
/// Computes the v3 recruitment-sources + top-referrers section for the
/// weekly briefing. Pulled out into its own static helper so
/// BriefingDataCollector stays focused on the core attention-item sections
/// it owned in v1/v2; this is purely additive.
///
/// ── Why not a private method on BriefingDataCollector ──
/// The collector already weighs in at ~640 lines and threads activity-batch
/// state through every section. Recruitment sources are independent — they
/// query a different table (InviteJoin) and don't share intermediate state
/// with anything else. A standalone helper keeps the seam clean and makes
/// this section easy to disable/replace if we ever change the data source.
/// </summary>
internal static class BriefingInviteSection
{
    /// <summary>
    /// Top N labels surfaced in the briefing. Matches the cap pattern used
    /// for AwolRisks / PromotionCandidates / etc. — bounds input token cost.
    /// </summary>
    public const int MaxRecruitmentSources = 8;

    /// <summary>Top N referring members surfaced in the briefing.</summary>
    public const int MaxTopReferrers = 5;

    /// <summary>
    /// Runs both queries (sources by label, top referrers) for the given week
    /// and returns capped, sorted result lists ready to drop into BriefingContext.
    /// Empty lists when the week saw no joins (or no attributable joins).
    /// </summary>
    public static async Task<(IReadOnlyList<RecruitmentSourceItem> Sources,
                              IReadOnlyList<TopReferrerItem> Referrers)>
        CollectAsync(
            SocketGuild guild,
            BotDbContext db,
            DateTime weekStart,
            DateTime weekEnd,
            CancellationToken ct = default)
    {
        // ── Sources by label ────────────────────────────────────────
        // Single GROUP BY on InviteJoin. We include sentinel labels
        // ("Vanity", "Unknown", "Ambiguous", "Unattributed") in the result
        // — the prompt decides whether to highlight them. Filtering them
        // out here would hide useful operational signal (e.g. a sudden
        // spike in "Unknown" suggests an invite was created via the UI
        // and never labeled).
        var sourceRows = await db.InviteJoins
            .Where(j => j.GuildId == guild.Id
                     && j.JoinedAt >= weekStart
                     && j.JoinedAt < weekEnd)
            .GroupBy(j => j.LabelSnapshot)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var sources = sourceRows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecruitmentSources)
            .Select(r => new RecruitmentSourceItem(r.Label, r.Count))
            .ToList();

        // ── Top member referrers ────────────────────────────────────
        // Discord populates Inviter only for joins via regular invites
        // (no inviter on vanity / discovery / unknown), so this naturally
        // excludes sentinels without any extra filtering. We also exclude
        // ambiguous joins because attributing a referral to anyone in the
        // ambiguous case is a guess.
        var referrerRows = await db.InviteJoins
            .Where(j => j.GuildId == guild.Id
                     && j.JoinedAt >= weekStart
                     && j.JoinedAt < weekEnd
                     && j.InviterDiscordId != null
                     && !j.IsAmbiguous)
            .GroupBy(j => j.InviterDiscordId!.Value)
            .Select(g => new { InviterId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(MaxTopReferrers)
            .ToListAsync(ct);

        var referrers = new List<TopReferrerItem>(referrerRows.Count);
        foreach (var row in referrerRows)
        {
            // Resolve display name from the live guild member if present,
            // otherwise fall back to a generic "Departed Member" label.
            var member = guild.GetUser(row.InviterId);
            string username;
            if (member is not null)
            {
                username = !string.IsNullOrWhiteSpace(member.Nickname)   ? member.Nickname
                         : !string.IsNullOrWhiteSpace(member.GlobalName) ? member.GlobalName
                         : member.Username ?? "Unknown";
            }
            else
            {
                username = "Departed Member";
            }

            referrers.Add(new TopReferrerItem(
                Username: username,
                DiscordUserId: row.InviterId,
                ReferralCount: row.Count));
        }

        return (sources, referrers);
    }
}
