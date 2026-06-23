using System.Collections.Concurrent;
using System.Text;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Background service for the two passive THE FINALS surfaces, both driven by one
/// leaderboard fetch + one roster read per cycle (via <see cref="FinalsRosterService"/>):
///
///   1. Clan leaderboard board — a single auto-updating message in
///      <see cref="BotConfig.FinalsBoardChannelId"/> ranking every clan member who's
///      on the global top-10k leaderboard. Edited in place when it changes; re-posted
///      if it goes missing. (Not a "sticky/re-stick on every message" board like the
///      events board — the FINALS channel is a low-traffic showcase, so a periodic
///      edit-in-place keeps it clean without API churn.)
///
///   2. Rank-up announcements — when a tracked member reaches a higher league than
///      we last saw, a celebratory message is posted to
///      <see cref="BotConfig.FinalsAnnounceChannelId"/>. The last-seen league is held
///      in memory; the FIRST observation of any player is a silent baseline (so a
///      restart never re-announces existing ranks), and only an actual climb after a
///      known baseline announces.
///
/// Each surface is independently gated (FinalsBoardEnabled / FinalsRankUpEnabled) and
/// the whole feature is gated by FinalsEnabled.
/// </summary>
public sealed class FinalsLeaderboardService : BackgroundService
{
    public const string BoardTitle = "🏆 THE FINALS — Clan Leaderboard";
    private const int BoardScanLimit = 50;
    private const int MaxBoardEntries = 25;
    private const int MaxDescription = 3900; // headroom under Discord's 4096 cap

    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private readonly FinalsRosterService _roster;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<FinalsLeaderboardService> _logger;

    // Player Embark name (or "id:{discordId}") → last-seen league number. First
    // observation seeds the baseline silently; only a later increase announces.
    private readonly ConcurrentDictionary<string, int> _lastLeague = new();

    public FinalsLeaderboardService(
        FinalsRosterService roster,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<FinalsLeaderboardService> logger)
    {
        _roster = roster;
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.FinalsEnabled)
        {
            _logger.LogInformation("FinalsLeaderboardService: FinalsEnabled is false — service idle.");
            return;
        }

        // Wait for Discord to be ready.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(15, _config.FinalsRefreshIntervalMinutes));
        _logger.LogInformation(
            "FinalsLeaderboardService started — board={Board}, rank-ups={RankUp}, every {Min}min",
            _config.FinalsBoardEnabled, _config.FinalsRankUpEnabled, interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "FINALS leaderboard cycle failed; will retry next interval"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        if (!_config.FinalsBoardEnabled && !_config.FinalsRankUpEnabled) return;

        var rankings = await _roster.GetClanRankingsAsync(ct);
        if (rankings.Count == 0)
        {
            _logger.LogDebug("FINALS: no ranked clan members this cycle (none in the top 10k, or roster/leaderboard unavailable).");
            // Still allow the board to render an empty state so it doesn't go stale.
        }

        if (_config.FinalsRankUpEnabled)
        {
            try { await CheckRankUpsAsync(rankings, ct); }
            catch (Exception ex) { _logger.LogError(ex, "FINALS rank-up check failed"); }
        }

        if (_config.FinalsBoardEnabled)
        {
            try { await UpdateBoardAsync(rankings, ct); }
            catch (Exception ex) { _logger.LogError(ex, "FINALS board update failed"); }
        }
    }

    // ─── Rank-up announcements ───────────────────────────────────────────────

    private async Task CheckRankUpsAsync(IReadOnlyList<FinalsMemberRank> rankings, CancellationToken ct)
    {
        var channelId = _config.FinalsAnnounceChannelId;
        if (channelId == 0)
        {
            _logger.LogDebug("FINALS rank-ups enabled but FinalsAnnounceChannelId is unset — skipping.");
            return;
        }
        if (_client.GetChannel(channelId) is not SocketTextChannel channel) return;

        foreach (var m in rankings)
        {
            var key = m.DiscordId.HasValue ? $"id:{m.DiscordId.Value}" : $"embark:{m.Entry.Name}";
            var current = m.Entry.LeagueNumber;

            if (_lastLeague.TryGetValue(key, out var previous))
            {
                if (current > previous)
                {
                    await AnnounceRankUpAsync(channel, m, ct);
                }
            }
            // else: first time we've seen this player — seed baseline silently below.

            _lastLeague[key] = current;
        }
    }

