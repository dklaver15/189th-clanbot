using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /timeline slash command. Officer+ gated, ephemeral.
/// Aggregates a single member's history across every operational table the
/// bot owns into one chronological embed: account creation, server join
/// with invite attribution, current rank assignment, officer applications,
/// AWOL flags, AWOL kicks, security audit hits, and recent event
/// attendance. Pairs with /awol-check for activity numbers and
/// /promo-eligibility for the promotion verdict — /timeline answers
/// "what's the story on this member?" in a single ephemeral response.
///
/// ── Permission gate ──
/// Officer+, matching SlashCommandHandler.HasElevatedPermissions. Opening
/// access to everyone would reveal other members' AWOL / application /
/// security history, which is sensitive enough to keep off the everyone
/// tier. SyncWithHandlers: SlashCommandHandler.HasElevatedPermissions,
/// HealthCommandHandler.HasElevatedPermissions.
///
/// ── Ex-members ──
/// The user picker surfaces ex-members from Discord's cache. When the
/// target is no longer a guild member (SocketGuildUser cast fails on
/// GetUser), we degrade gracefully: skip the "current state" fields that
/// need roles (rank role check, AWOL role check) and continue with the
/// DB-only history. The timeline is most useful in exactly this case —
/// "what happened with the member who just left?"
///
/// ── Rank history chain ──
/// Backed by the append-only RankChange table (seeded from RankHistory by
/// RankChangeBackfillService on first deploy; appended by RankTrackingHandler
/// on every observed rank transition). Each entry renders as a promotion,
/// demotion, initial assignment, or full-removal event with its own
/// timestamp. The single-row RankHistory table is still consulted for the
/// "current rank" summary field and for the events-at-current-rank counter.
/// Promotions that happened BEFORE this feature deployed are not recoverable;
/// the backfill only seeds the current rank's assignment timestamp as a
/// single "Initial" entry.
///
/// ── EventAttendance scope ──
/// Manual /add-event-credit rows write to EventAttendance with
/// CalendarEventId=0 as a sentinel (see EventCreditCommandHandler).
/// Those are excluded from the chronological list and from the
/// "lifetime events" counter so the displayed counts reflect real
/// Apollo events only. The "Events at &lt;rank&gt;" summary still includes
/// them (via EventAttendanceHelper) because they legitimately count
/// toward promotion math.
///
/// ── Dry-run AWOL kicks ──
/// AwolKickAuditRecord rows with WasDryRun=true are skipped. Those are
/// operational testing artifacts (officer running /kick-awols dry-run
/// to preview the list) rather than real history.
///
/// ── Display ordering ──
/// Newest-first, matching /attendance, /security-audit, and /usage-stats.
/// Officers usually open this command to investigate something recent;
/// the relevant context surfaces at the top.
///
/// ── Truncation ──
/// Entries are capped at MaxTimelineEntries (35) after the merge+sort
/// step. With ~60 chars per entry plus a Discord timestamp tag, 35
/// entries leaves comfortable headroom under the 4096-char description
/// limit. When truncated, a "+ X earlier entries not shown" footer line
/// indicates the cap was hit. A long-tenured member with deep history
/// will see their most recent 35 events; for deeper drill-downs, use
/// the per-feature commands (/security-audit, /attendance, /invite info).
///
/// ── Catalog entry ──
/// Officer+ tier. NOTE: also keep CommandsCommandHandler.BuildCatalog
/// in sync when this command's gate or description changes.
/// </summary>
public class TimelineCommandHandler
{
    public const string CommandName = "timeline";

    /// <summary>How many recent events to include in the chronological list.</summary>
    private const int RecentEventsLimit = 5;

    /// <summary>How many recent security-audit hits to include.</summary>
    private const int SecurityRecordsLimit = 5;

    /// <summary>
    /// Hard cap on chronological-list entries. Discord allows 4096 chars in
    /// a description; budgeting ~60 chars per entry plus footer headroom
    /// gives this cap. Cap is applied AFTER merge+sort so the most recent
    /// entries always make it in.
    /// </summary>
    private const int MaxTimelineEntries = 35;

