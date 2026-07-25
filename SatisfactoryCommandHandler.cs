using System.Text.RegularExpressions;
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
/// Owns the Satisfactory slash commands against the clan's Dedicated Server.
///
///   • /satisfactory-status      — live server state (players, session, tier, tick rate)  (everyone)
///   • /satisfactory-mods        — the server's exact modpack + versions                  (everyone)
///   • /satisfactory-report      — factory report: power, production, sink, players       (everyone)
///   • /satisfactory-playtime    — one player's total hours and last-seen                 (everyone)
///   • /satisfactory-leaderboard — most playtime, public                                  (everyone)
///   • /satisfactory-link        — bind a Discord account to an in-game name              (everyone; others = Satisfactory Mod)
///   • /satisfactory-admin       — save / restart / run-command                           (Satisfactory Mod)
///
/// ── Two data sources ──
/// <see cref="SatisfactoryApiService"/> is the game's own HTTPS API: authoritative
/// for server state and the only thing that can save/restart, but it exposes no
/// player list — a COUNT is all it will ever give.
/// <see cref="FrmApiService"/> is the Ficsit Remote Monitoring mod, which supplies
/// what that can't: player NAMES and the mod list. It's optional, so every FRM
/// read here is best-effort — a null result drops the extra field rather than
/// failing the command, and /satisfactory-status still works with FRM switched off.
///
/// ── Gateway safety ──
/// Every command hits the network (the server's HTTPS API), and Discord.NET runs
/// handlers ON the gateway task — a slow call would stall the heartbeat. So each
/// handler dispatches to Task.Run and returns immediately (same rule that came out
/// of /health freezing during a long purge).
///
/// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (permission labels).
/// </summary>
public class SatisfactoryCommandHandler
{
    private static readonly string[] CommandNames =
    {
        "satisfactory-status",
        "satisfactory-mods",
        "satisfactory-report",
        "satisfactory-playtime",
        "satisfactory-leaderboard",
        "satisfactory-graph",
        "satisfactory-link",
        "satisfactory-admin",
    };

    /// <summary>Cap on leaderboard rows, so the embed can't blow the description limit.</summary>
    private const int MaxLeaderboardRows = 15;

    private readonly ILogger<SatisfactoryCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly SatisfactoryApiService _api;
    private readonly FrmApiService _frm;
    private readonly SatisfactoryDigestBuilder _digest;
    private readonly SatisfactoryChartRenderer _charts;
    private readonly IServiceProvider _services;

