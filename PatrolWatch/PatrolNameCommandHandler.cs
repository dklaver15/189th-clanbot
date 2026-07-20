using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.PatrolWatch;

/// <summary>
/// /patrol-name — officer CRUD over the canonical display names shown on Patrol
/// Watch embeds (<see cref="PatrolWatchNameOverride"/>).
///
///   /patrol-name set   user:@u  tag:&lt;text&gt;  — force this exact name on the roster line
///   /patrol-name clear user:@u                    — remove the override (back to Discord name)
///
/// ── Why a separate command from /patrol ──
/// /patrol (off/on/info) is self-service — it only ever acts on the invoker. This
/// one acts on ANOTHER member, so it's an officer action and lives on its own gated
/// surface rather than being bolted onto the open command.
///
/// ── Why "patrol-name" and not "palworld-name" ──
/// The override applies to every tracked game's embed, not just Palworld. The
/// cache-fallback bug it works around (Nickname → GlobalName degradation under
/// presence churn) is game-agnostic; Palworld-on-Mac is merely its loudest trigger.
///
/// ── Permission ──
/// Manage Nicknames (or Administrator). This is literally overriding how a member's
/// name is displayed, so it mirrors the permission Discord uses for nicknames and
/// the bot's own /setnick gate — no new config key for one command.
///
/// NOTE: keep CommandsCommandHandler.BuildCatalog in sync when changing /patrol-name.
/// </summary>
public sealed class PatrolNameCommandHandler
{
    public const string CommandName = "patrol-name";

    private const int MaxNameLength = 40;

    private readonly IServiceProvider _services;
    private readonly ILogger<PatrolNameCommandHandler> _logger;

    public PatrolNameCommandHandler(
        IServiceProvider services,
        ILogger<PatrolNameCommandHandler> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Set the name shown for a member on Patrol Watch embeds (Manage Nicknames)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("set")
                .WithDescription("Force an exact name on a member's Patrol Watch roster line")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "The member", isRequired: true)
                .AddOption("tag", ApplicationCommandOptionType.String,
                    "Exactly how their name should appear", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("clear")
                .WithDescription("Remove a member's override (back to their Discord name)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "The member", isRequired: true))
            .Build();

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
            catch { /* interaction already gone */ }
        }
    }

    private async Task DispatchAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!HasPermission(caller))
        {
            await command.FollowupAsync(
                "❌ You need the **Manage Nicknames** permission to use this command.",
                ephemeral: true);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        switch (sub?.Name)
        {
            case "set":   await HandleSetAsync(command, guildId, sub, caller); break;
            case "clear": await HandleClearAsync(command, guildId, sub);       break;
            default:
                await command.FollowupAsync(
                    "Unknown subcommand. Use `/patrol-name set` or `/patrol-name clear`.",
                    ephemeral: true);
                break;
        }
    }

    private async Task HandleSetAsync(
        SocketSlashCommand command, ulong guildId, SocketSlashCommandDataOption sub, SocketGuildUser caller)
    {
        var target = sub.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var tag = (sub.Options.FirstOrDefault(o => o.Name == "tag")?.Value as string ?? "").Trim();

        if (target is null)
        {
            await command.FollowupAsync("Pick a member to set the name for.", ephemeral: true);
            return;
        }

        if (tag.Length == 0)
        {
            await command.FollowupAsync(
                "The name can't be blank. To remove an override use `/patrol-name clear`.",
                ephemeral: true);
            return;
        }

        if (tag.Length > MaxNameLength)
        {
            await command.FollowupAsync(
                $"That name is too long ({tag.Length} chars). Keep it to {MaxNameLength} or fewer.",
                ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Upsert on (GuildId, UserId): one override per member, edited in place.
        var row = await db.PatrolWatchNameOverrides
            .FirstOrDefaultAsync(n => n.GuildId == guildId && n.UserId == target.Id);

        if (row is null)
        {
            db.PatrolWatchNameOverrides.Add(new PatrolWatchNameOverride
            {
                GuildId       = guildId,
                UserId        = target.Id,
                CanonicalName = tag,
                SetByUserId   = caller.Id,
                UpdatedAtUtc  = DateTime.UtcNow,
            });
        }
        else
        {
            row.CanonicalName = tag;
            row.SetByUserId   = caller.Id;
            row.UpdatedAtUtc  = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "PatrolWatch name override set: {Target} → '{Tag}' by {Caller}",
            target.Id, tag, caller.Id);

        await command.FollowupAsync(
            $"✅ <@{target.Id}> will now show as **{tag}** on Patrol Watch embeds. " +
            "It'll take effect the next time their patrol embed updates.",
            ephemeral: true);
    }

    private async Task HandleClearAsync(
        SocketSlashCommand command, ulong guildId, SocketSlashCommandDataOption sub)
    {
        var target = sub.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        if (target is null)
        {
            await command.FollowupAsync("Pick a member to clear.", ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var row = await db.PatrolWatchNameOverrides
            .FirstOrDefaultAsync(n => n.GuildId == guildId && n.UserId == target.Id);

        if (row is null)
        {
            await command.FollowupAsync(
                $"<@{target.Id}> has no Patrol Watch name override — nothing to clear.",
                ephemeral: true);
            return;
        }

        db.PatrolWatchNameOverrides.Remove(row);
        await db.SaveChangesAsync();

        _logger.LogInformation("PatrolWatch name override cleared: {Target}", target.Id);

        await command.FollowupAsync(
            $"✅ Cleared. <@{target.Id}> will show with their normal Discord name again.",
            ephemeral: true);
    }

    /// <summary>
    /// Manage Nicknames or Administrator. Mirrors Discord's own nickname permission
    /// and the bot's /setnick gate — this command controls displayed names, so the
    /// same authority applies.
    /// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (patrolName).
    /// </summary>
    private static bool HasPermission(SocketGuildUser user) =>
        user.GuildPermissions.ManageNicknames || user.GuildPermissions.Administrator;
}
