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
/// /ticket-panel — HQ-gated slash command that posts (or re-posts) the
/// persistent ticket panel to the configured tickets channel
/// (BotConfig.TicketCenterChannelId). The panel is an embed plus a category
/// select menu; picking a category opens the creation modal, handled by
/// TicketInteractionHandler.
///
/// ── Idempotency ──
/// The panel message ID is stored on BotState.TicketPanelMessageId. Re-running
/// without force:true reports the existing panel and does nothing. force:true
/// deletes the old panel (if still present) and posts a fresh one — use it
/// when the category list or copy changes.
///
/// ── Handler split ──
/// This handler ONLY posts the panel. The select-menu, modal, and control
/// buttons all live on TicketInteractionHandler. The select-menu CustomId
/// (TicketInteractionHandler.CategorySelectId) is referenced here so the two
/// stay in sync without a circular dependency.
/// </summary>
public sealed class TicketPanelCommandHandler
{
    public const string CommandName = "ticket-panel";

    private readonly IServiceProvider _services;
    private readonly ILogger<TicketPanelCommandHandler> _logger;
    private readonly BotConfig _config;

    public TicketPanelCommandHandler(
        IServiceProvider services,
        ILogger<TicketPanelCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Post (or re-post) the ticket panel to the tickets channel (HQ only)")
            .AddOption("force", ApplicationCommandOptionType.Boolean,
                "Re-post even if a panel already exists",
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
            await HandleAsync(command);
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

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        var invoker = guild.GetUser(command.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await command.FollowupAsync("⛔ You need the HQ role to use this command.", ephemeral: true);
            return;
        }

        if (_config.TicketCenterChannelId == 0)
        {
            await command.FollowupAsync(
                "⚠️ `TicketCenterChannelId` is not configured.", ephemeral: true);
            return;
        }

        var channel = guild.GetTextChannel(_config.TicketCenterChannelId);
        if (channel is null)
        {
            await command.FollowupAsync(
                $"⚠️ Could not find the tickets channel (<#{_config.TicketCenterChannelId}>) in this guild.",
                ephemeral: true);
            return;
        }

        var categories = _config.GetTicketCategories();
        if (categories.Count == 0)
        {
            await command.FollowupAsync(
                "⚠️ No ticket categories are configured (`TicketCategoriesCsv`).", ephemeral: true);
            return;
        }

        var force = (bool)(command.Data.Options.FirstOrDefault(o => o.Name == "force")?.Value ?? false);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var state = await GetOrCreateBotStateAsync(db);

        if (state.TicketPanelMessageId is { } existingId && !force)
        {
            var jump = $"https://discord.com/channels/{guild.Id}/{channel.Id}/{existingId}";
            await command.FollowupAsync(
                $"ℹ️ A ticket panel already exists: {jump}\nRe-run with `force:true` to replace it.",
                ephemeral: true);
            return;
        }

        if (force && state.TicketPanelMessageId is { } oldId)
        {
            try
            {
                var old = await channel.GetMessageAsync(oldId);
                if (old is not null) await old.DeleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete previous ticket panel message {MessageId}", oldId);
            }
        }

        var embed      = BuildPanelEmbed(categories);
        var components = TicketInteractionHandler.BuildPanelComponents(categories);

        var posted = await channel.SendMessageAsync(embed: embed, components: components);

        state.TicketPanelMessageId = posted.Id;
        await db.SaveChangesAsync();

        var postedJump = $"https://discord.com/channels/{guild.Id}/{channel.Id}/{posted.Id}";
        await command.FollowupAsync($"✅ Ticket panel posted: {postedJump}", ephemeral: true);

        _logger.LogInformation(
            "Ticket panel posted to channel {ChannelId} as message {MessageId} by {Invoker} (force={Force})",
            channel.Id, posted.Id, command.User.Username, force);
    }

    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;
        if (_config.TicketHqRoleId == 0) return false;
        return invoker.Roles.Any(r => r.Id == _config.TicketHqRoleId);
    }

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

    private Embed BuildPanelEmbed(IReadOnlyList<TicketCategoryDef> categories)
    {
        var lines = categories.Select(c =>
        {
            var prefix = string.IsNullOrWhiteSpace(c.Emoji) ? "•" : c.Emoji;
            var anon   = c.AnonymousAllowed ? "  *(can be submitted anonymously)*" : "";
            return $"{prefix} **{c.Label}**{anon}";
        });

        var builder = new EmbedBuilder()
            .WithTitle("🎫 189th Support Tickets")
            .WithColor(new Color(0x58, 0x65, 0xF2))
            .WithDescription(
                "Need to reach HQ? Pick the category that best fits from the menu below and I'll "
                + "open a **private thread** just for you — only you and the relevant team can see it.\n\n"
                + string.Join("\n", lines)
                + "\n​")
            .AddField("How it works",
                "1️⃣ Pick a category below.\n"
                + "2️⃣ Fill in the short form (subject + a few details).\n"
                + "3️⃣ Your private ticket opens and HQ is notified — reply right in that thread.\n"
                + "4️⃣ It's closed once resolved; you'll still be able to reach HQ anytime.\n​")
            .AddField("Good to know",
                "• **One ticket per issue** — add details inside your ticket instead of opening more.\n"
                + "• **Report a Member** can be sent **anonymously** — you'll continue in DMs with HQ and your name stays hidden.\n"
                + "• HQ replies as soon as someone's available, so hang tight after opening one.")
            .WithFooter("Choose a category below to get started.");

        if (!string.IsNullOrWhiteSpace(_config.TicketPanelThumbnailUrl))
            builder.WithThumbnailUrl(_config.TicketPanelThumbnailUrl);

        return builder.Build();
    }
}
