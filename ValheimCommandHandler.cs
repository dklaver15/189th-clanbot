using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns the Valheim slash commands against the clan's Shockbyte-hosted server.
///
///   • /valheim-status — live server state (players, world, version, join address)  (everyone)
///
/// ── Why this surface is so much smaller than Palworld's or Satisfactory's ──
/// Everything here reads <see cref="ValheimQueryService"/>, i.e. the Steam A2S
/// query protocol, because vanilla Valheim has no REST API and no RCON. A2S
/// returns a player COUNT and never a name, so the -playtime, -leaderboard and
/// -link commands that exist for the other two servers have no data to stand on
/// and are deliberately absent rather than stubbed. It is also read-only in the
/// strongest sense: A2S has no write side at all, so there is no /valheim-admin.
///
/// Closing that gap needs DiscordConnector installed server-side, which is a
/// separate integration and needs Shockbyte panel/FTP access.
///
/// ── Gateway safety ──
/// The status query is a UDP round-trip with a 5s timeout, and Discord.NET runs
/// handlers ON the gateway task — awaiting that inline would stall the heartbeat
/// every time the server is down. So the handler dispatches to Task.Run and
/// returns immediately, same rule as the other game-server handlers.
///
/// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (permission labels).
/// </summary>
public class ValheimCommandHandler
{
    private static readonly string[] CommandNames =
    {
        "valheim-status",
    };

    /// <summary>Valheim's steel-blue UI accent, to distinguish the embed at a glance.</summary>
    private static readonly Color ValheimBlue = new(0x38607C);

    private readonly ILogger<ValheimCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly ValheimQueryService _query;

    public ValheimCommandHandler(
        ILogger<ValheimCommandHandler> logger,
        IOptions<BotConfig> config,
        ValheimQueryService query)
    {
        _logger = logger;
        _config = config.Value;
        _query = query;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStatusCommand() =>
        new SlashCommandBuilder()
            .WithName("valheim-status")
            .WithDescription("Live status of the clan's Valheim server — who's on, world, version, join address")
            .Build();

    // ─── Dispatch ────────────────────────────────────────────────────────────

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!CommandNames.Contains(command.Data.Name)) return Task.CompletedTask;

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
            case "valheim-status": await HandleStatusAsync(command); break;
        }
    }

    // ─── /valheim-status ─────────────────────────────────────────────────────

    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await EnsureEnabledAsync(command)) return;

        var info = await _query.QueryAsync();

        // A null read is "didn't answer", which for UDP covers more ground than
        // "offline": the server could be up with its query port firewalled. Say
        // what we actually know instead of asserting it's down.
        if (info is null)
        {
            await command.FollowupAsync(
                $"🔴 The Valheim server isn't answering on `{_config.ValheimHost}:{_query.QueryPort}`. " +
                "It's most likely offline or restarting.\n\n" +
                OfflineHint(),
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🛡️ {ValheimStatusService.Escape(string.IsNullOrWhiteSpace(info.Name) ? info.World : info.Name)}")
            .WithColor(ValheimBlue)
            .WithCurrentTimestamp();

        embed.AddField("Players", $"{info.Players}/{info.MaxPlayers}", true);

        if (!string.IsNullOrWhiteSpace(info.World))
            embed.AddField("World", ValheimStatusService.Escape(info.World), true);

        if (info.DisplayVersion is { } version)
            embed.AddField("Version", ValheimStatusService.Escape(version), true);

        embed.AddField("Password", info.PasswordProtected ? "🔒 Required" : "🔓 Open", true);
        embed.AddField("Query ping", $"{(int)info.RoundTrip.TotalMilliseconds} ms", true);
        embed.AddField("Join", $"`{_query.JoinAddress}`", false);

        // A2S structurally cannot name players, and someone WILL ask why the
        // count is there without the names. Say it once, in the footer, rather
        // than fielding it in chat every time.
        embed.WithFooter("Valheim's query protocol reports a player count only — names need a server-side mod.");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<bool> EnsureEnabledAsync(SocketSlashCommand command)
    {
        if (_config.ValheimEnabled && _query.IsConfigured) return true;

        await command.FollowupAsync(
            "The Valheim integration isn't set up yet (`ValheimEnabled` / `ValheimHost`). Ask an admin.",
            ephemeral: true);
        return false;
    }

    /// <summary>
    /// Where a human can go when the bot can't help. A2S is read-only, so the bot
    /// structurally cannot restart the server — every failure message has to point
    /// at the panel instead of implying a fix that doesn't exist.
    /// SyncWithHandlers: BotConfig.ValheimHostPanelName / ValheimHostPanelUrl.
    /// </summary>
    private string OfflineHint()
    {
        var name = string.IsNullOrWhiteSpace(_config.ValheimHostPanelName)
            ? "the host's control panel"
            : _config.ValheimHostPanelName;

        var where = string.IsNullOrWhiteSpace(_config.ValheimHostPanelUrl)
            ? name
            : $"[{name}]({_config.ValheimHostPanelUrl})";

        return $"The bot can only read the server's status, never start it — someone with access will need to " +
               $"bring it up from {where}, or from the game server card in this Discord.";
    }
}
