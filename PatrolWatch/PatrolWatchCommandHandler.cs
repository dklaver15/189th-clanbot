using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.PatrolWatch;

/// <summary>
/// /patrol slash command surface. Three subcommands:
///
///   /patrol off   — opt out of being shown on Patrol Watch embeds
///   /patrol on    — opt back in (default state if no row exists)
///   /patrol info  — explain how Patrol Watch works and why a member might
///                   not be appearing on the embed despite being in voice
///                   playing the game (the answer is almost always "console
///                   account not linked to Discord")
///
/// Open to all members, ephemeral responses. The actual opt-out filtering
/// happens in PatrolWatchService.DoRecomputeAsync via the PatrolWatchOptOuts
/// table; this handler is just the CRUD UI on top of it.
///
/// NOTE: also keep CommandsCommandHandler.BuildCatalog in sync when changing
/// /patrol — same convention as every other slash-command surface in the bot.
/// </summary>
public sealed class PatrolWatchCommandHandler
{
    public const string CommandName = "patrol";

    private readonly IServiceProvider _services;
    private readonly ILogger<PatrolWatchCommandHandler> _logger;

    public PatrolWatchCommandHandler(
        IServiceProvider services,
        ILogger<PatrolWatchCommandHandler> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Manage your Patrol Watch presence")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("off")
                .WithDescription("Hide me from Patrol Watch embeds")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("on")
                .WithDescription("Show me again on Patrol Watch embeds (default)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("info")
                .WithDescription("How Patrol Watch works (and why I might not be on it)")
                .WithType(ApplicationCommandOptionType.SubCommand))
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
            catch { /* swallowed */ }
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

        var sub = command.Data.Options.FirstOrDefault();
        var subName = sub?.Name ?? string.Empty;

        switch (subName)
        {
            case "off":
                await HandleOffAsync(command);
                break;
            case "on":
                await HandleOnAsync(command);
                break;
            case "info":
                await HandleInfoAsync(command);
                break;
            default:
                await command.FollowupAsync(
                    "Unknown subcommand. Use `/patrol off`, `/patrol on`, or `/patrol info`.",
                    ephemeral: true);
                break;
        }
    }

    private async Task HandleOffAsync(SocketSlashCommand command)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var guildId = command.GuildId!.Value;
        var userId  = command.User.Id;

        var existing = await db.PatrolWatchOptOuts
            .FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId);

        if (existing != null)
        {
            await command.FollowupAsync(
                "You're already opted out of Patrol Watch. Use `/patrol on` to show up again.",
                ephemeral: true);
            return;
        }

        db.PatrolWatchOptOuts.Add(new PatrolWatchOptOut
        {
            GuildId       = guildId,
            UserId        = userId,
            OptedOutAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await command.FollowupAsync(
            "✅ You're now hidden from Patrol Watch embeds. Squads will form without showing your name. Use `/patrol on` to undo.",
            ephemeral: true);
    }

    private async Task HandleOnAsync(SocketSlashCommand command)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var guildId = command.GuildId!.Value;
        var userId  = command.User.Id;

        var existing = await db.PatrolWatchOptOuts
            .FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId);

        if (existing == null)
        {
            await command.FollowupAsync(
                "You're already opted in to Patrol Watch (the default).",
                ephemeral: true);
            return;
        }

        db.PatrolWatchOptOuts.Remove(existing);
        await db.SaveChangesAsync();

        await command.FollowupAsync(
            "✅ You'll show up on Patrol Watch embeds again the next time you're in voice playing a tracked game.",
            ephemeral: true);
    }

    private async Task HandleInfoAsync(SocketSlashCommand command)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Patrol Watch")
            .WithDescription(
                "When 3 or more members are in the same voice channel and Discord shows them all playing the same tracked game, " +
                "ClanGuard posts a rolling embed in #lfg so anyone scrolling by can see what's running and jump in.\n\n" +
                "**Why might I not be showing up?**\n" +
                "• Most common: your console isn't linked to Discord. PS5 and Xbox players have to link their account under " +
                "*User Settings → Connections* — without that link, Discord can't see what you're playing on console.\n" +
                "• Activity privacy is off in *User Settings → Activity Privacy*. Toggle on \"Display current activity as a status message.\"\n" +
                "• You used `/patrol off` (use `/patrol on` to undo).\n" +
                "• The squad is below 3 — duos don't trigger the embed.\n" +
                "• Your voice channel is excluded by design (the events category and AFK rooms — events have their own announcement system, AFK rooms aren't ops).\n\n" +
                "**Privacy**\n" +
                "Use `/patrol off` to hide yourself from these embeds. Squads still form without you on the roster line.")
            .WithColor(new Color(0xC9, 0xA6, 0x47))
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }
}