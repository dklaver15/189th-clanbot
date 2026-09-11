using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Turns DiscordConnector's webhook posts into <see cref="ValheimSession"/> and
/// <see cref="ValheimDeath"/> rows, and optionally posts a readable join/leave feed.
///
/// ── The data path ──
/// Valheim server → DiscordConnector (BepInEx mod) → Discord webhook →
/// <see cref="BotConfig.ValheimRawChannelId"/> → this handler. The mod's message
/// templates are configured to emit the machine-readable format that
/// <see cref="ValheimEventParser"/> reads; the prose defaults are not parsed.
///
/// This indirection through a Discord channel exists because the bot exposes no
/// inbound HTTP surface (see HeartbeatService for why that was avoided), so a
/// webhook into a channel the bot already reads is the delivery mechanism that costs
/// no new infrastructure.
///
/// ── Trust boundary ──
/// The ingest channel is a SHARED channel (#bot-stuff), not a locked one, so the
/// authenticity check matters: only messages posted by a WEBHOOK are considered, and
/// when <see cref="BotConfig.ValheimRawWebhookId"/> is set, only that specific one.
/// Ordinary members cannot post as a webhook, so they cannot forge a join or leave
/// and inflate their own playtime. <see cref="ValheimEventParser"/> then whitelists
/// the shape of what a webhook did send.
///
/// ── Why everything is serialized ──
/// Discord delivers events on the gateway task and several can land within
/// milliseconds (a join and a death; everyone leaving at once on shutdown). Each
/// handler opens its own DbContext and reads-then-writes the same open session rows,
/// so without a lock two events for one player can interleave and produce two open
/// sessions or a double-close. Volume here is a handful of events per minute at
/// worst, so one global semaphore is simpler than per-player locks and costs
/// nothing.
///
/// ── Gateway safety ──
/// Handlers run ON the gateway task, and this one does database work, so it
/// dispatches to Task.Run and returns immediately — the same rule the slash-command
/// handlers follow.
/// </summary>
public class ValheimEventIngestHandler
{
    /// <summary>Serializes DB mutation; see the class remarks.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ValheimQueryService _query;
    private readonly ILogger<ValheimEventIngestHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Whether we've already complained that %PLAYER_ID% isn't substituting. Logged
    /// once rather than per event — it's a configuration fact, not a recurring
    /// incident, and it would otherwise fill the log for as long as it went unfixed.
    /// </summary>
    private bool _warnedAboutMissingId;

