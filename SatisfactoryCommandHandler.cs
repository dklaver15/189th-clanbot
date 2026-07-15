using System.Text.RegularExpressions;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns the Satisfactory slash commands against the clan's Dedicated Server.
///
///   • /satisfactory-status  — live server state (players, session, tier, tick rate)  (everyone)
///   • /satisfactory-admin    — save / restart / run-command                          (Satisfactory Mod)
///
/// ── Why fewer commands than Palworld ──
/// Satisfactory's HTTPS API exposes no player list, no in-game broadcast, and no
/// kick/ban. So there's no announce, no per-player playtime, and no link command —
/// the API simply can't back them. What remains is a status read and the admin
/// verbs the API does support.
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
        "satisfactory-admin",
    };

    private readonly ILogger<SatisfactoryCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly SatisfactoryApiService _api;

    public SatisfactoryCommandHandler(
        ILogger<SatisfactoryCommandHandler> logger,
        IOptions<BotConfig> config,
        SatisfactoryApiService api)
    {
        _logger = logger;
        _config = config.Value;
        _api = api;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStatusCommand() =>
        new SlashCommandBuilder()
            .WithName("satisfactory-status")
            .WithDescription("Live status of the clan's Satisfactory server — players on, session, tier, tick rate")
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

        // A null read is "unreachable", so say that rather than "0 players online".
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
            embed.AddField("Playtime", Humanize(TimeSpan.FromSeconds(state.TotalGameDurationSeconds)), true);
            embed.AddField("Tick rate", $"{state.AverageTickRate:0.0} tps", true);
            embed.AddField("State", state.IsGamePaused ? "⏸️ Paused" : state.IsGameRunning ? "▶️ Running" : "⏳ Awaiting session", true);
        }

        if (!string.IsNullOrWhiteSpace(health))
            embed.WithFooter(health.Equals("healthy", StringComparison.OrdinalIgnoreCase)
                ? "Health: healthy"
                : $"Health: {health} (tick rate is low)");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
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

    /// <summary>"2h 14m" / "47m". Local copy so the command doesn't depend on the poller.</summary>
    private static string Humanize(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "under a minute";
        var days = (int)d.TotalDays;
        var hours = d.Hours;
        var minutes = d.Minutes;
        if (days > 0) return $"{days}d {hours}h";
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
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
