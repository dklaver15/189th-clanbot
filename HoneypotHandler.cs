using System.Collections.Concurrent;
using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #7: Honeypot trap channel.
///
/// ── What it does ──
/// One channel (<see cref="BotConfig.HoneypotChannelId"/>) is set up so
/// every member can see it and a pinned/auto-posted warning embed tells
/// humans NOT to post in it. Real members comply; spam bots and
/// compromised "self-bot" accounts iterate over the channel list and
/// blast their payload everywhere, so any user-authored message in this
/// channel is almost certainly an automated account. We act on it.
///
/// ── Modes (<see cref="BotConfig.HoneypotMode"/>) ──
///   "Off"        — feature disabled. No index, no trap, no warning post.
///   "AlertOnly"  — DEFAULT. On a hit: write a SecurityAuditRecord and
///                  post to #alerts. The offending message is NOT
///                  deleted, the account is NOT banned, the counter does
///                  NOT move. Deploy here first, watch the alerts channel
///                  for a clean signal, THEN flip to Enforce.
///   "Enforce"    — On a hit: ban the account, delete its message in the
///                  honeypot channel AND every other message it posted in
///                  the last <see cref="BotConfig.HoneypotPurgeWindowMinutes"/>
///                  minutes across the whole server, write the audit row,
///                  bump the ban counter, and edit the warning embed to
///                  show the new total.
///
/// ── The cross-channel purge ──
/// Discord has no "list a user's messages server-wide" API — you can only
/// page per channel, and scanning 100+ channels on every hit would be slow
/// and rate-limit-heavy. Instead we keep a lightweight in-memory rolling
/// index of <c>userId → (channelId, messageId, postedUtc)</c> for the last
/// <see cref="BotConfig.HoneypotPurgeWindowMinutes"/> minutes, fed from the
/// same MessageReceived stream. On a hit we look up exactly that user's
/// recent messages and bulk-delete them, touching only the 1–3 channels
/// they actually posted in. The index stores message *metadata only* — no
/// content — so it carries no privacy weight and needs no extra intent.
///
/// Caveat: the index is in-memory, so a bot restart forgets the prior
/// window. Acceptable for a 15-minute horizon; the alternative (a DB write
/// per message server-wide) is a lot of churn for a rare event. The
/// honeypot message that triggered the ban is itself in the index, so it
/// is purged along with the rest.
///
/// A just-banned account generally can't post again, but to cover the
/// in-flight-message race we also keep banned IDs in a short
/// <see cref="_recentlyBanned"/> set for ~60s and delete any straggler
/// that lands while it's still there.
///
/// ── Exemptions ──
/// The bot itself, other bots, webhooks, and system messages are never
/// acted on (this is also why the auto-posted warning embed can't trip the
/// trap). Server Administrators and members at
/// <see cref="BotConfig.HoneypotExemptMinRank"/> or above are exempt — a
/// curious officer poking the channel should never get banned. Exempt
/// members are skipped entirely (no delete, no alert), same as the other
/// security features.
///
/// ── Warning embed + ban counter ──
/// On Ready we ensure the warning embed exists in the honeypot channel:
/// if <see cref="BotState.HoneypotWarningMessageId"/> points at a live
/// message we edit it (so the ban count refreshes after a restart); if it
/// is null or the message was deleted, we post a fresh one and store the
/// new ID. The embed's thumbnail defaults to the guild icon (the 189th
/// logo) and its banner image is whatever URL is set in
/// <see cref="BotConfig.HoneypotImageUrl"/> (empty = no banner). The ban
/// total lives on <see cref="BotState.HoneypotBanCount"/> and the embed is
/// re-edited on each Enforce ban.
/// </summary>
public sealed class HoneypotHandler
{
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";
    public const string ModeEnforce   = "Enforce";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<HoneypotHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Rolling per-user index of recent messages for the cross-channel
    /// purge. Pruned to the purge window by <see cref="SweepIndex"/>.
    /// Each user's list is locked on itself for mutation/read.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, List<TrackedMessage>> _recent = new();

    /// <summary>Accounts banned in the last ~60s, to catch in-flight stragglers.</summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _recentlyBanned = new();

    /// <summary>Dedupe so a burst from one account isn't re-actioned per message.</summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _recentlyActioned = new();

    /// <summary>Serializes counter increment + warning-embed edit during bursts.</summary>
    private readonly SemaphoreSlim _embedLock = new(1, 1);

