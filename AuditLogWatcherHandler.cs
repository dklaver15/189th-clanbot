using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #3: Audit Log Watcher.
///
/// ── What it does ──
/// Subscribes to Discord's <c>AuditLogCreated</c> gateway event and surfaces
/// high-signal "structural" actions — channel deletes, role changes, bans,
/// bot additions, server-settings updates, webhook creations — to the
/// security-alerts channel in real time.
///
/// ── Why this exists ──
/// The threat model here isn't external trolls; it's a TRUSTED OFFICER'S
/// ACCOUNT GETTING COMPROMISED. The first 60 seconds of a compromise
/// typically look like: delete channels, grant Administrator, mass-ban
/// members, install a malicious bot. Discord retains its own audit log
/// for 45 days, but no one is checking it proactively. This handler
/// puts an alert in front of leadership within seconds of any of those
/// actions, while there's still time to revoke the attacker's session
/// and reverse the damage.
///
/// ── Modes ──
///   "Off"        — Feature disabled.
///   "AlertOnly"  — Default. Post to security-alerts and write an audit
///                  row for every watched event. No auto-response — that's
///                  intentional for v1 so we can build trust in what the
///                  watcher catches before adding any automatic remediation
///                  (role strip, channel lock, kick) that could lock out
///                  an innocent officer on a false positive.
///
/// ── Watched actions ──
/// The full Discord <see cref="ActionType"/> enum is large (50+ entries)
/// and most of it is operational noise — emoji changes, message pins,
/// thread creation, voice moves. We watch a focused set of actions where
/// a single occurrence is genuinely worth a human glance:
///   • ChannelDeleted   (server destruction — CRITICAL)
///   • Ban              (manual ban — officers normally only kick)
///   • BotAdded         (backdoor risk — CRITICAL)
///   • GuildUpdated     (name/icon/vanity URL hijack — CRITICAL)
///   • RoleCreated      (permission escalation prep)
///   • RoleUpdated      (permission escalation — added Admin to a role)
///   • RoleDeleted      (removing a role wholesale, e.g. wiping ranks)
///   • WebhookCreated   (sneaky backdoor — will be enhanced by the
///                       future webhook-audit feature)
/// Actions tagged CRITICAL trigger a ping via
/// <see cref="BotConfig.AuditLogWatcherCriticalMention"/>; everything else
/// posts the embed silently.
///
/// ── Why we don't watch these ──
///   • Kick               — ClanGuard's /kick-awols and AccountAgeGate
///                          fire kicks constantly. Even after filtering
///                          out our own actions, manual officer kicks
///                          are routine moderation.
///   • MemberRoleUpdated  — ClanGuard's AutoPromotion does this nightly
///                          en masse. Officer /promote/demote runs go
///                          through the bot too. The signal-to-noise on
///                          alerting every role change is terrible. A
///                          future enhancement could alert only when a
///                          role with dangerous permissions (Admin,
///                          Manage Server, Manage Roles, Ban Members)
///                          is being added.
///   • Channel/Invite/Message/Overwrite create/update — high-volume
///                          routine activity.
///   • Emoji/Sticker/Thread/Stage/Onboarding — low security signal.
///
/// ── Filtering our own bot's actions ──
/// ClanGuard performs many audit-loggable actions: AutoPromotion role
/// assignments, AWOL kicks, AccountAgeGate kicks, InviteLinkFilter
/// message deletes, /promote / /demote / /setnick commands. Every one
/// of these shows up in the audit log with our bot as the actor. We
/// filter them out by comparing <c>entry.User.Id</c> to the bot's own
/// user ID — otherwise the watcher would be 90% self-generated noise.
///
/// ── Permission + intent requirements ──
/// The bot needs the <c>View Audit Log</c> guild permission to receive
/// these events at all. The <c>GuildBans</c> gateway intent must
/// also be enabled in <c>Program.cs</c> (added in this round). Discord
/// renamed this intent to <c>GUILD_MODERATION</c> server-side, but
/// Discord.NET 3.17 still exposes it as <c>GuildBans</c> in the
/// enum — same bit value, same coverage, just an older name on the
/// .NET side. Without either the permission or the intent,
/// AuditLogCreated never fires.
///
/// ── Failure handling ──
/// All work is fire-and-forget from the gateway callback. Internal
/// exceptions are caught and logged. Per-phase isolation between the
/// audit write and the alert post mirrors AccountAgeGateHandler so a
/// DB hiccup never blocks the alert and an alert-channel failure never
/// drops the audit row.
/// </summary>
public sealed class AuditLogWatcherHandler
{
    // Mode string constants — single source of truth for the config values.
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";

