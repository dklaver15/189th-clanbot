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
    private readonly FinalsApiService _api;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<FinalsLeaderboardService> _logger;

    // "{season}:{playerKey}" → highest league number we've seen for that player this
    // season (a high-water mark). First observation seeds the mark silently; only a
    // climb ABOVE the mark announces, and the mark is only ever raised — never lowered.
    // This makes announcements idempotent against transient dips (a flaky API cycle that
    // reports a lower/zero league, then recovers) which would otherwise re-fire the same
    // rank-up. Keying by season lets a legitimate season reset re-baseline silently.
    private readonly ConcurrentDictionary<string, int> _lastLeague = new();

    // The newer season id we've already nagged about, so the rollover reminder
    // posts once rather than every cycle until the config is bumped (manual mode).
    private string? _rolloverAlertedFor;

    // The effective season seen on the previous cycle (auto-detect mode). First
    // cycle seeds this silently; a later increase posts a one-time "switched" FYI.
    private string? _lastKnownEffectiveVersion;

    public FinalsLeaderboardService(
        FinalsRosterService roster,
        FinalsApiService api,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<FinalsLeaderboardService> logger)
    {
        _roster = roster;
        _api = api;
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
        if (_config.FinalsSeasonRolloverReminderEnabled)
        {
            try { await CheckSeasonRolloverAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "FINALS season-rollover check failed"); }
        }

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

        // Scope the dedup keys to the current season so a season reset re-baselines
        // silently rather than announcing everyone's placement climbs as "rank-ups".
        var season = string.IsNullOrWhiteSpace(_api.EffectiveVersion) ? "?" : _api.EffectiveVersion;

        foreach (var m in rankings)
        {
            var current = m.Entry.LeagueNumber;

            // Ignore invalid/unknown reads (0 or negative). Treating a flaky "no league"
            // response as a real value would drop the high-water mark and let the next
            // valid read re-announce the same rank-up.
            if (current <= 0) continue;

            var playerKey = m.DiscordId.HasValue ? $"id:{m.DiscordId.Value}" : $"embark:{m.Entry.Name}";
            var key = $"{season}:{playerKey}";

            if (_lastLeague.TryGetValue(key, out var best))
            {
                // Only announce a genuine climb above the best we've ever seen this
                // season, and only then raise the mark. A dip (or unchanged league)
                // never lowers it, so a later recovery won't re-announce.
                if (current > best)
                {
                    await AnnounceRankUpAsync(channel, m, ct);
                    _lastLeague[key] = current;
                }
            }
            else
            {
                // First sighting this season — seed the high-water mark silently.
                _lastLeague[key] = current;
            }
        }
    }

    private async Task AnnounceRankUpAsync(SocketTextChannel channel, FinalsMemberRank m, CancellationToken ct)
    {
        // Prefer the Discord ID linked in the roster; if the roster row has no ID (the
        // member linked only a gamertag), try to resolve them by name in this guild so
        // we can still tag them. Fall back to a bold display name if no match is found.
        var discordId = m.DiscordId ?? ResolveGuildUserId(channel.Guild, m.DiscordName);
        var mention = discordId.HasValue ? $"<@{discordId.Value}>" : $"**{m.DiscordName}**";
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
                text: discordId.HasValue ? mention : null,
                embed: embed,
                allowedMentions: discordId.HasValue ? new AllowedMentions { UserIds = new List<ulong> { discordId.Value } } : AllowedMentions.None);

            _logger.LogInformation("FINALS rank-up announced: {Player} → {League}", m.Entry.Name, m.Entry.League);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FINALS: failed to post rank-up for {Player}", m.Entry.Name);
        }
    }

    /// <summary>
    /// Best-effort resolution of a Discord user id from a display name, for members
    /// whose roster row has no linked id. Matches (case-insensitive) on nickname,
    /// global/display name, or username; returns null if there's no unambiguous hit.
    /// </summary>
    private static ulong? ResolveGuildUserId(SocketGuild guild, string? name)
    {
        if (guild is null || string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();

        var matches = guild.Users
            .Where(u =>
                string.Equals(u.Nickname, n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.DisplayName, n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.GlobalName, n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Username, n, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Id)
            .Distinct()
            .Take(2)
            .ToList();

        // Only tag on a single unambiguous match — never risk pinging the wrong person.
        return matches.Count == 1 ? matches[0] : null;
    }

    // ─── Season-rollover reminder ────────────────────────────────────────────

    private async Task CheckSeasonRolloverAsync(CancellationToken ct)
    {
        if (_config.FinalsAutoDetectSeason)
            await CheckAutoSwitchAsync(ct);
        else
            await CheckManualRolloverAsync(ct);
    }

    /// <summary>
    /// Auto-detect mode: the bot already follows the live season on its own, so this
    /// just posts a one-time FYI when the resolved season advances. Warms the API
    /// first so the resolved season is current before we compare.
    /// </summary>
    private async Task CheckAutoSwitchAsync(CancellationToken ct)
    {
        await _api.EnsureWarmAsync(ct);
        var eff = _api.EffectiveVersion;
        if (string.IsNullOrWhiteSpace(eff)) return;

        if (_lastKnownEffectiveVersion is null)
        {
            _lastKnownEffectiveVersion = eff;  // silent baseline — no announce on first sight / restart
            return;
        }

        if (eff == _lastKnownEffectiveVersion)
            return;

        var advanced = SeasonNumber(eff) is { } now && SeasonNumber(_lastKnownEffectiveVersion) is { } prev && now > prev;
        var previous = _lastKnownEffectiveVersion;
        _lastKnownEffectiveVersion = eff;

        if (!advanced) return;  // changed but not an advance (e.g. config edit) — update silently

        var embed = new EmbedBuilder()
            .WithTitle("🗓️ THE FINALS — new season")
            .WithColor(new Color(0x57F287))
            .WithDescription(
                $"A new ranked season (**{eff}**) is live, and the bot has **automatically switched** to it " +
                $"(was {previous}). `/finals-rank`, the clan board, and rank-ups now track **{eff}** — no action needed.")
            .WithFooter("Auto-detected • THE FINALS leaderboard")
            .Build();

        await PostNoticeAsync(embed, $"auto-switched {previous}→{eff}", officerFacing: false, ct);
    }

    /// <summary>
    /// Manual mode (auto-detect off): nag once to bump the config when a newer
    /// season goes live, since the bot won't follow it on its own.
    /// </summary>
    private async Task CheckManualRolloverAsync(CancellationToken ct)
    {
        var newer = await _api.DetectNewerSeasonAsync(ct);
        if (newer is null || newer == _rolloverAlertedFor) return;

        var current = _api.EffectiveVersion;
        var embed = new EmbedBuilder()
            .WithTitle("🗓️ THE FINALS — new season detected")
            .WithColor(new Color(0xFAA61A))
            .WithDescription(
                $"A newer ranked season (**{newer}**) is now live, but the bot is still reading **{current}** " +
                $"(now frozen/archived).\n\n" +
                $"Update **`BotConfig.FinalsLeaderboardVersion`** to **`{newer}`** in `appsettings.json` and restart " +
                $"— or set **`FinalsAutoDetectSeason: true`** to have the bot follow seasons automatically.")
            .WithFooter("One-time reminder • THE FINALS leaderboard")
            .Build();

        await PostNoticeAsync(embed, $"manual rollover reminder for {newer}", officerFacing: true, ct);
        _rolloverAlertedFor = newer;
    }

    /// <summary>
    /// Posts a season notice. Officer-facing notices (the manual-mode "bump the config"
    /// nag) go to HQ first; member-facing notices (the auto-switch FYI, which the whole
    /// clan should see and needs no action) go to the public FINALS channel first so they
    /// don't land in the moderator log.
    /// </summary>
    private async Task PostNoticeAsync(Embed embed, string what, bool officerFacing, CancellationToken ct)
    {
        var channelId = officerFacing
            ? (_config.HqChannelId != 0 ? _config.HqChannelId
                : _config.FinalsAnnounceChannelId != 0 ? _config.FinalsAnnounceChannelId
                : _config.FinalsBoardChannelId)
            : (_config.FinalsAnnounceChannelId != 0 ? _config.FinalsAnnounceChannelId
                : _config.FinalsBoardChannelId != 0 ? _config.FinalsBoardChannelId
                : _config.HqChannelId);

        if (channelId == 0 || _client.GetChannel(channelId) is not SocketTextChannel channel)
        {
            _logger.LogWarning("FINALS: {What}, but no channel is configured to post the notice.", what);
            return;
        }

        try
        {
            await channel.SendMessageAsync(embed: embed);
            _logger.LogInformation("FINALS season notice posted ({What})", what);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FINALS: failed to post season notice ({What})", what);
        }
    }

    private static int? SeasonNumber(string version) =>
        version.StartsWith("s", StringComparison.OrdinalIgnoreCase) && int.TryParse(version.AsSpan(1), out var n)
            ? n
            : null;

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
