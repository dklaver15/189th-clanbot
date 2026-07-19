using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns every Palworld slash command against the clan's DatHost server.
///
///   • /palworld-status       — live server + who's online          (everyone)
///   • /palworld-playtime     — hours played, per member or player  (everyone)
///   • /palworld-leaderboard  — top playtime / levels               (everyone)
///   • /palworld-link         — bind a Discord member to a player   (everyone; self)
///   • /palworld-admin        — announce/kick/ban/unban/save/restart (Palworld Mod)
///
/// ── Why the admin actions are subcommands ──
/// Grouping them under one command keeps six destructive verbs behind a single
/// registration and a single permission check, and means the Admin Password never
/// has to leave the bot: the mods moderate the game server from Discord instead of
/// being handed a credential that can shut it down.
///
/// The gate is the "Palworld Mod" ROLE (<see cref="BotConfig.PalworldAdminRoleId"/>),
/// not a rank floor — see <see cref="HasAdminRole"/>.
///
/// ── Gateway safety ──
/// Every command here hits the network (the Palworld REST API), and Discord.NET
/// runs handlers ON the gateway task — a slow call would stall the bot's heartbeat.
/// So the handler dispatches to Task.Run and returns immediately, the same rule
/// that came out of /health freezing during a long purge.
///
/// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (permission labels).
/// </summary>
public class PalworldCommandHandler
{
    private const int MaxLeaderboardRows = 10;

    private static readonly string[] CommandNames =
    {
        "palworld-status",
        "palworld-playtime",
        "palworld-leaderboard",
        "palworld-link",
        "palworld-performance",
        "palworld-admin",
    };

    private readonly ILogger<PalworldCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly IServiceProvider _services;
    private readonly PalworldApiService _api;

    public PalworldCommandHandler(
        ILogger<PalworldCommandHandler> logger,
        IOptions<BotConfig> config,
        IServiceProvider services,
        PalworldApiService api)
    {
        _logger = logger;
        _config = config.Value;
        _services = services;
        _api = api;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStatusCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-status")
            .WithDescription("Live status of the clan's Palworld server — who's on, uptime, in-game day")
            .Build();

    public static SlashCommandProperties BuildPlaytimeCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-playtime")
            .WithDescription("How long someone has played on the clan's Palworld server")
            .AddOption("user", ApplicationCommandOptionType.User,
                "The member to look up (defaults to you). Must be linked — see /palworld-link",
                isRequired: false)
            .AddOption("name", ApplicationCommandOptionType.String,
                "Look up by in-game player name instead of a Discord member",
                isRequired: false)
            .Build();

    public static SlashCommandProperties BuildLeaderboardCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-leaderboard")
            .WithDescription("Top Palworld players in the clan by playtime or level")
            // NOTE: building_count is intentionally not a sort option — Palworld
            // attributes structures to the guild, not the individual, so it reads 0
            // for every player on a shared-guild clan server. See PalworldSession.
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("sort")
                .WithDescription("What to rank by (default playtime)")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .AddChoice("playtime", "playtime")
                .AddChoice("level", "level"))
            .Build();

