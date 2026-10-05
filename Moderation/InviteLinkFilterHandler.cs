using System.Text.RegularExpressions;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #2: Invite-Link Filter.
///
/// ── What it does ──
/// Scans every user-authored message (and every edit) for Discord invite
/// links. Codes that don't belong to THIS guild are treated as
/// cross-promotion / scam / poaching attempts and dealt with according
/// to the configured mode.
///
/// ── Modes ──
///   "Off"        — Feature disabled.
///   "AlertOnly"  — Post to security-alerts channel and write an audit
///                  row, but DO NOT delete the message or DM the user.
///                  Useful for tuning the rank-exemption floor before
///                  flipping to Enforce.
///   "Enforce"    — Delete the offending message, best-effort DM the
///                  poster explaining why, post to security-alerts, and
///                  write an audit row. Default — the user's "enforced
///                  for everyone below MAJ" intent.
///
/// ── Rank exemption ──
/// Members at <see cref="BotConfig.InviteLinkFilterExemptMinRank"/> or
/// above (default MAJ) are exempt — they can legitimately share
/// invites to partnered servers, sister clans, or events without
/// tripping the filter. Server Administrators are always exempt
/// (matches every other rank-gated handler in the codebase).
///
/// Bots and webhooks are always exempt — Apollo posts, bot
/// announcements, and integrations don't need this gate.
///
/// ── "External" definition ──
/// An invite is "ours" if its code matches:
///   1. Any code in the InviteCacheService snapshot (regular invites,
///      kept fresh by InviteAttributionService), OR
///   2. The guild's vanity URL slug (queried via GetVanityInviteAsync).
/// Everything else is "external" — including invites that USED to be
/// ours but were deleted, since by definition they no longer point to
/// our server.
///
/// ── Fail-open when the cache isn't ready ──
/// If <see cref="InviteCacheService.IsHydrated"/> is false (bot just
/// started, cache hasn't been populated yet), we skip enforcement and
/// log a warning rather than risk false-positive-kicking our own
/// invites during the startup window. Same defensive pattern as
/// InviteAttributionService's "Unattributed" fallback. The cache
/// typically hydrates within a few seconds of Ready, so this only
/// affects the very first messages after a restart.
///
/// ── Edits ──
/// We subscribe to MessageUpdated too — otherwise users could post a
/// benign message, wait, then edit in an invite link to bypass the
/// filter. The handler is idempotent on the same message ID: if the
/// edit re-introduces an invite link that the original also had, we'd
/// double-alert. In practice the original would have already been
/// deleted in Enforce mode, so this is only a theoretical concern in
/// AlertOnly mode — and even there, duplicates in the audit log are
/// preferable to silent bypasses.
///
/// ── Coexistence with other MessageReceived subscribers ──
/// ActivityTrackingHandler counts message events. It runs concurrently
/// with this handler. The message-count write happens whether or not
/// we delete — Discord delivered the message, the user "communicated."
/// We don't undo the activity credit when we delete; that's a feature,
/// not a bug.
///
/// ── Failure handling ──
/// All work is fire-and-forget from the gateway callbacks (same pattern
/// as AccountAgeGateHandler and MemberLifecycleHandler). Internal
/// exceptions are caught and logged; a Discord API hiccup must never
/// bring down the gateway listener. Failures in delete/DM/alert/audit
/// are each isolated so one broken phase doesn't block the others.
/// </summary>
public sealed class InviteLinkFilterHandler
{
    // Mode string constants — single source of truth for the config values.
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";
    public const string ModeEnforce   = "Enforce";

