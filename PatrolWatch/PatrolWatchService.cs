using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.PatrolWatch;

/// <summary>
/// Hosted service that watches voice + presence to surface "squads on patrol" —
/// 3+ members in the same voice channel who are simultaneously playing a
/// matched game (BF6 today; Arc Raiders etc. via config). Posts ONE rolling
/// embed per active patrol to the LFG channel and edits it in place as the
/// roster changes.
///
/// ── Architecture ──
/// State lives in memory only, keyed on voice channel ID. Patrols don't
/// need to survive a bot restart; on restart, any orphaned embed in Discord
/// becomes stale and an officer can delete it. A v2 could startup-scan the
/// LFG channel for orphans and clean them up.
///
/// ── Event flow ──
///   UserVoiceStateUpdated / PresenceUpdated
///     → ScheduleRecompute(channel) — debounced per-channel, ~12s
///       → RecomputePatrolAsync(channel) — gathers state, posts/edits/stands-down
///
/// PresenceUpdated fires constantly (status, custom status, activity changes).
/// The debouncer collapses bursts into a single recompute. Within a recompute
/// we always rebuild the matched-member set from scratch — never trust delta
/// logic.
///
/// ── Threading ──
/// ConcurrentDictionary for state + per-channel SemaphoreSlim to serialize
/// recomputes for the same channel. Two recomputes for *different* channels
/// can run in parallel.
///
/// ── PS5 / Xbox visibility caveat ──
/// Members on console only broadcast a Playing activity to Discord if they've
/// linked PSN / Xbox under User Settings → Connections. If they haven't, the
/// bot can't see them on patrol regardless of how active they are. /patrol info
/// surfaces this so members can self-diagnose.
/// </summary>
public sealed class PatrolWatchService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<PatrolWatchService> _logger;
    private readonly PatrolWatchOptions _options;

    // In-memory state. Bot restart wipes both — patrol sessions don't persist.
    private readonly ConcurrentDictionary<ulong, PatrolState> _activePatrols = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _debouncers = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();

    public PatrolWatchService(
        DiscordSocketClient client,
        IServiceProvider services,
        ILogger<PatrolWatchService> logger,
        IOptions<PatrolWatchOptions> options)
    {
        _client   = client;
        _services = services;
        _logger   = logger;
        _options  = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PatrolWatch disabled via config; not subscribing to events");
            return Task.CompletedTask;
        }

        if (_options.LfgChannelId == 0)
            _logger.LogWarning("PatrolWatch enabled but LfgChannelId is 0; embeds will not post");

        _client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        _client.PresenceUpdated       += OnPresenceUpdated;
        _client.Ready                 += OnReadyAsync;

        _logger.LogInformation(
            "PatrolWatch started; channel={LfgChannelId}, minSquad={MinSquad}, debounce={Debounce}s, games=[{Games}], excludedCategories={ExCats}, excludedChannels={ExChans}",
            _options.LfgChannelId,
            _options.MinSquadSize,
            _options.DebounceSeconds,
            string.Join(", ", _options.MatchedGames.Select(g => g.DisplayName)),
            _options.ExcludedCategoryIds.Count,
            _options.ExcludedChannelIds.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        _client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        _client.PresenceUpdated       -= OnPresenceUpdated;
        _client.Ready                 -= OnReadyAsync;

        return Task.CompletedTask;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        // A user moving from VC A to VC B affects BOTH channels:
        // A may have just dropped below threshold; B may have just crossed it.
        if (before.VoiceChannel != null) ScheduleRecompute(before.VoiceChannel);
        if (after.VoiceChannel != null && after.VoiceChannel.Id != before.VoiceChannel?.Id)
            ScheduleRecompute(after.VoiceChannel);
        return Task.CompletedTask;
    }

    private Task OnPresenceUpdated(SocketUser user, SocketPresence before, SocketPresence after)
    {
        // PresenceUpdated provides a SocketUser — not necessarily a SocketGuildUser —
        // so we look up guild membership ourselves. For a single-guild bot this
        // loop runs once; for multi-guild it scales linearly with mutual guilds.
        foreach (var guild in _client.Guilds)
        {
            var guildUser = guild.GetUser(user.Id);
            if (guildUser?.VoiceChannel != null)
                ScheduleRecompute(guildUser.VoiceChannel);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// On Ready, sweep every voice channel in every guild and schedule a
    /// recompute for the populated ones. This covers the cold-start gap:
    /// without it, members already in voice when the bot booted would be
    /// invisible to the watcher until something fired an event (mic toggle,
    /// status change, anyone joining or leaving a VC).
    ///
    /// Ready fires once per gateway handshake. We don't subscribe to Connected
    /// for reconnects — Discord.Net replays state diffs as gateway events on
    /// reconnect, so the existing UserVoiceStateUpdated / PresenceUpdated
    /// handlers cover that case without a second sweep.
    ///
    /// Each ScheduleRecompute call is debounced by DebounceSeconds, so embeds
    /// land roughly 12s after Ready — fine for startup, and any events that
    /// fire during that window coalesce into the same recompute.
    /// </summary>
    private Task OnReadyAsync()
    {
        var scanned   = 0;
        var populated = 0;
        var excluded  = 0;
        var scheduled = 0;

        foreach (var guild in _client.Guilds)
        {
            foreach (var vc in guild.VoiceChannels)
            {
                scanned++;
                if (vc.ConnectedUsers.Count == 0) continue;
                populated++;

                if (IsExcluded(vc))
                {
                    excluded++;
                    continue;
                }

                scheduled++;
                ScheduleRecompute(vc);
            }
        }

        _logger.LogInformation(
            "PatrolWatch cold-start scan: {Scanned} VCs scanned, {Populated} populated, {Excluded} excluded, {Scheduled} recomputes scheduled",
            scanned, populated, excluded, scheduled);

        return Task.CompletedTask;
    }

    // ── Debounce + recompute ────────────────────────────────────────

    private void ScheduleRecompute(SocketVoiceChannel channel)
    {
        // Skip channels we don't watch (events category, AFK rooms, etc).
        // Cheaper to bail here than to debounce + recompute + early-return —
        // PresenceUpdated fires hard during big events and we'd rather not
        // even allocate the per-channel CTS for excluded rooms.
        if (IsExcluded(channel)) return;

        var newCts = new CancellationTokenSource();

        // Replace any pending debouncer for this channel. If two events come
        // in milliseconds apart we cancel the first scheduled work and start
        // fresh — at most one recompute per debounce window.
        if (_debouncers.TryGetValue(channel.Id, out var existing))
        {
            try { existing.Cancel(); existing.Dispose(); } catch { /* ignored */ }
        }
        _debouncers[channel.Id] = newCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.DebounceSeconds), newCts.Token);
                await RecomputePatrolAsync(channel);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a fresher event — fine, ignore.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PatrolWatch recompute failed for channel {ChannelId}", channel.Id);
            }
        });
    }

    private async Task RecomputePatrolAsync(SocketVoiceChannel channel)
    {
        // Per-channel serialization. Two recomputes for the same channel never
        // overlap; recomputes for different channels run in parallel.
        var sem = _channelLocks.GetOrAdd(channel.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            await DoRecomputeAsync(channel);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task DoRecomputeAsync(SocketVoiceChannel channel)
    {
        // 1. Members currently in the VC (skip bots).
        var voiceMembers = channel.ConnectedUsers.Where(u => !u.IsBot).ToList();

        // 2. Filter to those broadcasting a matched game.
        var matched = new List<MatchedMember>();
        foreach (var member in voiceMembers)
        {
            var game = MatchActivity(member);
            if (game != null) matched.Add(new MatchedMember(member, game));
        }

        // 3. Drop opt-outs (one batched DB query rather than per-member).
        if (matched.Count > 0)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var memberIds = matched.Select(m => m.User.Id).ToList();
            var optedOutIds = await db.PatrolWatchOptOuts
                .Where(o => o.GuildId == channel.Guild.Id && memberIds.Contains(o.UserId))
                .Select(o => o.UserId)
                .ToListAsync();

            if (optedOutIds.Count > 0)
            {
                var optedOutSet = optedOutIds.ToHashSet();
                matched = matched.Where(m => !optedOutSet.Contains(m.User.Id)).ToList();
            }
        }

        // 4. State machine.
        _activePatrols.TryGetValue(channel.Id, out var existingState);

        if (matched.Count >= _options.MinSquadSize)
        {
            // Pick the dominant game when multiple are matched (e.g. someone on
            // BF6 + someone on Arc Raiders sharing a VC). Most members on a
            // single game wins. Members on a different game don't appear on
            // *this* patrol's roster line.
            //
            // Group by DisplayName (string) rather than reference equality on
            // the MatchedGame object — two ActivitySubstrings can both map to
            // the same logical game, and we want them counted together.
            var dominantGroup = matched
                .GroupBy(m => m.Game.DisplayName)
                .OrderByDescending(g => g.Count())
                .First();

            var dominantGame = dominantGroup.First().Game;

            var rosterForEmbed = dominantGroup
                .Select(m => m.User)
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Re-check threshold after filtering to dominant game.
            if (rosterForEmbed.Count < _options.MinSquadSize)
            {
                if (existingState != null)
                    await StandDownAsync(channel, existingState);
                return;
            }

            if (existingState == null)
                await CreateEmbedAsync(channel, rosterForEmbed, dominantGame);
            else if (RosterChanged(existingState, rosterForEmbed, dominantGame.DisplayName))
                await EditEmbedAsync(channel, existingState, rosterForEmbed, dominantGame);
        }
        else
        {
            if (existingState != null)
                await StandDownAsync(channel, existingState);
        }
    }

    // ── Activity matching ───────────────────────────────────────────

    private MatchedGame? MatchActivity(SocketGuildUser member)
    {
        if (member.Activities == null || member.Activities.Count == 0) return null;

        foreach (var activity in member.Activities)
        {
            // Playing covers most cases; Streaming covers members who are
            // streaming the same game on Twitch / YouTube — same op, count them.
            if (activity.Type != ActivityType.Playing && activity.Type != ActivityType.Streaming)
                continue;

            var name = activity.Name;
            if (string.IsNullOrEmpty(name)) continue;

            foreach (var game in _options.MatchedGames)
            {
                foreach (var sub in game.ActivitySubstrings)
                {
                    if (string.IsNullOrEmpty(sub)) continue;
                    if (name.Contains(sub, StringComparison.OrdinalIgnoreCase))
                        return game;
                }
            }
        }
        return null;
    }

    // ── Exclusions ──────────────────────────────────────────────────

    /// <summary>
    /// True if this voice channel should be ignored by Patrol Watch entirely.
    /// Three layers:
    ///   1. The guild's official AFK channel (Discord-native concept — the
    ///      VC that idle members get auto-moved into). Always excluded; no
    ///      config needed.
    ///   2. Any channel whose parent category is in ExcludedCategoryIds —
    ///      typically the events category, since events have their own
    ///      announcement surface and a Patrol Watch embed would just
    ///      duplicate the noise.
    ///   3. Any channel whose ID is in ExcludedChannelIds — for AFK rooms
    ///      not registered as Discord's official AFK channel, mod-only
    ///      VCs, etc.
    /// Checked once at the top of ScheduleRecompute so excluded channels
    /// never even allocate a debouncer CTS.
    /// </summary>
    private bool IsExcluded(SocketVoiceChannel channel)
    {
        if (channel.Guild.AFKChannel?.Id == channel.Id) return true;

        if (_options.ExcludedChannelIds.Contains(channel.Id)) return true;

        if (channel.CategoryId is ulong catId
            && _options.ExcludedCategoryIds.Contains(catId)) return true;

        return false;
    }

    // ── Embed lifecycle ─────────────────────────────────────────────

    private async Task CreateEmbedAsync(
        SocketVoiceChannel voiceChannel,
        List<SocketGuildUser> roster,
        MatchedGame game)
    {
        var lfgChannel = ResolveLfgChannel(voiceChannel.Guild);
        if (lfgChannel == null) return;

        var startedAt = DateTime.UtcNow;
        var embed = BuildEmbed(roster, game, voiceChannel, startedAt);

        try
        {
            var msg = await lfgChannel.SendMessageAsync(embed: embed);
            _activePatrols[voiceChannel.Id] = new PatrolState
            {
                VoiceChannelId = voiceChannel.Id,
                LfgChannelId   = lfgChannel.Id,
                MessageId      = msg.Id,
                StartedAtUtc   = startedAt,
                CurrentRoster  = roster.Select(u => u.Id).ToHashSet(),
                CurrentGame    = game.DisplayName,
            };

            _logger.LogInformation(
                "PatrolWatch embed posted in #{LfgChannel} for VC {VoiceChannel} ({Members} on {Game})",
                lfgChannel.Name, voiceChannel.Name, roster.Count, game.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post PatrolWatch embed in {LfgChannelId} for VC {VoiceChannelId}",
                lfgChannel.Id, voiceChannel.Id);
        }
    }

    private async Task EditEmbedAsync(
        SocketVoiceChannel voiceChannel,
        PatrolState state,
        List<SocketGuildUser> roster,
        MatchedGame game)
    {
        var lfgChannel = ResolveLfgChannel(voiceChannel.Guild);
        if (lfgChannel == null) return;

        var embed = BuildEmbed(roster, game, voiceChannel, state.StartedAtUtc);

        try
        {
            await lfgChannel.ModifyMessageAsync(state.MessageId, m => m.Embed = embed);
            state.CurrentRoster = roster.Select(u => u.Id).ToHashSet();
            state.CurrentGame   = game.DisplayName;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // Someone deleted the embed manually. Drop state so the next
            // recompute creates a fresh one.
            _logger.LogWarning("PatrolWatch embed {MessageId} no longer exists; dropping state", state.MessageId);
            _activePatrols.TryRemove(voiceChannel.Id, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to edit PatrolWatch embed {MessageId} for VC {VoiceChannelId}",
                state.MessageId, voiceChannel.Id);
        }
    }

    private async Task StandDownAsync(SocketVoiceChannel voiceChannel, PatrolState state)
    {
        // Always drop in-memory state, even if Discord ops fail —
        // we don't want to retry forever.
        _activePatrols.TryRemove(voiceChannel.Id, out _);

        var lfgChannel = ResolveLfgChannel(voiceChannel.Guild);
        if (lfgChannel == null) return;

        var lasted = DateTime.UtcNow - state.StartedAtUtc;
        var standDownEmbed = BuildStandDownEmbed(state.CurrentGame, lasted);

        try
        {
            await lfgChannel.ModifyMessageAsync(state.MessageId, m => m.Embed = standDownEmbed);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return; // Already gone, nothing to clean up.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to edit PatrolWatch embed {MessageId} on stand-down",
                state.MessageId);
            return;
        }

        // Schedule deletion after the linger window.
        var lingerSeconds = _options.StoodDownLingerSeconds;
        var messageId = state.MessageId;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(lingerSeconds));
                await lfgChannel.DeleteMessageAsync(messageId);
            }
            catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already deleted — fine.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete stood-down PatrolWatch embed {MessageId}",
                    messageId);
            }
        });
    }

    // ── Embed building ──────────────────────────────────────────────

    private static Embed BuildEmbed(
        List<SocketGuildUser> roster,
        MatchedGame game,
        SocketVoiceChannel voiceChannel,
        DateTime startedAtUtc)
    {
        // Vertical bullet roster — scales much better than inline-comma joins
        // for 4+ members and reads more like a deployment manifest.
        var rosterLines = string.Join("\n",
            roster.Select(u => $"- **{u.DisplayName}**"));

        // Discord's <t:unix:R> renders as a live-updating "X minutes ago"
        // string that the client refreshes on its own — we don't have to
        // edit the embed every minute to keep it fresh.
        var unixStart = new DateTimeOffset(
            DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        var description =
            $"{rosterLines}\n\n" +
            $"In <#{voiceChannel.Id}> · started <t:{unixStart}:R>";

        var color = ParseColor(game.AccentColor, fallback: new Color(0xC9, 0xA6, 0x47));

        var builder = new EmbedBuilder()
            .WithTitle($"Squad on patrol — {game.DisplayName}")
            .WithDescription(description)
            .WithFooter($"{roster.Count} on patrol")
            .WithTimestamp(startedAtUtc)
            .WithColor(color);

        // Skip the thumbnail entirely if not configured, rather than passing
        // an empty string (which Discord would reject).
        if (!string.IsNullOrWhiteSpace(game.ThumbnailUrl))
            builder.WithThumbnailUrl(game.ThumbnailUrl);

        return builder.Build();
    }

    private static Embed BuildStandDownEmbed(string game, TimeSpan lasted)
    {
        var formatted = lasted.TotalHours >= 1
            ? $"{(int)lasted.TotalHours}h {lasted.Minutes}m"
            : $"{lasted.Minutes}m";

        // Stand-down keeps the muted brown across all games on purpose —
        // the visual contrast (bright game-specific accent → uniform brown)
        // is the at-a-glance signal that the patrol is over.
        return new EmbedBuilder()
            .WithTitle($"Patrol stood down — {game}")
            .WithDescription($"Lasted {formatted}.")
            .WithColor(new Color(0x6B, 0x61, 0x47))
            .Build();
    }

    /// <summary>
    /// Parse a hex color string like "#C9A647" or "C9A647" into a Discord
    /// Color. Returns the fallback for null, empty, or unparseable input.
    /// Lenient on input format because config strings are easy to typo —
    /// we'd rather render with the default gold than crash on a missing #.
    /// </summary>
    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var trimmed = hex.TrimStart('#');
        if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return new Color(rgb);
        }
        return fallback;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private SocketTextChannel? ResolveLfgChannel(SocketGuild guild)
    {
        if (_options.LfgChannelId == 0) return null;
        var ch = guild.GetTextChannel(_options.LfgChannelId);
        if (ch == null)
        {
            _logger.LogWarning(
                "PatrolWatch LfgChannelId {LfgChannelId} not found in guild {GuildName}",
                _options.LfgChannelId, guild.Name);
        }
        return ch;
    }

    private static bool RosterChanged(PatrolState state, List<SocketGuildUser> roster, string game)
    {
        if (state.CurrentGame != game) return true;
        if (state.CurrentRoster.Count != roster.Count) return true;
        foreach (var u in roster)
            if (!state.CurrentRoster.Contains(u.Id)) return true;
        return false;
    }

    private record MatchedMember(SocketGuildUser User, MatchedGame Game);

    private sealed class PatrolState
    {
        public ulong VoiceChannelId { get; set; }
        public ulong LfgChannelId   { get; set; }
        public ulong MessageId      { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public HashSet<ulong> CurrentRoster { get; set; } = new();
        public string CurrentGame { get; set; } = string.Empty;
    }
}