    private Timer? _sweepTimer;
    private int _warningEnsured; // Interlocked one-shot guard per process.

    /// <summary>Per-user list cap so a flood can't grow memory unbounded.</summary>
    private const int MaxTrackedPerUser = 500;

    /// <summary>Window we keep a banned ID around to nuke stragglers.</summary>
    private static readonly TimeSpan StragglerWindow = TimeSpan.FromSeconds(60);

    /// <summary>Re-action cooldown for the same account.</summary>
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(60);

    public HoneypotHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<HoneypotHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.Ready           += OnReady;

        // Sweep the rolling index every 60s. Cheap, and keeps memory bounded
        // even on a high-traffic server.
        _sweepTimer = new Timer(_ => SweepIndex(), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    // ── Ready: ensure the warning embed exists ─────────────────────────

    private Task OnReady()
    {
        // Ready can fire on every reconnect; only run the ensure once per
        // process. Restart reconciliation is handled by the stored message ID.
        if (Interlocked.Exchange(ref _warningEnsured, 1) == 1)
            return Task.CompletedTask;

        _ = EnsureWarningMessageAsync();
        return Task.CompletedTask;
    }

    private async Task EnsureWarningMessageAsync()
    {
        try
        {
            var mode = (_config.HoneypotMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
                return;

            if (_client.GetChannel(_config.HoneypotChannelId) is not SocketTextChannel channel)
            {
                _logger.LogWarning(
                    "Honeypot: configured channel {ChannelId} is not a resolvable text channel; " +
                    "warning embed not posted.", _config.HoneypotChannelId);
                return;
            }

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db);

            var embed = BuildWarningEmbed(channel.Guild, state.HoneypotBanCount);

            // Try to edit the existing message first (refreshes the counter
            // after a restart and avoids spawning a duplicate embed).
            if (state.HoneypotWarningMessageId is { } existingId)
            {
                try
                {
                    if (await channel.GetMessageAsync(existingId) is IUserMessage existing)
                    {
                        await existing.ModifyAsync(m => m.Embed = embed);
                        _logger.LogInformation(
                            "Honeypot: refreshed existing warning embed {MessageId} (ban count {Count}).",
                            existingId, state.HoneypotBanCount);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Honeypot: stored warning message {MessageId} could not be edited; " +
                        "posting a fresh one.", existingId);
                }
            }

            var posted = await channel.SendMessageAsync(embed: embed);
            try { await posted.PinAsync(); } catch { /* pin is best-effort */ }

            state.HoneypotWarningMessageId = posted.Id;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Honeypot: posted warning embed as message {MessageId} in #{Channel}.",
                posted.Id, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honeypot: failed to ensure warning embed.");
        }
    }

    // ── MessageReceived: index everything, trap the honeypot ───────────

    private Task OnMessageReceived(SocketMessage message)
    {
        _ = InspectAsync(message);
        return Task.CompletedTask;
    }

    private async Task InspectAsync(SocketMessage message)
    {
        try
        {
            var mode = (_config.HoneypotMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
                return;

            // Only index/act on real user messages in guild channels.
            if (message is not SocketUserMessage userMsg) return;
            if (userMsg.Author.Id == _client.CurrentUser.Id) return;
            if (userMsg.Author.IsWebhook) return;
            if (userMsg.Author.IsBot) return;
            if (userMsg.Channel is not SocketGuildChannel guildChannel) return;

            // Feed the rolling index BEFORE the trap so the offending message
            // is itself eligible for the purge.
            RecordMessage(userMsg.Author.Id, guildChannel.Id, userMsg.Id);

            // Straggler sweep: a just-banned account that slipped a message
            // through gets it deleted immediately.
            if (_recentlyBanned.TryGetValue(userMsg.Author.Id, out var bannedAt)
                && DateTime.UtcNow - bannedAt < StragglerWindow)
            {
                await TryDeleteAsync(guildChannel.Id, userMsg.Id);
                return;
            }

            // Not the trap channel → indexing only, nothing else to do.
            if (guildChannel.Id != _config.HoneypotChannelId) return;

            var member = userMsg.Author as SocketGuildUser
                         ?? guildChannel.Guild.GetUser(userMsg.Author.Id);

            // Exempt staff/admins are never actioned.
            if (member is not null && IsRankExempt(member))
            {
                _logger.LogInformation(
                    "Honeypot: exempt member {User} ({Id}) posted in the trap; ignoring.",
                    member.Username, member.Id);
                return;
            }

            // Burst dedupe.
            var now = DateTime.UtcNow;
            if (_recentlyActioned.TryGetValue(userMsg.Author.Id, out var last)
                && now - last < ActionCooldown)
                return;
            _recentlyActioned[userMsg.Author.Id] = now;

            _logger.LogWarning(
                "Honeypot tripped by {User} ({Id}) in #{Channel} (mode {Mode}).",
                userMsg.Author.Username, userMsg.Author.Id, guildChannel.Name, mode);

            if (string.Equals(mode, ModeEnforce, StringComparison.OrdinalIgnoreCase))
            {
                await EnforceAsync(userMsg, member, guildChannel.Guild);
            }
            else
            {
                // AlertOnly: observe only. No delete, no ban, no counter move.
                await WriteAuditAsync(guildChannel.Guild, userMsg.Author, member,
                    action: "Alerted", purged: 0, channelsTouched: 0, error: null);
                await PostAlertAsync(guildChannel.Guild, userMsg.Author, member,
                    enforced: false, banned: false, purged: 0, error: null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honeypot handler crashed for message {MessageId}.", message.Id);
        }
    }

    // ── Enforce: ban + cross-channel purge + counter + embed refresh ───

    private async Task EnforceAsync(SocketUserMessage trigger, SocketGuildUser? member, SocketGuild guild)
    {
        var userId   = trigger.Author.Id;
        var username = trigger.Author.Username;
        var reason   = $"Honeypot: posted in trap channel #{_config.HoneypotChannelId}.";

        // ── Role-hierarchy guard (mirrors AccountAgeGateHandler) ──
        if (member is not null)
        {
            var botTop    = guild.CurrentUser.Roles.Max(r => r.Position);
            var memberTop = member.Roles.Any() ? member.Roles.Max(r => r.Position) : 0;
            if (memberTop >= botTop)
            {
                _logger.LogWarning(
                    "Honeypot cannot ban {User} ({Id}) — member's top role is at/above the bot's. " +
                    "Falling back to alert only.", username, userId);
                await WriteAuditAsync(guild, trigger.Author, member,
                    action: "BanSkippedHierarchy", purged: 0, channelsTouched: 0, error: null);
                await PostAlertAsync(guild, trigger.Author, member,
                    enforced: true, banned: false, purged: 0, error: "bot role too low to ban");
                return;
            }
        }

        // Mark first so any straggler in the next 60s is auto-deleted.
        _recentlyBanned[userId] = DateTime.UtcNow;

        // ── Purge recent messages everywhere (window) ──
        var (purged, channelsTouched) = await PurgeRecentAsync(userId);

        // ── Ban (by ID, so it sticks even if they already left) ──
        var banned = false;
        string? banError = null;
        try
        {
            await guild.AddBanAsync(
                userId,
                pruneDays: 0, // the windowed purge above does the message scrubbing
                reason: reason,
                options: new RequestOptions { AuditLogReason = reason });
            banned = true;
            _logger.LogWarning("Honeypot: banned {User} ({Id}); purged {Purged} message(s) across {Channels} channel(s).",
                username, userId, purged, channelsTouched);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // A ban applies even to a departed user, so this is unusual; treat as success.
            banned = true;
            _logger.LogInformation("Honeypot: {User} ({Id}) already gone; ban applied anyway.", username, userId);
        }
        catch (Exception ex)
        {
            banError = ex.Message;
            _logger.LogError(ex, "Honeypot: failed to ban {User} ({Id}).", username, userId);
        }

        // ── Counter + audit + alert + embed refresh ──
        if (banned)
            await IncrementCounterAndRefreshEmbedAsync(guild);

        await WriteAuditAsync(guild, trigger.Author, member,
            action: banned ? "Banned" : "BanFailed",
            purged: purged, channelsTouched: channelsTouched, error: banError);

        await PostAlertAsync(guild, trigger.Author, member,
            enforced: true, banned: banned, purged: purged, error: banError);
    }

    /// <summary>
    /// Deletes every message the user posted in the purge window, grouped by
    /// channel. Bulk-deletes (≥2 per channel) with an individual-delete
    /// fallback — same shape as ClearAwolListCommandHandler.
    /// </summary>
    private async Task<(int purged, int channelsTouched)> PurgeRecentAsync(ulong userId)
    {
        if (!_recent.TryGetValue(userId, out var list)) return (0, 0);

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _config.HoneypotPurgeWindowMinutes));

        List<TrackedMessage> snapshot;
        lock (list)
        {
            snapshot = list.Where(m => m.PostedUtc >= cutoff).ToList();
            list.Clear(); // consumed
        }

        var purged = 0;
        var channelsTouched = 0;

        foreach (var group in snapshot.GroupBy(m => m.ChannelId))
        {
            if (_client.GetChannel(group.Key) is not SocketTextChannel channel) continue;

            // Skip threads if configured to leave them alone.
            if (!_config.HoneypotIncludeThreadsInPurge && channel is SocketThreadChannel) continue;

            var ids = group.Select(m => m.MessageId).Distinct().ToList();
            if (ids.Count == 0) continue;
            channelsTouched++;

            try
            {
                if (ids.Count >= 2)
                    await channel.DeleteMessagesAsync(ids);
                else
                    await channel.DeleteMessageAsync(ids[0]);
                purged += ids.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Honeypot: bulk delete failed in #{Channel}; falling back to individual deletes.",
                    channel.Name);
                foreach (var id in ids)
                {
                    if (await TryDeleteAsync(group.Key, id)) purged++;
                }
            }
        }

        return (purged, channelsTouched);
    }

    private async Task IncrementCounterAndRefreshEmbedAsync(SocketGuild guild)
    {
        await _embedLock.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db);

            state.HoneypotBanCount += 1;
            await db.SaveChangesAsync();

            if (state.HoneypotWarningMessageId is { } msgId
                && _client.GetChannel(_config.HoneypotChannelId) is SocketTextChannel channel)
            {
                try
                {
                    if (await channel.GetMessageAsync(msgId) is IUserMessage existing)
                    {
                        var embed = BuildWarningEmbed(guild, state.HoneypotBanCount);
                        await existing.ModifyAsync(m => m.Embed = embed);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Honeypot: could not refresh warning embed counter (message {MessageId}).", msgId);
                }
            }
        }
        finally
        {
            _embedLock.Release();
        }
    }