    public SatisfactoryCommandHandler(
        ILogger<SatisfactoryCommandHandler> logger,
        IOptions<BotConfig> config,
        SatisfactoryApiService api,
        FrmApiService frm,
        SatisfactoryDigestBuilder digest,
        SatisfactoryChartRenderer charts,
        IServiceProvider services)
    {
        _logger = logger;
        _config = config.Value;
        _api = api;
        _frm = frm;
        _digest = digest;
        _charts = charts;
        _services = services;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStatusCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-status")
            .WithDescription("Live status of the clan's Satisfactory server — players on, session, tier, tick rate")
            .Build();

    /// <summary>
    /// /satisfactory-mods — the server's exact modpack and versions.
    ///
    /// Exists because joining a modded server requires the client to match, and
    /// the alternative is someone hand-listing mods in chat every time the pack
    /// changes. Reads from FRM's getModList, so the answer is whatever the
    /// server actually loaded, not what someone remembers installing.
    /// </summary>
    public static SlashCommandProperties BuildModsCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-mods")
            .WithDescription("List the mods installed on the clan's Satisfactory server, with versions")
            .Build();

    /// <summary>
    /// /satisfactory-report — the same embed the daily digest posts, on demand.
    ///
    /// Shares <see cref="SatisfactoryDigestBuilder"/> with
    /// <see cref="SatisfactoryFactoryService"/>, so what you see here is exactly
    /// what lands in the channel at the scheduled hour — which also makes the
    /// digest testable without waiting for the clock.
    /// </summary>
    public static SlashCommandProperties BuildReportCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-report")
            .WithDescription("Factory report — power, production, AWESOME Sink, and who's been playing")
            .Build();

    public static SlashCommandProperties BuildPlaytimeCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-playtime")
            .WithDescription("How long someone has spent on the clan's Satisfactory server")
            .AddOption("user", ApplicationCommandOptionType.User,
                "Discord member to look up (defaults to you). Requires them to be linked.", isRequired: false)
            .AddOption("name", ApplicationCommandOptionType.String,
                "In-game name to look up instead — works even for people who aren't in Discord", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildLeaderboardCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-leaderboard")
            .WithDescription("Who's put the most hours into the clan's Satisfactory server")
            .Build();

    /// <summary>
    /// /satisfactory-graph — power or playtime as a chart image.
    ///
    /// <para>Power history comes from SatisfactoryMetricSample, which only
    /// starts accumulating once the bot has been running with metrics enabled —
    /// so this command says "not enough data yet" rather than erroring for the
    /// first hour or so after deployment.</para>
    /// </summary>
    public static SlashCommandProperties BuildGraphCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-graph")
            .WithDescription("Charts for the clan's Satisfactory server — power over time, or playtime per day")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("type")
                .WithDescription("What to chart")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("Power", "power")
                .AddChoice("Playtime", "playtime"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("range")
                .WithDescription("How far back to look (default 24 hours for power, 14 days for playtime)")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .AddChoice("24 hours", "24h")
                .AddChoice("7 days", "7d")
                .AddChoice("30 days", "30d"))
            .Build();

    public static SlashCommandProperties BuildLinkCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-link")
            .WithDescription("Link your Discord account to your Satisfactory in-game name (you must be online in-game)")
            .AddOption("name", ApplicationCommandOptionType.String,
                "Your in-game name, exactly as it appears in game", isRequired: true)
            .AddOption("user", ApplicationCommandOptionType.User,
                "Link on someone else's behalf (Satisfactory Mod only)", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildAdminCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-admin")
            .WithDescription("Administer the Satisfactory server — save, restart, run a console command (Satisfactory Mod)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("save")
                .WithDescription("Force a world save")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("name", ApplicationCommandOptionType.String,
                    "Save file name (defaults to the current session)", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("restart")
                .WithDescription("Save, then shut the server down (the host restart script brings it back up)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("command")
                .WithDescription("Run a console command on the server and show its output")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("command", ApplicationCommandOptionType.String, "The console command line to run", isRequired: true))
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
                catch { /* interaction already expired */ }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "satisfactory-status": await HandleStatusAsync(command); break;
            case "satisfactory-mods":   await HandleModsAsync(command); break;
            case "satisfactory-report": await HandleReportAsync(command); break;
            case "satisfactory-playtime":    await HandlePlaytimeAsync(command); break;
            case "satisfactory-leaderboard": await HandleLeaderboardAsync(command); break;
            case "satisfactory-graph":       await HandleGraphAsync(command); break;
            case "satisfactory-link":        await HandleLinkAsync(command); break;
            case "satisfactory-admin":  await HandleAdminAsync(command); break;
        }
    }

    // ─── /satisfactory-status ────────────────────────────────────────────────

    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await EnsureEnabledAsync(command)) return;

        var state = await _api.GetServerStateAsync();
        var health = await _api.GetHealthAsync();

        // A null game read is "unreachable" — and with no host-panel API there is no
        // second opinion available, so we can't say whether it crashed or is just
        // mid-restart. Say so rather than guessing.
        if (state is null && health is null)
        {
            await command.FollowupAsync(
                "🔴 The Satisfactory server isn't responding — it's most likely offline or restarting.",
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🏭 {(state is null || string.IsNullOrWhiteSpace(state.ActiveSessionName) ? "Satisfactory server" : Escape(state.ActiveSessionName))}")
            .WithColor(new Color(0xE59344)); // FICSIT orange

        if (state is not null)
        {
            embed.AddField("Players", $"{state.NumConnectedPlayers}/{state.PlayerLimit}", true);
            embed.AddField("Tech tier", state.TechTier.ToString(), true);
            embed.AddField("Game phase", PrettyPhase(state.GamePhase), true);
            embed.AddField("Playtime", SatisfactoryPresenceService.Humanize(TimeSpan.FromSeconds(state.TotalGameDurationSeconds)), true);
            embed.AddField("Tick rate", $"{state.AverageTickRate:0.0} tps", true);
            embed.AddField("State", state.IsGamePaused ? "⏸️ Paused" : state.IsGameRunning ? "▶️ Running" : "⏳ Awaiting session", true);
        }

        // Names, when FRM is available. The game API can only ever give a count,
        // so this field is the entire reason the mod is wired up. Best-effort: a
        // null read just omits the field rather than failing the command.
        if (_frm.IsConfigured)
        {
            var online = await _frm.GetOnlinePlayersAsync();
            if (online is { Count: > 0 })
                embed.AddField("Who's on", string.Join(", ", online.Select(p => Escape(p.Name))), false);
        }

        if (!string.IsNullOrWhiteSpace(health))
            embed.WithFooter(health.Equals("healthy", StringComparison.OrdinalIgnoreCase)
                ? "Health: healthy"
                : $"Health: {health} (tick rate is low)");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /satisfactory-mods ──────────────────────────────────────────────────

    private async Task HandleModsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (!_frm.IsConfigured)
        {
            await command.FollowupAsync(
                "The mod list needs the Ficsit Remote Monitoring integration, which isn't set up yet " +
                "(`FrmEnabled` / `FrmBaseUrl`). Ask an admin.",
                ephemeral: true);
            return;
        }

        var mods = await _frm.GetModListAsync();
        if (mods is null)
        {
            await command.FollowupAsync(
                "⚠️ Couldn't reach the mod list — the server may be offline, or FRM's web server didn't start.",
                ephemeral: true);
            return;
        }

        // getModList includes pseudo-entries for the base game and the loader.
        // Split them out: members need the actual mods to install, but the game
        // build and SML version are exactly what a version mismatch turns on.
        var platform = mods.Where(m => IsPlatformEntry(m.SmrName)).ToList();
        var actual = mods.Where(m => !IsPlatformEntry(m.SmrName))
                         .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();

        var embed = new EmbedBuilder()
            .WithTitle("🧩 Satisfactory server mods")
            .WithColor(new Color(0xE59344))
            .WithCurrentTimestamp();

        if (actual.Count == 0)
        {
            embed.WithDescription("The server reports no mods installed.");
        }
        else
        {
            // SMRName is what you type into Satisfactory Mod Manager, so lead
            // with it; the friendly name is the parenthetical.
            var lines = actual.Select(m =>
                $"• `{Escape(m.SmrName)}` **{Escape(m.Version)}**" +
                (string.Equals(m.Name, m.SmrName, StringComparison.OrdinalIgnoreCase) ? "" : $" — {Escape(m.Name)}") +
                (m.RequiredOnRemote ? " *(required)*" : ""));

            embed.WithDescription(Truncate(string.Join("\n", lines), 3800));
            embed.WithFooter($"{actual.Count} mod{(actual.Count == 1 ? "" : "s")} · install these with Satisfactory Mod Manager to join");
        }

        foreach (var p in platform)
            embed.AddField(Escape(p.Name), Escape(p.Version), true);

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    /// <summary>
    /// getModList reports the base game and the mod loader alongside real mods.
    /// They're useful to display but must not appear in the "install these" list.
    ///
    /// <para>Takes a nullable string on purpose. FrmMod is deserialized by
    /// System.Text.Json, which does not enforce non-nullable reference
    /// annotations, so a getModList entry missing its SMRName key yields null
    /// here and the instance Equals would throw — taking out
    /// /satisfactory-mods entirely. string.Equals is the static, null-safe
    /// form.</para>
    /// </summary>
    private static bool IsPlatformEntry(string? smrName) =>
        string.Equals(smrName, "FactoryGame", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(smrName, "SML", StringComparison.OrdinalIgnoreCase);

    // ─── /satisfactory-report ────────────────────────────────────────────────

    private async Task HandleReportAsync(SocketSlashCommand command)
    {
        // Public, like /satisfactory-leaderboard: a factory report is worth the
        // whole channel seeing, not just whoever typed the command.
        await command.DeferAsync();

        // Failures stay PRIVATE though — "the integration isn't configured" is
        // noise for everyone except the person who asked, and a public error
        // embed is worse than no embed.
        if (!_frm.IsConfigured)
        {
            await command.FollowupAsync(
                "The factory report needs the Ficsit Remote Monitoring integration, which isn't set up yet " +
                "(`FrmEnabled` / `FrmBaseUrl`). Ask an admin.",
                ephemeral: true);
            return;
        }

        // Five FRM reads plus a database query — deferred above, because this is
        // comfortably past Discord's 3-second initial-response window.
        var embed = await _digest.BuildAsync();
        if (embed is null)
        {
            await command.FollowupAsync(
                "⚠️ Couldn't build the report — the server may be offline, or FRM's web server didn't start.",
                ephemeral: true);
            return;
        }

        await command.FollowupAsync(embed: embed);
    }

    // ─── /satisfactory-graph ─────────────────────────────────────────────────

    /// <summary>
    /// Renders a chart and posts it publicly, matching /satisfactory-report.
    ///
    /// <para>Deferred immediately: a render allocates a 1800x600 surface and
    /// runs a font system after a database read, which is comfortably past
    /// Discord's 3-second initial-response window.</para>
    ///
    /// <para>A null from the renderer is NOT an error — it also means "not
    /// enough data yet", which is the expected state for the first hour after
    /// deployment. The message says so rather than reporting a failure.</para>
    /// </summary>
    private async Task HandleGraphAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var type = command.Data.Options
            .FirstOrDefault(o => o.Name == "type")?.Value as string ?? "power";
        var range = command.Data.Options
            .FirstOrDefault(o => o.Name == "range")?.Value as string;

        byte[]? png;
        string fileName;

        if (string.Equals(type, "playtime", StringComparison.OrdinalIgnoreCase))
        {
            var days = range switch
            {
                "24h" => 2,      // a one-day bar chart is a single bar; show yesterday too
                "30d" => 30,
                _     => 14,
            };

            png = await _charts.TryRenderPlaytimeChartAsync(
                days, DigestZone(), $"Playtime — last {days} days");
            fileName = "playtime.png";
        }
        else
        {
            var window = range switch
            {
                "7d"  => TimeSpan.FromDays(7),
                "30d" => TimeSpan.FromDays(30),
                _     => TimeSpan.FromHours(24),
            };

            var label = window.TotalHours <= 24 ? "last 24 hours" : $"last {(int)window.TotalDays} days";
            png = await _charts.TryRenderPowerChartAsync(window, $"Power — {label}");
            fileName = "power.png";
        }

        if (png is null)
        {
            await command.FollowupAsync(
                "Not enough data for that chart yet. Power history builds up from the alert poll " +
                "(a couple of hours gives a useful picture), and playtime needs at least one recorded session.",
                ephemeral: true);
            return;
        }

        using var ms = new MemoryStream(png);
        await command.FollowupWithFileAsync(ms, fileName);
    }

    /// <summary>
    /// The clan's timezone, for bucketing playtime days. Shares the digest's
    /// setting so a bar labelled "Jul 24" means the same thing in the chart and
    /// in the morning report. Falls back to UTC on an unrecognised id.
    /// </summary>
    private TimeZoneInfo DigestZone() =>
        !string.IsNullOrWhiteSpace(_config.SatisfactoryDigestTimeZone)
        && TimeZoneInfo.TryFindSystemTimeZoneById(_config.SatisfactoryDigestTimeZone, out var tz)
        && tz is not null
            ? tz
            : TimeZoneInfo.Utc;

    // ─── /satisfactory-playtime ──────────────────────────────────────────────

    private async Task HandlePlaytimeAsync(SocketSlashCommand command)
    {
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

        string playerName;
        ulong? linkedDiscordId = null;

        if (!string.IsNullOrWhiteSpace(rawName))
        {
            // Name path: no link required, so it works for anyone who has ever
            // played — including people who aren't in the Discord at all.
            playerName = rawName.Trim();
        }
        else
        {
            var discordId = targetUser?.Id ?? command.User.Id;
            var link = await db.SatisfactoryLinks
                .FirstOrDefaultAsync(l => l.GuildId == guildId && l.DiscordUserId == discordId);

            if (link is null)
            {
                var who = targetUser is null ? "You aren't" : $"<@{discordId}> isn't";
                await command.FollowupAsync(
                    $"{who} linked to a Satisfactory player yet. " +
                    "Hop on the server and run `/satisfactory-link name:<your in-game name>`.",
                    ephemeral: true);
                return;
            }

            playerName = link.SatisfactoryPlayerName;
            linkedDiscordId = discordId;
        }

        var sessions = await db.SatisfactorySessions
            .Where(s => s.PlayerName == playerName)
            .ToListAsync();

        var label = linkedDiscordId is ulong did
            ? $"<@{did}> (**{Escape(playerName)}**)"
            : $"**{Escape(playerName)}**";

        if (sessions.Count == 0)
        {
            await command.FollowupAsync(
                $"{label} hasn't been seen on the Satisfactory server yet. " +
                "Note that names are matched exactly, including capitalisation.",
                ephemeral: true);
            return;
        }

        // Duration is computed from LastSeenUtc, so an open session contributes
        // only what's actually been observed — see SatisfactorySession.
        var total = sessions.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
        var lastSeen = sessions.Max(s => s.EndedUtc ?? s.LastSeenUtc);
        var isOnline = sessions.Any(s => s.EndedUtc is null);

        var embed = new EmbedBuilder()
            .WithTitle("🏭 Satisfactory playtime")
            .WithColor(isOnline ? Color.Green : new Color(0xE59344))
            .WithDescription(label + (isOnline ? "  ·  🟢 online now" : ""))
            .AddField("Total played", SatisfactoryPresenceService.Humanize(total), true)
            .AddField("Sessions", sessions.Count.ToString(), true)
            .AddField("Last seen", $"<t:{new DateTimeOffset(lastSeen, TimeSpan.Zero).ToUnixTimeSeconds()}:R>", true)
            .WithFooter("Playtime is measured by polling the server, so it's accurate to about a minute.");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /satisfactory-leaderboard ───────────────────────────────────────────

    private async Task HandleLeaderboardAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var sessions = await db.SatisfactorySessions.ToListAsync();
        if (sessions.Count == 0)
        {
            await command.FollowupAsync("Nobody's played on the Satisfactory server yet.");
            return;
        }

        // Duration is a computed property, so the aggregation happens in memory
        // rather than in SQL. Fine at clan scale — this table gains a handful of
        // rows a day — but it's the thing to revisit if it ever gets big.
        var players = sessions
            .GroupBy(s => s.PlayerName, StringComparer.Ordinal)
            .Select(g => new
            {
                Name = g.Key,
                Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration),
                Sessions = g.Count(),
                Online = g.Any(s => s.EndedUtc is null),
            })
            .OrderByDescending(p => p.Total)
            .ToList();

        var lines = players
            .Take(MaxLeaderboardRows)
            .Select((p, i) => $"`{i + 1,2}.` **{Escape(p.Name)}** — " +
                              $"{SatisfactoryPresenceService.Humanize(p.Total)}  ·  {p.Sessions} session{(p.Sessions == 1 ? "" : "s")}" +
                              (p.Online ? "  🟢" : ""));

        var embed = new EmbedBuilder()
            .WithTitle("🏭 Satisfactory leaderboard — most playtime")
            .WithColor(new Color(0xE59344))
            .WithDescription(string.Join("\n", lines))
            .WithFooter($"{players.Count} player(s) tracked" +
                        (players.Count > MaxLeaderboardRows ? $" · showing top {MaxLeaderboardRows}" : ""));

        await command.FollowupAsync(embed: embed.Build());
    }

    // ─── /satisfactory-link ──────────────────────────────────────────────────

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!_frm.IsConfigured)
        {
            await command.FollowupAsync(
                "Linking needs the Ficsit Remote Monitoring integration, which isn't set up yet " +
                "(`FrmEnabled` / `FrmBaseUrl`). Ask an admin.",
                ephemeral: true);
            return;
        }

        var name = (command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string ?? "").Trim();
        var onBehalfOf = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;

        // Linking someone else is a moderator action — otherwise anyone could bind
        // a teammate's character to their own account, or vice versa.
        if (onBehalfOf is not null && onBehalfOf.Id != caller.Id && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                "❌ Only Satisfactory Mods can link someone else. Leave `user` blank to link yourself.",
                ephemeral: true);
            return;
        }

        var targetId = onBehalfOf?.Id ?? caller.Id;

        // Requiring the player to be ONLINE is the only verification available:
        // there's no handshake between Discord and Satisfactory, so the live
        // player list is the one thing that proves the name is really in use.
        // Accepting a typed-in name unchecked would let anyone claim anyone's
        // playtime.
        var online = await _frm.GetOnlinePlayersAsync();
        if (online is null)
        {
            await command.FollowupAsync(
                "🔴 Can't reach the Satisfactory server right now, so I can't verify that name. Try again shortly.",
                ephemeral: true);
            return;
        }

        var match = online.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var who = online.Count == 0
                ? "_Nobody is online right now._"
                : string.Join(", ", online.Select(p => $"`{p.Name}`"));
            await command.FollowupAsync(
                $"❌ No online player named **{Escape(name)}**. You have to be logged into the server when you link, " +
                $"so I can see you.\n\nOnline now: {who}",
                ephemeral: true);
            return;
        }

