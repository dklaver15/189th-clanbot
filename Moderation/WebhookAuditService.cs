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
/// Server-protection feature #5: Webhook Audit (scan side).
///
/// ── What it does ──
/// Every <see cref="BotConfig.WebhookAuditScanIntervalMinutes"/>
/// minutes (default 360 = 6 hours), enumerates every webhook in every
/// guild via <c>SocketGuild.GetWebhooksAsync</c>, diffs against the
/// <see cref="WebhookSnapshot"/> table, and posts alerts for any new
/// / deleted / changed webhooks that aren't on the allowlist.
///
/// ── Why this is periodic, not event-driven ──
/// The Audit Log Watcher (feature #3) catches <c>WebhookCreated</c>
/// in real time on the gateway, which is the primary defense. But:
///   • Real-time events can be missed if the bot is offline during
///     creation.
///   • The audit-log watcher doesn't currently surface webhook
///     deletes or renames at all.
///   • Discord's own audit log only retains 45 days, so any
///     forensic question past that horizon has no answer without our
///     own persistence.
/// The periodic scan is the catch-everything backstop and the source
/// of truth for "what webhooks exist right now" (consumed by the
/// /webhook-audit slash command).
///
/// ── First-run behavior ──
/// On the very first scan after deployment (signaled by
/// <see cref="BotState.LastWebhookAuditScanCompletedUtc"/> being null),
/// we populate the <c>WebhookSnapshot</c> table from the live state
/// WITHOUT firing alerts. Otherwise every existing legitimate webhook
/// would show as "new" and flood #alerts. After this seed run,
/// LastWebhookAuditScanCompletedUtc is set; subsequent scans diff and
/// alert normally.
///
/// ── Allowlist semantics ──
/// Tokens in <see cref="BotConfig.WebhookAuditAllowlist"/> are matched
/// against each webhook before alerting:
///   • Numeric token → match against <c>ApplicationId</c>. Most
///     robust — app IDs are issued by Discord, can't be spoofed.
///   • Non-numeric token → case-insensitive match against
///     <c>Name</c>. Convenient but spoofable (an attacker can name
///     their hook to match). Prefer app IDs when possible.
/// Allowlisted webhooks are still persisted (so they appear in
/// /webhook-audit) but never alert on new/changed/deleted.
///
/// ── Permission requirements ──
/// Bot must have the <c>Manage Webhooks</c> guild permission to call
/// <c>GetWebhooksAsync</c>. Without it the API returns 403 and the
/// scan logs an error then continues to the next guild.
/// </summary>
public sealed class WebhookAuditService : BackgroundService
{
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";

    /// <summary>
    /// Hard lower bound on scan interval. Protects against fat-fingered
    /// configs (e.g. someone setting WebhookAuditScanIntervalMinutes=0
    /// or 1) that would hammer Discord's API. 15 minutes is below any
    /// reasonable production setting but high enough that even a stuck
    /// service won't get the bot rate-limited.
    /// </summary>
    private static readonly TimeSpan MinScanInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<WebhookAuditService> _logger;
    private readonly BotConfig _config;

