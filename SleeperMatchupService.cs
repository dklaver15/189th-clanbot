using System.Security.Cryptography;
using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Posts the fantasy football week to <see cref="BotConfig.SleeperChannelId"/>.
/// Three surfaces, each independently switchable:
///
///   1. Preview. When a new NFL week opens, the week's games go up once, with
///      records, before anybody kicks off.
///   2. Live scoreboard. One message, edited in place while games are on, with
///      the closest game at the top. Edited only when a score actually moved, so
///      a quiet Wednesday costs nothing.
///   3. Recap. When the NFL clock rolls past a week the bot posted, the final
///      scores go up with the week's high score, closest game and worst beating.
///
/// ── Why the NFL clock and not a calendar ──
/// Every decision here keys off Sleeper's own <c>/state/nfl</c>: which week is
/// live, and when it ends. Deriving a week from the date would drift the first time
/// the schedule shifts, and would have no way to know a week had finalised.
///
/// ── Restart safety ──
/// The three message ids per week live in <see cref="SleeperWeekPost"/>, not in
/// memory. The bot restarts on every deploy, which is far more often than a week
/// lasts, so in-memory state would re-post the preview and orphan the scoreboard
/// it was editing.
/// </summary>
public sealed class SleeperMatchupService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    /// <summary>Floor on the configured interval, to stay well inside Sleeper's rate limit.</summary>
    private const int MinIntervalMinutes = 5;

    private readonly SleeperApiService _api;
    private readonly SleeperLinkService _links;
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<SleeperMatchupService> _logger;

    public SleeperMatchupService(
        SleeperApiService api,
        SleeperLinkService links,
        DiscordSocketClient client,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<SleeperMatchupService> logger)
    {
        _api = api;
        _links = links;
        _client = client;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.SleeperEnabled)
        {
            _logger.LogInformation("SleeperMatchupService: SleeperEnabled is false, service idle.");
            return;
        }

        if (!_api.IsConfigured)
        {
            _logger.LogWarning("SleeperMatchupService: SleeperEnabled is true but SleeperLeagueId is empty, service idle.");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(MinIntervalMinutes, _config.SleeperRefreshIntervalMinutes));
        _logger.LogInformation(
            "SleeperMatchupService started. preview={Preview}, live={Live}, recap={Recap}, every {Min}min",
            _config.SleeperMatchupPreviewEnabled, _config.SleeperLiveScoresEnabled,
            _config.SleeperRecapEnabled, interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Sleeper matchup cycle failed; will retry next interval"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        if (!_config.SleeperMatchupPreviewEnabled
            && !_config.SleeperLiveScoresEnabled
            && !_config.SleeperRecapEnabled) return;

        var channelId = _config.SleeperChannelId;
        if (channelId == 0)
        {
            _logger.LogDebug("Sleeper posts are on but SleeperChannelId is unset, skipping.");
            return;
        }
        if (_client.GetChannel(channelId) is not SocketTextChannel channel) return;

        var state = await _api.GetNflStateAsync(ct);
        if (state is null) return;

        // Nothing to post before the season starts. Sleeper reports week 0 during
        // the preseason, so this also guards against a "week 0" row being created.
        if (!state.IsRegularSeason && !state.IsPostSeason)
        {
            _logger.LogDebug("Sleeper: NFL season type is {Type}, nothing to post yet.", state.SeasonType);
            return;
        }

        var league = await _api.GetLeagueAsync(ct);
        if (league is null) return;

        if (!league.IsDrafted)
        {
            _logger.LogDebug("Sleeper: league status is {Status}, no matchups until the draft is done.", league.Status);
            return;
        }

        var week = state.EffectiveWeek;
        if (week <= 0) return;

        var teams = await _api.GetTeamsAsync(ct);
        if (teams.Count == 0) return;

        var links = await _links.GetSleeperToDiscordAsync(channel.Guild.Id);

        // Close out any earlier weeks first, so a recap never lands after the next
        // week's preview when the bot was down over the rollover.
        if (_config.SleeperRecapEnabled)
            await PostPendingRecapsAsync(channel, league, state, week, teams, links, ct);

        var sides = await _api.GetMatchupsAsync(week, ct);
        var games = SleeperFormat.PairGames(sides, teams);
        if (games.Count == 0) return;

        var row = await GetOrCreateWeekRowAsync(channel, state.Season, week, ct);

        if (_config.SleeperMatchupPreviewEnabled && row.PreviewMessageId is null)
            await PostPreviewAsync(channel, league, week, games, links, row, ct);

        if (_config.SleeperLiveScoresEnabled && games.Any(g => g.HasStarted))
            await UpdateScoreboardAsync(channel, league, week, games, links, row, ct);
    }

    // ─── Preview ─────────────────────────────────────────────────────────────

    private async Task PostPreviewAsync(
        SocketTextChannel channel,
        SleeperLeague league,
        int week,
        IReadOnlyList<SleeperGame> games,
        IReadOnlyDictionary<string, ulong> links,
        SleeperWeekPost row,
        CancellationToken ct)
    {
        // Skip the preview entirely if the week is already under way when the bot
        // first sees it (a deploy mid-Sunday). A "preview" posted after kickoff is
        // just a worse scoreboard.
        if (games.Any(g => g.HasStarted))
        {
            _logger.LogDebug("Sleeper: week {Week} already has scores, skipping the preview.", week);
            await MarkPreviewSkippedAsync(row, ct);
            return;
        }

        var embed = SleeperFormat.BuildMatchupsEmbed(
            league, week, games, links, _config.SleeperMentionLinkedMembers,
            titlePrefix: "Matchups",
            footerNote: $"{league.ScoringLabel} · {games.Count} games · scores post here once they start");

        var msg = await channel.SendMessageAsync(embed: embed, allowedMentions: MentionPolicy());

        row.PreviewMessageId = msg.Id;
        row.UpdatedUtc = DateTime.UtcNow;
        await SaveRowAsync(row, ct);

        _logger.LogInformation("Sleeper: posted week {Week} preview ({Games} games)", week, games.Count);
    }

    /// <summary>
    /// Records that the preview was deliberately not posted, using the scoreboard's
    /// own sentinel of 0. Without this the service would re-evaluate the preview on
    /// every cycle for the rest of the week.
    /// </summary>
    private async Task MarkPreviewSkippedAsync(SleeperWeekPost row, CancellationToken ct)
    {
        row.PreviewMessageId = 0;
        row.UpdatedUtc = DateTime.UtcNow;
        await SaveRowAsync(row, ct);
    }

    // ─── Live scoreboard ─────────────────────────────────────────────────────

    private async Task UpdateScoreboardAsync(
        SocketTextChannel channel,
        SleeperLeague league,
        int week,
        IReadOnlyList<SleeperGame> games,
        IReadOnlyDictionary<string, ulong> links,
        SleeperWeekPost row,
        CancellationToken ct)
    {
        var body = SleeperFormat.BuildGamesBody(games, links, _config.SleeperMentionLinkedMembers, showScores: true);
        var hash = Hash(body);

        // Nothing moved since the last cycle. Skipping here is the difference
        // between a handful of edits a week and one every five minutes all season.
        if (row.ScoreboardMessageId is not null && row.ScoreboardHash == hash) return;

        var embed = SleeperFormat.BuildMatchupsEmbed(
            league, week, games, links, _config.SleeperMentionLinkedMembers,
            titlePrefix: "Scoreboard");

        if (row.ScoreboardMessageId is null or 0)
        {
            var msg = await channel.SendMessageAsync(embed: embed, allowedMentions: MentionPolicy());
            row.ScoreboardMessageId = msg.Id;
            _logger.LogInformation("Sleeper: posted week {Week} live scoreboard", week);
        }
        else
        {
            try
            {
                var existing = await channel.GetMessageAsync(row.ScoreboardMessageId.Value);
                if (existing is IUserMessage userMsg)
                {
                    await userMsg.ModifyAsync(m =>
                    {
                        m.Embed = embed;
                        m.AllowedMentions = MentionPolicy();
                    });
                }
                else
                {
                    // Deleted by hand, or too old to fetch. Re-post rather than
                    // silently stop updating for the rest of the week.
                    var msg = await channel.SendMessageAsync(embed: embed, allowedMentions: MentionPolicy());
                    row.ScoreboardMessageId = msg.Id;
                    _logger.LogInformation("Sleeper: week {Week} scoreboard was missing, re-posted", week);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sleeper: could not edit the week {Week} scoreboard", week);
                return;
            }
        }

        row.ScoreboardHash = hash;
        row.UpdatedUtc = DateTime.UtcNow;
        await SaveRowAsync(row, ct);
    }

    // ─── Recap ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Posts the recap for every week the bot covered that the NFL clock has since
    /// moved past. Bounded to weeks that already have a row, so deploying mid-season
    /// does not backfill recaps for weeks nobody was watching.
    /// </summary>
    private async Task PostPendingRecapsAsync(
        SocketTextChannel channel,
        SleeperLeague league,
        SleeperNflState state,
        int currentWeek,
        IReadOnlyList<SleeperTeam> teams,
        IReadOnlyDictionary<string, ulong> links,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var pending = await db.SleeperWeekPosts
            .Where(p => p.GuildId == channel.Guild.Id
                     && p.Season == state.Season
                     && p.Week < currentWeek
                     && p.RecapMessageId == null)
            .OrderBy(p => p.Week)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var players = await _api.GetPlayersAsync(ct);

        foreach (var row in pending)
        {
            var sides = await _api.GetMatchupsAsync(row.Week, ct);
            var games = SleeperFormat.PairGames(sides, teams);
            if (games.Count == 0) continue;

            var embed = SleeperFormat.BuildRecapEmbed(
                league, row.Week, games, links, _config.SleeperMentionLinkedMembers, players);

            var target = _client.GetChannel(row.ChannelId) as SocketTextChannel ?? channel;
            var msg = await target.SendMessageAsync(embed: embed, allowedMentions: MentionPolicy());

            row.RecapMessageId = msg.Id;
            row.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Sleeper: posted week {Week} recap", row.Week);
        }
    }

    // ─── Persistence ─────────────────────────────────────────────────────────

    private async Task<SleeperWeekPost> GetOrCreateWeekRowAsync(
        SocketTextChannel channel, string season, int week, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var row = await db.SleeperWeekPosts
            .FirstOrDefaultAsync(p => p.GuildId == channel.Guild.Id && p.Season == season && p.Week == week, ct);

        if (row is not null) return row;

        row = new SleeperWeekPost
        {
            GuildId = channel.Guild.Id,
            Season = season,
            Week = week,
            ChannelId = channel.Id,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        db.SleeperWeekPosts.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Writes a week row back through a fresh scope. The row was read on an earlier
    /// scope that is already disposed, so it is re-attached here rather than tracked
    /// across the cycle.
    /// </summary>
    private async Task SaveRowAsync(SleeperWeekPost row, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var tracked = await db.SleeperWeekPosts.FirstOrDefaultAsync(p => p.Id == row.Id, ct);
        if (tracked is null) return;

        tracked.PreviewMessageId = row.PreviewMessageId;
        tracked.ScoreboardMessageId = row.ScoreboardMessageId;
        tracked.RecapMessageId = row.RecapMessageId;
        tracked.ScoreboardHash = row.ScoreboardHash;
        tracked.UpdatedUtc = row.UpdatedUtc;

        await db.SaveChangesAsync(ct);
    }

    // ─── Shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the weekly posts actually notify people.
    ///
    /// Default is text-only: a linked member's name still renders as a clickable
    /// mention, it just does not fire a notification. Sixteen pings a week, every
    /// week, from a side activity is how a feature gets muted. Flip
    /// <see cref="BotConfig.SleeperPingOnPost"/> if the league wants the nudge.
    ///
    /// Even when pings are on the policy is users-only, never Everyone or Roles.
    /// Team names are member-supplied free text, and a permissive policy would make
    /// an @everyone in a team name the league's problem sixteen times a season.
    /// </summary>
    private AllowedMentions MentionPolicy() =>
        _config.SleeperPingOnPost
            ? new AllowedMentions(AllowedMentionTypes.Users)
            : AllowedMentions.None;

    private static string Hash(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes);
    }
}