    private async Task AnnounceRankUpAsync(SocketTextChannel channel, FinalsMemberRank m, CancellationToken ct)
    {
        var mention = m.DiscordId.HasValue ? $"<@{m.DiscordId.Value}>" : $"**{m.DiscordName}**";
        var emoji = FinalsFormat.LeagueEmoji(m.Entry.League);

        var embed = new EmbedBuilder()
            .WithTitle($"{emoji} Rank Up — THE FINALS!")
            .WithColor(new Color(0xFFD166))
            .WithDescription(
                $"{mention} just climbed to {emoji} **{m.Entry.League}**!\n\n" +
                $"Rank Score **{m.Entry.RankScore:N0}** · Global Rank **#{m.Entry.Rank:N0}**")
            .WithFooter("THE FINALS leaderboard")
            .Build();

        try
        {
            await channel.SendMessageAsync(
                text: m.DiscordId.HasValue ? mention : null,
                embed: embed,
                allowedMentions: m.DiscordId.HasValue ? new AllowedMentions { UserIds = new List<ulong> { m.DiscordId.Value } } : AllowedMentions.None);

            _logger.LogInformation("FINALS rank-up announced: {Player} → {League}", m.Entry.Name, m.Entry.League);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FINALS: failed to post rank-up for {Player}", m.Entry.Name);
        }
    }

    // ─── Clan leaderboard board ──────────────────────────────────────────────

    private async Task UpdateBoardAsync(IReadOnlyList<FinalsMemberRank> rankings, CancellationToken ct)
    {
        var channelId = _config.FinalsBoardChannelId;
        if (channelId == 0)
        {
            _logger.LogDebug("FINALS board enabled but FinalsBoardChannelId is unset — skipping.");
            return;
        }
        if (_client.GetChannel(channelId) is not SocketTextChannel channel) return;
        if (_client.CurrentUser is null) return;

        var description = BuildBoardDescription(rankings);
        var embed = new EmbedBuilder()
            .WithTitle(BoardTitle)
            .WithColor(new Color(0xE63946))
            .WithDescription(description)
            .WithFooter("Auto-updated • global top-10k ranked players • use /finals-rank")
            .WithCurrentTimestamp()
            .Build();

        var recent = (await channel.GetMessagesAsync(BoardScanLimit).FlattenAsync()).ToList();
        var boards = recent.OfType<IUserMessage>().Where(IsBoardMessage).ToList();

        if (boards.Count >= 1)
        {
            var board = boards[0];
            var current = board.Embeds.FirstOrDefault();
            if (current is null || current.Description != description)
                await board.ModifyAsync(m => m.Embed = embed);

            // Clean up any stray duplicate boards.
            foreach (var dup in boards.Skip(1))
            {
                try { await dup.DeleteAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "FINALS board: couldn't delete duplicate {Msg}", dup.Id); }
            }
            return;
        }

        var posted = await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
        _logger.LogInformation("FINALS board (re)posted in channel {Channel} as {Msg}", channelId, posted.Id);
    }

    private bool IsBoardMessage(IUserMessage m) =>
        m.Author.Id == _client.CurrentUser!.Id
        && m.Embeds.Any(e => string.Equals(e.Title, BoardTitle, StringComparison.Ordinal));

    private static string BuildBoardDescription(IReadOnlyList<FinalsMemberRank> rankings)
    {
        if (rankings.Count == 0)
            return "_No clan members are on the ranked leaderboard right now (it's the global top 10,000). Link your Embark ID with **/gamertags** and climb!_";

        var sb = new StringBuilder();
        var shown = 0;
        for (var i = 0; i < rankings.Count && shown < MaxBoardEntries; i++)
        {
            var m = rankings[i];
            var place = (i + 1) switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => $"`#{i + 1,2}`",
            };

            var line =
                $"{place} {FinalsFormat.LeagueEmoji(m.Entry.League)} **{Sanitize(m.DiscordName)}** — " +
                $"{m.Entry.League} · {m.Entry.RankScore:N0} _(global #{m.Entry.Rank:N0})_\n";

            if (sb.Length + line.Length > MaxDescription)
                break;

            sb.Append(line);
            shown++;
        }

        if (rankings.Count > shown)
            sb.Append($"\n_…and {rankings.Count - shown} more ranked member(s)._");

        return sb.ToString().TrimEnd();
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        return s.Replace("`", "'").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "(").Replace("]", ")").Trim();
    }
}