    /// <summary>
    /// Matches Discord invite URLs in any of the canonical forms:
    ///   discord.gg/CODE
    ///   discord.com/invite/CODE
    ///   discordapp.com/invite/CODE
    ///   {www|ptb|canary}.discord(app)?.com/invite/CODE
    /// Captures the invite code in group 1.
    ///
    /// The leading <c>(?&lt;![\w.])</c> negative lookbehind prevents the
    /// regex from matching <c>discord.gg/foo</c> as a substring of
    /// <c>notdiscord.gg/foo</c> or <c>mydiscord.gg/foo</c>. The character
    /// immediately before our match must not be a word character or a
    /// dot — effectively anchoring against whitespace, string start, or
    /// most punctuation, but allowing the URL to appear after a comma,
    /// open-paren, etc.
    ///
    /// Code charset: alphanumeric plus hyphen (vanity URLs can have
    /// hyphens). Length 2–32 — Discord-issued codes are typically 7–10
    /// characters, vanity slugs can be longer but are capped well under
    /// 32. The cap keeps pathological inputs from producing huge match
    /// values while comfortably covering real codes.
    ///
    /// Compiled because this regex fires on every message in the guild.
    /// </summary>
    private static readonly Regex InviteRegex = new(
        @"(?<![\w.])(?:https?:\/\/)?(?:(?:www|ptb|canary)\.)?(?:discord(?:app)?\.com\/invite|discord\.gg)\/([a-z0-9-]{2,32})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IServiceProvider _services;
    private readonly InviteCacheService _inviteCache;
    private readonly ILogger<InviteLinkFilterHandler> _logger;
    private readonly BotConfig _config;

    public InviteLinkFilterHandler(
        IServiceProvider services,
        InviteCacheService inviteCache,
        ILogger<InviteLinkFilterHandler> logger,
        IOptions<BotConfig> config)
    {
        _services    = services;
        _inviteCache = inviteCache;
        _logger      = logger;
        _config      = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.MessageUpdated  += OnMessageUpdated;
    }

    private Task OnMessageReceived(SocketMessage message)
    {
        _ = InspectAsync(message);
        return Task.CompletedTask;
    }

    private Task OnMessageUpdated(
        Cacheable<IMessage, ulong> _before,
        SocketMessage after,
        ISocketMessageChannel _channel)
    {
        _ = InspectAsync(after);
        return Task.CompletedTask;
    }

    private async Task InspectAsync(SocketMessage message)
    {
        try
        {
            // ── Mode check ────────────────────────────────────────────────
            var mode = (_config.InviteLinkFilterMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // ── Filter to user messages in guild channels ─────────────────
            if (message is not SocketUserMessage userMsg) return;
            if (userMsg.Author.IsBot) return;
            if (userMsg.Author.IsWebhook) return;
            if (userMsg.Channel is not SocketGuildChannel guildChannel) return;

            // Author should already be a SocketGuildUser when
            // AlwaysDownloadUsers is true (set in Program.cs), but fall back
            // to a guild lookup if not — covers the edge case where the
            // user cache hasn't hydrated for a fresh joiner or where a
            // proxy SocketUser came through. If neither works, we can't
            // check rank, so skip — a false negative here beats false-
            // positive deleting an officer's message.
            var member = userMsg.Author as SocketGuildUser
                         ?? guildChannel.Guild.GetUser(userMsg.Author.Id);
            if (member is null) return;

            // ── Rank exemption ────────────────────────────────────────────
            if (IsRankExempt(member))
            {
                return;
            }

            // ── Extract invite codes from message content ─────────────────
            var content = userMsg.Content ?? string.Empty;
            var codes = ExtractInviteCodes(content);
            if (codes.Count == 0) return;

            // ── Filter to codes that are NOT ours ─────────────────────────
            var externalCodes = await FilterExternalCodesAsync(codes, guildChannel.Guild);
            if (externalCodes.Count == 0) return;

            // Validate mode AFTER we know we have something to act on — keeps
            // the warning log focused on cases that would have actually
            // triggered enforcement.
            var enforce = string.Equals(mode, ModeEnforce, StringComparison.OrdinalIgnoreCase);
            if (!enforce && !string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown InviteLinkFilterMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly, Enforce.",
                    mode);
            }

            _logger.LogInformation(
                "Invite link filter fired for {Username} ({UserId}) in #{Channel}: " +
                "{Count} external code(s) — {Codes} (mode: {Mode})",
                member.Username, member.Id, guildChannel.Name,
                externalCodes.Count, string.Join(", ", externalCodes), mode);

            // ── Enforce (if Enforce mode) ─────────────────────────────────
            var outcome = InviteLinkFilterOutcome.Alerted;
            string? errorMessage = null;
            if (enforce)
            {
                (outcome, errorMessage) = await TryEnforceAsync(userMsg, externalCodes, member);
            }

            // ── Write audit row ───────────────────────────────────────────
            await WriteAuditAsync(userMsg, member, guildChannel, externalCodes, outcome, errorMessage);

            // ── Post alert embed ──────────────────────────────────────────
            await PostAlertAsync(userMsg, member, guildChannel, externalCodes, mode, outcome, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Invite link filter handler crashed for message {MessageId}",
                message.Id);
        }
    }

    /// <summary>
    /// Server Admins always pass. Otherwise: holder of any role at or above
    /// the configured rank-list index is exempt. Same role-list-index
    /// pattern as every other rank-gated check in the codebase.
    /// </summary>
    private bool IsRankExempt(SocketGuildUser member)
    {
        if (member.GuildPermissions.Administrator) return true;

        var minRank = _config.InviteLinkFilterExemptMinRank;
        if (string.IsNullOrWhiteSpace(minRank)) return false;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return member.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    /// <summary>
    /// Pulls every invite code referenced in the content, deduped
    /// case-insensitively. Multiple links to the same external server in
    /// one message count as one violation.
    /// </summary>
    private static List<string> ExtractInviteCodes(string content)
    {
        var matches = InviteRegex.Matches(content);
        if (matches.Count == 0) return new List<string>();

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            if (m.Groups.Count >= 2)
            {
                codes.Add(m.Groups[1].Value);
            }
        }
        return codes.ToList();
    }

    /// <summary>
    /// Returns the subset of <paramref name="codes"/> that are NOT ours.
    /// "Ours" = present in the InviteCacheService snapshot, or matches
    /// the guild's vanity URL slug. If the cache hasn't hydrated yet,
    /// returns an empty list (fail open) and warns — better to let a
    /// few startup-window posts through than to false-positive every
    /// invite while the cache is empty.
    /// </summary>
    private async Task<List<string>> FilterExternalCodesAsync(List<string> codes, SocketGuild guild)
    {
        if (!_inviteCache.IsHydrated)
        {
            _logger.LogWarning(
                "Invite link filter skipping enforcement — InviteCacheService not yet hydrated. " +
                "Will resume once the cache populates.");
            return new List<string>();
        }

        var ourCodes = new HashSet<string>(_inviteCache.Snapshot().Keys, StringComparer.OrdinalIgnoreCase);

        // Vanity URL is held in a separate API; fetch it on-demand. Cheap
        // call and cached server-side by Discord, but we still wrap in
        // try/catch — guilds without Boost level 3 don't have one.
        string? vanitySlug = null;
        try
        {
            var vanity = await guild.GetVanityInviteAsync();
            vanitySlug = vanity?.Code;
        }
        catch
        {
            // No vanity — nothing to compare against. Fine.
        }

        var external = new List<string>();
        foreach (var code in codes)
        {
            if (ourCodes.Contains(code)) continue;
            if (vanitySlug is not null && code.Equals(vanitySlug, StringComparison.OrdinalIgnoreCase)) continue;
            external.Add(code);
        }
        return external;
    }

    /// <summary>
    /// Delete the message + best-effort DM the user. Returns the outcome
    /// for embed/audit rendering. Does NOT throw — each phase is isolated
    /// so one Discord-side failure doesn't cascade.
    /// </summary>
    private async Task<(InviteLinkFilterOutcome, string?)> TryEnforceAsync(
        SocketUserMessage userMsg,
        List<string> externalCodes,
        SocketGuildUser member)
    {
        InviteLinkFilterOutcome outcome;
        string? errorMessage = null;

        // ── Delete the offending message ──────────────────────────────────
        try
        {
            await userMsg.DeleteAsync(new RequestOptions
            {
                AuditLogReason = $"Invite link filter: external invite(s) {string.Join(",", externalCodes)}"
            });
            outcome = InviteLinkFilterOutcome.DeletedAndDmd;

            _logger.LogInformation(
                "Invite link filter deleted message {MessageId} from {Username} ({UserId})",
                userMsg.Id, member.Username, member.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Invite link filter failed to delete message {MessageId} from {Username} ({UserId})",
                userMsg.Id, member.Username, member.Id);
            errorMessage = $"Delete failed: {ex.Message}";
            // We still try to DM and alert below — the audit/alert is more
            // important than the delete in cases where Discord said no.
            outcome = InviteLinkFilterOutcome.DeleteFailed;
        }

        // ── Best-effort DM the user ───────────────────────────────────────
        // Same pattern as AccountAgeGateHandler / KickAwolsCommandHandler:
        // give the user context, but a closed DM doesn't break the flow.
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                "Hey — your message in the **189th** Discord was removed because it contained " +
                "an invite link to another server. Cross-promotion is restricted to leadership.\n\n" +
                "If you believe this was a mistake (e.g. the link pointed back to one of our own " +
                "channels), reach out to an officer and we'll sort it out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DMs disabled / closed / blocked the bot. Note that the outcome
            // string still says "DeletedAndDmd" — Discord doesn't tell us
            // reliably whether the DM landed, so we report intent rather
            // than receipt. If this becomes a forensic gap we can split
            // into separate outcomes.
        }

        return (outcome, errorMessage);
    }

