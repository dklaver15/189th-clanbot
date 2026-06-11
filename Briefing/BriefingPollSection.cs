using ClanGuardBot.Data;
using ClanGuardBot.Models;
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
/// actually weighed in, and which question drew the room.
///
/// ── Scope ──
/// Counts are scoped to polls CREATED in the briefing week — the "what happened
/// this week" frame used everywhere else. OpenNow is the one live cross-week
/// figure: polls still accepting votes right now, so officers know what's
/// outstanding.
/// </summary>
internal static class BriefingPollSection
{
    /// <summary>
    /// Returns null when the week saw no polls created AND none are currently
    /// open — operationally identical to "feature unused this week," so the
    /// prompt skips the section rather than printing boilerplate (same null-means-
    /// skip convention as RedditLeads / Retention).
    /// </summary>
    public static async Task<PollsSnapshot?> CollectAsync(
        BotDbContext db,
        ulong guildId,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default)
    {
        // Polls created in the briefing window (lightweight projection).
        var weekPolls = await db.Polls
            .Where(p => p.GuildId == guildId && p.CreatedAt >= weekStart && p.CreatedAt < weekEnd)
            .Select(p => new { p.Id, p.Question, p.Kind, p.ClosedAtUtc })
            .ToListAsync(ct);

        var openNow = await db.Polls
            .CountAsync(p => p.GuildId == guildId && p.Status == PollStatus.Open, ct);

        if (weekPolls.Count == 0 && openNow == 0) return null;

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

        // Distinct voters per poll → average participation + the standout poll.
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
            TopPollKind:      topVoters > 0 ? top?.Kind.ToString().ToLowerInvariant() : null);
    }
}
