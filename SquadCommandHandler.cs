using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /squads slash command, which fetches every (human) member
/// currently connected to the events voice channel and randomly assigns
/// them to 4-man squads. Built for the 189th's Vendetta Battlefield 6
/// Portal mode — squad assignment runs BEFORE the game starts so the
/// running officer can call out party assignments.
///
/// ── Voice channel ──
/// Reads from BotConfig.EventsVoiceChannelId — the same channel ID used
/// by EventAttendanceSnapshotService's legacy fallback path. Immune to
/// channel renames.
///
/// ── Filtering ──
/// Bots in the VC (music bots, utility bots, ClanGuard itself) are
/// excluded so they don't take squad slots. Reserves and Guests are NOT
/// excluded — if they're in the VC for an event, they get drafted like
/// anyone else.
///
/// ── Squad sizing ──
/// Defaults to 4-man squads (DefaultSquadSize const) for Vendetta, but
/// the officer can override per invocation with the optional `size`
/// integer parameter (e.g. /squads size:3). The final squad is smaller
/// if the player count isn't divisible by the chosen size; partial
/// squads render with just the assigned names — no "needs more" tag,
/// since the running officer is the only one seeing the draw and
/// already knows how many they have. On reroll the size is preserved by
/// encoding it in the button's custom id.
///
/// ── Visibility ──
/// Ephemeral. Only the officer who ran the command sees the result, so
/// they can reroll repeatedly to balance teams before announcing the
/// final draw out loud or in chat.
///
/// ── Reroll ──
/// The result message carries a 🎲 Reroll button. Pressing it re-queries
/// the VC roster (so late joiners / early leavers are picked up) and
/// re-shuffles in-place via UpdateAsync. No invoker restriction needed:
/// ephemeral messages are only visible to the invoker, so by construction
/// nobody else can press the button.
///
/// ── Permissions ──
/// Officer+ (ManageRoles or Administrator, plus members of any exempt
/// role per BotConfig.ExemptRoles). Mirrors SlashCommandHandler's
/// HasElevatedPermissions check verbatim — SyncWithHandlers:
/// SlashCommandHandler.HasElevatedPermissions,
/// CleanupCalendarDupesCommandHandler.HasElevatedPermissions,
/// CommandsCommandHandler.BuildCatalog (Officer+ tier).
/// </summary>
public class SquadCommandHandler
{
    public const string CommandName = "squads";

    /// <summary>
    /// Default squad size when the optional `size` slash parameter is
    /// omitted. Vendetta uses 4-man squads; the officer can override per
    /// invocation (e.g. `/squads size:3`) for other modes.
    /// </summary>
    private const int DefaultSquadSize = 4;

    /// <summary>
    /// Name of the optional integer slash parameter controlling squad size.
    /// SyncWithBuilder: DiscordBotService.cs /squads SlashCommandOptionBuilder.
    /// </summary>
    private const string SizeOptionName = "size";

    /// <summary>
    /// Custom-id prefix for the reroll button. The chosen squad size is
    /// appended after the colon (e.g. "squads_reroll:3") so rerolls preserve
    /// the size the officer originally picked. No invoker ID encoded because
    /// the result message is ephemeral — only the officer who ran the command
    /// can see (and therefore press) the button in the first place.
    /// </summary>
    private const string RerollButtonPrefix = "squads_reroll:";

    private readonly ILogger<SquadCommandHandler> _logger;
    private readonly BotConfig _config;

    public SquadCommandHandler(
        ILogger<SquadCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ButtonExecuted += OnButtonExecutedAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleSquadsAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch
            {
                try { await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
                catch { /* swallow */ }
            }
        }
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(RerollButtonPrefix, StringComparison.Ordinal)) return;

