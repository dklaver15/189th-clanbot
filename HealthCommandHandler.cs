using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /health slash command — a single ephemeral embed that surfaces
/// every operational signal worth checking from a phone at 2am without SSHing
/// into the droplet. Officer+ gated (matches SlashCommandHandler.HasElevatedPermissions
/// verbatim — SyncWithHandlers: SlashCommandHandler.HasElevatedPermissions).
///
/// ── What's on the embed ──
/// • Process — uptime, runtime, assembly version
/// • Discord — gateway state, latency, guild + member counts
/// • Database — file path, file size (DB + WAL), key table row counts
/// • Scheduled jobs — last completed timestamps for auto-promotion, webhook
///   audit, roster export, and SQLite backup (from BotState)
/// • Calendar outbox — pending count, oldest pending row age, most recent
///   LastError if any rows are still retrying
/// • Discord status monitor — last successful poll, dedupe-table counts
///   (total tracked vs. actually live-posted), age of last live post
/// • Operational queues — AWOL records pending notification, pending officer
///   applications, upcoming calendar events
/// • Security — SecurityAuditRecords written in the last 24h, grouped by feature
/// • Footer — version, runtime, and codebase size (hand-written .cs file
///   count + line count, baked in at build time via BuildInfo.g.cs)
///
/// ── Why every datum is local ──
/// /health must always be cheap. Every field reads from a column or table
/// we already own; no external API calls (Drive, Sheets, Calendar, Reddit)
/// happen during the command. The cost of a network call to test "is Sheets
/// alive" is high enough that the field would be wrong by the time it
/// rendered. Instead, the bot stamps BotState columns when each subsystem
/// successfully completes its own scheduled work, and /health just reports
/// what's already on disk. Side benefit: /health works even if the network
/// to Drive/Sheets/Calendar is down — which is exactly when you need it.
///
/// ── Bot start time ──
/// Captured at handler construction via DateTime.UtcNow because the
/// singleton lifecycle means construction happens once during DI
/// container build, before the Discord client connects. Close enough to
/// process start for the use case (informational "how long since the
/// last restart"). If you ever need true process start, swap in
/// Process.GetCurrentProcess().StartTime.ToUniversalTime() — leaving
/// the construction-time variant as default because it doesn't depend
/// on the host honouring StartTime correctly (some container runtimes
/// report container start vs. process start differently).
///
/// ── Catalog entry ──
/// Officer+ tier. NOTE: also keep CommandsCommandHandler.BuildCatalog
/// in sync when this command's gate or description changes.
/// </summary>
public class HealthCommandHandler
{
    public const string CommandName = "health";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<HealthCommandHandler> _logger;
    private readonly DateTime _botStartedUtc;
    private readonly string _assemblyVersion;
    private readonly string _runtimeVersion;

