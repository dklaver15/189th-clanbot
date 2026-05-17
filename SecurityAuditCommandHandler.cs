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
/// Handles /security-audit — read side of the SecurityAuditRecords table
/// written by the server-protection feature set (account-age gate today;
/// invite-link filter, audit-log watcher, nickname impersonation,
/// webhook audit, token-grabber scanner planned).
///
/// ── Why one command with filters, not subcommands ──
/// /usage-stats uses subcommands (top / user / command) because each shape
/// is a genuinely different query (leaderboard aggregate vs. per-user
/// history vs. parameter breakdown). The security-audit reads are all the
/// same query with a different WHERE clause — "recent rows" filtered by
/// optional (user, feature, time window, row cap). Subcommands here would
/// add ceremony without adding clarity, so we collapse to one command
/// with all-optional parameters. Discord's slash-command UI surfaces them
/// as a tab-able list, same as any complex command.
///
/// ── Permission model ──
/// BG+ hardcoded floor (same defensive pattern as
/// UsageStatsCommandHandler.MinRankFloor and AttendanceCommandHandler) —
/// config can't drift below intent. Server Administrators bypass.
/// Higher floor than /usage-stats (MAJ+) by design: this surface
/// exposes raw join-time forensics including the usernames and IDs of
/// kicked/alerted accounts, which is sensitive enough that access is
/// restricted to general-grade leadership rather than the broader
/// officer corps.
///
/// ── No retention ceiling (yet) ──
/// SecurityAuditRecords is unbounded today. No equivalent of
/// CommandUsagePruneService exists for this table because volume is
/// expected to be low (single-digit-per-day at most once all six
/// features are live, dominated by trolls during raid windows). If the
/// table grows enough to matter we'll add a 1-year prune service then;
/// until then, days is clamped to <see cref="MaxDays"/> as a sanity
/// floor against runaway result sets, not as a retention boundary.
/// </summary>
public sealed class SecurityAuditCommandHandler
{
    public const string CommandName = "security-audit";

    /// <summary>
    /// Hardcoded minimum rank — BG+. Higher than /usage-stats (MAJ+)
    /// because this command exposes raw forensics (kicked usernames,
    /// account IDs, error messages) that should stay with general-grade
    /// leadership rather than the broader officer corps.
    /// </summary>
    private const string MinRankFloor = "BG";

    /// <summary>
    /// Upper bound on the days parameter. Not a retention boundary (the
    /// table doesn't prune) — just a sanity cap so a fat-fingered request
    /// can't try to render a year of rows into a single embed.
    /// </summary>
    private const int MaxDays     = 90;
    private const int DefaultDays = 7;

    /// <summary>Upper bound on rows returned. Hard cap for embed-size safety.</summary>
    private const int MaxLimit     = 25;
    private const int DefaultLimit = 10;

