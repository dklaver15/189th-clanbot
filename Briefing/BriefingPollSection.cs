using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Briefing;

/// <summary>
/// Computes the poll-participation section for the weekly briefing. Mirrors the
/// BriefingRedditLeadsSection pattern: a standalone static helper, kept out of
/// BriefingDataCollector so the existing collector logic stays untouched.
///
/// ── Why this section exists ──
/// MEE6/native polls evaporate — Discord discards the vote breakdown once the
/// post ages out. Because we persist every poll + vote (anonymous votes from our
/// buttons, native votes captured off the gateway), the briefing can turn polls
/// into a recurring engagement signal: how many polls ran, how many members
/// actually weighed in, which question drew the room — and, over a longer window,
/// who's chronically tuning polls out.
///
/// ── Scope ──
/// The weekly counts are scoped to polls CREATED in the briefing week. OpenNow is
/// a live cross-week figure (polls still accepting votes). The chronic-non-voter
/// block uses its own rolling window (see <see cref="ChronicWindowDays"/>) because
/// "consistently doesn't vote" is only meaningful across several polls.
/// </summary>
internal static class BriefingPollSection
{
    /// <summary>Rolling window for the chronic-non-voter signal.</summary>
    public const int ChronicWindowDays = 30;

    /// <summary>
    /// Minimum polls in the window before "chronic" means anything. Below this we
    /// emit no chronic block — a fresh feature or a quiet stretch shouldn't flag
    /// the whole roster as non-voters.
    /// </summary>
    public const int MinPollsForChronic = 3;

    /// <summary>Cap on chronic non-voter names surfaced (officers get a sample, not a wall).</summary>
    public const int MaxChronicSample = 12;

    /// <summary>
    /// Returns null when the week saw no polls created, none are currently open,
    /// AND there's no chronic block to show — operationally identical to "feature
    /// unused recently," so the prompt skips the section (same null-means-skip
    /// convention as RedditLeads / Retention).
    /// </summary>
    public static async Task<PollsSnapshot?> CollectAsync(
        BotDbContext db,
        SocketGuild guild,
        DateTime weekStart,
        DateTime weekEnd,
        IReadOnlyCollection<string> exemptRoleNames,
        string awolRoleName,
        CancellationToken ct = default)
    {
        var guildId = guild.Id;

        // ── Polls created in the briefing window ──
        var weekPolls = await db.Polls
            .Where(p => p.GuildId == guildId && p.CreatedAt >= weekStart && p.CreatedAt < weekEnd)
            .Select(p => new { p.Id, p.Question, p.Kind, p.ClosedAtUtc })
            .ToListAsync(ct);

        var openNow = await db.Polls
            .CountAsync(p => p.GuildId == guildId && p.Status == PollStatus.Open, ct);

        var chronic = await ComputeChronicAsync(db, guild, exemptRoleNames, awolRoleName, ct);

        if (weekPolls.Count == 0 && openNow == 0 && chronic is null) return null;

        var pollsCreated = weekPolls.Count;
        var nativeCount = weekPolls.Count(p => p.Kind == PollKind.Native);
        var anonymousCount = weekPolls.Count(p => p.Kind == PollKind.Anonymous);
        var pollsClosed = weekPolls.Count(p => p.ClosedAtUtc >= weekStart && p.ClosedAtUtc < weekEnd);

        // Votes on this week's polls — one query, aggregated in memory.
        var weekPollIds = weekPolls.Select(p => p.Id).ToHashSet();
        var votes = weekPollIds.Count == 0
            ? new List<(int PollId, ulong UserId)>()
            : (await db.PollVotes
                .Where(v => weekPollIds.Contains(v.PollId))
                .Select(v => new { v.PollId, v.UserId })
                .ToListAsync(ct))
                .Select(v => (v.PollId, v.UserId))
                .ToList();

        var totalVotes = votes.Count;
        var distinctVoters = votes.Select(v => v.UserId).Distinct().Count();

        var votersByPoll = votes
            .GroupBy(v => v.PollId)
            .ToDictionary(g => g.Key, g => g.Select(v => v.UserId).Distinct().Count());

        var avgVotersPerPoll = pollsCreated == 0
            ? 0d
            : Math.Round(weekPolls.Sum(p => votersByPoll.GetValueOrDefault(p.Id, 0)) / (double)pollsCreated, 1);

        var top = weekPolls
            .OrderByDescending(p => votersByPoll.GetValueOrDefault(p.Id, 0))
            .ThenBy(p => p.Id)
            .FirstOrDefault();
        var topVoters = top is null ? 0 : votersByPoll.GetValueOrDefault(top.Id, 0);

        return new PollsSnapshot(
            PollsCreated:     pollsCreated,
            PollsClosed:      pollsClosed,
            NativeCount:      nativeCount,
            AnonymousCount:   anonymousCount,
            TotalVotesCast:   totalVotes,
            DistinctVoters:   distinctVoters,
            AvgVotersPerPoll: avgVotersPerPoll,
            OpenPollsNow:     openNow,
            TopPollQuestion:  topVoters > 0 ? top?.Question : null,
            TopPollVoters:    topVoters,
            TopPollKind:      topVoters > 0 ? top?.Kind.ToString().ToLowerInvariant() : null,
            ChronicNonVoters: chronic);
    }