    /// <summary>
    /// Writes one SecurityAuditRecord row for the violation. Wrapped in its
    /// own try/catch — a DB failure here is logged but never propagated.
    /// </summary>
    private async Task WriteAuditAsync(
        SocketUserMessage userMsg,
        SocketGuildUser member,
        SocketGuildChannel guildChannel,
        List<string> externalCodes,
        InviteLinkFilterOutcome outcome,
        string? errorMessage)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = new SecurityAuditRecord
            {
                GuildId      = guildChannel.Guild.Id,
                Feature      = "InviteLinkFilter",
                Action       = outcome.ToString(),
                UserId       = member.Id,
                Username     = member.Username,
                DisplayName  = member.DisplayName ?? member.Username,
                ChannelId    = guildChannel.Id,
                Details      = $"{externalCodes.Count} external invite code(s): " +
                               $"{string.Join(", ", externalCodes)}. " +
                               $"Channel: #{guildChannel.Name}. " +
                               $"Message: {userMsg.Id}.",
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = errorMessage,
            };

            db.SecurityAuditRecords.Add(record);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write SecurityAuditRecord for invite link filter event " +
                "(user {UserId}, message {MessageId}, outcome {Outcome})",
                member.Id, userMsg.Id, outcome);
        }
    }

    /// <summary>
    /// Posts the alert embed to the configured security-alerts channel.
    /// Resolves the same way as AccountAgeGateHandler — ID first, then
    /// HqChannelName fallback.
    /// </summary>
    private async Task PostAlertAsync(
        SocketUserMessage userMsg,
        SocketGuildUser member,
        SocketGuildChannel guildChannel,
        List<string> externalCodes,
        string mode,
        InviteLinkFilterOutcome outcome,
        string? errorMessage)
    {
        var channel = ResolveSecurityAlertsChannel(guildChannel.Guild);
        if (channel is null)
        {
            _logger.LogWarning(
                "Invite link filter has no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var (color, title, statusLine) = outcome switch
        {
            InviteLinkFilterOutcome.DeletedAndDmd => (Color.Red,        "🛡️ Invite Link Filter — Deleted",     "Message was removed and the poster DM'd."),
            InviteLinkFilterOutcome.Alerted       => (Color.Orange,     "🛡️ Invite Link Filter — Alert",       "Mode is **AlertOnly** — no action taken."),
            InviteLinkFilterOutcome.DeleteFailed  => (Color.DarkRed,    "🛡️ Invite Link Filter — DELETE FAILED", "Could not remove the message. " + (errorMessage ?? "")),
            _                                     => (Color.DarkerGrey, "🛡️ Invite Link Filter",                "Unknown outcome."),
        };

        // Show a content preview rather than full content — invite links in
        // the alert channel would also unfurl into preview cards, which is
        // exactly the cross-promotion noise we're trying to suppress. Strip
        // the URLs so the alert describes the violation without
        // re-broadcasting it.
        var preview = StripInviteUrls(userMsg.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(preview)) preview = "_(no other content)_";
        if (preview.Length > 500) preview = preview.Substring(0, 499) + "…";

        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithDescription(statusLine)
            .AddField("Member",       $"{member.Mention} (`{member.Username}` / `{member.Id}`)", inline: false)
            .AddField("Channel",      $"<#{guildChannel.Id}>", inline: true)
            .AddField("Mode",         mode,                    inline: true)
            .AddField("External code(s)", $"`{string.Join("`, `", externalCodes)}`", inline: false)
            .AddField("Message preview (URLs stripped)", preview, inline: false)
            .WithFooter("ClanGuard • Invite Link Filter")
            .WithCurrentTimestamp();

        try
        {
            await channel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post invite-link-filter alert to channel {Channel} ({ChannelId}) " +
                "for user {UserId}",
                channel.Name, channel.Id, member.Id);
        }
    }

    /// <summary>
    /// Channel resolution: ID first, fall back to name lookup. Same as
    /// AccountAgeGateHandler.ResolveSecurityAlertsChannel.
    /// </summary>
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

    /// <summary>
    /// Replaces every Discord invite URL match with [link removed] so the
    /// alert embed describes what happened without re-posting the actual
    /// invite link. Discord auto-unfurls invite URLs into preview cards
    /// even inside embed field values; we don't want the alerts channel
    /// to become a directory of every external server someone tried to
    /// share.
    /// </summary>
    private static string StripInviteUrls(string content)
        => InviteRegex.Replace(content, "[link removed]");
}

/// <summary>
/// Outcomes the invite-link filter can record for a single violation.
/// Mirrors the AccountAgeGateOutcome pattern — internal to the handler,
/// promoted to a public type only if/when a consumer outside the
/// handler needs to switch on these values.
/// </summary>
internal enum InviteLinkFilterOutcome
{
    /// <summary>AlertOnly mode — no enforcement action taken.</summary>
    Alerted,

    /// <summary>Enforce mode — message deleted and poster DM'd (DM is best-effort).</summary>
    DeletedAndDmd,

    /// <summary>Enforce mode tried to delete but Discord rejected the call. See logs / ErrorMessage.</summary>
    DeleteFailed,
}