    // ── Audit + alert ──────────────────────────────────────────────────

    private async Task WriteAuditAsync(
        SocketGuild guild, SocketUser author, SocketGuildUser? member,
        string action, int purged, int channelsTouched, string? error)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var details = new StringBuilder();
            details.Append($"AccountAgeDays={(DateTime.UtcNow - author.CreatedAt.UtcDateTime).TotalDays:F1}. ");
            if (action is "Banned" or "BanFailed")
                details.Append($"PurgedMessages={purged}. ChannelsTouched={channelsTouched}. ");
            details.Append($"PurgeWindowMinutes={_config.HoneypotPurgeWindowMinutes}.");

            db.SecurityAuditRecords.Add(new SecurityAuditRecord
            {
                GuildId      = guild.Id,
                Feature      = "Honeypot",
                Action       = action,
                UserId       = author.Id,
                Username     = author.Username,
                DisplayName  = member?.DisplayName ?? author.Username,
                ChannelId    = _config.HoneypotChannelId,
                Details      = details.ToString(),
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = error,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honeypot: failed to write SecurityAuditRecord ({Action}).", action);
        }
    }

    private async Task PostAlertAsync(
        SocketGuild guild, SocketUser author, SocketGuildUser? member,
        bool enforced, bool banned, int purged, string? error)
    {
        var alerts = ResolveSecurityAlertsChannel(guild);
        if (alerts is null)
        {
            _logger.LogWarning(
                "Honeypot: no resolvable alerts channel (SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var outcome = !enforced
            ? "Observed (AlertOnly) — no action taken"
            : banned
                ? $"Banned + purged {purged} message(s)"
                : $"Ban FAILED ({error ?? "see logs"})";

        var ageDays = (DateTime.UtcNow - author.CreatedAt.UtcDateTime).TotalDays;

        var embed = new EmbedBuilder()
            .WithColor(banned || !enforced ? Color.Orange : Color.Red)
            .WithTitle("🍯 Honeypot Triggered")
            .AddField("Account",
                $"{author.Mention} (`{author.Username}` / `{author.Id}`)", inline: false)
            .AddField("Outcome", outcome, inline: false)
            .AddField("Account age", $"{ageDays:F1} days", inline: true)
            .AddField("In server",
                member?.JoinedAt is { } j ? $"{(DateTime.UtcNow - j.UtcDateTime).TotalDays:F1} days" : "left / unknown",
                inline: true)
            .WithFooter("ClanGuard • Honeypot")
            .WithCurrentTimestamp()
            .Build();

        try { await alerts.SendMessageAsync(embed: embed); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honeypot: failed to post alert to #{Channel}.", alerts.Name);
        }
    }

    // ── Warning embed ──────────────────────────────────────────────────

    private Embed BuildWarningEmbed(SocketGuild guild, int banCount)
    {
        var builder = new EmbedBuilder()
            .WithColor(Color.Red)
            .WithTitle("⛔ HONEYPOT")
            .WithDescription(
                "**Important notice:** this channel is a trap for spam bots.\n\n" +
                "**__DO NOT SEND A MESSAGE IN THIS CHANNEL.__**\n\n" +
                "Posting here is treated as automated/bot activity and will get you " +
                "removed from the server automatically. If you have a question about " +
                "any other channel, ask a member of leadership.")
            .AddField("🔨 Bots caught so far", banCount.ToString(), inline: true)
            .WithFooter("189th • ClanGuard");

        // Thumbnail: explicit override, else the guild icon (the 189th logo).
        var thumb = !string.IsNullOrWhiteSpace(_config.HoneypotThumbnailUrl)
            ? _config.HoneypotThumbnailUrl
            : guild.IconUrl;
        if (!string.IsNullOrWhiteSpace(thumb))
            builder.WithThumbnailUrl(thumb);

        // Banner image: optional, supplied via config (use your own, not HLL's).
        if (!string.IsNullOrWhiteSpace(_config.HoneypotImageUrl))
            builder.WithImageUrl(_config.HoneypotImageUrl);

        return builder.Build();
    }

    // ── Rolling index helpers ──────────────────────────────────────────

    private void RecordMessage(ulong userId, ulong channelId, ulong messageId)
    {
        var list = _recent.GetOrAdd(userId, _ => new List<TrackedMessage>());
        lock (list)
        {
            list.Add(new TrackedMessage(channelId, messageId, DateTime.UtcNow));
            // Keep the most-recent N to bound memory under a flood.
            if (list.Count > MaxTrackedPerUser)
                list.RemoveRange(0, list.Count - MaxTrackedPerUser);
        }
    }

    private void SweepIndex()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _config.HoneypotPurgeWindowMinutes));