    public HealthCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<HealthCommandHandler> logger)
    {
        _services = services;
        _client = client;
        _config = config.Value;
        _logger = logger;
        _botStartedUtc = DateTime.UtcNow;

        var asm = Assembly.GetExecutingAssembly();
        _assemblyVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
        _runtimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Bot health diagnostic — uptime, DB stats, scheduled jobs, queues (Officer+ only)")
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        if (command.User is not SocketGuildUser caller || !HasElevatedPermissions(caller))
        {
            await command.RespondAsync(
                "⛔ This command is restricted to officers.",
                ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        try
        {
            var embed = await BuildHealthEmbedAsync();
            await command.FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/health failed");
            await command.FollowupAsync(
                $"❌ /health failed: `{Truncate(ex.Message, 800)}`",
                ephemeral: true);
        }
    }

    private async Task<Embed> BuildHealthEmbedAsync()
    {
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── BotState ──
        var state = await db.BotStates.AsNoTracking().FirstOrDefaultAsync();

        // ── Discord ──
        var gatewayState = _client.ConnectionState.ToString();
        var latency = _client.Latency;
        var guild = _client.Guilds.FirstOrDefault();
        var guildLabel = guild is null
            ? "—"
            : $"{guild.Name} ({guild.MemberCount} members)";

        // ── Database file size ──
        var dbPath = ResolveDbPath();
        var (dbBytes, walBytes) = MeasureDbFiles(dbPath);

        // ── Row counts (cheap COUNT(*) — SQLite uses internal counters where it can) ──
        var msgCount   = await db.MessageEvents.CountAsync();
        var voiceCount = await db.VoiceSessions.CountAsync();
        var eventCount = await db.CalendarEvents.CountAsync();
        var attCount   = await db.EventAttendances.CountAsync();

        // ── Calendar outbox ──
        var pendingOutboxRows = await db.CalendarOutbox
            .Where(r => r.CompletedAt == null)
            .Select(r => new { r.CreatedAt, r.AttemptCount, r.LastError, r.NextAttemptAt })
            .ToListAsync();
        var outboxPending = pendingOutboxRows.Count;
        var outboxOldest  = pendingOutboxRows.Count > 0
            ? pendingOutboxRows.Min(r => r.CreatedAt)
            : (DateTime?)null;
        var outboxLatestError = pendingOutboxRows
            .Where(r => !string.IsNullOrEmpty(r.LastError))
            .OrderByDescending(r => r.AttemptCount)
            .Select(r => r.LastError)
            .FirstOrDefault();

        // ── Discord status monitor ──
        // Liveness comes from BotState (stamped on every successful poll).
        // Counts come from the dedupe table — total tracked vs. actually
        // posted to the channel (Seeded = false means we sent a message).
        var statusTotal  = await db.DiscordStatusIncidentUpdates.CountAsync();
        var statusPosted = await db.DiscordStatusIncidentUpdates
            .CountAsync(u => !u.Seeded);
        var statusLastPostedAt = await db.DiscordStatusIncidentUpdates
            .Where(u => !u.Seeded)
            .OrderByDescending(u => u.PostedAt)
            .Select(u => (DateTime?)u.PostedAt)
            .FirstOrDefaultAsync();

        // ── Operational queues ──
        var pendingAwol = await db.AwolRecords
            .CountAsync(r => !r.NotificationSent);
        var pendingApplications = await db.OfficerApplications
            .CountAsync(a => a.Status == OfficerApplicationStatus.Pending);
        var upcomingEvents = await db.CalendarEvents
            .CountAsync(e => e.StartUtc > now && e.StartUtc < now.AddDays(7));

        // ── Security (last 24h, grouped) ──
        var since24h = now.AddHours(-24);
        var securityRows = await db.SecurityAuditRecords
            .Where(r => r.OccurredAt >= since24h)
            .GroupBy(r => r.Feature)
            .Select(g => new { Feature = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();
        var securityTotal = securityRows.Sum(r => r.Count);

        // ── Build embed ──
        var embed = new EmbedBuilder()
            .WithTitle("🩺 ClanGuard Health")
            .WithColor(PickColor(outboxPending, pendingOutboxRows.Count > 0 && outboxOldest.HasValue
                                                  && (now - outboxOldest.Value) > TimeSpan.FromHours(24),
                                state?.LastSqliteBackupError))
            .WithTimestamp(now)
            .WithFooter($"v{_assemblyVersion} · {_runtimeVersion} · " +
                        $"{BuildInfo.SourceFileCount} files · {BuildInfo.SourceLineCount:N0} LOC" +
                        (BuildInfo.CommitCount > 0 ? $" · {BuildInfo.CommitCount:N0} commits" : ""));

        // Process
        embed.AddField("⏱️ Uptime",
            FormatDuration(now - _botStartedUtc),
            inline: true);
        embed.AddField("🌐 Gateway",
            $"{StateIcon(gatewayState)} {gatewayState}\n{latency}ms",
            inline: true);
        embed.AddField("🏰 Guild",
            guildLabel,
            inline: true);

        // Database
        embed.AddField("💾 Database",
            $"`{ShortenPath(dbPath)}`\n" +
            $"DB: **{FormatBytes(dbBytes)}** · WAL: **{FormatBytes(walBytes)}**",
            inline: false);
        embed.AddField("📊 Row counts",
            $"Messages: **{msgCount:N0}** · Voice sessions: **{voiceCount:N0}**\n" +
            $"Calendar events: **{eventCount:N0}** · Attendance rows: **{attCount:N0}**",
            inline: false);

        // Scheduled jobs (all from BotState)
        var sb = new StringBuilder();
        sb.AppendLine($"Auto-promotion: {FormatLastRun(state?.LastAutoPromotionCompletedUtc, now)}");
        sb.AppendLine($"Webhook audit: {FormatLastRun(state?.LastWebhookAuditScanCompletedUtc, now)}");
        sb.AppendLine($"Roster export: {FormatLastRun(state?.LastRosterExportCompletedUtc, now)}");
        // Weekly cadence, so an 8-day staleness window (one week + a day of
        // grace) instead of the daily default — otherwise a perfectly healthy
        // briefing would show ⚠️ for most of the week.
        sb.AppendLine($"Weekly briefing: {FormatLastRun(state?.LastBriefingCompletedUtc, now, TimeSpan.FromDays(8))}");
        var backupLine = $"SQLite backup: {FormatLastRun(state?.LastSqliteBackupCompletedUtc, now)}";
        if (state?.LastSqliteBackupSizeBytes is long bytes && bytes > 0)
            backupLine += $" ({FormatBytes(bytes)})";
        if (!string.IsNullOrEmpty(state?.LastSqliteBackupError))
            backupLine += $"\n  ⚠️ Last error: `{Truncate(state.LastSqliteBackupError, 180)}`";
        sb.AppendLine(backupLine);
        embed.AddField("⏰ Scheduled jobs", sb.ToString().TrimEnd(), inline: false);

        // Calendar outbox
        var outboxLine = outboxPending == 0
            ? "✅ Empty"
            : $"**{outboxPending}** pending · oldest **{FormatDuration(now - outboxOldest!.Value)}** ago";
        if (!string.IsNullOrEmpty(outboxLatestError))
            outboxLine += $"\n⚠️ Latest error: `{Truncate(outboxLatestError, 200)}`";
        embed.AddField("📤 Calendar outbox", outboxLine, inline: false);

        // Discord status monitor
        // Shape:
        //   Last good poll: 3m ago            ← liveness from BotState
        //   Tracked: 183 · Live-posted: 21    ← dedupe-table counts
        //   Last live post: …                 ← only when statusPosted > 0
        // While polls are failing the first line also carries the consecutive-
        // failure count and how long ago the most recent attempt was, and the
        // error text drops onto its own line.
        //
        // The count is the point. Without it a single stale error looks exactly
        // like an ongoing outage, which is how this monitor sat "broken" on
        // /health for three days in July 2026 while working perfectly.
        //
        // The error is gated on failures > 0 rather than on the string being
        // non-empty. Success clears both together, so they can only disagree on
        // a row written by the older code, and in that case the count is the
        // one telling the truth.
        var statusFailures = state?.DiscordStatusPollConsecutiveFailures ?? 0;

        var statusLine = state?.LastDiscordStatusPollCompletedUtc.HasValue == true
            ? $"Last good poll: **{FormatDuration(now - state.LastDiscordStatusPollCompletedUtc.Value)}** ago"
            : "No polls completed yet";

        if (statusFailures > 0)
        {
            statusLine += $" · ⚠️ **{statusFailures:N0}** consecutive failure(s)";
            if (state?.LastDiscordStatusPollAttemptUtc.HasValue == true)
                statusLine +=
                    $", last tried **{FormatDuration(now - state.LastDiscordStatusPollAttemptUtc.Value)}** ago";
            if (!string.IsNullOrEmpty(state?.LastDiscordStatusPollError))
                statusLine += $"\n⚠️ `{Truncate(state.LastDiscordStatusPollError, 120)}`";
        }

        statusLine += $"\nTracked: **{statusTotal}** · Live-posted: **{statusPosted}**";
        if (statusPosted > 0 && statusLastPostedAt.HasValue)
            statusLine += $"\nLast live post: **{FormatDuration(now - statusLastPostedAt.Value)}** ago";
        embed.AddField("📡 Discord status monitor", statusLine, inline: false);

        // Queues
        embed.AddField("📋 Queues",
            $"AWOL pending notification: **{pendingAwol}**\n" +
            $"Officer apps awaiting review: **{pendingApplications}**\n" +
            $"Events upcoming (next 7d): **{upcomingEvents}**",
            inline: false);

        // Security (last 24h)
        var secLine = securityTotal == 0
            ? "✅ Quiet (last 24h)"
            : string.Join("\n", securityRows.Select(r => $"• {r.Feature}: **{r.Count}**"));
        embed.AddField($"🛡️ Security audit (24h, {securityTotal} total)", secLine, inline: false);

        return embed.Build();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        // SyncWithHandlers: SlashCommandHandler.HasElevatedPermissions
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;
        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static string ResolveDbPath()
    {
        // Mirrors the path construction in Program.cs. Kept local because /health
        // is otherwise self-contained and dragging a config value through DI just
        // for this one read isn't worth the wiring.
        return Path.Combine(AppContext.BaseDirectory, "data", "clanguard.db");
    }

    private static (long dbBytes, long walBytes) MeasureDbFiles(string dbPath)
    {
        long db = 0, wal = 0;
        try { if (File.Exists(dbPath)) db = new FileInfo(dbPath).Length; } catch { /* file system races are fine to ignore */ }
        try
        {
            var walPath = dbPath + "-wal";
            if (File.Exists(walPath)) wal = new FileInfo(walPath).Length;
        }
        catch { }
        return (db, wal);
    }

    private static string ShortenPath(string path)
    {
        // Trim noisy absolute prefix when running inside the container so the
        // path fits comfortably on a phone screen.
        const string prefix = "/app/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static string FormatLastRun(DateTime? lastRunUtc, DateTime now, TimeSpan? staleAfter = null)
    {
        if (!lastRunUtc.HasValue) return "—  *never*";
        var ago = now - lastRunUtc.Value;
        var icon = ago < (staleAfter ?? TimeSpan.FromHours(36)) ? "✅" : "⚠️";
        return $"{icon} {FormatDuration(ago)} ago";
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24)
            return d.Minutes == 0
                ? $"{(int)d.TotalHours}h"
                : $"{(int)d.TotalHours}h {d.Minutes}m";
        return d.Hours == 0
            ? $"{(int)d.TotalDays}d"
            : $"{(int)d.TotalDays}d {d.Hours}h";
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:0.#} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024.0:0.##} MB";
        return $"{b / 1024.0 / 1024.0 / 1024.0:0.##} GB";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static string StateIcon(string state) =>
        state.Equals("Connected", StringComparison.OrdinalIgnoreCase) ? "🟢"
        : state.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ? "🟡"
        : "🔴";

    /// <summary>
    /// Picks the embed color based on red flags. Yellow when there's pending
    /// queue work that's getting old; red when a backup error is on the record.
    /// </summary>
    private static Color PickColor(int outboxPending, bool outboxStale, string? backupError)
    {
        if (!string.IsNullOrEmpty(backupError)) return new Color(220, 70, 70);
        if (outboxStale) return new Color(220, 70, 70);
        if (outboxPending > 0) return new Color(230, 180, 60);
        return new Color(80, 170, 110);
    }
}