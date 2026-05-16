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
/// /setup-gamertags — slash command that posts (or re-posts) the persistent
/// "Enter Gamertags" button to the configured instructions channel. The
/// button's CustomId is stable across restarts so Discord's interaction
/// routing continues to work for messages posted in past runs.
///
/// ── Idempotency ──
/// The message ID of the posted button is stored on BotState. Re-running
/// the command without `force:true` reports the existing message and does
/// nothing. `force:true` deletes the previous message (if still present)
/// and posts a fresh one. Use the force flag when the embed copy changes
/// or the previous message has been deleted by a moderator.
///
/// ── Handler split ──
/// This handler ONLY posts the button — the button click and modal flow
/// live on GamertagCommandHandler. The CustomId "gamertags:open" is
/// declared as a public const on that handler so this setup handler can
/// reference it without circular dependencies, and only GamertagCommandHandler
/// subscribes to ButtonExecuted.
///
/// ── Permission gate ──
/// Administrator always passes. Otherwise the invoker must hold the
/// configured GamertagSetupRoleId. If that role ID is unset (0), only
/// Administrators can run the command.
/// </summary>
public sealed class GamertagSetupCommandHandler
{
    public const string CommandName = "setup-gamertags";

    private readonly IServiceProvider _services;
    private readonly ILogger<GamertagSetupCommandHandler> _logger;
    private readonly BotConfig _config;

    public GamertagSetupCommandHandler(
        IServiceProvider services,
        ILogger<GamertagSetupCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Post (or re-post) the gamertags button (HQ only)")
            .AddOption("force", ApplicationCommandOptionType.Boolean,
                "Re-post even if a button message already exists",
                isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleSetupAsync(command);
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
            catch { /* swallow */ }
        }
    }

    private async Task HandleSetupAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Could not resolve the guild.", ephemeral: true);
            return;
        }

        var invoker = guild.GetUser(command.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await command.FollowupAsync(
                "⛔ You don't have permission to run this command.",
                ephemeral: true);
            return;
        }

        // ── Validate config ──────────────────────────────────────────
        if (_config.GamertagInstructionsChannelId == 0)
        {
            await command.FollowupAsync(
                "⚠️ `GamertagInstructionsChannelId` is not configured.",
                ephemeral: true);
            return;
        }

        var instructionsChannel = guild.GetTextChannel(_config.GamertagInstructionsChannelId);
        if (instructionsChannel is null)
        {
            await command.FollowupAsync(
                $"⚠️ Could not find the instructions channel "
                + $"(<#{_config.GamertagInstructionsChannelId}>) in this guild.",
                ephemeral: true);
            return;
        }

        var force = (bool)(command.Data.Options.FirstOrDefault(o => o.Name == "force")?.Value ?? false);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var state = await GetOrCreateBotStateAsync(db);

        // ── Idempotency check ────────────────────────────────────────
        if (state.GamertagButtonMessageId is { } existingId && !force)
        {
            var jumpUrl = $"https://discord.com/channels/{guild.Id}/{instructionsChannel.Id}/{existingId}";
            await command.FollowupAsync(
                $"ℹ️ A button message already exists: {jumpUrl}\n"
                + "Re-run with `force:true` to delete it and post a fresh one.",
                ephemeral: true);
            return;
        }

        // ── If forcing, delete the old message first ─────────────────
        if (force && state.GamertagButtonMessageId is { } oldId)
        {
            try
            {
                var oldMessage = await instructionsChannel.GetMessageAsync(oldId);
                if (oldMessage is not null)
                    await oldMessage.DeleteAsync();
            }
            catch (Exception ex)
            {
                // Non-fatal — the message may have been manually deleted
                // already. Log and continue to post the replacement.
                _logger.LogWarning(ex,
                    "Could not delete previous gamertags button message {MessageId}",
                    oldId);
            }
        }

        // ── Post the embed + button ──────────────────────────────────
        var embed       = BuildInstructionsEmbed();
        var components  = BuildEnterButton();

        var posted = await instructionsChannel.SendMessageAsync(
            embed: embed, components: components);

        state.GamertagButtonMessageId = posted.Id;
        await db.SaveChangesAsync();

        var postedJumpUrl = $"https://discord.com/channels/{guild.Id}/{instructionsChannel.Id}/{posted.Id}";
        await command.FollowupAsync(
            $"✅ Posted the gamertags button: {postedJumpUrl}",
            ephemeral: true);

        _logger.LogInformation(
            "Gamertags button posted to channel {ChannelId} as message {MessageId} by {Invoker} (force={Force})",
            instructionsChannel.Id, posted.Id, command.User.Username, force);
    }

    /// <summary>
    /// Admins always pass; otherwise the invoker must hold the configured
    /// setup role ID. If GamertagSetupRoleId is unset (0), only Administrators
    /// can run the command.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;
        if (_config.GamertagSetupRoleId == 0) return false;
        return invoker.Roles.Any(r => r.Id == _config.GamertagSetupRoleId);
    }

    /// <summary>
    /// Singleton BotState load — same pattern AutoPromotionService and
    /// OfficerApplicationSetupCommandHandler use.
    /// </summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db)
    {
        var state = await db.BotStates.FirstOrDefaultAsync();
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync();
        }
        return state;
    }

    private Embed BuildInstructionsEmbed()
    {
        var builder = new EmbedBuilder()
            .WithTitle("🎮 Submit Your Gamertags")
            .WithColor(new Color(0xC9, 0xA2, 0x27)) // clan gold
            .WithDescription(
                "So the rest of the 189th can find you across platforms, drop your gamertags "
                + "into the roster. Anything you don't have, just leave blank."
                + "\n\u200B")
            .AddField("What You'll Enter",
                "**Page 1:** EA · Steam · PSN\n"
                + "**Page 2:** Xbox · Embark · Bungie"
                + "\n\u200B")
            .AddField("How Long",
                "About 30 seconds. Two short pages — submit page 1, then click "
                + "**Continue to Page 2** to finish. Nothing saves until page 2 is submitted."
                + "\n\u200B")
            .AddField("Updating Later",
                "Click the button again any time to update or correct your entries. "
                + "We'll find your existing row and update it in place.")
            .WithFooter("Press the button below to begin.");

        if (!string.IsNullOrWhiteSpace(_config.GamertagInstructionsThumbnailUrl))
            builder.WithThumbnailUrl(_config.GamertagInstructionsThumbnailUrl);

        if (!string.IsNullOrWhiteSpace(_config.GamertagInstructionsBannerImageUrl))
            builder.WithImageUrl(_config.GamertagInstructionsBannerImageUrl);

        return builder.Build();
    }

    private static MessageComponent BuildEnterButton() =>
        new ComponentBuilder()
            .WithButton("Enter Gamertags", GamertagCommandHandler.OpenButtonId, ButtonStyle.Primary, new Emoji("🎮"))
            .Build();
}
