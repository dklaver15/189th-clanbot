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
/// /setup-officer-app — HQ-only slash command that posts (or re-posts) the
/// persistent "Apply for Officer" button to the configured instructions
/// channel. The button's CustomId is stable across restarts so Discord's
/// interaction routing continues to work for messages posted in past runs.
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
/// live on OfficerApplicationModalHandler. The CustomId
/// "officer_app:open" is declared as a public const here so the modal
/// handler can subscribe to it without circular dependencies, but only
/// the modal handler subscribes to ButtonExecuted.
/// </summary>
public sealed class OfficerApplicationSetupCommandHandler
{
    public const string CommandName = "setup-officer-app";

    private readonly IServiceProvider _services;
    private readonly ILogger<OfficerApplicationSetupCommandHandler> _logger;
    private readonly BotConfig _config;

    public OfficerApplicationSetupCommandHandler(
        IServiceProvider services,
        ILogger<OfficerApplicationSetupCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Post (or re-post) the officer application button (HQ only)")
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
                "⛔ You need the HQ role to use this command.",
                ephemeral: true);
            return;
        }

        // ── Validate config ──────────────────────────────────────────
        if (_config.OfficerAppInstructionsChannelId == 0)
        {
            await command.FollowupAsync(
                "⚠️ `OfficerAppInstructionsChannelId` is not configured.",
                ephemeral: true);
            return;
        }

        var instructionsChannel = guild.GetTextChannel(_config.OfficerAppInstructionsChannelId);
        if (instructionsChannel is null)
        {
            await command.FollowupAsync(
                $"⚠️ Could not find the instructions channel "
                + $"(<#{_config.OfficerAppInstructionsChannelId}>) in this guild.",
                ephemeral: true);
            return;
        }

        var force = (bool)(command.Data.Options.FirstOrDefault(o => o.Name == "force")?.Value ?? false);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var state = await GetOrCreateBotStateAsync(db);

        // ── Idempotency check ────────────────────────────────────────
        if (state.OfficerAppButtonMessageId is { } existingId && !force)
        {
            var jumpUrl = $"https://discord.com/channels/{guild.Id}/{instructionsChannel.Id}/{existingId}";
            await command.FollowupAsync(
                $"ℹ️ A button message already exists: {jumpUrl}\n"
                + "Re-run with `force:true` to delete it and post a fresh one.",
                ephemeral: true);
            return;
        }

        // ── If forcing, delete the old message first ─────────────────
        if (force && state.OfficerAppButtonMessageId is { } oldId)
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
                    "Could not delete previous officer-app button message {MessageId}",
                    oldId);
            }
        }

        // ── Post the embed + button ──────────────────────────────────
        var embed       = BuildInstructionsEmbed();
        var components  = BuildApplyButton();

        var posted = await instructionsChannel.SendMessageAsync(
            embed: embed, components: components);

        state.OfficerAppButtonMessageId = posted.Id;
        await db.SaveChangesAsync();

        var postedJumpUrl = $"https://discord.com/channels/{guild.Id}/{instructionsChannel.Id}/{posted.Id}";
        await command.FollowupAsync(
            $"✅ Posted the officer application button: {postedJumpUrl}",
            ephemeral: true);

        _logger.LogInformation(
            "Officer-app button posted to channel {ChannelId} as message {MessageId} by {Invoker} (force={Force})",
            instructionsChannel.Id, posted.Id, command.User.Username, force);
    }

    /// <summary>
    /// HQ permission gate. Admins always pass; otherwise the invoker must
    /// hold the configured HQ role ID.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;
        if (_config.OfficerAppHqRoleId == 0) return false;
        return invoker.Roles.Any(r => r.Id == _config.OfficerAppHqRoleId);
    }

    /// <summary>
    /// Singleton BotState load — same pattern AutoPromotionService uses.
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
            .WithTitle("189th Officer Application")
            .WithColor(new Color(0xC9, 0xA2, 0x27)) // clan gold; tune in code if needed
            .WithDescription(
                "The 189th is always looking for dedicated members to step up and help lead. "
                + "If you're ready to take on more responsibility and shape the future of the clan, "
                + "this is your path forward."
                + "\n\u200B")
            .AddField("Eligibility",
                "• Rank of **SGT** or higher\n"
                + "• In good standing (not currently AWOL)"
                + "\n\u200B")
            .AddField("What to Expect",
                "Click the button below to open the application. You'll answer three short "
                + "questions about where you want to contribute, ideas you'd bring, and how "
                + "you'd handle a leadership situation. The form takes about 5 minutes.\n\n"
                + "Your application is reviewed by HQ alongside your service record — rank, "
                + "tenure, event attendance, voice activity, and message activity are all "
                + "considered. You don't need to list any of that yourself; we already have it."
                + "\n\u200B")
            .AddField("After You Apply",
                "HQ will review and reach out with a decision. If you're not selected, you're "
                + "welcome to apply again in the future as you continue to contribute.")
            .WithFooter("Press the button below to begin.");

        if (!string.IsNullOrWhiteSpace(_config.OfficerAppInstructionsThumbnailUrl))
            builder.WithThumbnailUrl(_config.OfficerAppInstructionsThumbnailUrl);

        if (!string.IsNullOrWhiteSpace(_config.OfficerAppInstructionsBannerImageUrl))
            builder.WithImageUrl(_config.OfficerAppInstructionsBannerImageUrl);

        return builder.Build();
    }

    private static MessageComponent BuildApplyButton() =>
        new ComponentBuilder()
            .WithButton("Apply for Officer", OfficerApplicationModalHandler.OpenButtonId, ButtonStyle.Primary, new Emoji("📋"))
            .Build();
}