    /// <summary>
    /// Known Feature values. The handler accepts any string in the column —
    /// this list only drives the slash-command choices so officers don't
    /// have to remember the canonical strings. Add a new entry when a new
    /// server-protection feature starts writing rows.
    /// </summary>
    public static readonly (string Value, string Display)[] KnownFeatures =
    {
        ("AccountAgeGate",        "Account-Age Gate"),
        ("InviteLinkFilter",      "Invite Link Filter"),
        ("AuditLogWatcher",       "Audit Log Watcher"),
        ("NicknameImpersonation", "Nickname Impersonation"),
        ("WebhookAudit",          "Webhook Audit"),
        ("TokenGrabberScanner",   "Token-Grabber Scanner"),
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<SecurityAuditCommandHandler> _logger;
    private readonly BotConfig _config;

    public SecurityAuditCommandHandler(
        IServiceProvider services,
        ILogger<SecurityAuditCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
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

        // ── Parse + clamp optional parameters ─────────────────────────────
        // Discord.NET passes Integer-typed slash options as boxed long. The
        // `as long?` cast pattern is the same one UsageStatsCommandHandler
        // uses for its days option — type-safe whether the option was
        // provided or omitted.
        SocketUser? userFilter = null;
        string? featureFilter  = null;
        int days  = DefaultDays;
        int limit = DefaultLimit;

        foreach (var opt in command.Data.Options)
        {
            switch (opt.Name)
            {
                case "user":
                    userFilter = opt.Value as SocketUser;
                    break;
                case "feature":
                    featureFilter = opt.Value as string;
                    break;
                case "days":
                    if (opt.Value is long d)
                        days = (int)Math.Clamp(d, 1L, (long)MaxDays);
                    break;
                case "limit":
                    if (opt.Value is long l)
                        limit = (int)Math.Clamp(l, 1L, (long)MaxLimit);
                    break;
            }
        }

        var cutoff  = DateTime.UtcNow.AddDays(-days);
        var guildId = command.GuildId!.Value;

        // ── Query ─────────────────────────────────────────────────────────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var query = db.SecurityAuditRecords
            .Where(r => r.GuildId == guildId && r.OccurredAt >= cutoff);

        if (userFilter is not null)
            query = query.Where(r => r.UserId == userFilter.Id);

        if (!string.IsNullOrWhiteSpace(featureFilter))
            query = query.Where(r => r.Feature == featureFilter);

        var rows = await query
            .OrderByDescending(r => r.OccurredAt)
            .Take(limit)
            .ToListAsync();

        // Total count (unclamped) so the embed can say "showing N of M".
        var totalInWindow = await query.CountAsync();

        // ── Render ────────────────────────────────────────────────────────
        var embed = BuildEmbed(rows, totalInWindow, userFilter, featureFilter, days, limit);
        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private static EmbedBuilder BuildEmbed(
        List<SecurityAuditRecord> rows,
        int totalInWindow,
        SocketUser? userFilter,
        string? featureFilter,
        int days,
        int limit)
    {
        var filters = new List<string>();
        if (userFilter is not null)            filters.Add($"user: {userFilter.Mention}");
        if (featureFilter is not null)         filters.Add($"feature: `{featureFilter}`");
        filters.Add($"window: {days}d");
        filters.Add($"limit: {limit}");

        var embed = new EmbedBuilder()
            .WithColor(rows.Count == 0 ? Color.LightGrey : Color.Blue)
            .WithTitle("🛡️ Security Audit")
            .WithDescription("Filters — " + string.Join(" · ", filters))
            .WithFooter($"ClanGuard • Showing {rows.Count} of {totalInWindow} row(s) in window")
            .WithCurrentTimestamp();

        if (rows.Count == 0)
        {
            embed.AddField("No events", "Nothing matched. Either the window is empty or the filters are too tight.");
            return embed;
        }

        // One field per row keeps each event individually readable and stays
        // well under Discord's 25-field-per-embed and 1024-chars-per-field
        // caps — even on a verbose Details string. The hard MaxLimit of 25
        // is set against the 25-field embed ceiling.
        foreach (var r in rows)
        {
            var name  = BuildRowHeader(r);
            var value = BuildRowBody(r);
            embed.AddField(name, value, inline: false);
        }

        return embed;
    }

    /// <summary>
    /// Per-row header: timestamp + feature + action. Compact, scannable.
    /// </summary>
    private static string BuildRowHeader(SecurityAuditRecord r)
    {
        // SQLite via EF Core loses DateTimeKind on read; explicitly mark UTC
        // before constructing the DateTimeOffset to avoid Local-zone
        // interpretation. Same pattern as InviteCommandHandler and
        // PromoEligibilityCommandHandler.
        var utc  = DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc);
        var unix = new DateTimeOffset(utc).ToUnixTimeSeconds();
        var icon = ActionIcon(r.Action);
        return $"{icon} <t:{unix}:R> · `{r.Feature}` · **{r.Action}**";
    }

    /// <summary>
    /// Per-row body: user (if any), then details, then error (if any).
    /// Truncates to keep the row well under the 1024-char field limit.
    /// </summary>
    private static string BuildRowBody(SecurityAuditRecord r)
    {
        var sb = new StringBuilder();

        if (r.UserId is not null)
        {
            sb.Append($"<@{r.UserId.Value}>");
            if (!string.IsNullOrWhiteSpace(r.DisplayName))
                sb.Append($" (`{r.DisplayName}`)");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(r.Details))
        {
            sb.AppendLine(Truncate(r.Details, 600));
        }

        if (!string.IsNullOrWhiteSpace(r.ErrorMessage))
        {
            sb.Append("⚠️ ");
            sb.AppendLine(Truncate(r.ErrorMessage, 300));
        }

        var body = sb.ToString().TrimEnd();
        return string.IsNullOrEmpty(body) ? "—" : body;
    }

    private static string ActionIcon(string action) => action switch
    {
        "Kicked"               => "🚪",
        "Alerted"              => "🟠",
        "UserAlreadyLeft"      => "👋",
        "KickSkippedHierarchy" => "🛑",
        "KickFailed"           => "❌",
        _                      => "•",
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// BG+ rank gate. Same role-list-index pattern as
    /// UsageStatsCommandHandler.HasMinRankFloor. Server Administrators
    /// bypass the rank check entirely.
    /// </summary>
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
    /// Slash-command shape for the OnReadyAsync registration list. Lives on
    /// the handler so the option list stays next to the code that consumes
    /// it — same pattern as UsageStatsCommandHandler.BuildCommand(). Catalog
    /// entry in CommandsCommandHandler.BuildCatalog must stay in sync.
    /// </summary>
    public static SlashCommandProperties BuildCommand()
    {
        var builder = new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Read the server-protection audit log (BG+ only)")
            .AddOption("user", ApplicationCommandOptionType.User,
                "Filter to events involving this member",
                isRequired: false)
            .AddOption(BuildFeatureOption())
            .AddOption("days", ApplicationCommandOptionType.Integer,
                $"Window size in days (1–{MaxDays}, default {DefaultDays})",
                isRequired: false)
            .AddOption("limit", ApplicationCommandOptionType.Integer,
                $"Max rows to show (1–{MaxLimit}, default {DefaultLimit})",
                isRequired: false);

        return builder.Build();
    }

    private static SlashCommandOptionBuilder BuildFeatureOption()
    {
        var option = new SlashCommandOptionBuilder()
            .WithName("feature")
            .WithDescription("Filter to one server-protection feature")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(false);

        foreach (var (value, display) in KnownFeatures)
        {
            option.AddChoice(display, value);
        }

        return option;
    }
}