        try
        {
            await HandleRerollAsync(component);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /squads reroll button");
            try { await component.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task HandleSquadsAsync(SocketSlashCommand command)
    {
        // Shuffle work is synchronous and trivial, so skip DeferAsync and reply
        // directly inside Discord's 3-second response window.
        if (command.GuildId is null)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasElevatedPermissions(caller))
        {
            await command.RespondAsync(
                "❌ You don't have permission to use this command (Officer+ only).",
                ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.RespondAsync("Could not determine the guild for this command.", ephemeral: true);
            return;
        }

        var players = GetEventVcPlayers(guild);
        if (players.Count == 0)
        {
            await command.RespondAsync(
                $"📭 Nobody is currently in <#{_config.EventsVoiceChannelId}>. " +
                "Have everyone join the events VC first, then re-run `/squads`.",
                ephemeral: true);
            return;
        }

        var squadSize = ResolveSquadSize(command);

        var squads = BuildSquads(players, squadSize);
        var embed = BuildEmbed(squads, players.Count, caller);

        await command.RespondAsync(embed: embed, components: BuildRerollButton(squadSize), ephemeral: true);

        _logger.LogInformation(
            "/squads: {Caller} drew {SquadCount} squad(s) of {SquadSize} from {PlayerCount} player(s) in events VC",
            caller.Username, squads.Count, squadSize, players.Count);
    }

    private async Task HandleRerollAsync(SocketMessageComponent component)
    {
        // Result messages are ephemeral, so only the original invoker can ever
        // see (and therefore press) this button — no explicit invoker check.
        var guild = (component.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        var caller = component.User as SocketGuildUser;
        if (caller is null) return;

        var squadSize = ParseRerollSquadSize(component.Data.CustomId);

        var players = GetEventVcPlayers(guild);
        if (players.Count == 0)
        {
            await component.UpdateAsync(msg =>
            {
                msg.Embed = new EmbedBuilder()
                    .WithTitle("🎲 Squad Draw — Reroll")
                    .WithColor(Color.Red)
                    .WithDescription(
                        $"📭 Nobody is currently in <#{_config.EventsVoiceChannelId}>. " +
                        "Have everyone rejoin the events VC, then press 🎲 Reroll again.")
                    .WithCurrentTimestamp()
                    .Build();
                msg.Components = BuildRerollButton(squadSize);
            });
            return;
        }

        var squads = BuildSquads(players, squadSize);
        var embed = BuildEmbed(squads, players.Count, caller, isReroll: true);

        await component.UpdateAsync(msg =>
        {
            msg.Embed = embed;
            msg.Components = BuildRerollButton(squadSize);
        });

        _logger.LogInformation(
            "/squads reroll: {Caller} re-drew {SquadCount} squad(s) of {SquadSize} from {PlayerCount} player(s)",
            caller.Username, squads.Count, squadSize, players.Count);
    }

    /// <summary>
    /// Returns the human members currently connected to the configured
    /// events VC. Bots are filtered so music / utility bots never take a
    /// squad slot. Returns an empty list if the channel can't be resolved
    /// (deleted, mis-IDed in config, or not yet cached) — callers treat
    /// that the same as an empty VC.
    /// </summary>
    private List<SocketGuildUser> GetEventVcPlayers(SocketGuild guild)
    {
        var vc = guild.GetVoiceChannel(_config.EventsVoiceChannelId);
        if (vc is null) return new List<SocketGuildUser>();

        return vc.ConnectedUsers
            .Where(u => !u.IsBot)
            .ToList();
    }

    /// <summary>
    /// Reads the optional `size` integer parameter, falling back to
    /// <see cref="DefaultSquadSize"/> when it's omitted. The slash-command
    /// builder already clamps to 1..30, so no extra bounds check here.
    /// </summary>
    private static int ResolveSquadSize(SocketSlashCommand command)
    {
        var opt = command.Data.Options
            .FirstOrDefault(o => o.Name == SizeOptionName);
        if (opt?.Value is long size && size >= 1)
            return (int)size;
        return DefaultSquadSize;
    }

    /// <summary>
    /// Recovers the squad size encoded in the reroll button's custom id
    /// (see <see cref="RerollButtonPrefix"/>). Falls back to
    /// <see cref="DefaultSquadSize"/> if the suffix is missing or malformed.
    /// </summary>
    private static int ParseRerollSquadSize(string customId)
    {
        var suffix = customId.Substring(RerollButtonPrefix.Length);
        if (int.TryParse(suffix, out var size) && size >= 1)
            return size;
        return DefaultSquadSize;
    }

    /// <summary>
    /// Fisher-Yates shuffle then chunk into <paramref name="squadSize"/>-man
    /// squads. The final squad may have 1..<paramref name="squadSize"/> members
    /// depending on the total player count. Uses <see cref="Random.Shared"/> so
    /// we don't allocate a new RNG per invocation.
    /// </summary>
    private static List<List<SocketGuildUser>> BuildSquads(List<SocketGuildUser> players, int squadSize)
    {
        var rng = Random.Shared;
        var shuffled = players.ToList();
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        var squads = new List<List<SocketGuildUser>>();
        for (int i = 0; i < shuffled.Count; i += squadSize)
        {
            squads.Add(shuffled.Skip(i).Take(squadSize).ToList());
        }
        return squads;
    }

    private Embed BuildEmbed(
        List<List<SocketGuildUser>> squads,
        int totalPlayers,
        SocketGuildUser caller,
        bool isReroll = false)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < squads.Count; i++)
        {
            sb.AppendLine($"**Squad {i + 1}**");
            foreach (var player in squads[i])
            {
                // Mentions inside an embed render as @Name but do NOT trigger
                // notifications — visual link without ping spam.
                sb.AppendLine($"• {player.Mention}");
            }
            sb.AppendLine();
        }

        var title = isReroll ? "🎲 Squad Draw — Rerolled" : "🎲 Squad Draw";
        var footer = $"{totalPlayers} player{(totalPlayers == 1 ? "" : "s")} • Drawn by {caller.DisplayName}";

        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(sb.ToString().TrimEnd())
            .WithColor(new Color(201, 166, 71)) // 0xC9A647 — 189th gold
            .WithFooter(footer)
            .WithCurrentTimestamp()
            .Build();
    }

    private static MessageComponent BuildRerollButton(int squadSize) =>
        new ComponentBuilder()
            .WithButton("🎲 Reroll", RerollButtonPrefix + squadSize, ButtonStyle.Secondary)
            .Build();

    /// <summary>
    /// Officer+ permission check. SyncWithHandlers:
    /// SlashCommandHandler.HasElevatedPermissions and
    /// CleanupCalendarDupesCommandHandler.HasElevatedPermissions —
    /// keep this body identical to those.
    /// </summary>
    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;
        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}