    public WebhookAuditService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<WebhookAuditService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same gateway-readiness wait pattern as AwolCheckService.
        while (_client.ConnectionState != ConnectionState.Connected
               || !_client.Guilds.Any())
        {
            _logger.LogInformation("WebhookAuditService waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var interval = ResolveInterval();
        _logger.LogInformation(
            "WebhookAuditService started. Scanning every {Interval}. Mode={Mode}. Allowlist tokens: {Tokens}",
            interval, _config.WebhookAuditMode,
            string.Join(", ", _config.GetWebhookAuditAllowlist().DefaultIfEmpty("(none)")));

        // Initial small offset so the scan doesn't run literally during
        // bot startup. Lets DiscordBotService finish slash-command
        // registration first, keeps startup logs tidy.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScanCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebhookAuditService scan cycle failed");
            }

            try
            {
                await Task.Delay(ResolveInterval(), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var configured = TimeSpan.FromMinutes(Math.Max(1, _config.WebhookAuditScanIntervalMinutes));
        return configured < MinScanInterval ? MinScanInterval : configured;
    }

    private async Task RunScanCycleAsync(CancellationToken ct)
    {
        var mode = (_config.WebhookAuditMode ?? ModeOff).Trim();
        if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("WebhookAuditMode=Off, skipping cycle");
            return;
        }

        if (!string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Unknown WebhookAuditMode '{Mode}' — falling back to AlertOnly. " +
                "Valid values: Off, AlertOnly.",
                mode);
        }

        foreach (var guild in _client.Guilds)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                await ScanGuildAsync(guild, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebhookAuditService scan failed for guild {Guild}", guild.Name);
            }
        }
    }

    private async Task ScanGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        // ── 1. Fetch live state from Discord ─────────────────────────
        IReadOnlyCollection<Discord.Rest.RestWebhook> live;
        try
        {
            live = await guild.GetWebhooksAsync();
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "WebhookAuditService got 403 fetching webhooks for guild {Guild} — bot is missing the Manage Webhooks permission",
                guild.Name);
            return;
        }

        _logger.LogInformation(
            "WebhookAuditService: guild {Guild} has {Count} live webhook(s)",
            guild.Name, live.Count);

        // ── 2. Load stored state ──────────────────────────────────────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var stored = await db.WebhookSnapshots
            .Where(s => s.GuildId == guild.Id)
            .ToListAsync(ct);

        var storedById = stored.ToDictionary(s => s.WebhookId);
        var liveById   = live.ToDictionary(w => w.Id);

        // ── 3. Check first-run flag ──────────────────────────────────
        // Per-guild scan-completed tracking lives in BotState (single-
        // row global state). If the global timestamp is null, EVERY
        // guild's first scan is treated as a seed. That's fine for our
        // single-guild deployment; if we ever go multi-guild we'd want
        // a per-guild row instead, but that's a future migration.
        var botState = await db.BotStates.FirstOrDefaultAsync(ct);
        if (botState is null)
        {
            botState = new BotState();
            db.BotStates.Add(botState);
        }
        var isFirstRun = botState.LastWebhookAuditScanCompletedUtc is null;

        if (isFirstRun)
        {
            _logger.LogInformation(
                "WebhookAuditService first-run for guild {Guild} — seeding {Count} snapshot rows silently",
                guild.Name, live.Count);
        }

        // ── 4. Diff: new / deleted / changed ─────────────────────────
        var allowlist = _config.GetWebhookAuditAllowlist();
        var now = DateTime.UtcNow;

        var newOnes     = new List<Discord.Rest.RestWebhook>();
        var deletedOnes = new List<WebhookSnapshot>();
        var changedOnes = new List<(WebhookSnapshot stored, Discord.Rest.RestWebhook liveHook, string diff)>();

        foreach (var w in live)
        {
            if (storedById.TryGetValue(w.Id, out var existing))
            {
                // Same webhook — update LastSeen and check for field changes
                existing.LastSeenUtc = now;

                var diff = DescribeChanges(existing, w);
                if (diff is not null)
                {
                    // Capture pre-update values for the alert; mutate after.
                    var preUpdate = CloneShallow(existing);
                    existing.Name          = w.Name;
                    existing.ChannelId     = w.ChannelId;
                    existing.ApplicationId = w.ApplicationId;
                    // CreatorUserId/Username intentionally not overwritten — they
                    // anchor to first detection. Updating them would obscure who
                    // originally created the webhook if the creator left the guild.

                    changedOnes.Add((preUpdate, w, diff));
                }
            }
            else
            {
                // New webhook — create snapshot row
                var creator = w.Creator;
                var snapshot = new WebhookSnapshot
                {
                    GuildId         = guild.Id,
                    WebhookId       = w.Id,
                    ChannelId       = w.ChannelId,
                    Name            = w.Name ?? string.Empty,
                    ApplicationId   = w.ApplicationId,
                    CreatorUserId   = creator?.Id,
                    CreatorUsername = creator?.Username,
                    FirstSeenUtc    = now,
                    LastSeenUtc     = now,
                };
                db.WebhookSnapshots.Add(snapshot);
                newOnes.Add(w);
            }
        }

        // Anything stored but not live = deleted since last scan
        foreach (var s in stored)
        {
            if (!liveById.ContainsKey(s.WebhookId))
            {
                deletedOnes.Add(s);
                db.WebhookSnapshots.Remove(s);
            }
        }

        // ── 5. Persist diffs + update timestamp ──────────────────────
        botState.LastWebhookAuditScanCompletedUtc = now;
        await db.SaveChangesAsync(ct);

        // ── 6. Alert (only on non-first-run) ─────────────────────────
        if (isFirstRun)
        {
            return;
        }

        foreach (var w in newOnes)
        {
            if (IsAllowlisted(w.Name, w.ApplicationId, allowlist))
            {
                _logger.LogDebug(
                    "WebhookAuditService: new webhook {Name} ({Id}) matched allowlist — no alert",
                    w.Name, w.Id);
                continue;
            }

            await WriteAuditAsync(db, guild, "WebhookNew", w.Id, w.Name, BuildNewDetails(w));
            await PostAlertAsync(guild, severity: AlertSeverity.New,
                title:     $"🔗 New Webhook Detected",
                summary:   FormatWebhookSummary(w),
                extraLine: null);
        }

        foreach (var s in deletedOnes)
        {
            // Deletes ALWAYS log (no allowlist) — even a legitimate webhook
            // disappearing is forensically interesting. The real-time
            // audit-log watcher doesn't watch WebhookDeleted, so this is
            // the only channel that surfaces it.
            await WriteAuditAsync(db, guild, "WebhookDeleted", s.WebhookId, s.Name, BuildDeletedDetails(s));
            await PostAlertAsync(guild, severity: AlertSeverity.Deleted,
                title:     $"🗑️ Webhook Deleted",
                summary:   FormatSnapshotSummary(s),
                extraLine: $"First seen: <t:{new DateTimeOffset(s.FirstSeenUtc).ToUnixTimeSeconds()}:R>");
        }

        foreach (var (pre, liveHook, diff) in changedOnes)
        {
            if (IsAllowlisted(liveHook.Name, liveHook.ApplicationId, allowlist)
                && IsAllowlisted(pre.Name, pre.ApplicationId, allowlist))
            {
                // Both before and after match the allowlist — still
                // someone's tweaking it but it's a known integration.
                // Skip alert.
                continue;
            }

            await WriteAuditAsync(db, guild, "WebhookChanged", liveHook.Id, liveHook.Name, BuildChangedDetails(pre, liveHook, diff));
            await PostAlertAsync(guild, severity: AlertSeverity.Changed,
                title:     $"✏️ Webhook Changed",
                summary:   FormatWebhookSummary(liveHook),
                extraLine: $"Diff: {diff}");
        }

        // SaveChanges again to capture the audit-record rows the alerts wrote.
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "WebhookAuditService scan complete for {Guild}: new={New} deleted={Deleted} changed={Changed}",
            guild.Name, newOnes.Count, deletedOnes.Count, changedOnes.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private enum AlertSeverity { New, Deleted, Changed }

    /// <summary>
    /// Compares stored snapshot to live webhook. Returns a short human-
    /// readable diff string, or null if nothing changed. Only checks
    /// the fields we care about (Name, ChannelId, ApplicationId) —
    /// avatar / token changes etc. are intentionally ignored.
    /// </summary>
    private static string? DescribeChanges(WebhookSnapshot stored, Discord.Rest.RestWebhook live)
    {
        var parts = new List<string>();

        var liveName = live.Name ?? string.Empty;
        if (!string.Equals(stored.Name, liveName, StringComparison.Ordinal))
            parts.Add($"name `{stored.Name}` → `{liveName}`");

        if (stored.ChannelId != live.ChannelId)
            parts.Add($"channel {FormatChannelRef(stored.ChannelId)} → {FormatChannelRef(live.ChannelId)}");

        if (stored.ApplicationId != live.ApplicationId)
            parts.Add($"appId {stored.ApplicationId?.ToString() ?? "(none)"} → {live.ApplicationId?.ToString() ?? "(none)"}");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string FormatChannelRef(ulong? id) =>
        id is null ? "(none)" : $"<#{id}>";

    /// <summary>Shallow clone for pre-update capture. WebhookSnapshot is small enough that field-by-field is fine and avoids EF tracking confusion.</summary>
    private static WebhookSnapshot CloneShallow(WebhookSnapshot s) => new()
    {
        Id              = s.Id,
        GuildId         = s.GuildId,
        WebhookId       = s.WebhookId,
        ChannelId       = s.ChannelId,
        Name            = s.Name,
        ApplicationId   = s.ApplicationId,
        CreatorUserId   = s.CreatorUserId,
        CreatorUsername = s.CreatorUsername,
        FirstSeenUtc    = s.FirstSeenUtc,
        LastSeenUtc     = s.LastSeenUtc,
    };

    /// <summary>
    /// True if the webhook matches any allowlist token. Numeric tokens
    /// match ApplicationId; non-numeric match Name case-insensitively.
    /// </summary>
    private static bool IsAllowlisted(string? name, ulong? appId, List<string> allowlist)
    {
        if (allowlist.Count == 0) return false;

        foreach (var token in allowlist)
        {
            if (ulong.TryParse(token, out var tokenAppId))
            {
                if (appId.HasValue && appId.Value == tokenAppId) return true;
            }
            else
            {
                if (!string.IsNullOrEmpty(name)
                    && string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string FormatWebhookSummary(Discord.Rest.RestWebhook w)
    {
        var lines = new List<string>
        {
            $"**Name:** `{w.Name}`",
            $"**ID:** `{w.Id}`",
            $"**Channel:** {FormatChannelRef(w.ChannelId)}",
        };
        if (w.ApplicationId.HasValue)
            lines.Add($"**Application ID:** `{w.ApplicationId.Value}`");
        if (w.Creator is not null)
            lines.Add($"**Created by:** <@{w.Creator.Id}> (`{w.Creator.Username}`)");
        return string.Join("\n", lines);
    }

    private static string FormatSnapshotSummary(WebhookSnapshot s)
    {
        var lines = new List<string>
        {
            $"**Name:** `{s.Name}`",
            $"**ID:** `{s.WebhookId}`",
            $"**Channel:** {FormatChannelRef(s.ChannelId)}",
        };
        if (s.ApplicationId.HasValue)
            lines.Add($"**Application ID:** `{s.ApplicationId.Value}`");
        if (s.CreatorUserId.HasValue)
            lines.Add($"**Created by:** <@{s.CreatorUserId.Value}> (`{s.CreatorUsername ?? "?"}`)");
        return string.Join("\n", lines);
    }

    private static string BuildNewDetails(Discord.Rest.RestWebhook w)
    {
        var parts = new List<string>
        {
            $"Name='{w.Name}'",
            $"ChannelId={w.ChannelId?.ToString() ?? "null"}",
            $"ApplicationId={w.ApplicationId?.ToString() ?? "null"}",
            $"CreatorId={w.Creator?.Id.ToString() ?? "null"}",
        };
        return "New webhook. " + string.Join(", ", parts) + ".";
    }

    private static string BuildDeletedDetails(WebhookSnapshot s)
    {
        var parts = new List<string>
        {
            $"Name='{s.Name}'",
            $"ChannelId={s.ChannelId?.ToString() ?? "null"}",
            $"ApplicationId={s.ApplicationId?.ToString() ?? "null"}",
            $"FirstSeenUtc={s.FirstSeenUtc:O}",
        };
        return "Webhook deleted. " + string.Join(", ", parts) + ".";
    }

    private static string BuildChangedDetails(WebhookSnapshot pre, Discord.Rest.RestWebhook live, string diff)
        => $"Webhook changed. Diff: {diff}. WebhookId={live.Id}.";

    /// <summary>
    /// Writes a SecurityAuditRecord row for the diff finding. Mirrors the
    /// pattern used by the other server-protection handlers — same Feature
    /// constant ("WebhookAudit"), Action varying by event type.
    /// </summary>
    private static Task WriteAuditAsync(
        BotDbContext db, SocketGuild guild,
        string action, ulong webhookId, string? webhookName,
        string details)
    {
        db.SecurityAuditRecords.Add(new SecurityAuditRecord
        {
            GuildId      = guild.Id,
            Feature      = "WebhookAudit",
            Action       = action,
            UserId       = null,       // not user-scoped — webhook is the subject
            Username     = null,
            DisplayName  = webhookName,
            ChannelId    = null,
            Details      = details,
            OccurredAt   = DateTime.UtcNow,
            ErrorMessage = null,
        });
        return Task.CompletedTask;
    }

    private async Task PostAlertAsync(
        SocketGuild guild,
        AlertSeverity severity,
        string title,
        string summary,
        string? extraLine)
    {
        var channel = ResolveSecurityAlertsChannel(guild);
        if (channel is null)
        {
            _logger.LogWarning(
                "WebhookAuditService has no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var color = severity switch
        {
            AlertSeverity.New     => Color.Orange,
            AlertSeverity.Changed => Color.Orange,
            AlertSeverity.Deleted => Color.LightGrey,
            _                     => Color.Default,
        };

        var description = extraLine is null
            ? summary
            : summary + "\n" + extraLine;

        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithDescription(description)
            .WithFooter("ClanGuard • Webhook Audit")
            .WithCurrentTimestamp()
            .Build();

        try
        {
            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WebhookAuditService failed to post {Severity} alert to channel {Channel} ({ChannelId})",
                severity, channel.Name, channel.Id);
        }
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
}