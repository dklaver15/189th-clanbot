using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// /leads slash command surface. Two subcommands:
///
///   /leads stats   — rolling-window rollup: per-subreddit counts and
///                    overall conversion funnel (New → Claimed → Contacted
///                    → Joined). The data flywheel: this is what tells us
///                    which subreddits actually convert vs. which look
///                    busy but never produce members.
///
///   /leads recent  — the last N surfaced leads with current status, for
///                    "what came in today?" / "did Discord drop one?"
///                    spot checks.
///
/// ── Permissions ──
/// Hardcoded MAJ floor mirrors AttendanceCommandHandler — "officer-only"
/// in code so a config drift can never widen access. Server Administrator
/// bypasses the rank check (same convention every command in the bot uses).
/// Responses are ephemeral; the leads channel is already perms-gated, but
/// /leads can be invoked from anywhere, so we don't leak there either.
/// </summary>
public sealed class RedditLeadsCommandHandler
{
    public const string CommandName = "leads";

    /// <summary>
    /// Hardcoded floor — same defensive pattern as AttendanceCommandHandler
    /// and EventCreditCommandHandler. Config drift can't drop access below
    /// MAJ for officer-only commands.
    /// </summary>
    private const string MinRankFloor = "MAJ";

    private const int RecentDefaultCount = 10;
    private const int RecentMaxCount = 25;

    private readonly IServiceProvider _services;
    private readonly ILogger<RedditLeadsCommandHandler> _logger;
    private readonly BotConfig _config;