        // Store the server's exact spelling, not what was typed: session rows are
        // keyed on the name the server reports, and SQLite compares text
        // case-sensitively, so a casing mismatch here would silently orphan the
        // link from all the playtime it's supposed to point at.
        var canonicalName = match.Name;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Re-linking overwrites in BOTH directions: one Discord account per
        // in-game name and vice versa. Clearing the other side stops two members
        // both claiming the same player.
        var existing = await db.SatisfactoryLinks
            .Where(l => l.GuildId == guildId
                     && (l.DiscordUserId == targetId || l.SatisfactoryPlayerName == canonicalName))
            .ToListAsync();
        if (existing.Count > 0)
            db.SatisfactoryLinks.RemoveRange(existing);

        db.SatisfactoryLinks.Add(new SatisfactoryLink
        {
            GuildId = guildId,
            DiscordUserId = targetId,
            SatisfactoryPlayerName = canonicalName,
            LinkedUtc = DateTime.UtcNow,
            LinkedByUserId = caller.Id,
        });

        await db.SaveChangesAsync();

        _logger.LogInformation("Satisfactory link: Discord {Target} → {PlayerName} by {Caller}",
            targetId, canonicalName, caller.Id);

        var forWhom = targetId == caller.Id ? "You're" : $"<@{targetId}> is";
        await command.FollowupAsync(
            $"✅ {forWhom} now linked to **{Escape(canonicalName)}**. " +
            "Playtime from here on will show against that account.",
            ephemeral: true);
    }

    // ─── /satisfactory-admin ─────────────────────────────────────────────────

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
                "❌ You don't have permission to use this command (requires the Satisfactory Mod role).",
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

        switch (sub.Name)
        {
            case "save":
            {
                var name = (Opt("name") ?? "").Trim();
                if (name.Length == 0)
                    name = (await _api.GetServerStateAsync())?.ActiveSessionName ?? "ClanSave";

                var ok = await _api.SaveGameAsync(name);
                if (ok) _logger.LogInformation("Satisfactory save '{Name}' by {Caller}", name, caller.Id);

                await ReportAsync(command, ok,
                    $"💾 Saved to **{Escape(name)}**.",
                    "Save failed — the server may be offline.");
                break;
            }

            case "restart":
            {
                // Save first, on purpose: a shutdown without a save can cost whatever
                // was built since the last autosave, and this is most often run
                // precisely because something is misbehaving.
                var name = (await _api.GetServerStateAsync())?.ActiveSessionName ?? "ClanSave";
                var saved = await _api.SaveGameAsync(name);
                if (!saved)
                {
                    await command.FollowupAsync(
                        "⚠️ Couldn't save the world, so I did NOT restart. The server may be offline — " +
                        "check the host panel before forcing anything.",
                        ephemeral: true);
                    return;
                }

                var ok = await _api.ShutdownAsync();
                if (ok) _logger.LogWarning("Satisfactory restart (save + shutdown) by {Caller}", caller.Id);

                await ReportAsync(command, ok,
                    $"💾 Saved to **{Escape(name)}**, and sent the shutdown. If a restart script is configured " +
                    "on the host, it should come straight back up (otherwise someone has to start it).",
                    "Shutdown failed — the world WAS saved, though. Check the host panel.");
                break;
            }

            case "command":
            {
                var cmd = (Opt("command") ?? "").Trim();
                if (cmd.Length == 0)
                {
                    await command.FollowupAsync("Give me a command to run.", ephemeral: true);
                    return;
                }

                var (ok, output) = await _api.RunCommandAsync(cmd);
                if (ok) _logger.LogInformation("Satisfactory RunCommand '{Command}' by {Caller}", cmd, caller.Id);

                if (!ok)
                {
                    await command.FollowupAsync("⚠️ Command failed — the server may be offline.", ephemeral: true);
                    return;
                }

                var body = string.IsNullOrWhiteSpace(output) ? "_(no output)_" : $"```\n{Truncate(output, 1800)}\n```";
                await command.FollowupAsync($"🖥️ Ran `{Escape(cmd)}`:\n{body}", ephemeral: true);
                break;
            }

            default:
                await command.FollowupAsync($"Unknown subcommand `{sub.Name}`.", ephemeral: true);
                break;
        }
    }

    // ─── Shared ──────────────────────────────────────────────────────────────

    private async Task<bool> EnsureEnabledAsync(SocketSlashCommand command)
    {
        if (_config.SatisfactoryEnabled && _api.IsConfigured) return true;

        await command.FollowupAsync(
            "The Satisfactory integration isn't set up yet (`SatisfactoryEnabled` / `SatisfactoryBaseUrl` / " +
            "`SatisfactoryAdminPassword` or `SatisfactoryApiToken`). Ask an admin.",
            ephemeral: true);
        return false;
    }

    private static async Task ReportAsync(SocketSlashCommand command, bool ok, string success, string failure) =>
        await command.FollowupAsync(ok ? success : "⚠️ " + failure, ephemeral: true);

    /// <summary>
    /// Gate for /satisfactory-admin: holders of the "Satisfactory Mod" role
    /// (<see cref="BotConfig.SatisfactoryAdminRoleId"/>), plus Administrators. A role
    /// check, not a rank floor — these commands can shut the server down. An unset
    /// role id fails CLOSED (Administrators only).
    /// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (satisfactoryMod).
    /// </summary>
    private bool HasAdminRole(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.SatisfactoryAdminRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.SatisfactoryAdminRoleId);
    }

    /// <summary>
    /// Turns the raw game-phase asset path into something readable. The API returns
    /// e.g. ".../GP_Project_Assembly_Phase_2.GP_Project_Assembly_Phase_2'", or "None"
    /// before a game is running. Extract the phase number when present, else pass a
    /// tidy fallback.
    /// </summary>
    private static string PrettyPhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase) || phase.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "Not started";

        var m = Regex.Match(phase, @"Phase[_\s]?(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? $"Phase {m.Groups[1].Value}" : "In progress";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// Server-supplied text (session names, command echoes) is builder-controlled and
    /// lands in a Discord message. Neutralize markdown before it gets there.
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