    /// <summary>
    /// Activity window for the activity-snapshot summary fields. Matches
    /// /awol-status' default 28-day window so the numbers line up with
    /// what the member sees themselves.
    /// </summary>
    private const int ActivityWindowDays = 28;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly MemberActivityChartRenderer _chartRenderer;
    private readonly ILogger<TimelineCommandHandler> _logger;

    public TimelineCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        MemberActivityChartRenderer chartRenderer,
        ILogger<TimelineCommandHandler> logger)
    {
        _services      = services;
        _client        = client;
        _config        = config.Value;
        _chartRenderer = chartRenderer;
        _logger        = logger;
    }

    /// <summary>
    /// Builds the slash-command shape. The description here is mirrored in
    /// CommandsCommandHandler.BuildCatalog — keep both in sync.
    /// </summary>
    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Show a member's complete history — joins, ranks, AWOL, applications, events (Officer+ only)")
            .AddOption("user", ApplicationCommandOptionType.User,
                "The member whose timeline you want to see", isRequired: true)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        // ── Guild + permission gate ─────────────────────────────────────
        if (command.GuildId is null)
        {
            await command.RespondAsync(
                "This command can only be used in a server.",
                ephemeral: true);
            return;
        }

        if (command.User is not SocketGuildUser caller || !HasElevatedPermissions(caller))
        {
            await command.RespondAsync(
                "⛔ This command is restricted to officers.",
                ephemeral: true);
            return;
        }

        // ── Resolve the target user from the option ─────────────────────
        // SocketUser (not SocketGuildUser) — picker may surface ex-members
        // from Discord's cache; we want to allow lookups on them.
        var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "user");
        if (userOption?.Value is not SocketUser targetUser)
        {
            await command.RespondAsync(
                "Could not resolve the selected user.",
                ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        try
        {
            var result = await BuildTimelineAsync(command.GuildId.Value, targetUser);

            if (result.ChartPng is byte[] png)
            {
                // Chart rendered successfully — attach it inline. The embed's
                // ImageUrl already points at "attachment://<ChartFileName>"
                // so Discord renders the PNG below the embed body.
                using var ms = new MemoryStream(png);
                await command.FollowupWithFileAsync(
                    ms,
                    result.ChartFileName,
                    embed: result.Embed,
                    ephemeral: true);
            }
            else
            {
                // No chart (member with zero recent messages, or renderer
                // failure). Embed renders fine on its own.
                await command.FollowupAsync(embed: result.Embed, ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "/timeline failed for target {TargetId} invoked by {Caller}",
                targetUser.Id, caller.Username);
            await command.FollowupAsync(
                "Something went wrong building the timeline. Check the bot logs.",
                ephemeral: true);
        }
    }

    /// <summary>
    /// Container for the assembled timeline response. The chart bytes are
    /// optional — when null, the embed renders standalone without an
    /// <c>attachment://</c> image URL.
    /// </summary>
    private sealed record TimelineResult(Embed Embed, byte[]? ChartPng, string ChartFileName);

    private async Task<TimelineResult> BuildTimelineAsync(ulong guildId, SocketUser targetUser)
    {
        var embed = await BuildTimelineEmbedAsync(guildId, targetUser);

        // Chart rendering is intentionally best-effort: it fires its own
        // scope and DbContext (so a long render can't block the embed
        // scope's disposal), and it swallows-and-logs any failure to
        // return null. Treat null as "no chart this time" and the embed
        // path takes over.
        var displayName = ResolveDisplayName(guildId, targetUser);
        var chartPng = await _chartRenderer.TryRenderActivityChartAsync(
            guildId, targetUser.Id, displayName);

        const string chartFileName = "activity.png";

        if (chartPng is not null)
        {
            // Mutating the embed via .ToEmbedBuilder().WithImageUrl(...).Build()
            // keeps the immutable Embed pattern intact while pinning the
            // attachment URL. The filename here MUST match the one passed
            // to FollowupWithFileAsync — Discord ties the embed image to
            // the attached file by that name.
            embed = embed.ToEmbedBuilder()
                .WithImageUrl($"attachment://{chartFileName}")
                .Build();
        }

        return new TimelineResult(embed, chartPng, chartFileName);
    }

    /// <summary>
    /// Resolves the display name that the chart title should use for the
    /// target user. Mirrors the precedence used elsewhere in the embed.
    /// </summary>
    private string ResolveDisplayName(ulong guildId, SocketUser targetUser)
    {
        var guild = _client.GetGuild(guildId);
        var guildMember = guild?.GetUser(targetUser.Id);
        return guildMember?.DisplayName
            ?? targetUser.GlobalName
            ?? targetUser.Username;
    }

    private async Task<Embed> BuildTimelineEmbedAsync(ulong guildId, SocketUser targetUser)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var guild = _client.GetGuild(guildId);
        var guildMember = guild?.GetUser(targetUser.Id);  // null if ex-member

        // ── Pull every history slice in parallel-ish (all indexed user-scoped queries) ──
        // Each table has a (GuildId, UserId, ...) covering index, so these are O(rows-for-user).

        var rank = await db.RankHistories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.GuildId == guildId && r.UserId == targetUser.Id);

        // Full rank-change chain — append-only log of every transition the bot
        // observed. RankHistory only holds the CURRENT rank; this gives us the
        // chronology. Seeded by RankChangeBackfillService on first deploy.
        var rankChanges = await db.RankChanges
            .AsNoTracking()
            .Where(c => c.GuildId == guildId && c.UserId == targetUser.Id)
            .OrderByDescending(c => c.ChangedAt)
            .ToListAsync();

        var inviteJoin = await db.InviteJoins
            .AsNoTracking()
            .Where(j => j.GuildId == guildId && j.UserDiscordId == targetUser.Id)
            .OrderBy(j => j.JoinedAt)
            .FirstOrDefaultAsync();

        var awolRecords = await db.AwolRecords
            .AsNoTracking()
            .Where(r => r.GuildId == guildId && r.UserId == targetUser.Id)
            .OrderByDescending(r => r.AssignedAt)
            .ToListAsync();

        var awolKicks = await db.AwolKickAudits
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.UserId == targetUser.Id)
            .OrderByDescending(a => a.ProcessedAt)
            .ToListAsync();

        var applications = await db.OfficerApplications
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.UserId == targetUser.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync();

        var securityHits = await db.SecurityAuditRecords
            .AsNoTracking()
            .Where(s => s.GuildId == guildId && s.UserId == targetUser.Id)
            .OrderByDescending(s => s.OccurredAt)
            .Take(SecurityRecordsLimit)
            .ToListAsync();

        // Exclude manual /add-event-credit rows (CalendarEventId=0) — those
        // are bookkeeping adjustments, not real attendance events.
        var recentEvents = await db.EventAttendances
            .AsNoTracking()
            .Where(e => e.GuildId == guildId
                     && e.UserId == targetUser.Id
                     && e.CalendarEventId != 0)
            .OrderByDescending(e => e.EventEndUtc)
            .Take(RecentEventsLimit)
            .ToListAsync();

        // ── Activity snapshot (rolling window) ──────────────────────────
        var windowStart = DateTime.UtcNow.AddDays(-ActivityWindowDays);

        var messageCount = await db.MessageEvents
            .CountAsync(m => m.GuildId == guildId
                          && m.UserId == targetUser.Id
                          && m.Timestamp >= windowStart);

        var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, guildId, targetUser.Id, windowStart,
            _config.MaxSingleSessionHours);
        var voiceHours = voiceSeconds / 3600.0;

        // Total events at current rank — uses EventAttendanceHelper so
        // meetings are folded in and the rank-seed math matches what
        // AutoPromotionService and /promo-eligibility report.
        var eventsAtRank = 0;
        if (rank is not null)
        {
            eventsAtRank = await EventAttendanceHelper.CountEventsAttendedAsync(
                db, guildId, targetUser.Id,
                rank.AssignedAt, rank.SeedAppliedAt,
                rank.EventsAttendedAtRankBeforeBot);
        }

        // Lifetime real-event count (excludes manual credits).
        var totalRealEvents = await db.EventAttendances
            .CountAsync(e => e.GuildId == guildId
                          && e.UserId == targetUser.Id
                          && e.CalendarEventId != 0);

        // ── Merge into one chronological entry list ─────────────────────
        var entries = new List<TimelineEntry>();

        // Account creation — always known from Discord's snowflake.
        entries.Add(new TimelineEntry(
            targetUser.CreatedAt.UtcDateTime,
            "📅",
            "Discord account created"));

        // Server join — prefer SocketGuildUser.JoinedAt (live, accurate);
        // fall back to InviteJoin.JoinedAt for ex-members whose guild
        // record is gone.
        DateTime? joinedAt = guildMember?.JoinedAt?.UtcDateTime
                          ?? inviteJoin?.JoinedAt;
        if (joinedAt.HasValue)
        {
            var joinDesc = inviteJoin is not null
                ? $"Joined the server via **{EscapeMarkdown(inviteJoin.LabelSnapshot)}**"
                  + (inviteJoin.InviterDiscordId is ulong inviter ? $" (referred by <@{inviter}>)" : string.Empty)
                : "Joined the server";
            entries.Add(new TimelineEntry(joinedAt.Value, "🚪", joinDesc));
        }

        // Rank change chain — one entry per observed transition. The
        // Initial entry (FromRank=null) marks first observed rank;
        // Removed entries (ToRank=null) mark loss of all rank roles.
        // Promotion vs demotion is computed by rank-list index comparison.
        var rankRoles = _config.GetRankRolesList();
        foreach (var change in rankChanges)
        {
            string icon;
            string desc;
            if (change.FromRank is null && change.ToRank is not null)
            {
                icon = "🆕";
                desc = $"Assigned rank **{EscapeMarkdown(change.ToRank)}**";
            }
            else if (change.FromRank is not null && change.ToRank is null)
            {
                icon = "🔻";
                desc = $"Lost all rank roles (was **{EscapeMarkdown(change.FromRank)}**)";
            }
            else
            {
                // Both set — compute direction by rank-list index.
                var fromIdx = rankRoles.FindIndex(r =>
                    r.Equals(change.FromRank, StringComparison.OrdinalIgnoreCase));
                var toIdx = rankRoles.FindIndex(r =>
                    r.Equals(change.ToRank, StringComparison.OrdinalIgnoreCase));
                var direction = (fromIdx >= 0 && toIdx >= 0 && toIdx < fromIdx)
                    ? "Demoted"
                    : "Promoted";
                icon = direction == "Demoted" ? "⬇️" : "🎖️";
                desc = $"{direction} **{EscapeMarkdown(change.FromRank!)}** → **{EscapeMarkdown(change.ToRank!)}**";
            }
            entries.Add(new TimelineEntry(change.ChangedAt, icon, desc));
        }

        // Promotion-seed application — only shown if a seed was actually
        // applied at the current rank. Sits separately from the rank chain
        // because seed application doesn't change the rank itself.
        if (rank?.SeedAppliedAt is DateTime seedAt && rank.EventsAttendedAtRankBeforeBot > 0)
        {
            entries.Add(new TimelineEntry(
                seedAt,
                "🌱",
                $"Promotion seed applied — {rank.EventsAttendedAtRankBeforeBot} event(s) credited at {EscapeMarkdown(rank.RankName)}"));
        }

        // Officer applications — pending stays at SubmittedAt; resolved
        // apps land at ReviewedAt so the entry reflects the decision moment.
        foreach (var app in applications)
        {
            var (icon, text) = app.Status switch
            {
                OfficerApplicationStatus.Approved => ("✅", "Officer application **approved**"),
                OfficerApplicationStatus.Denied   => ("❌", "Officer application **denied**"),
                _                                  => ("📝", "Officer application **submitted** (pending review)"),
            };
            var entryTime = app.Status == OfficerApplicationStatus.Pending
                ? app.SubmittedAt
                : (app.ReviewedAt ?? app.SubmittedAt);
            entries.Add(new TimelineEntry(entryTime, icon, text));
        }

        // AWOL assignments. We don't store an explicit "cleared" timestamp
        // distinct from "HQ notified," so we surface only the flag event.
        foreach (var awol in awolRecords)
        {
            entries.Add(new TimelineEntry(
                awol.AssignedAt,
                "⚠️",
                "Flagged AWOL"));
        }

        // AWOL kicks — real (non-dry-run) outcomes only. Differentiate
        // "actually kicked" from "skipped/failed" via icon.
        foreach (var kick in awolKicks)
        {
            if (kick.WasDryRun) continue;
            var icon = kick.Outcome == "Kicked" ? "🥾" : "ℹ️";
            entries.Add(new TimelineEntry(
                kick.ProcessedAt,
                icon,
                $"AWOL kick — **{EscapeMarkdown(kick.Outcome)}**"));
        }

        // Security audit hits (capped at query time).
        foreach (var hit in securityHits)
        {
            entries.Add(new TimelineEntry(
                hit.OccurredAt,
                "🛡️",
                $"Security: {EscapeMarkdown(hit.Feature)} — {EscapeMarkdown(hit.Action)}"));
        }

        // Recent real events.
        foreach (var ev in recentEvents)
        {
            entries.Add(new TimelineEntry(
                ev.EventEndUtc,
                "🎯",
                $"Attended event ({ev.AttendedMinutes} min)"));
        }

        // ── Sort newest-first, cap entries ──────────────────────────────
        entries.Sort((a, b) => b.UtcTime.CompareTo(a.UtcTime));
        var totalEntryCount = entries.Count;
        var truncated = totalEntryCount > MaxTimelineEntries;
        if (truncated)
        {
            entries = entries.Take(MaxTimelineEntries).ToList();
        }

        // ── Render description body ─────────────────────────────────────
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append(DiscordTimestamp(entry.UtcTime, 'f'));
            sb.Append(' ');
            sb.Append(entry.Icon);
            sb.Append(' ');
            sb.AppendLine(entry.Description);
        }
        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine($"_+{totalEntryCount - MaxTimelineEntries} earlier entries not shown_");
        }

        // ── Construct the embed ─────────────────────────────────────────
        var displayName = guildMember?.DisplayName
                       ?? targetUser.GlobalName
                       ?? targetUser.Username;
        var memberStateSuffix = guildMember is null ? " (no longer in server)" : string.Empty;

        var embed = new EmbedBuilder()
            .WithAuthor($"{displayName}{memberStateSuffix}", targetUser.GetDisplayAvatarUrl())
            .WithTitle("📜 Member Timeline")
            // 189th gold for active members, neutral grey for ex-members so
            // the visual treatment matches the suffix.
            .WithColor(guildMember is null ? Color.LightGrey : new Color(201, 166, 71))
            .WithDescription(sb.Length == 0 ? "_No timeline entries on file._" : sb.ToString());

        // Current-state summary fields. AWOL role check requires a live
        // guild member; ex-members show "—".
        var isAwol = guildMember is not null
                     && guildMember.Roles.Any(r =>
                         r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

        embed.AddField("Current Rank",
            rank is null ? "—" : EscapeMarkdown(rank.RankName),
            inline: true);

        embed.AddField("AWOL Now",
            guildMember is null ? "—" : (isAwol ? "Yes" : "No"),
            inline: true);

        embed.AddField($"Last {ActivityWindowDays}d",
            $"💬 {messageCount} msgs\n🎤 {voiceHours:F1}h",
            inline: true);

        if (rank is not null)
        {
            embed.AddField($"Events at {EscapeMarkdown(rank.RankName)}",
                eventsAtRank.ToString(),
                inline: true);
        }

        embed.AddField("Lifetime Events",
            totalRealEvents.ToString(),
            inline: true);

        embed.WithFooter($"User ID: {targetUser.Id}");
        embed.WithCurrentTimestamp();

        return embed.Build();
    }

    /// <summary>
    /// Officer+ permission check. SyncWithHandlers:
    /// SlashCommandHandler.HasElevatedPermissions and
    /// HealthCommandHandler.HasElevatedPermissions — keep this body
    /// identical to those.
    /// </summary>
    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;
        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renders a Discord timestamp tag from a UTC DateTime. Discord clients
    /// format the value in the viewer's local time zone. Style 'f' is the
    /// "long date + short time" preset (e.g. "23 May 2026 14:30").
    /// </summary>
    private static string DiscordTimestamp(DateTime utc, char style) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:{style}>";

    /// <summary>
    /// Escapes Discord markdown so a rank name, invite label, or other
    /// user-derived string containing underscores or asterisks doesn't get
    /// rendered as italics/bold. Mirrors InviteCommandHandler.EscapeMarkdown.
    /// </summary>
    private static string EscapeMarkdown(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '*' or '_' or '`' or '~' or '\\' or '|' or '>') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed record TimelineEntry(DateTime UtcTime, string Icon, string Description);
}