    public RedditLeadsCommandHandler(
        IServiceProvider services,
        ILogger<RedditLeadsCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
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
                if (command.HasResponded)
                    await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                else
                    await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* swallowed */ }
        }
    }

    private async Task DispatchAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.User is not SocketGuildUser caller || !HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        if (sub is null)
        {
            await command.FollowupAsync(
                "Missing subcommand. Try `/leads stats` or `/leads recent`.",
                ephemeral: true);
            return;
        }

        switch (sub.Name)
        {
            case "stats":
                await HandleStatsAsync(command, sub);
                break;

            case "recent":
                await HandleRecentAsync(command, sub);
                break;

            default:
                await command.FollowupAsync($"Unknown subcommand `{sub.Name}`.", ephemeral: true);
                break;
        }
    }

    // ── /leads stats ─────────────────────────────────────────────────

    private async Task HandleStatsAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        var window = (sub.Options.FirstOrDefault(o => o.Name == "window")?.Value as string) ?? "30d";
        var (since, label) = ResolveWindow(window);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        IQueryable<RedditLead> q = db.RedditLeads.AsNoTracking();
        if (since is not null)
            q = q.Where(l => l.DiscoveredAtUtc >= since.Value);

        var leads = await q.ToListAsync();

        if (leads.Count == 0)
        {
            await command.FollowupAsync($"No leads in window: **{label}**.", ephemeral: true);
            return;
        }

        // Status counts.
        var byStatus = leads.GroupBy(l => l.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        int Get(LeadStatus s) => byStatus.GetValueOrDefault(s, 0);

        var total      = leads.Count;
        var newCt      = Get(LeadStatus.New);
        var claimedCt  = Get(LeadStatus.Claimed);
        var contactCt  = Get(LeadStatus.Contacted);
        var joinedCt   = Get(LeadStatus.Joined);
        var declinedCt = Get(LeadStatus.Declined);
        var norespCt   = Get(LeadStatus.NoResponse);
        var skippedCt  = Get(LeadStatus.Skipped);

        // Funnel — counts include later stages, since a Joined lead also
        // implicitly passed through Claimed and (sometimes) Contacted.
        var everClaimed   = total - newCt - skippedCt;
        var everContacted = contactCt + joinedCt + declinedCt + norespCt;
        var everJoined    = joinedCt;

        var bySub = leads
            .GroupBy(l => l.Subreddit)
            .Select(g => new
            {
                Sub      = g.Key,
                Total    = g.Count(),
                Joined   = g.Count(l => l.Status == LeadStatus.Joined),
                Working  = g.Count(l => l.Status == LeadStatus.Claimed || l.Status == LeadStatus.Contacted),
            })
            .OrderByDescending(x => x.Joined)
            .ThenByDescending(x => x.Total)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("**Funnel**");
        sb.AppendLine($"Surfaced: **{total}**");
        sb.AppendLine($"Claimed by an officer: **{everClaimed}** ({Pct(everClaimed, total)})");
        sb.AppendLine($"Contacted: **{everContacted}** ({Pct(everContacted, total)})");
        sb.AppendLine($"Joined: **{everJoined}** ({Pct(everJoined, total)})");
        sb.AppendLine();
        sb.AppendLine("**Outcomes**");
        sb.AppendLine($"🎉 Joined: **{joinedCt}**  ·  👋 Declined: **{declinedCt}**  ·  💤 No response: **{norespCt}**  ·  ⏭️ Skipped: **{skippedCt}**");
        sb.AppendLine($"✋ Currently claimed: **{claimedCt}**  ·  ✉️ Contacted: **{contactCt}**  ·  🎯 New unclaimed: **{newCt}**");
        sb.AppendLine();
        sb.AppendLine("**By subreddit**");
        foreach (var row in bySub)
        {
            sb.AppendLine($"r/{row.Sub} — {row.Total} surfaced · {row.Joined} joined · {row.Working} working");
        }

        var embed = new EmbedBuilder()
            .WithTitle($"📊 Reddit Lead Stats — {label}")
            .WithColor(Color.Blue)
            .WithDescription(sb.ToString().Trim())
            .WithFooter($"Window: {label}  ·  As of {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    // ── /leads recent ────────────────────────────────────────────────

    private async Task HandleRecentAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        var requested = sub.Options.FirstOrDefault(o => o.Name == "count")?.Value as long? ?? RecentDefaultCount;
        var count = (int)Math.Clamp(requested, 1, RecentMaxCount);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var rows = await db.RedditLeads.AsNoTracking()
            .OrderByDescending(l => l.DiscoveredAtUtc)
            .Take(count)
            .ToListAsync();

        if (rows.Count == 0)
        {
            await command.FollowupAsync("No leads have been surfaced yet.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var lead in rows)
        {
            var icon = lead.Status switch
            {
                LeadStatus.New        => "🎯",
                LeadStatus.Claimed    => "✋",
                LeadStatus.Contacted  => "✉️",
                LeadStatus.Joined     => "🎉",
                LeadStatus.Declined   => "👋",
                LeadStatus.NoResponse => "💤",
                LeadStatus.Skipped    => "⏭️",
                _                     => "•",
            };

            var ts = $"<t:{new DateTimeOffset(DateTime.SpecifyKind(lead.DiscoveredAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>";
            var orphanFlag = lead.DiscordMessageId == 0 ? "  ⚠️ no embed" : "";

            // Title bounded so a long Reddit title doesn't blow the embed
            // length budget across many rows.
            var title = lead.Title.Length > 80 ? lead.Title[..79] + "…" : lead.Title;

            sb.AppendLine($"{icon} **{lead.Status}** — r/{lead.Subreddit} · {ts}{orphanFlag}");
            sb.AppendLine($"   [{Escape(title)}]({lead.Url}) — u/{lead.AuthorUsername}");
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🕒 Recent Reddit Leads (last {rows.Count})")
            .WithColor(Color.Blue)
            .WithDescription(sb.ToString().Trim())
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (DateTime? Since, string Label) ResolveWindow(string raw) => raw switch
    {
        "7d"  => (DateTime.UtcNow.AddDays(-7),  "Last 7 days"),
        "30d" => (DateTime.UtcNow.AddDays(-30), "Last 30 days"),
        "90d" => (DateTime.UtcNow.AddDays(-90), "Last 90 days"),
        "all" => (null,                          "All time"),
        _     => (DateTime.UtcNow.AddDays(-30), "Last 30 days"),
    };

    private static string Pct(int part, int total) =>
        total == 0 ? "0%" : $"{(part * 100.0 / total):0.#}%";

    private static string Escape(string s) =>
        s.Replace("[", "\\[").Replace("]", "\\]");

    /// <summary>
    /// MAJ+ rank gate. Same role-list-index approach as AttendanceCommandHandler;
    /// Server Administrator bypasses.
    /// </summary>
    private bool HasMinRankFloor(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r => r.Equals(MinRankFloor, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    /// <summary>
    /// Slash-command shape. Keeps the option list next to the code that
    /// consumes it — same pattern as InviteCommandHandler.BuildCommand.
    /// </summary>
    public static SlashCommandProperties BuildCommand()
    {
        return new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Reddit recruitment lead stats and recent feed (Officer+)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("stats")
                .WithDescription("Funnel + per-subreddit breakdown")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("window")
                    .WithDescription("Time window. Default: 30d.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .AddChoice("Last 7 days",  "7d")
                    .AddChoice("Last 30 days", "30d")
                    .AddChoice("Last 90 days", "90d")
                    .AddChoice("All time",     "all")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("recent")
                .WithDescription("Show the most recently surfaced leads")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("count", ApplicationCommandOptionType.Integer,
                    $"How many to show (1–{RecentMaxCount}, default {RecentDefaultCount})",
                    isRequired: false))
            .Build();
    }
}
