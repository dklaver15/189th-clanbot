using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles /webhook-audit — read side of the WebhookSnapshot table plus
/// a fresh live fetch from Discord. Listing the current webhooks is
/// security-critical (every webhook is effectively a back door into a
/// channel), so this command is BG+ only.
///
/// ── Why fetch live, not just read the snapshot table ──
/// The snapshot is at most <see cref="BotConfig.WebhookAuditScanIntervalMinutes"/>
/// stale. For incident-response use cases ("did anyone just add a
/// webhook?") that's too stale to trust. The handler does a fresh
/// <c>GetWebhooksAsync</c> on every invocation and reconciles against
/// the snapshot table to surface "first seen on" for each entry.
///
/// ── Permission model ──
/// BG+ floor, same as /security-audit. Same rationale: the webhook
/// inventory is essentially a list of every backdoor into the server,
/// at the same sensitivity class as raw security forensics.
///
/// ── Output shape ──
/// Single ephemeral embed, grouped by channel, each line showing:
///   • Webhook name (code-formatted)
///   • Application name if known (via ApplicationId lookup) or "user-created"
///   • Creator if known
///   • First seen relative timestamp
///   • Allowlist marker if applicable
/// Designed for at-a-glance review during periodic security audits.
/// </summary>
public sealed class WebhookAuditCommandHandler
{
    public const string CommandName = "webhook-audit";

    /// <summary>
    /// Hardcoded BG+ rank floor. Same posture as
    /// <see cref="SecurityAuditCommandHandler.MinRankFloor"/> — the
    /// surface exposes raw backdoor inventory, which is general-grade
    /// leadership-only sensitivity.
    /// </summary>
    private const string MinRankFloor = "BG";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<WebhookAuditCommandHandler> _logger;
    private readonly BotConfig _config;