            foreach (var kvp in _recent)
            {
                var list = kvp.Value;
                lock (list)
                {
                    list.RemoveAll(m => m.PostedUtc < cutoff);
                }
                if (list.Count == 0)
                    _recent.TryRemove(kvp.Key, out _);
            }

            var banCutoff = DateTime.UtcNow - StragglerWindow;
            foreach (var kvp in _recentlyBanned)
                if (kvp.Value < banCutoff) _recentlyBanned.TryRemove(kvp.Key, out _);

            var actionCutoff = DateTime.UtcNow - ActionCooldown;
            foreach (var kvp in _recentlyActioned)
                if (kvp.Value < actionCutoff) _recentlyActioned.TryRemove(kvp.Key, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Honeypot: index sweep failed.");
        }
    }

    private async Task<bool> TryDeleteAsync(ulong channelId, ulong messageId)
    {
        try
        {
            if (_client.GetChannel(channelId) is SocketTextChannel channel)
            {
                await channel.DeleteMessageAsync(messageId);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Honeypot: could not delete message {MessageId} in {ChannelId}.",
                messageId, channelId);
        }
        return false;
    }

    // ── Shared helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Rank exemption: server Administrator, or a member at
    /// <see cref="BotConfig.HoneypotExemptMinRank"/> or above. Same role-index
    /// pattern as TokenGrabberScannerHandler / InviteLinkFilterHandler.
    /// </summary>
    private bool IsRankExempt(SocketGuildUser member)
    {
        if (member.GuildPermissions.Administrator) return true;

        var minRank = _config.HoneypotExemptMinRank;
        if (string.IsNullOrWhiteSpace(minRank)) return false;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return member.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    private SocketTextChannel? ResolveSecurityAlertsChannel(SocketGuild guild)
    {
        if (_config.SecurityAlertsChannelId != 0)
        {
            var byId = guild.GetTextChannel(_config.SecurityAlertsChannelId);
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(_config.HqChannelName))
        {
            return guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>Singleton BotState load — same pattern AutoPromotionService uses.</summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db)
    {
        var state = await db.BotStates.FirstOrDefaultAsync();
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync();
        }
        return state;
    }

    private readonly record struct TrackedMessage(ulong ChannelId, ulong MessageId, DateTime PostedUtc);
}
