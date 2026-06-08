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
/// Slash commands for the in-house event system:
///   • /event    — gated by EventCommandMinRank; opens the DM creation wizard.
///   • /timezone — open to everyone; set or view the timezone used to interpret
///                 your event time input.
/// The wizard itself lives in <see cref="EventCreationWizard"/>; this handler
/// only validates the command and hands off.
/// </summary>
public sealed class EventCommandHandler
{
    public const string EventCommandName    = "event";
    public const string TimezoneCommandName = "timezone";

    private readonly IServiceProvider _services;
    private readonly ILogger<EventCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly EventCreationWizard _wizard;
    private readonly EventTimeParser _timeParser;
    private readonly EventManagementHandler _management;

    public EventCommandHandler(
        IServiceProvider services,
        ILogger<EventCommandHandler> logger,
        IOptions<BotConfig> config,
        EventCreationWizard wizard,
        EventTimeParser timeParser,
        EventManagementHandler management)
    {
        _services   = services;
        _logger     = logger;
        _config     = config.Value;
        _wizard     = wizard;
        _timeParser = timeParser;
        _management = management;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public static SlashCommandProperties BuildEventCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(EventCommandName)
            .WithDescription("Create, edit, or cancel a clan event")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("create")
                .WithDescription($"Create a clan event — walks you through it in DMs ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("edit")
                .WithDescription("Edit one of your upcoming events")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cancel")
                .WithDescription("Cancel one of your upcoming events")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("sort")
                .WithDescription("Re-post all upcoming events in chronological order (officers only)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("image")
                .WithDescription("Set or remove the banner image on one of your events")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("image", ApplicationCommandOptionType.Attachment, "PNG/JPG/GIF/WebP, max 8 MB", isRequired: false)
                .AddOption("url", ApplicationCommandOptionType.String, "Link to a GIF/image (Tenor, Giphy, or direct)", isRequired: false)
                .AddOption("clear", ApplicationCommandOptionType.Boolean, "Remove the current image instead", isRequired: false))
            .Build();

    public static SlashCommandProperties BuildTimezoneCommand() =>
        new SlashCommandBuilder()
            .WithName(TimezoneCommandName)
            .WithDescription("Set or view the timezone used for your event times")
            .AddOption("zone", ApplicationCommandOptionType.String,
                "e.g. Central, CST, or America/Chicago — leave blank to view your current setting",
                isRequired: false)
            .Build();

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (EventCommandName or TimezoneCommandName))
            return;

        try
        {
            switch (command.Data.Name)
            {
                case EventCommandName:    await HandleEventAsync(command);    break;
                case TimezoneCommandName: await HandleTimezoneAsync(command); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try { await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    // ─── /event ──────────────────────────────────────────────────────────

    private async Task HandleEventAsync(SocketSlashCommand command)
    {
        // Dispatch by subcommand. Each branch owns its own defer/respond — do
        // NOT defer here, or edit/cancel (which defer in the management handler)
        // would double-acknowledge.
        var sub = command.Data.Options.FirstOrDefault()?.Name ?? "create";
        switch (sub)
        {
            case "create": await HandleCreateAsync(command);          break;
            case "edit":   await _management.StartEditAsync(command);  break;
            case "cancel": await _management.StartCancelAsync(command); break;
            case "sort":   await _management.StartSortAsync(command);   break;
            case "image":  await _management.StartImageAsync(command);  break;
            default:       await command.RespondAsync("Unknown subcommand.", ephemeral: true); break;
        }
    }

    private async Task HandleCreateAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        if (guildUser is null || !HasEventPermission(guildUser))
        {
            await command.FollowupAsync(
                $"❌ Creating events is restricted to **{_config.EventCommandMinRank} and above**.",
                ephemeral: true);
            return;
        }

        var started = await _wizard.StartAsync(command.User, command.GuildId.Value);
        if (started)
            await command.FollowupAsync("📬 Check your DMs — I'll walk you through creating the event there.", ephemeral: true);
        else
            await command.FollowupAsync(
                "I couldn't DM you. Enable **Direct Messages** from server members (Privacy Settings) and try again.",
                ephemeral: true);
    }

    // ─── /timezone ───────────────────────────────────────────────────────

    private async Task HandleTimezoneAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var zoneInput = command.Data.Options.FirstOrDefault(o => o.Name == "zone")?.Value as string;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        if (string.IsNullOrWhiteSpace(zoneInput))
        {
            var existing = await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == command.User.Id);
            var msg = existing is null
                ? $"You haven't set a timezone. Defaulting to **{_config.EventDefaultTimeZone}** for your event times. " +
                  "Set yours with `/timezone zone:Central` (or any IANA id)."
                : $"Your timezone is **{existing.IanaId}**.";
            await command.FollowupAsync(msg, ephemeral: true);
            return;
        }

        if (!_timeParser.TryResolveTimeZone(zoneInput, out _, out var iana))
        {
            await command.FollowupAsync(
                $"❌ I didn't recognize \"{zoneInput}\". Try a name like `Central`/`CST` or an IANA id like `America/Chicago`.",
                ephemeral: true);
            return;
        }

        var row = await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == command.User.Id);
        if (row is null)
            db.UserTimeZones.Add(new UserTimeZone { UserId = command.User.Id, IanaId = iana, UpdatedAt = DateTime.UtcNow });
        else
        {
            row.IanaId    = iana;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        await command.FollowupAsync($"✅ Timezone set to **{iana}**. Your event times will be read in this zone.", ephemeral: true);
    }

    // ─── Permission gate (mirrors CompEventCommandHandler) ─────────────────

    private bool HasEventPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.EventCommandMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "EventCommandMinRank '{MinRank}' not found in RankRoles — /event will be admin-only",
                _config.EventCommandMinRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }
}