    public ValheimEventIngestHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ValheimQueryService query,
        ILogger<ValheimEventIngestHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _query = query;
        _logger = logger;
        _config = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
    }

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!_config.ValheimEnabled || !_config.ValheimIngestEnabled) return Task.CompletedTask;
        if (_config.ValheimRawChannelId == 0) return Task.CompletedTask;
        if (message.Channel.Id != _config.ValheimRawChannelId) return Task.CompletedTask;

        // ── Authenticity ──
        // A member typing "DCX1|join|..." in a shared channel must not be able to
        // manufacture playtime. Webhook authorship is the barrier they can't cross.
        if (!message.Author.IsWebhook) return Task.CompletedTask;

        // Optional second lock: pin to one webhook, so another integration in the
        // same channel can't be mistaken for the game server either.
        if (_config.ValheimRawWebhookId != 0 && message.Author.Id != _config.ValheimRawWebhookId)
            return Task.CompletedTask;

        if (!ValheimEventParser.TryParse(message.Content, out var parsed) || parsed is null)
            return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Valheim: failed to ingest {Kind} event", parsed.Kind);
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(ValheimEvent ev)
    {
        if (ev.IsPlayerEvent && !ev.HasStableId && !_warnedAboutMissingId)
        {
            _warnedAboutMissingId = true;
            _logger.LogWarning(
                "Valheim: DiscordConnector is not supplying %PLAYER_ID% — sessions will be keyed on " +
                "character NAME instead, which breaks on renames and merges players who share a name. " +
                "Check the Player Join/Leave/Death message templates in the mod's messages config.");
        }

        string? feed = null;

        // Set by the server lifecycle events only; null means "not a transition",
        // which is every player event.
        bool? serverOnline = null;

        // Declared outside the lock so the Discord posts below can happen after it is
        // released — see the note at the end of this method.
        await Gate.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var now = DateTime.UtcNow;

            switch (ev.Kind)
            {
                case ValheimEventKind.Join:
                    feed = await HandleJoinAsync(db, ev, now);
                    break;

                case ValheimEventKind.Leave:
                    feed = await HandleLeaveAsync(db, ev, now);
                    break;

                case ValheimEventKind.Death:
                    feed = await HandleDeathAsync(db, ev, now);
                    break;

                case ValheimEventKind.ServerStart:
                case ValheimEventKind.ServerStop:
                    await CloseAllOpenSessionsAsync(db, ev.Kind, now);
                    serverOnline = ev.Kind == ValheimEventKind.ServerStart;

                    // Recorded so /valheim-status can answer "is it up, and since
                    // when?" after a bot restart, when nothing is in memory.
                    db.ValheimServerEvents.Add(new ValheimServerEvent
                    {
                        Online = serverOnline.Value,
                        OccurredUtc = now,
                    });
                    break;
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            Gate.Release();
        }

        // Discord I/O happens after the commit AND outside the lock. Both halves
        // matter: committing first means a failed post never costs a session row,
        // and releasing first means a slow or rate-limited REST call doesn't stall
        // every other event behind it. That is not hypothetical — a server shutdown
        // with a full lobby produces a burst of events at once, and serializing the
        // database work behind eight Discord round-trips is how a burst turns into a
        // backlog.
        if (feed is not null) await PostFeedAsync(feed);
        if (serverOnline is bool online) await PostStatusEmbedAsync(online);
    }

    // ─── Event handling ──────────────────────────────────────────────────────

    private async Task<string?> HandleJoinAsync(BotDbContext db, ValheimEvent ev, DateTime now)
    {
        // A join while a session is already open means the previous leave never
        // arrived — a crash, a webhook drop, or a reconnect fast enough to race.
        // Close the stale row at its last observed time rather than leaving two open
        // rows for one player, which would double-count every subsequent query.
        var stale = await OpenSessionsFor(db, ev).ToListAsync();
        foreach (var s in stale)
        {
            s.EndedUtc = s.LastSeenUtc;
            _logger.LogInformation(
                "Valheim: {Player} joined with a session already open; closed the stale one at {LastSeen:u}",
                ev.PlayerName, s.LastSeenUtc);
        }

        db.ValheimSessions.Add(new ValheimSession
        {
            ValheimPlayerId = ev.PlayerId,
            PlayerName = ev.PlayerName,
            StartedUtc = now,
            LastSeenUtc = now,
            EndedUtc = null,
        });

        return JoinMessage(ev, await ResolveLinkAsync(db, ev));
    }

    private async Task<string?> HandleLeaveAsync(BotDbContext db, ValheimEvent ev, DateTime now)
    {
        var session = await OpenSessionsFor(db, ev)
            .OrderByDescending(s => s.StartedUtc)
            .FirstOrDefaultAsync();

        // No open session: normal after a server_start closed everything, or if the
        // bot was down when they joined. Nothing to correct — a leave with no start
        // has no duration to credit.
        if (session is null)
        {
            _logger.LogDebug("Valheim: leave for {Player} with no open session; ignoring", ev.PlayerName);
            return LeaveMessage(ev, null, await ResolveLinkAsync(db, ev));
        }

        session.EndedUtc = now;
        session.LastSeenUtc = now;

        // Refresh the name snapshot: a rename mid-session should land on the row it
        // actually belongs to.
        if (ev.PlayerName.Length > 0) session.PlayerName = ev.PlayerName;

        return LeaveMessage(ev, session.Duration, await ResolveLinkAsync(db, ev));
    }

    private async Task<string?> HandleDeathAsync(BotDbContext db, ValheimEvent ev, DateTime now)
    {
        db.ValheimDeaths.Add(new ValheimDeath
        {
            ValheimPlayerId = ev.PlayerId,
            PlayerName = ev.PlayerName,
            DiedUtc = now,
        });

        // A death proves they were still connected, so it advances the open
        // session's upper bound. Without a poll loop this is the only thing keeping
        // LastSeenUtc current, and it is what bounds the damage if the leave is
        // later lost.
        var open = await OpenSessionsFor(db, ev).ToListAsync();
        foreach (var s in open) s.LastSeenUtc = now;

        return DeathMessage(ev, await ResolveLinkAsync(db, ev));
    }

    /// <summary>
    /// Closes every open session on a server lifecycle event.
    ///
    /// <para>This is the reconciliation anchor that a polling integration doesn't
    /// need. The server starting or stopping is proof that nobody is still connected
    /// from before it — and a crash, which is precisely when leave events go missing,
    /// is always followed by a start. Without this, one crash would leave sessions
    /// open forever and silently inflate the leaderboard.</para>
    ///
    /// ── Where the session is closed depends on which event ──
    /// <para>server_stop means the server is shutting down RIGHT NOW, so anyone still
    /// open was connected until this moment: close at <paramref name="now"/>. That
    /// matters more than it sounds. LastSeenUtc only advances on events, and a player
    /// who joins and then plays quietly generates none — so closing at LastSeenUtc
    /// would credit them from join to join, i.e. ZERO, for the whole stint. On a
    /// server that restarts on a schedule, that silently erases most of everyone's
    /// playtime.</para>
    ///
    /// <para>server_start is the opposite case: any session still open is a leftover
    /// from a previous run that never sent server_stop (a crash, or the bot being
    /// down). We have no idea when those players actually left, so close at
    /// LastSeenUtc and credit only what was observed — never the downtime.</para>
    ///
    /// <para>Deliberately silent either way: the players didn't leave, the server
    /// did. A burst of leave lines on every restart is exactly the noise this must
    /// not produce — same reasoning as the Palworld and Satisfactory pollers.</para>
    /// </summary>
    private async Task CloseAllOpenSessionsAsync(BotDbContext db, ValheimEventKind kind, DateTime now)
    {
        var open = await db.ValheimSessions.Where(s => s.EndedUtc == null).ToListAsync();
        if (open.Count == 0) return;

        var stopping = kind == ValheimEventKind.ServerStop;

        foreach (var s in open)
        {
            // Guard against a clock skew or a stale row making the session negative.
            var end = stopping && now > s.LastSeenUtc ? now : s.LastSeenUtc;
            s.EndedUtc = end;
            s.LastSeenUtc = end;
        }

        _logger.LogInformation(
            "Valheim: {Kind} closed {Count} open session(s) at {Where}",
            kind, open.Count, stopping ? "the shutdown time" : "their last-seen times");
    }

    /// <summary>
    /// Open sessions belonging to this player.
    ///
    /// <para>Matches on the platform id when we have one and on the character name
    /// otherwise — never both, because mixing them would let a renamed player match
    /// a stranger's row. See <see cref="ValheimEvent.IdentityKey"/>.</para>
    /// </summary>
    private static IQueryable<ValheimSession> OpenSessionsFor(BotDbContext db, ValheimEvent ev) =>
        ev.HasStableId
            ? db.ValheimSessions.Where(s => s.EndedUtc == null && s.ValheimPlayerId == ev.PlayerId)
            : db.ValheimSessions.Where(s => s.EndedUtc == null && s.ValheimPlayerId == "" && s.PlayerName == ev.PlayerName);

    // ─── Feed ────────────────────────────────────────────────────────────────

    private static async Task<ulong?> ResolveLinkAsync(BotDbContext db, ValheimEvent ev)
    {
        if (!ev.HasStableId) return null;

        var link = await db.ValheimLinks
            .Where(l => l.ValheimPlayerId == ev.PlayerId)
            .Select(l => (ulong?)l.DiscordUserId)
            .FirstOrDefaultAsync();

        return link;
    }

    /// <summary>"**Brandt** (@Dan)" when linked, else the escaped name. Safe to
    /// include a mention because every send uses AllowedMentions.None.</summary>
    private static string FeedName(ValheimEvent ev, ulong? discordId)
    {
        var name = ValheimStatusService.Escape(ev.PlayerName);
        return discordId is ulong id ? $"**{name}** (<@{id}>)" : $"**{name}**";
    }

    private static string JoinMessage(ValheimEvent ev, ulong? link) =>
        $"🟢 {FeedName(ev, link)} joined the server{CountSuffix(ev)}";

    private static string LeaveMessage(ValheimEvent ev, TimeSpan? played, ulong? link) =>
        $"🔴 {FeedName(ev, link)} left" +
        (played is TimeSpan d && d > TimeSpan.Zero ? $" after {ValheimStatusService.Humanize(d)}" : "") +
        CountSuffix(ev);

    private static string DeathMessage(ValheimEvent ev, ulong? link) =>
        $"💀 {FeedName(ev, link)} died.";

    private static string CountSuffix(ValheimEvent ev) =>
        ev.OnlineCount is int n ? $" — {n} online" : "";

    /// <summary>
    /// Posts the green/red server status embed on a lifecycle event.
    ///
    /// <para>Gated by <see cref="BotConfig.ValheimServerStatusAnnounceEnabled"/> and a
    /// channel, but NOT by <see cref="BotConfig.ValheimFeedEnabled"/> — an outage
    /// notice and per-player chatter are wanted at completely different rates, and
    /// the common preference is exactly this pair: feed off, outages on.</para>
    ///
    /// <para>Marked authoritative because the server process itself reported the
    /// transition, unlike <see cref="ValheimStatusService"/>'s inference from silence.
    /// That service defers to this one whenever ingest is configured, so only one of
    /// them ever speaks.</para>
    /// </summary>
    private async Task PostStatusEmbedAsync(bool online)
    {
        if (!_config.ValheimServerStatusAnnounceEnabled) return;
        if (_config.ValheimFeedChannelId == 0) return;

        var embed = ValheimStatusService.BuildStatusEmbed(online, _query.JoinAddress, authoritative: true);

        try
        {
            var channel = await ResolveFeedChannelAsync();
            if (channel is null) return;
            await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Valheim: failed to post server status embed");
        }
    }

    private async Task<IMessageChannel?> ResolveFeedChannelAsync()
    {
        var channel = _client.GetChannel(_config.ValheimFeedChannelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(_config.ValheimFeedChannelId) as IMessageChannel;

        if (channel is null)
            _logger.LogWarning("Valheim: could not resolve feed channel {ChannelId}", _config.ValheimFeedChannelId);

        return channel;
    }

    private async Task PostFeedAsync(string message)
    {
        if (!_config.ValheimFeedEnabled || _config.ValheimFeedChannelId == 0) return;

        try
        {
            var channel = await ResolveFeedChannelAsync();
            if (channel is null) return;

            await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            // A failed post is not a failed ingest — the row is already committed.
            _logger.LogWarning(ex, "Valheim: failed to post feed message");
        }
    }
}