    /// <summary>
    /// Actions the watcher cares about. Everything outside this set is
    /// silently ignored — keeps the alert channel focused on high-signal
    /// events. Adding to this list is a one-line change.
    /// </summary>
    private static readonly HashSet<ActionType> WatchedActions = new()
    {
        ActionType.ChannelDeleted,
        ActionType.Ban,
        ActionType.BotAdded,
        ActionType.GuildUpdated,
        ActionType.RoleCreated,
        ActionType.RoleUpdated,
        ActionType.RoleDeleted,
        ActionType.WebhookCreated,
    };

    /// <summary>
    /// Subset of WatchedActions severe enough to trigger an @here-style
    /// ping. These are the actions where a single occurrence outside of
    /// declared maintenance is a strong "wake someone up right now"
    /// signal. Everything else posts silently — the embed is enough.
    /// </summary>
    private static readonly HashSet<ActionType> CriticalActions = new()
    {
        ActionType.ChannelDeleted,
        ActionType.BotAdded,
        ActionType.GuildUpdated,
    };

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AuditLogWatcherHandler> _logger;
    private readonly BotConfig _config;

    public AuditLogWatcherHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<AuditLogWatcherHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.AuditLogCreated += OnAuditLogCreated;
    }

    private Task OnAuditLogCreated(SocketAuditLogEntry entry, SocketGuild guild)
    {
        // Fire-and-forget; gateway handlers must not block the event loop.
        _ = ProcessAsync(entry, guild);
        return Task.CompletedTask;
    }

    private async Task ProcessAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        try
        {
            // ── Mode check ────────────────────────────────────────────────
            var mode = (_config.AuditLogWatcherMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Anything other than Off implies AlertOnly today. Warn on typos
            // so a misconfiguration doesn't silently change behavior.
            if (!string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown AuditLogWatcherMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly.",
                    mode);
            }

            // ── Filter to watched actions ─────────────────────────────────
            if (!WatchedActions.Contains(entry.Action))
            {
                return;
            }

            // ── Skip our own bot's actions ────────────────────────────────
            // ClanGuard performs many audit-loggable actions (auto-promotion,
            // AWOL kicks, account-age gate kicks, invite-link deletes,
            // /promote, /demote, etc.). Surfacing those would be 90%+
            // self-generated noise and would mask the actual signal.
            // entry.User can be null for system actions; if null we don't
            // know who did it, so let it through.
            if (entry.User is not null
                && entry.User.Id == _client.CurrentUser.Id)
            {
                return;
            }

            _logger.LogInformation(
                "Audit log watcher fired: {Action} by {Actor} in guild {Guild}",
                entry.Action,
                entry.User?.Username ?? "(unknown)",
                guild.Name);

            var isCritical = CriticalActions.Contains(entry.Action);

            // ── Write durable audit row first ─────────────────────────────
            // DB is the durable record; the embed is the live notification.
            // Same ordering as AccountAgeGateHandler so a Discord-side
            // failure on the post doesn't drop the row.
            await WriteAuditAsync(entry, guild, isCritical);

            // ── Post alert embed (with optional ping for critical) ────────
            await PostAlertAsync(entry, guild, isCritical);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Audit log watcher crashed for entry {EntryId} in guild {GuildId}",
                entry.Id, guild.Id);
        }
    }

    /// <summary>
    /// Writes a SecurityAuditRecord row for the gateway event. UserId is
    /// the actor (who performed the action), not the target — for
    /// consistency with how /security-audit's user filter is used today
    /// ("what did this officer do recently?"). Target info lives in
    /// Details.
    /// </summary>
    private async Task WriteAuditAsync(SocketAuditLogEntry entry, SocketGuild guild, bool isCritical)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var (targetDesc, _) = DescribeTarget(entry);

            // Resolve actor as a guild member for DisplayName; fall back
            // to bare username if they've already left or aren't cached.
            var actorMember = entry.User is null ? null : guild.GetUser(entry.User.Id);
            var displayName = actorMember?.DisplayName
                              ?? entry.User?.Username
                              ?? "(unknown)";

            var details = new System.Text.StringBuilder();
            details.Append($"Action: {entry.Action}. ");
            if (!string.IsNullOrWhiteSpace(targetDesc))
                details.Append($"Target: {targetDesc}. ");
            if (!string.IsNullOrWhiteSpace(entry.Reason))
                details.Append($"Reason: {entry.Reason}. ");
            if (isCritical)
                details.Append("Severity: CRITICAL. ");
            details.Append($"AuditEntryId: {entry.Id}.");

            var record = new SecurityAuditRecord
            {
                GuildId      = guild.Id,
                Feature      = "AuditLogWatcher",
                Action       = entry.Action.ToString(),
                UserId       = entry.User?.Id,
                Username     = entry.User?.Username,
                DisplayName  = displayName,
                ChannelId    = null, // Not channel-scoped at this layer; target channel (if any) lives in Details.
                Details      = details.ToString(),
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = null,
            };

            db.SecurityAuditRecords.Add(record);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write SecurityAuditRecord for audit-log watcher event " +
                "(action {Action}, entry {EntryId})",
                entry.Action, entry.Id);
        }
    }

    /// <summary>
    /// Posts the alert embed to the configured security-alerts channel.
    /// Critical actions get the configured mention prefix (default @here)
    /// in message content with the Everyone allowed-mention flag set so
    /// the ping actually fires.
    /// </summary>
    private async Task PostAlertAsync(SocketAuditLogEntry entry, SocketGuild guild, bool isCritical)
    {
        var channel = ResolveSecurityAlertsChannel(guild);
        if (channel is null)
        {
            _logger.LogWarning(
                "Audit log watcher has no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var (targetDesc, targetMention) = DescribeTarget(entry);

        // Actor display: prefer guild nickname for context ("Officer.Bob"
        // rather than just "bob"), fall back to username, "(unknown)"
        // last. Include both the @mention (clickable) and the raw
        // username/ID so the alert is still readable if the user has
        // since left the guild and the mention becomes a dead link.
        var actorMember = entry.User is null ? null : guild.GetUser(entry.User.Id);
        string actorLine;
        if (entry.User is not null)
        {
            var dn = actorMember?.DisplayName ?? entry.User.Username;
            actorLine = $"{entry.User.Mention} (`{dn}` / `{entry.User.Id}`)";
        }
        else
        {
            actorLine = "_(unknown / system action)_";
        }

        var (color, title) = isCritical
            ? (Color.Red,    $"🚨 Audit Log Alert — {FormatActionTitle(entry.Action)}")
            : (Color.Orange, $"🛡️ Audit Log Alert — {FormatActionTitle(entry.Action)}");

        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .AddField("Actor",  actorLine, inline: false);

        if (!string.IsNullOrWhiteSpace(targetMention))
            embed.AddField("Target", targetMention, inline: false);
        else if (!string.IsNullOrWhiteSpace(targetDesc))
            embed.AddField("Target", targetDesc, inline: false);

        if (!string.IsNullOrWhiteSpace(entry.Reason))
            embed.AddField("Reason", Truncate(entry.Reason, 1000), inline: false);

        embed.AddField("Severity",        isCritical ? "**CRITICAL**" : "Standard", inline: true);
        embed.AddField("Audit Entry ID",  $"`{entry.Id}`",                          inline: true);

        embed.WithFooter("ClanGuard • Audit Log Watcher")
             .WithCurrentTimestamp();

        // ── Critical mention (default @here) ──────────────────────────────
        // Building message content + AllowedMentions is what actually makes
        // the ping fire. Discord ignores @here / @everyone in embed fields
        // entirely. The mention has to be in the message text AND the
        // AllowedMentions flag has to permit it, or it shows as plain text.
        string messageContent = string.Empty;
        AllowedMentions allowedMentions = AllowedMentions.None;

        if (isCritical && !string.IsNullOrWhiteSpace(_config.AuditLogWatcherCriticalMention))
        {
            messageContent = _config.AuditLogWatcherCriticalMention;
            // @here and @everyone both ride the same "everyone" allowed-
            // mention flag in Discord's API. For role mentions you'd need
            // AllowedMentions.RoleIds — out of scope for v1.
            if (messageContent.Contains("@here", StringComparison.OrdinalIgnoreCase)
                || messageContent.Contains("@everyone", StringComparison.OrdinalIgnoreCase))
            {
                allowedMentions = new AllowedMentions
                {
                    AllowedTypes = AllowedMentionTypes.Everyone,
                };
            }
        }

        try
        {
            await channel.SendMessageAsync(
                text:            messageContent,
                embed:           embed.Build(),
                allowedMentions: allowedMentions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post audit-log-watcher alert to channel {Channel} ({ChannelId}) " +
                "for action {Action} entry {EntryId}",
                channel.Name, channel.Id, entry.Action, entry.Id);
        }
    }

    /// <summary>
    /// Builds a target description for the audit entry. Returns:
    ///   (humanDescription, mentionableIfAny)
    /// The "mentionable" string is a Discord mention like &lt;#123&gt; or &lt;@456&gt;
    /// when the target has one. Falls back to a plain description for
    /// types without a mention form (e.g. roles, webhooks).
    /// </summary>
    private static (string description, string? mention) DescribeTarget(SocketAuditLogEntry entry)
    {
        // Each ActionType has a corresponding *AuditLogData class in
        // Discord.NET. We pattern-match on the live shapes we care about.
        // Unhandled data types fall through to the default — the alert
        // still posts, just with less specific target info.
        switch (entry.Data)
        {
            case ChannelDeleteAuditLogData ch:
                return ($"Channel `#{ch.ChannelName}` ({ch.ChannelId})", null);

            case BanAuditLogData ban:
                var u = ban.Target;
                return u is null
                    ? ($"User ({entry.Action})", null)
                    : ($"{u.Username} (`{u.Id}`)", $"<@{u.Id}>");

            case BotAddAuditLogData botAdd:
                var b = botAdd.Target;
                return b is null
                    ? ("Bot added (no detail)", null)
                    : ($"Bot {b.Username} (`{b.Id}`)", $"<@{b.Id}>");

            case RoleCreateAuditLogData rc:
                return ($"Role `{rc.Properties.Name}` ({rc.RoleId})", $"<@&{rc.RoleId}>");

            case RoleUpdateAuditLogData ru:
                // RoleEditInfo is a struct (value type), so we can't use `?.`
                // on ru.Before / ru.After — they're never null. The .Name
                // STRING inside, however, can be null when the update didn't
                // change the name (Discord only includes changed fields in
                // the diff). Handle that with IsNullOrEmpty + coalesce.
                var beforeName = ru.Before.Name;
                var afterName  = ru.After.Name;
                string nameDesc;
                if (!string.IsNullOrEmpty(beforeName)
                    && !string.IsNullOrEmpty(afterName)
                    && beforeName != afterName)
                {
                    nameDesc = $"`{beforeName}` → `{afterName}`";
                }
                else
                {
                    var n = afterName ?? beforeName ?? "?";
                    nameDesc = $"`{n}`";
                }
                return ($"Role {nameDesc} ({ru.RoleId})", $"<@&{ru.RoleId}>");

            case RoleDeleteAuditLogData rd:
                return ($"Role `{rd.Properties.Name}` ({rd.RoleId})", null);

            // WebhookCreateAuditLogData: the property names for this type
            // aren't clearly documented and Discord.NET's webhook audit
            // log shapes have shifted across minor versions. We fall
            // through to the default ($"{entry.Action}") rather than
            // risk a compile error on a property guess — the alert
            // still posts with the action name + actor, and the
            // dedicated webhook-audit feature (planned next) will own
            // the detailed webhook target rendering.

            case GuildUpdateAuditLogData gu:
                // Guild updates can carry many before/after diffs; surfacing
                // them all here would be noisy. Just say "Guild settings"
                // and let the reviewing officer open the audit log directly
                // for the full diff. The Severity/Action title plus the
                // actor is usually enough to know whether to investigate.
                return ("Guild settings", null);

            default:
                return ($"{entry.Action}", null);
        }
    }

    /// <summary>
    /// Channel resolution: ID first, then HqChannelName fallback. Same
    /// pattern as AccountAgeGateHandler / InviteLinkFilterHandler.
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

    /// <summary>Convert "ChannelDeleted" → "Channel Deleted". Pure cosmetic.</summary>
    private static string FormatActionTitle(ActionType action)
    {
        var s = action.ToString();
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}