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
/// Server-protection features #7 (Honeypot trap channel) and #8 (behavioral
/// cross-channel spam trap). Both detectors live here because they share one
/// in-memory rolling message index and the same ban / purge / audit / alert
/// machinery; only the *trigger* differs.
///
/// ── What it does ──
/// One channel (<see cref="BotConfig.HoneypotChannelId"/>) is set up so
/// every member can see it and a pinned/auto-posted warning embed tells
/// humans NOT to post in it. Real members comply; spam bots and
/// compromised "self-bot" accounts iterate over the channel list and
/// blast their payload everywhere, so any user-authored message in this
/// channel is almost certainly an automated account. We act on it.
///
/// ── The behavioral spam trap (feature #8) ──
/// A compromised account often blasts most channels but never happens to hit
/// the one honeypot channel. The honeypot alone is therefore probabilistic.
/// The spam trap closes that gap by reading the SAME rolling index: if one
/// non-bot account posts in <see cref="BotConfig.SpamTrapChannelThreshold"/>+
/// DISTINCT channels within <see cref="BotConfig.SpamTrapWindowSeconds"/>
/// seconds — something no human does by hand — it is treated exactly like a
/// honeypot hit (ban + server-wide purge, or alert-only, per
/// <see cref="BotConfig.SpamTrapMode"/>). It needs no trap channel and fires
/// within the first few spam messages. Its actions are stamped Feature
/// "SpamTrap" (vs "Honeypot") and do NOT move the honeypot embed counter.
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
/// trap). The ONLY human exemption is the server owner, who Discord does not
/// permit a bot to ban. Everyone else — including HQ and Administrators — is
/// actioned, because the whole point of the trap is to catch compromised
/// officer/admin accounts. (The bot can still only ban members below it in
/// the role hierarchy, so its role must sit above all HQ roles; see
/// EnforceAsync.) A hijacked owner is handled via the recovery runbook, not
/// here.
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
            var honeypotMode = (_config.HoneypotMode ?? ModeOff).Trim();
            var spamMode     = (_config.SpamTrapMode ?? ModeOff).Trim();
            var honeypotOn = !string.Equals(honeypotMode, ModeOff, StringComparison.OrdinalIgnoreCase);
            var spamOn     = !string.Equals(spamMode,     ModeOff, StringComparison.OrdinalIgnoreCase);

            // Nothing to do if both detectors are off.
            if (!honeypotOn && !spamOn) return;

            // Only index/act on real user messages in guild channels.
            if (message is not SocketUserMessage userMsg) return;
            if (userMsg.Author.Id == _client.CurrentUser.Id) return;
            if (userMsg.Author.IsWebhook) return;
            if (userMsg.Author.IsBot) return;
            if (userMsg.Channel is not SocketGuildChannel guildChannel) return;

            // Feed the rolling index BEFORE the traps so the offending message
            // is itself eligible for the purge and counts toward the velocity
            // window.
            RecordMessage(userMsg.Author.Id, guildChannel.Id, userMsg.Id);

            // Straggler sweep: a just-banned account that slipped a message
            // through gets it deleted immediately.
            if (_recentlyBanned.TryGetValue(userMsg.Author.Id, out var bannedAt)
                && DateTime.UtcNow - bannedAt < StragglerWindow)
            {
                await TryDeleteAsync(guildChannel.Id, userMsg.Id);
                return;
            }

            var member = userMsg.Author as SocketGuildUser
                         ?? guildChannel.Guild.GetUser(userMsg.Author.Id);

            // ── Behavioral cross-channel spam trap (feature #8) ──
            // Checked first and on EVERY channel: a compromised account that
            // blasts many channels is caught here without needing to hit the
            // honeypot channel at all. If it trips (or is deduped from a prior
            // trip) we're done — no need to also run the honeypot check.
            if (spamOn && IsCrossChannelSpam(userMsg.Author.Id, out var distinctChannels))
            {
                var windowSecs = Math.Max(1, _config.SpamTrapWindowSeconds);
                var threshold  = Math.Max(2, _config.SpamTrapChannelThreshold);
                var ctx = new TrapHit(
                    Feature:    "SpamTrap",
                    AlertTitle: "🚨 Cross-Channel Spam Detected",
                    Reason:     $"Cross-channel spam: posted in {distinctChannels} distinct channels within {windowSecs}s.",
                    BumpHoneypotCounter: false,
                    ChannelId:  guildChannel.Id,
                    ExtraDetail: $"Posted in {distinctChannels} distinct channels within {windowSecs}s (threshold {threshold}).",
                    AlertOnOwner: true);
                await HandleTripAsync(userMsg, member, guildChannel.Guild, spamMode, ctx);
                return;
            }

            // ── Honeypot channel trap (feature #7) ──
            if (!honeypotOn) return;
            if (guildChannel.Id != _config.HoneypotChannelId) return;

            var honeypotCtx = new TrapHit(
                Feature:    "Honeypot",
                AlertTitle: "🍯 Honeypot Triggered",
                Reason:     $"Honeypot: posted in trap channel #{_config.HoneypotChannelId}.",
                BumpHoneypotCounter: true,
                ChannelId:  _config.HoneypotChannelId,
                ExtraDetail: null,
                AlertOnOwner: false);
            await HandleTripAsync(userMsg, member, guildChannel.Guild, honeypotMode, honeypotCtx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honeypot handler crashed for message {MessageId}.", message.Id);
        }
    }

    /// <summary>
    /// Shared trip handler for both detectors. Applies the owner exemption,
    /// burst dedupe, and then either enforces (ban + purge) or alert-only,
    /// per <paramref name="mode"/>. The <paramref name="ctx"/> carries the
    /// trap-specific labelling so audit rows and alerts are attributed to the
    /// right feature.
    /// </summary>
    private async Task HandleTripAsync(
        SocketUserMessage trigger, SocketGuildUser? member, SocketGuild guild, string mode, TrapHit ctx)
    {
        var author = trigger.Author;

        // The server owner can never be banned by a bot. For the honeypot a
        // curious owner poking the channel is just noise (log + drop). For the
        // spam trap, an owner blasting many channels is a strong sign the owner
        // account is compromised, so we alert loudly even though we can't ban.
        if (author.Id == guild.OwnerId)
        {
            _logger.LogWarning(
                "{Feature}: server owner {User} ({Id}) tripped the trap; a bot cannot ban the server " +
                "owner. {Note}",
                ctx.Feature, author.Username, author.Id,
                ctx.AlertOnOwner
                    ? "Alerting — owner account may be compromised; see the server-recovery runbook."
                    : "No action taken.");

            if (ctx.AlertOnOwner)
            {
                await WriteAuditAsync(guild, author, member, ctx,
                    action: "BanSkippedOwner", purged: 0, channelsTouched: 0,
                    error: "server owner — cannot be banned by a bot",
                    postedContent: FormatPostedContentForAudit(trigger));
                await PostAlertAsync(guild, author, member, ctx,
                    enforced: true, banned: false, purged: 0,
                    error: "server owner — cannot be banned by a bot",
                    postedContent: FormatPostedContent(trigger));
            }
            return;
        }

        // Burst dedupe: one account's flood is actioned once, not per message.
        // Shared across both detectors so a spam-trip and a honeypot-hit from
        // the same account inside the cooldown don't double-fire.
        var now = DateTime.UtcNow;
        if (_recentlyActioned.TryGetValue(author.Id, out var last)
            && now - last < ActionCooldown)
            return;
        _recentlyActioned[author.Id] = now;

        _logger.LogWarning(
            "{Feature} tripped by {User} ({Id}) (mode {Mode}). {Extra}",
            ctx.Feature, author.Username, author.Id, mode, ctx.ExtraDetail ?? string.Empty);

        if (string.Equals(mode, ModeEnforce, StringComparison.OrdinalIgnoreCase))
        {
            await EnforceAsync(trigger, member, guild, ctx);
        }
        else
        {
            // AlertOnly: observe only. No delete, no ban, no counter move.
            await WriteAuditAsync(guild, author, member, ctx,
                action: "Alerted", purged: 0, channelsTouched: 0, error: null,
                postedContent: FormatPostedContentForAudit(trigger));
            await PostAlertAsync(guild, author, member, ctx,
                enforced: false, banned: false, purged: 0, error: null,
                postedContent: FormatPostedContent(trigger));
        }
    }

    /// <summary>
    /// Behavioral detector: returns true when <paramref name="userId"/> has
    /// posted in <see cref="BotConfig.SpamTrapChannelThreshold"/>+ DISTINCT
    /// channels within the last <see cref="BotConfig.SpamTrapWindowSeconds"/>
    /// seconds, read straight off the rolling index. Cheap: bounded to the
    /// per-user list (≤ <see cref="MaxTrackedPerUser"/>) and short-circuits when
    /// the list can't possibly meet the threshold.
    /// </summary>
    private bool IsCrossChannelSpam(ulong userId, out int distinctChannels)
    {
        distinctChannels = 0;

        var threshold  = Math.Max(2, _config.SpamTrapChannelThreshold);
        var windowSecs = Math.Max(1, _config.SpamTrapWindowSeconds);

        if (!_recent.TryGetValue(userId, out var list)) return false;

        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(windowSecs);
        lock (list)
        {
            // Total tracked < threshold ⇒ distinct-in-window can't reach it.
            if (list.Count < threshold) return false;
            distinctChannels = list
                .Where(m => m.PostedUtc >= cutoff)
                .Select(m => m.ChannelId)
                .Distinct()
                .Count();
        }

        return distinctChannels >= threshold;
    }

    // ── Enforce: ban + cross-channel purge + counter + embed refresh ───

    private async Task EnforceAsync(SocketUserMessage trigger, SocketGuildUser? member, SocketGuild guild, TrapHit ctx)
    {
        var userId   = trigger.Author.Id;
        var username = trigger.Author.Username;
        var reason   = ctx.Reason;

        // ── Role-hierarchy guard (mirrors AccountAgeGateHandler) ──
        if (member is not null)
        {
            var botTop    = guild.CurrentUser.Roles.Max(r => r.Position);
            var memberTop = member.Roles.Any() ? member.Roles.Max(r => r.Position) : 0;
            if (memberTop >= botTop)
            {
                _logger.LogWarning(
                    "{Feature} cannot ban {User} ({Id}) — member's top role is at/above the bot's. " +
                    "Falling back to alert only.", ctx.Feature, username, userId);
                await WriteAuditAsync(guild, trigger.Author, member, ctx,
                    action: "BanSkippedHierarchy", purged: 0, channelsTouched: 0, error: null,
                    postedContent: FormatPostedContentForAudit(trigger));
                await PostAlertAsync(guild, trigger.Author, member, ctx,
                    enforced: true, banned: false, purged: 0, error: "bot role too low to ban",
                    postedContent: FormatPostedContent(trigger));
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
            _logger.LogWarning("{Feature}: banned {User} ({Id}); purged {Purged} message(s) across {Channels} channel(s).",
                ctx.Feature, username, userId, purged, channelsTouched);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // A ban applies even to a departed user, so this is unusual; treat as success.
            banned = true;
            _logger.LogInformation("{Feature}: {User} ({Id}) already gone; ban applied anyway.", ctx.Feature, username, userId);
        }
        catch (Exception ex)
        {
            banError = ex.Message;
            _logger.LogError(ex, "{Feature}: failed to ban {User} ({Id}).", ctx.Feature, username, userId);
        }

        // ── Counter + audit + alert + embed refresh ──
        // Only the honeypot maintains the visible "bots caught" embed counter;
        // a spam-trap ban is recorded in the audit log but does not move it.
        if (banned && ctx.BumpHoneypotCounter)
            await IncrementCounterAndRefreshEmbedAsync(guild);

        await WriteAuditAsync(guild, trigger.Author, member, ctx,
            action: banned ? "Banned" : "BanFailed",
            purged: purged, channelsTouched: channelsTouched, error: banError,
            postedContent: FormatPostedContentForAudit(trigger));

        await PostAlertAsync(guild, trigger.Author, member, ctx,
            enforced: true, banned: banned, purged: purged, error: banError,
            postedContent: FormatPostedContent(trigger));
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
        SocketGuild guild, SocketUser author, SocketGuildUser? member, TrapHit ctx,
        string action, int purged, int channelsTouched, string? error, string? postedContent = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var details = new StringBuilder();
            details.Append($"AccountAgeDays={(DateTime.UtcNow - author.CreatedAt.UtcDateTime).TotalDays:F1}. ");
            if (!string.IsNullOrWhiteSpace(ctx.ExtraDetail))
                details.Append($"{ctx.ExtraDetail} ");
            if (action is "Banned" or "BanFailed")
                details.Append($"PurgedMessages={purged}. ChannelsTouched={channelsTouched}. ");
            details.Append($"PurgeWindowMinutes={_config.HoneypotPurgeWindowMinutes}.");
            if (!string.IsNullOrWhiteSpace(postedContent))
                details.Append($" Posted=\"{postedContent}\"");

            db.SecurityAuditRecords.Add(new SecurityAuditRecord
            {
                GuildId      = guild.Id,
                Feature      = ctx.Feature,
                Action       = action,
                UserId       = author.Id,
                Username     = author.Username,
                DisplayName  = member?.DisplayName ?? author.Username,
                ChannelId    = ctx.ChannelId,
                Details      = details.ToString(),
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = error,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Feature}: failed to write SecurityAuditRecord ({Action}).", ctx.Feature, action);
        }
    }

    private async Task PostAlertAsync(
        SocketGuild guild, SocketUser author, SocketGuildUser? member, TrapHit ctx,
        bool enforced, bool banned, int purged, string? error, string? postedContent = null)
    {
        var alerts = ResolveSecurityAlertsChannel(guild);
        if (alerts is null)
        {
            _logger.LogWarning(
                "{Feature}: no resolvable alerts channel (SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                ctx.Feature, _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var outcome = !enforced
            ? "Observed (AlertOnly) — no action taken"
            : banned
                ? $"Banned + purged {purged} message(s)"
                : error is not null && error.Contains("owner", StringComparison.OrdinalIgnoreCase)
                    ? "NOT actioned — server owner cannot be banned by a bot (possible compromise; see recovery runbook)"
                    : $"Ban FAILED ({error ?? "see logs"})";

        var ageDays = (DateTime.UtcNow - author.CreatedAt.UtcDateTime).TotalDays;

        var builder = new EmbedBuilder()
            .WithColor(banned || !enforced ? Color.Orange : Color.Red)
            .WithTitle(ctx.AlertTitle)
            .AddField("Account",
                $"{author.Mention} (`{author.Username}` / `{author.Id}`)", inline: false)
            .AddField("Outcome", outcome, inline: false);

        if (!string.IsNullOrWhiteSpace(ctx.ExtraDetail))
            builder.AddField("Detected", ctx.ExtraDetail, inline: false);

        if (!string.IsNullOrWhiteSpace(postedContent))
            builder.AddField("Posted", postedContent, inline: false);

        var embed = builder
            .AddField("Account age", $"{ageDays:F1} days", inline: true)
            .AddField("In server",
                member?.JoinedAt is { } j ? $"{(DateTime.UtcNow - j.UtcDateTime).TotalDays:F1} days" : "left / unknown",
                inline: true)
            .WithFooter($"ClanGuard • {ctx.Feature}")
            .WithCurrentTimestamp()
            .Build();

        // AllowedMentions.None so a payload full of @everyone / role pings that
        // we're quoting back can never re-ping the mod channel.
        try { await alerts.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Feature}: failed to post alert to #{Channel}.", ctx.Feature, alerts.Name);
        }
    }

    /// <summary>
    /// Builds a safe, log-friendly rendering of the message that tripped the
    /// trap for the "Posted" field of the mod-log embed: mentions neutralised
    /// so quoting it back can't ping anyone, code-fenced, truncated to fit an
    /// embed field (1024 char cap), with a fallback to attachment/sticker info
    /// when the message carries no text (image/link-only spam is common).
    /// </summary>
    private static string FormatPostedContent(SocketUserMessage msg)
    {
        const int MaxTextLen = 900; // leave room for the code fence + attachments.

        var sb = new StringBuilder();

        var text = msg.Content ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Neutralise @everyone / @here (belt-and-braces alongside AllowedMentions.None).
            text = text.Replace("@everyone", "@​everyone")
                       .Replace("@here", "@​here");
            if (text.Length > MaxTextLen)
                text = text[..MaxTextLen] + "…";
            // Break any backticks so the content can't escape our own code fence.
            text = text.Replace("```", "ˋˋˋ");
            sb.Append("```\n").Append(text).Append("\n```");
        }

        // Attachments / stickers are often the actual payload.
        var extras = new List<string>();
        if (msg.Attachments.Count > 0)
            extras.AddRange(msg.Attachments.Select(a => $"📎 {a.Filename} — {a.Url}"));
        if (msg.Stickers.Count > 0)
            extras.AddRange(msg.Stickers.Select(s => $"🏷️ sticker: {s.Name}"));
        if (extras.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Join("\n", extras));
        }

        if (sb.Length == 0)
            return "*(no text — embed/attachment-only message)*";

        var result = sb.ToString();
        return result.Length > 1024 ? result[..1021] + "…" : result;
    }

    /// <summary>
    /// Plain single-string rendering of the triggering message for the
    /// SecurityAuditRecord.Details field (no code fences — this is stored, not
    /// re-rendered in Discord). Newlines collapsed, truncated, attachment info
    /// appended.
    /// </summary>
    private static string FormatPostedContentForAudit(SocketUserMessage msg)
    {
        const int MaxLen = 500;

        var parts = new List<string>();

        var text = (msg.Content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(text);

        if (msg.Attachments.Count > 0)
            parts.AddRange(msg.Attachments.Select(a => $"[attachment: {a.Filename} {a.Url}]"));
        if (msg.Stickers.Count > 0)
            parts.AddRange(msg.Stickers.Select(s => $"[sticker: {s.Name}]"));

        if (parts.Count == 0)
            return "(no text — embed/attachment-only message)";

        var result = string.Join(" ", parts);
        return result.Length > MaxLen ? result[..MaxLen] + "…" : result;
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

    /// <summary>
    /// Trap-specific labelling threaded through the shared enforce / audit /
    /// alert path so the honeypot (#7) and the behavioral spam trap (#8) can
    /// reuse one code path while still being attributed correctly.
    /// </summary>
    /// <param name="Feature">SecurityAuditRecord.Feature stamp ("Honeypot" / "SpamTrap").</param>
    /// <param name="AlertTitle">Title for the #alerts embed.</param>
    /// <param name="Reason">Ban / audit reason string.</param>
    /// <param name="BumpHoneypotCounter">Whether a ban increments the honeypot embed's "bots caught" counter.</param>
    /// <param name="ChannelId">Channel to stamp on the audit row (the trap channel, or the triggering channel).</param>
    /// <param name="ExtraDetail">Optional extra context for the audit row + alert (e.g. the spam channel count).</param>
    /// <param name="AlertOnOwner">Whether to alert when the server owner trips the trap (true for the spam trap — a strong compromise signal).</param>
    private sealed record TrapHit(
        string Feature,
        string AlertTitle,
        string Reason,
        bool BumpHoneypotCounter,
        ulong? ChannelId,
        string? ExtraDetail,
        bool AlertOnOwner);
}