    public WebhookAuditCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<WebhookAuditCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await DispatchAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task DispatchAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return;
        }

        var guild = _client.GetGuild(command.GuildId!.Value);
        if (guild is null)
        {
            await command.FollowupAsync("Couldn't resolve guild.", ephemeral: true);
            return;
        }

        // ── Fetch live state ──────────────────────────────────────────
        IReadOnlyCollection<Discord.Rest.RestWebhook> live;
        try
        {
            live = await guild.GetWebhooksAsync();
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            await command.FollowupAsync(
                "❌ The bot is missing the **Manage Webhooks** permission, so it can't enumerate webhooks. " +
                "Add the permission to ClanGuard's role and try again.",
                ephemeral: true);
            return;
        }

        // ── Reconcile against snapshot table for FirstSeen data ───────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var snapshots = await db.WebhookSnapshots
            .Where(s => s.GuildId == guild.Id)
            .ToListAsync();
        var snapshotById = snapshots.ToDictionary(s => s.WebhookId);

        // ── Resolve creator user IDs via REST ─────────────────────────
        // Webhook objects from GetWebhooksAsync include creator metadata,
        // but Discord renders <@id> mentions as clickable links only when
        // the user is currently in the guild. Creators who have since
        // left show up as raw mention text — useless for forensics. We
        // pre-resolve each unique creator ID with Rest.GetUserAsync,
        // which returns the user object regardless of guild membership.
        // This gives us a fresh Username we can render alongside the
        // mention so leadership always sees a name, even for accounts
        // that left months ago.
        //
        // Batched once per unique creator ID rather than per-webhook to
        // keep API calls minimal — for our server size that's typically
        // single-digit lookups.
        var resolvedCreators = await ResolveCreatorsAsync(live);

        // ── Render ────────────────────────────────────────────────────
        if (live.Count == 0)
        {
            var emptyEmbed = new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("🔗 Webhook Audit — clean")
                .WithDescription("No webhooks exist in this server. That's the safest possible state.")
                .WithFooter("ClanGuard • /webhook-audit")
                .WithCurrentTimestamp()
                .Build();
            await command.FollowupAsync(embed: emptyEmbed, ephemeral: true);
            return;
        }

        var allowlist = _config.GetWebhookAuditAllowlist();

        // Group by channel
        var byChannel = live
            .GroupBy(w => w.ChannelId ?? 0)
            .OrderBy(g => guild.GetTextChannel(g.Key)?.Name ?? "zzz");

        var description = new StringBuilder();
        description.AppendLine($"**{live.Count}** webhook(s) across **{byChannel.Count()}** channel(s)");
        description.AppendLine();

        foreach (var group in byChannel)
        {
            var ch = group.Key == 0 ? null : guild.GetTextChannel(group.Key);
            var channelLabel = ch is null ? "(unknown / app-owned)" : ch.Mention;
            description.AppendLine($"**{channelLabel}**");

            foreach (var w in group.OrderBy(x => x.Name))
            {
                var line = BuildWebhookLine(w, snapshotById, resolvedCreators, allowlist);
                description.AppendLine(line);
            }
            description.AppendLine();
        }

        // Hard cap at Discord's 4096-char description limit. If we
        // overflow, truncate with a note. Won't happen at any plausible
        // server scale (would need 80+ webhooks), but kept defensive.
        var descStr = description.ToString();
        if (descStr.Length > 4000)
        {
            descStr = descStr.Substring(0, 4000) + "\n\n…truncated, server has too many webhooks to render in one embed.";
        }

        var embed = new EmbedBuilder()
            .WithColor(Color.Blue)
            .WithTitle("🔗 Webhook Audit")
            .WithDescription(descStr)
            .WithFooter("ClanGuard • /webhook-audit • allowlist via WebhookAuditAllowlist config")
            .WithCurrentTimestamp()
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    private static string BuildWebhookLine(
        Discord.Rest.RestWebhook w,
        Dictionary<ulong, WebhookSnapshot> snapshotById,
        Dictionary<ulong, IUser> resolvedCreators,
        List<string> allowlist)
    {
        var parts = new List<string> { $"• `{w.Name}`" };

        // App vs user-created tag
        if (w.ApplicationId.HasValue)
            parts.Add($"app `{w.ApplicationId.Value}`");
        else
            parts.Add("user-created");

        // Creator: prefer REST-resolved username (works for users who
        // have left the guild), fall back to whatever the webhook
        // object carried, fall back to "(unknown)" as last resort.
        // We render BOTH the bold username AND the <@id> mention so
        // leadership sees a readable name regardless of whether
        // Discord's client can resolve the mention to a clickable
        // link — when the creator has left the guild the mention
        // shows as raw text but the username next to it answers
        // "who?".
        if (w.Creator is not null)
        {
            string username;
            if (resolvedCreators.TryGetValue(w.Creator.Id, out var resolved))
                username = resolved.Username;
            else if (!string.IsNullOrWhiteSpace(w.Creator.Username))
                username = w.Creator.Username;
            else
                username = "(unknown)";

            parts.Add($"by **{username}** (<@{w.Creator.Id}>)");
        }

        // Discord creation timestamp — pulled from the webhook's
        // snowflake ID (the high bits of every Discord snowflake encode
        // creation time). Always available, no extra API call. This is
        // the AUTHORITATIVE age of the webhook regardless of when our
        // bot started scanning — a pre-existing webhook will read
        // "created 2 years ago" even if its First-Seen is recent
        // because the bot only started watching last week.
        var createdUnix = w.CreatedAt.ToUnixTimeSeconds();
        parts.Add($"created <t:{createdUnix}:R>");

        // First-seen from snapshot (if we have it). Distinct from the
        // creation timestamp above: this is when OUR scan first saw
        // the webhook. For a webhook created before the bot was
        // deployed, First-Seen will read much more recently than
        // CreatedAt — the gap between the two answers "has this
        // existed since before we started watching?"
        if (snapshotById.TryGetValue(w.Id, out var snap))
        {
            var unix = new DateTimeOffset(snap.FirstSeenUtc).ToUnixTimeSeconds();
            parts.Add($"seen <t:{unix}:R>");
        }
        else
        {
            // The webhook exists live but isn't in our snapshot yet.
            // Means it was created since the last scan — the next scan
            // will catch it. Worth flagging visually.
            parts.Add("**new since last scan**");
        }

        // Allowlist tag
        if (WebhookAuditService_IsAllowlisted(w.Name, w.ApplicationId, allowlist))
            parts.Add("✅ allowlisted");

        return string.Join(" — ", parts);
    }

    /// <summary>
    /// Mirror of <c>WebhookAuditService.IsAllowlisted</c>. Kept as a
    /// private static here rather than reaching across to the service
    /// to avoid coupling the slash command's read path to the scan
    /// service's lifecycle. If the allowlist matching rules ever
    /// change, update both.
    /// </summary>
    private static bool WebhookAuditService_IsAllowlisted(string? name, ulong? appId, List<string> allowlist)
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

    /// <summary>
    /// Looks up the current username for every unique creator user ID
    /// across the live webhook list via <c>DiscordSocketClient.Rest.
    /// GetUserAsync</c>. Returns a map keyed by user ID; users that
    /// fail to resolve (deleted accounts, network errors) are simply
    /// omitted — callers fall back to the embedded webhook creator
    /// data when the map doesn't contain a given ID.
    ///
    /// Batched at the (unique-IDs across all webhooks) level rather
    /// than per-webhook so a server with 20 webhooks all created by
    /// the same person costs one API call, not twenty. Each lookup is
    /// awaited sequentially — the call is fast and the typical count
    /// is small, so the simplicity wins over parallelism. If a server
    /// ever grows past dozens of distinct creators we can revisit
    /// with Task.WhenAll.
    /// </summary>
    private async Task<Dictionary<ulong, IUser>> ResolveCreatorsAsync(
        IReadOnlyCollection<Discord.Rest.RestWebhook> live)
    {
        var map = new Dictionary<ulong, IUser>();

        var creatorIds = live
            .Where(w => w.Creator is not null)
            .Select(w => w.Creator!.Id)
            .Distinct()
            .ToList();

        foreach (var id in creatorIds)
        {
            try
            {
                var user = await _client.Rest.GetUserAsync(id);
                if (user is not null)
                    map[id] = user;
            }
            catch (Exception ex)
            {
                // Single-user failure shouldn't fail the whole command.
                // Log and continue; the caller falls back to the
                // embedded webhook creator data for any IDs missing
                // from the map.
                _logger.LogWarning(ex,
                    "WebhookAuditCommandHandler: REST lookup failed for creator user {Id} — " +
                    "embedded webhook creator data will be used as fallback",
                    id);
            }
        }

        return map;
    }

    /// <summary>Same role-list-index pattern as SecurityAuditCommandHandler.HasMinRankFloor.</summary>
    private bool HasMinRankFloor(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(MinRankFloor, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    /// <summary>
    /// Slash-command shape for the OnReadyAsync registration list.
    /// CommandsCommandHandler.BuildCatalog must stay in sync.
    /// </summary>
    public static SlashCommandProperties BuildCommand()
    {
        return new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("List every webhook in the server, grouped by channel (BG+ only)")
            .Build();
    }
}