    public static SlashCommandProperties BuildLinkCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-link")
            .WithDescription("Link your Discord account to your Palworld character (you must be online in-game)")
            .AddOption("name", ApplicationCommandOptionType.String,
                "Your in-game character name, exactly as it appears in game", isRequired: true)
            .AddOption("user", ApplicationCommandOptionType.User,
                "Link on someone else's behalf (officers only)", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildPerformanceCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-performance")
            .WithDescription("Server FPS history — is the lag caused by player count or by uptime?")
            .AddOption("hours", ApplicationCommandOptionType.Integer,
                "How far back to look (default 24, max 168)", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildAdminCommand() =>
        new SlashCommandBuilder()
            .WithName("palworld-admin")
            .WithDescription("Administer the Palworld server — announce, kick, ban, save, restart (Palworld Mod)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("announce")
                .WithDescription("Broadcast a message to everyone in-game")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("message", ApplicationCommandOptionType.String, "What to say in-game", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("kick")
                .WithDescription("Kick a player who is currently online")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("player", ApplicationCommandOptionType.String,
                    "In-game name of an online player, or a raw user id", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String,
                    "Shown to the player", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("ban")
                .WithDescription("Ban a player from the server")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("player", ApplicationCommandOptionType.String,
                    "In-game name of an online player, or a raw user id", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String,
                    "Shown to the player", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("unban")
                .WithDescription("Lift a ban")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user_id", ApplicationCommandOptionType.String,
                    "The player's raw user id (e.g. steam_0110000…)", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("save")
                .WithDescription("Force a world save")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("restart")
                .WithDescription("Save, warn players, then shut down (DatHost brings it back up)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("seconds", ApplicationCommandOptionType.Integer,
                    "Warning countdown before shutdown (default 60)", isRequired: false)
                .AddOption("message", ApplicationCommandOptionType.String,
                    "Warning shown in-game", isRequired: false))
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!CommandNames.Contains(command.Data.Name)) return Task.CompletedTask;

        // Offload immediately: these handlers all make an outbound HTTP call, and
        // awaiting one on the gateway task is what froze /health during a purge.
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling /{Command} for {User}",
                    command.Data.Name, command.User.Username);
                try
                {
                    if (command.HasResponded)
                        await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                    else
                        await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                }
                catch { /* interaction already expired — nothing more to do */ }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "palworld-status":      await HandleStatusAsync(command); break;
            case "palworld-playtime":    await HandlePlaytimeAsync(command); break;
            case "palworld-leaderboard": await HandleLeaderboardAsync(command); break;
            case "palworld-link":        await HandleLinkAsync(command); break;
            case "palworld-performance": await HandlePerformanceAsync(command); break;
            case "palworld-admin":       await HandleAdminAsync(command); break;
        }
    }

    // ─── /palworld-status ────────────────────────────────────────────────────

    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        if (!await PassesMemberGateAsync(command)) return;
        await command.DeferAsync(ephemeral: true);

        var info = await _api.GetInfoAsync();
        var metrics = await _api.GetMetricsAsync();
        var players = await _api.GetPlayersAsync();

        // A null read is "unreachable", so say that rather than "0 players online".
        if (info is null && metrics is null && players is null)
        {
            await command.FollowupAsync(
                "🔴 The Palworld server isn't responding — it's most likely offline or restarting.",
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🌴 {(string.IsNullOrWhiteSpace(info?.ServerName) ? "Palworld server" : info!.ServerName)}")
            .WithColor(new Color(0x4FB477));

        if (metrics is not null)
        {
            embed.AddField("Players", $"{metrics.CurrentPlayerNum}/{metrics.MaxPlayerNum}", true);
            embed.AddField("Uptime", PalworldPresenceService.Humanize(TimeSpan.FromSeconds(metrics.Uptime)), true);
            embed.AddField("In-game day", metrics.Days.ToString(), true);
            embed.AddField("Server FPS", metrics.ServerFps.ToString(), true);
            embed.AddField("Base camps", metrics.BaseCampNum.ToString(), true);
        }

        if (players is { Count: > 0 })
        {
            var lines = players
                .OrderByDescending(p => p.Level)
                .Select(p => $"• **{Escape(p.Name)}** — level {p.Level}, {p.Ping}ms");
            embed.AddField("Online now", string.Join("\n", lines));
        }
        else if (players is not null)
        {
            embed.AddField("Online now", "_Nobody's on right now._");
        }

        if (!string.IsNullOrWhiteSpace(info?.Version))
            embed.WithFooter($"Palworld {info!.Version}");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /palworld-playtime ──────────────────────────────────────────────────

    private async Task HandlePlaytimeAsync(SocketSlashCommand command)
    {
        if (!await PassesMemberGateAsync(command)) return;
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is not ulong guildId)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var targetUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var rawName = command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        string label;
        List<PalworldSession> sessions;

        if (!string.IsNullOrWhiteSpace(rawName))
        {
            // Name path: no link required, so it works for anyone who's ever played
            // — including people who aren't in the Discord at all.
            var name = rawName.Trim();
            sessions = await db.PalworldSessions
                .Where(s => s.PlayerName == name)
                .ToListAsync();
            label = $"**{Escape(name)}**";
        }
        else
        {
            var discordId = targetUser?.Id ?? command.User.Id;
            var link = await db.PalworldLinks
                .FirstOrDefaultAsync(l => l.GuildId == guildId && l.DiscordUserId == discordId);

            if (link is null)
            {
                var who = targetUser is null ? "You aren't" : $"<@{discordId}> isn't";
                await command.FollowupAsync(
                    $"{who} linked to a Palworld character yet. " +
                    "Hop on the server and run `/palworld-link name:<your in-game name>`.",
                    ephemeral: true);
                return;
            }

            sessions = await db.PalworldSessions
                .Where(s => s.PalworldUserId == link.PalworldUserId)
                .ToListAsync();
            label = $"<@{discordId}> (**{Escape(link.PalworldName)}**)";
        }

        if (sessions.Count == 0)
        {
            await command.FollowupAsync($"{label} hasn't been seen on the Palworld server yet.", ephemeral: true);
            return;
        }

        var total = sessions.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
        var lastSeen = sessions.Max(s => s.EndedUtc ?? s.LastSeenUtc);
        var bestLevel = sessions.Max(s => s.Level);
        var isOnline = sessions.Any(s => s.EndedUtc is null);

        var embed = new EmbedBuilder()
            .WithTitle("🌴 Palworld playtime")
            .WithColor(isOnline ? Color.Green : new Color(0x4FB477))
            .WithDescription(label + (isOnline ? "  ·  🟢 online now" : ""))
            .AddField("Total played", PalworldPresenceService.Humanize(total), true)
            .AddField("Sessions", sessions.Count.ToString(), true)
            .AddField("Highest level", bestLevel.ToString(), true)
            .AddField("Last seen", $"<t:{new DateTimeOffset(lastSeen, TimeSpan.Zero).ToUnixTimeSeconds()}:R>", true)
            .WithFooter("Playtime is measured by the bot polling the server, so it's accurate to about a minute.");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /palworld-leaderboard ───────────────────────────────────────────────

    private async Task HandleLeaderboardAsync(SocketSlashCommand command)
    {
        // Gate BEFORE the (public) defer, so a denial is a private response and
        // never leaves a stray public "thinking…" in the channel.
        if (!await PassesMemberGateAsync(command)) return;
        await command.DeferAsync();

        var sort = command.Data.Options.FirstOrDefault(o => o.Name == "sort")?.Value as string ?? "playtime";

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var sessions = await db.PalworldSessions.ToListAsync();
        if (sessions.Count == 0)
        {
            await command.FollowupAsync("Nobody's played on the Palworld server yet.");
            return;
        }

        // Group on the stable user id, but display the most RECENT name — a rename
        // shouldn't split someone into two leaderboard rows.
        var players = sessions
            .GroupBy(s => s.PalworldUserId)
            .Select(g => new
            {
                Name = g.OrderByDescending(s => s.LastSeenUtc).First().PlayerName,
                Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration),
                Level = g.Max(s => s.Level),
            })
            .ToList();

        var (title, ranked) = sort switch
        {
            "level" => ("🏅 Highest level",
                players.OrderByDescending(p => p.Level).ThenByDescending(p => p.Total).ToList()),
            _ => ("⏱️ Most playtime",
                players.OrderByDescending(p => p.Total).ToList()),
        };

        var lines = ranked
            .Take(MaxLeaderboardRows)
            .Select((p, i) => $"`{i + 1,2}.` **{Escape(p.Name)}** — " +
                              $"{PalworldPresenceService.Humanize(p.Total)}  ·  lvl {p.Level}");

        var embed = new EmbedBuilder()
            .WithTitle($"🌴 Palworld leaderboard — {title}")
            .WithColor(new Color(0x4FB477))
            .WithDescription(string.Join("\n", lines))
            .WithFooter($"{players.Count} player(s) tracked");

        await command.FollowupAsync(embed: embed.Build());
    }

    // ─── /palworld-link ──────────────────────────────────────────────────────

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        if (!await PassesMemberGateAsync(command)) return;
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var name = (command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string ?? "").Trim();
        var onBehalfOf = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;

        // Linking someone else is an officer action — otherwise anyone could bind a
        // teammate's character to their own account (or vice versa).
        if (onBehalfOf is not null && onBehalfOf.Id != caller.Id && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                "❌ Only Palworld Mods can link someone else. Leave `user` blank to link yourself.",
                ephemeral: true);
            return;
        }

        var targetId = onBehalfOf?.Id ?? caller.Id;

        // The ONLY way to learn someone's UserId is to see them in the live player
        // list, which is why linking requires being online in-game. Accepting a
        // typed-in id instead would let anyone claim anyone's playtime.
        var players = await _api.GetPlayersAsync();
        if (players is null)
        {
            await command.FollowupAsync(
                "🔴 The Palworld server isn't responding, so I can't verify that character right now. Try again shortly.",
                ephemeral: true);
            return;
        }

        var match = players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var online = players.Count == 0
                ? "_Nobody is online right now._"
                : string.Join(", ", players.Select(p => $"`{p.Name}`"));
            await command.FollowupAsync(
                $"❌ No online player named **{Escape(name)}**. You have to be logged into the server when you link, " +
                $"so I can see your character.\n\nOnline now: {online}",
                ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Re-linking overwrites, in both directions: one Discord account per
        // Palworld identity and vice versa. Clearing the other side prevents two
        // members both claiming the same character.
        var existing = await db.PalworldLinks
            .Where(l => l.GuildId == guildId
                     && (l.DiscordUserId == targetId || l.PalworldUserId == match.UserId))
            .ToListAsync();
        if (existing.Count > 0)
            db.PalworldLinks.RemoveRange(existing);

        db.PalworldLinks.Add(new PalworldLink
        {
            GuildId = guildId,
            DiscordUserId = targetId,
            PalworldUserId = match.UserId,
            PalworldName = match.Name,
            LinkedUtc = DateTime.UtcNow,
            LinkedByUserId = caller.Id,
        });

        await db.SaveChangesAsync();

        _logger.LogInformation("Palworld link: Discord {Target} → {PalworldName} ({UserId}) by {Caller}",
            targetId, match.Name, match.UserId, caller.Id);

        await command.FollowupAsync(
            $"✅ Linked <@{targetId}> to **{Escape(match.Name)}** (level {match.Level}). " +
            "`/palworld-playtime` will track them from here.",
            ephemeral: true);
    }

    // ─── /palworld-performance ───────────────────────────────────────────────

    /// <summary>
    /// Turns "it feels laggy when a lot of people are on" into a testable answer by
    /// bucketing recorded server FPS two ways:
    ///
    ///   • by PLAYER COUNT — if FPS falls as the bucket grows, the bottleneck is
    ///     simulation load (Palworld's main thread is single-threaded), and the fix
    ///     is fewer worker Pals / bases: BaseCampWorkerMaxNum, BaseCampMaxNumInGuild.
    ///
    ///   • by HOURS SINCE RESTART — if FPS falls the longer the server has been up
    ///     REGARDLESS of load, that's the well-documented Palworld memory leak, and
    ///     the fix is a shorter restart cadence, not settings.
    ///
    /// Those two have opposite remedies, which is the entire reason this exists.
    /// Both can of course be true at once.
    /// </summary>
    private async Task HandlePerformanceAsync(SocketSlashCommand command)
    {
        if (!await PassesMemberGateAsync(command)) return;
        await command.DeferAsync(ephemeral: true);

        var hours = 24;
        if (command.Data.Options.FirstOrDefault(o => o.Name == "hours")?.Value is long h)
            hours = (int)Math.Clamp(h, 1, 168);

        var since = DateTime.UtcNow.AddHours(-hours);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var samples = await db.PalworldMetricSamples
            .Where(s => s.SampledUtc >= since)
            .ToListAsync();

        if (samples.Count == 0)
        {
            await command.FollowupAsync(
                $"No health samples in the last {hours}h yet. The bot records one per minute while the " +
                "server is up, so give it a little time (or check that the server has been online).",
                ephemeral: true);
            return;
        }

        var avgFps = samples.Average(s => s.ServerFps);
        var minFps = samples.Min(s => s.ServerFps);
        var peakPlayers = samples.Max(s => s.PlayerCount);

        var embed = new EmbedBuilder()
            .WithTitle("🌴 Palworld server performance")
            .WithColor(avgFps >= 50 ? Color.Green : avgFps >= 35 ? new Color(0xE67E22) : Color.Red)
            .WithDescription($"Last **{hours}h** · {samples.Count} samples · peak {peakPlayers} players online")
            .AddField("Average FPS", $"{avgFps:0.0}", true)
            .AddField("Worst FPS", minFps.ToString(), true)
            .AddField("Base camps", samples.Last().BaseCampCount.ToString(), true);

        // ── By player count ──
        var byPlayers = BucketLines(samples, new (string Label, Func<PalworldMetricSample, bool> Match)[]
        {
            ("0–5 players",   s => s.PlayerCount <= 5),
            ("6–10 players",  s => s.PlayerCount is >= 6 and <= 10),
            ("11–15 players", s => s.PlayerCount is >= 11 and <= 15),
            ("16+ players",   s => s.PlayerCount >= 16),
        });
        if (byPlayers.Count > 0)
            embed.AddField("FPS by player count", string.Join("\n", byPlayers));

        // ── By uptime ──
        var byUptime = BucketLines(samples, new (string Label, Func<PalworldMetricSample, bool> Match)[]
        {
            ("Under 3h up",  s => s.UptimeSeconds < 3 * 3600),
            ("3–6h up",      s => s.UptimeSeconds >= 3 * 3600 && s.UptimeSeconds < 6 * 3600),
            ("6–12h up",     s => s.UptimeSeconds >= 6 * 3600 && s.UptimeSeconds < 12 * 3600),
            ("Over 12h up",  s => s.UptimeSeconds >= 12 * 3600),
        });
        if (byUptime.Count > 0)
            embed.AddField("FPS by time since restart", string.Join("\n", byUptime));

        embed.AddField("How to read this", Interpret(samples));

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    /// <summary>
    /// Renders one "label — avg FPS (n samples)" line per non-empty bucket. Empty
    /// buckets are dropped rather than shown as zero, which would read as a
    /// catastrophic FPS reading instead of "no data".
    /// </summary>
    private static List<string> BucketLines(
        List<PalworldMetricSample> samples,
        (string Label, Func<PalworldMetricSample, bool> Match)[] buckets)
    {
        var lines = new List<string>();
        foreach (var (label, match) in buckets)
        {
            var rows = samples.Where(match).ToList();
            if (rows.Count == 0) continue;
            lines.Add($"`{rows.Average(r => r.ServerFps),4:0.0}` FPS — {label}  _({rows.Count} samples)_");
        }
        return lines;
    }

    /// <summary>
    /// Compares the best and worst buckets on each axis and names the likelier
    /// culprit. Deliberately hedged: this is a correlation over a small sample, not
    /// a diagnosis, and both causes can be present at once.
    /// </summary>
    private static string Interpret(List<PalworldMetricSample> samples)
    {
        static double? Spread(List<PalworldMetricSample> rows, Func<PalworldMetricSample, int> axis)
        {
            // Compare the lowest-axis third against the highest-axis third; needs a
            // real spread on that axis to mean anything.
            var ordered = rows.OrderBy(axis).ToList();
            if (ordered.Count < 12) return null;
            if (axis(ordered[^1]) - axis(ordered[0]) <= 0) return null;

            var third = ordered.Count / 3;
            var low = ordered.Take(third).Average(r => r.ServerFps);
            var high = ordered.Skip(ordered.Count - third).Average(r => r.ServerFps);
            return low - high;   // positive => FPS drops as the axis rises
        }

        var byLoad = Spread(samples, s => s.PlayerCount);
        var byAge = Spread(samples, s => s.UptimeSeconds);

        if (byLoad is null && byAge is null)
            return "Not enough variation yet — check back after a busy session.";

        var notes = new List<string>();

        if (byLoad is > 8)
            notes.Add("📉 FPS drops noticeably as more players join — that points at **simulation load**. " +
                      "Lowering `BaseCampWorkerMaxNum` and `BaseCampMaxNumInGuild` is the usual fix.");
        if (byAge is > 8)
            notes.Add("⏳ FPS also degrades the longer the server has been up — the classic Palworld " +
                      "**memory leak**. More frequent restarts help more than settings here.");

        if (notes.Count == 0)
            notes.Add("✅ No strong correlation with either player count or uptime so far. " +
                      "If people still report lag, it may be client-side or network rather than the server.");

        return string.Join("\n\n", notes);
    }

    // ─── /palworld-admin ─────────────────────────────────────────────────────

    private async Task HandleAdminAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await EnsureEnabledAsync(command)) return;

        if (command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!HasAdminRole(caller))
        {
            await command.FollowupAsync(
                "❌ You don't have permission to use this command (requires the Palworld Mod role).",
                ephemeral: true);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        if (sub is null)
        {
            await command.FollowupAsync("No subcommand given.", ephemeral: true);
            return;
        }

        var opts = sub.Options?.ToList() ?? new List<SocketSlashCommandDataOption>();
        string? Opt(string n) => opts.FirstOrDefault(o => o.Name == n)?.Value as string;
        long? OptInt(string n) => opts.FirstOrDefault(o => o.Name == n)?.Value as long?;

        switch (sub.Name)
        {
            case "announce":
            {
                var message = Opt("message") ?? "";
                var ok = await _api.AnnounceAsync(message);
                await ReportAsync(command, ok,
                    $"📣 Announced in-game: “{Escape(message)}”",
                    "Couldn't send the announcement — the server may be offline.");
                break;
            }

            case "kick":
            {
                var (userId, display, err) = await ResolveTargetAsync(Opt("player") ?? "");
                if (err is not null) { await command.FollowupAsync(err, ephemeral: true); return; }

                var reason = Opt("reason") ?? "Kicked by an officer.";
                var ok = await _api.KickAsync(userId!, reason);

                if (ok)
                    _logger.LogInformation("Palworld kick: {Player} ({UserId}) by {Caller} — {Reason}",
                        display, userId, caller.Id, reason);

                await ReportAsync(command, ok,
                    $"👢 Kicked **{Escape(display!)}** — {Escape(reason)}",
                    "Kick failed — the server may be offline, or the player already left.");
                break;
            }

            case "ban":
            {
                var (userId, display, err) = await ResolveTargetAsync(Opt("player") ?? "");
                if (err is not null) { await command.FollowupAsync(err, ephemeral: true); return; }

                var reason = Opt("reason") ?? "Banned by an officer.";
                var ok = await _api.BanAsync(userId!, reason);

                if (ok)
                    _logger.LogWarning("Palworld ban: {Player} ({UserId}) by {Caller} — {Reason}",
                        display, userId, caller.Id, reason);

                await ReportAsync(command, ok,
                    $"🔨 Banned **{Escape(display!)}** — {Escape(reason)}\n" +
                    $"To lift it: `/palworld-admin unban user_id:{userId}`",
                    "Ban failed — the server may be offline.");
                break;
            }

            case "unban":
            {
                var userId = (Opt("user_id") ?? "").Trim();
                if (userId.Length == 0)
                {
                    await command.FollowupAsync("Give me the player's raw user id.", ephemeral: true);
                    return;
                }

                var ok = await _api.UnbanAsync(userId);

                if (ok)
                    _logger.LogInformation("Palworld unban: {UserId} by {Caller}", userId, caller.Id);

                await ReportAsync(command, ok,
                    $"✅ Unbanned `{userId}`.",
                    "Unban failed — the server may be offline, or that id isn't banned.");
                break;
            }

            case "save":
            {
                var ok = await _api.SaveAsync();
                await ReportAsync(command, ok,
                    "💾 World saved.",
                    "Save failed — the server may be offline.");
                break;
            }

            case "restart":
            {
                var seconds = (int)Math.Clamp(OptInt("seconds") ?? 60, 5, 600);
                var message = Opt("message") ?? $"Server restarting in {seconds} seconds.";

                // Save first, on purpose: a shutdown without a save can cost players
                // whatever they've done since the last autosave, and this command is
                // most often run precisely because something is misbehaving.
                var saved = await _api.SaveAsync();
                if (!saved)
                {
                    await command.FollowupAsync(
                        "⚠️ Couldn't save the world, so I did NOT restart. The server may be offline — " +
                        "check DatHost before forcing anything.",
                        ephemeral: true);
                    return;
                }

                var ok = await _api.ShutdownAsync(seconds, message);

                if (ok)
                    _logger.LogWarning("Palworld restart: {Seconds}s countdown by {Caller}", seconds, caller.Id);

                await ReportAsync(command, ok,
                    $"💾 Saved, and the server will shut down in **{seconds}s** (players warned). " +
                    "DatHost should bring it straight back up.",
                    "Shutdown failed — the world WAS saved, though. Check the DatHost panel.");
                break;
            }

            default:
                await command.FollowupAsync($"Unknown subcommand `{sub.Name}`.", ephemeral: true);
                break;
        }
    }

    /// <summary>
    /// Turns the free-text `player` option into a user id: matches an online
    /// player's name first (case-insensitive), and otherwise accepts a raw id so a
    /// player who has already logged off can still be banned.
    /// </summary>
    private async Task<(string? UserId, string? Display, string? Error)> ResolveTargetAsync(string input)
    {
        var value = input.Trim();
        if (value.Length == 0)
            return (null, null, "Give me a player name or user id.");

        var players = await _api.GetPlayersAsync();

        if (players is not null)
        {
            var match = players.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return (match.UserId, match.Name, null);
        }

        // Raw-id fallback. Palworld ids are platform-prefixed ("steam_…"), so an
        // underscore is a decent signal that this is an id and not a mistyped name.
        if (value.Contains('_'))
            return (value, value, null);

        var online = players is null
            ? "_(the server isn't responding, so I can't list online players)_"
            : players.Count == 0
                ? "_nobody is online right now_"
                : string.Join(", ", players.Select(p => $"`{p.Name}`"));

        return (null, null,
            $"❌ No online player named **{Escape(value)}**, and that doesn't look like a raw user id.\n\nOnline: {online}");
    }

    // ─── Shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The member-facing gate for /palworld-status, -playtime, -leaderboard and
    /// -link: feature enabled + configured, in a guild, and at or above
    /// <see cref="BotConfig.PalworldMinRank"/> (default PFC). Administrator bypasses.
    ///
    /// Runs BEFORE the handler defers and replies with RespondAsync (not Followup),
    /// so a denial is a clean ephemeral message with no dangling deferred response —
    /// which matters for /palworld-leaderboard, whose defer is public.
    ///
    /// /palworld-admin does NOT use this — it has its own role gate
    /// (<see cref="HasAdminRole"/>) that is independent of the rank ladder, because
    /// the server's owner holds the mod role without holding a clan rank.
    /// </summary>
    private async Task<bool> PassesMemberGateAsync(SocketSlashCommand command)
    {
        if (!_config.PalworldEnabled || !_api.IsConfigured)
        {
            await command.RespondAsync(
                "The Palworld integration isn't set up yet (`PalworldEnabled` / `PalworldBaseUrl` / " +
                "`PalworldAdminPassword`). Ask an admin.",
                ephemeral: true);
            return false;
        }

        if (command.User is not SocketGuildUser caller)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return false;
        }

        if (!HasMinRank(caller))
        {
            await command.RespondAsync(
                $"❌ You need to be **{_config.PalworldMinRank}+** to use the Palworld commands.",
                ephemeral: true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enabled-only check retained for /palworld-admin (which gates on the mod role,
    /// not rank, so it can't reuse <see cref="PassesMemberGateAsync"/>). Deferred
    /// first, so this uses a followup.
    /// </summary>
    private async Task<bool> EnsureEnabledAsync(SocketSlashCommand command, bool ephemeral = true)
    {
        if (_config.PalworldEnabled && _api.IsConfigured) return true;

        await command.FollowupAsync(
            "The Palworld integration isn't set up yet (`PalworldEnabled` / `PalworldBaseUrl` / " +
            "`PalworldAdminPassword`). Ask an admin.",
            ephemeral: ephemeral);
        return false;
    }

    /// <summary>
    /// Rank floor for the member-facing commands: Administrator bypasses, otherwise
    /// any role at or above <see cref="BotConfig.PalworldMinRank"/> in the RankRoles
    /// ladder. An unrecognized configured rank fails CLOSED (nobody but Admins),
    /// same conservative default as the other rank gates.
    /// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (MinRank(PalworldMinRank)).
    /// </summary>
    private bool HasMinRank(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(_config.PalworldMinRank, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    private static async Task ReportAsync(SocketSlashCommand command, bool ok, string success, string failure) =>
        await command.FollowupAsync(ok ? success : "⚠️ " + failure, ephemeral: true);

    /// <summary>
    /// Gate for /palworld-admin: holders of the "Palworld Mod" role
    /// (<see cref="BotConfig.PalworldAdminRoleId"/>), plus Administrators.
    ///
    /// A role check, NOT a rank floor. These commands can shut the game server down,
    /// so the set of people who can run them should be an explicit group, not a side
    /// effect of the promotion ladder. It's also a role of its own rather than the
    /// HQ role, because the membership differs — the server's owner is on it without
    /// being in HQ. An unset role id fails CLOSED (Administrators only) rather than
    /// falling open to everyone.
    /// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (palworldMod).
    /// </summary>
    private bool HasAdminRole(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.PalworldAdminRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.PalworldAdminRoleId);
    }

    /// <summary>
    /// In-game names are player-controlled text. Neutralize markdown before it
    /// reaches a Discord message. Mirrors PalworldPresenceService.Escape.
    /// </summary>
    private static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "(unnamed)"
            : s.Replace("\\", "\\\\")
               .Replace("*", "\\*")
               .Replace("_", "\\_")
               .Replace("~", "\\~")
               .Replace("`", "\\`")
               .Replace("|", "\\|")
               .Replace("@", "@​")
               .Replace("\n", " ")
               .Replace("\r", " ");
}