    /// <summary>
    /// Crosses the active roster against poll voters over the rolling window.
    /// Eligible = non-bot, non-exempt, non-AWOL members who joined before the
    /// window's first poll (so they had a shot at every poll in it). Returns null
    /// when there aren't enough polls in the window, or no eligible members.
    /// </summary>
    private static async Task<PollChronicNonVoters?> ComputeChronicAsync(
        BotDbContext db,
        SocketGuild guild,
        IReadOnlyCollection<string> exemptRoleNames,
        string awolRoleName,
        CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-ChronicWindowDays);

        var windowPolls = await db.Polls
            .Where(p => p.GuildId == guild.Id && p.CreatedAt >= windowStart)
            .Select(p => new { p.Id, p.CreatedAt })
            .ToListAsync(ct);

        if (windowPolls.Count < MinPollsForChronic) return null;

        var earliestPollUtc = windowPolls.Min(p => p.CreatedAt);
        var windowPollIds = windowPolls.Select(p => p.Id).ToHashSet();

        var voterIds = (await db.PollVotes
                .Where(v => windowPollIds.Contains(v.PollId))
                .Select(v => v.UserId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(awolRoleName, StringComparison.OrdinalIgnoreCase));

        // Eligible: active members who were present for the whole window.
        var eligible = new List<SocketGuildUser>();
        foreach (var m in guild.Users)
        {
            if (m.IsBot) continue;
            if (m.Roles.Any(r => exemptRoleNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase))) continue;
            if (awolRole is not null && m.Roles.Any(r => r.Id == awolRole.Id)) continue;
            if (!m.JoinedAt.HasValue) continue;
            if (m.JoinedAt.Value.UtcDateTime > earliestPollUtc) continue; // joined after the first poll → not a fair sample
            eligible.Add(m);
        }

        if (eligible.Count == 0) return null;

        var nonVoters = eligible.Where(m => !voterIds.Contains(m.Id)).ToList();
        var participants = eligible.Count - nonVoters.Count;
        var rate = Math.Round(participants * 100.0 / eligible.Count, 0);

        var sample = nonVoters
            .OrderBy(m => m.JoinedAt!.Value) // longest-tenured ghosters first — most worth a nudge
            .Take(MaxChronicSample)
            .Select(m => m.DisplayName)
            .ToList();

        return new PollChronicNonVoters(
            WindowDays:           ChronicWindowDays,
            PollsInWindow:        windowPolls.Count,
            EligibleMembers:      eligible.Count,
            Participants:         participants,
            NonVoters:            nonVoters.Count,
            ParticipationRatePct: rate,
            Sample:               sample);
    }
}
