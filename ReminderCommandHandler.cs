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
/// The <c>/reminder</c> slash command — a shared, officer-gated tool for
/// scheduling one-off or recurring announcements. Four subcommands:
///   • create — opens the DM wizard (<see cref="ReminderCreationWizard"/>).
///   • list   — ephemeral list of upcoming reminders in this server.
///   • cancel — pick an upcoming reminder from a menu and cancel it.
///   • edit   — pick one and re-open the DM wizard seeded with its values.
///
/// Gated by <see cref="BotConfig.ReminderCommandMinRank"/> (default 2ndLT — the
/// same "any officer" floor as /event) plus Administrator / Manage Roles. The
/// posting itself is done later by <see cref="ReminderSchedulerService"/>.
/// </summary>
public sealed class ReminderCommandHandler
{
    public const string CommandName = "reminder";
    private const string CancelMenuId = "remcmd:cancel";
    private const string EditMenuId   = "remcmd:edit";

    private readonly IServiceProvider _services;
    private readonly ILogger<ReminderCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly ReminderCreationWizard _wizard;

    public ReminderCommandHandler(
        IServiceProvider services,
        ILogger<ReminderCommandHandler> logger,
        IOptions<BotConfig> config,
        ReminderCreationWizard wizard)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
        _wizard   = wizard;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.SelectMenuExecuted   += OnSelectMenuAsync;
    }

    public static SlashCommandProperties BuildCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription($"Schedule an announcement reminder ({minRank}+ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("create")
                .WithDescription($"Create a reminder — walks you through it in DMs ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list")
                .WithDescription("Show upcoming scheduled reminders")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cancel")
                .WithDescription("Cancel an upcoming reminder")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("edit")
                .WithDescription("Edit an upcoming reminder")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            if (command.GuildId is null || command.User is not SocketGuildUser guildUser)
            {
                await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
                return;
            }

            if (!HasPermission(guildUser))
            {
                await command.RespondAsync(
                    $"❌ Scheduling reminders is restricted to **{_config.ReminderCommandMinRank} and above**.",
                    ephemeral: true);
                return;
            }

            var sub = command.Data.Options.FirstOrDefault()?.Name ?? "create";
            switch (sub)
            {
                case "create": await HandleCreateAsync(command);           break;
                case "list":   await HandleListAsync(command);             break;
                case "cancel": await HandlePickAsync(command, forEdit: false); break;
                case "edit":   await HandlePickAsync(command, forEdit: true);  break;
                default:       await command.RespondAsync("Unknown subcommand.", ephemeral: true); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /reminder");
            try { await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    // ─── create ──────────────────────────────────────────────────────────

    private async Task HandleCreateAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var started = await _wizard.StartCreateAsync(command.User, command.GuildId!.Value);
        if (started)
            await command.FollowupAsync("📬 Check your DMs — I'll walk you through creating the reminder there.", ephemeral: true);
        else
            await command.FollowupAsync(
                "I couldn't DM you. Enable **Direct Messages** from server members (Privacy Settings) and try again.",
                ephemeral: true);
    }

    // ─── list ────────────────────────────────────────────────────────────

    private async Task HandleListAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var upcoming = await GetUpcomingAsync(command.GuildId!.Value);
        if (upcoming.Count == 0)
        {
            await command.FollowupAsync("There are no upcoming reminders scheduled. Use `/reminder create` to add one.", ephemeral: true);
            return;
        }

        var eb = new EmbedBuilder()
            .WithTitle("🔔 Upcoming reminders")
            .WithColor(new Color(0x5865F2));

        foreach (var r in upcoming.Take(25))
        {
            var repeat = r.Frequency is null ? "" : $" • 🔁 {r.Frequency}";
            eb.AddField(
                Truncate(r.Title, 240),
                $"{EventTimeParser.Stamp(r.NextFireUtc, 'F')} ({EventTimeParser.Stamp(r.NextFireUtc, 'R')})\n" +
                $"→ <#{r.ChannelId}> • by {r.CreatorName}{repeat}",
                inline: false);
        }
        if (upcoming.Count > 25)
            eb.WithFooter($"Showing the next 25 of {upcoming.Count}.");

        await command.FollowupAsync(embed: eb.Build(), ephemeral: true);
    }

    // ─── cancel / edit picker ────────────────────────────────────────────

    private async Task HandlePickAsync(SocketSlashCommand command, bool forEdit)
    {
        await command.DeferAsync(ephemeral: true);

        var upcoming = await GetUpcomingAsync(command.GuildId!.Value);
        if (upcoming.Count == 0)
        {
            await command.FollowupAsync("There are no upcoming reminders to " + (forEdit ? "edit." : "cancel."), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId(forEdit ? EditMenuId : CancelMenuId)
            .WithPlaceholder(forEdit ? "Pick a reminder to edit…" : "Pick a reminder to cancel…")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var r in upcoming.Take(25))
        {
            var desc = $"{r.NextFireUtc:MMM d, HH:mm} UTC → #{ResolveChannelName(command.GuildId!.Value, r.ChannelId)}";
            menu.AddOption(Truncate(r.Title, 100), r.Id.ToString(), Truncate(desc, 100));
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await command.FollowupAsync(
            forEdit ? "Which reminder do you want to edit?" : "Which reminder do you want to cancel?",
            components: components, ephemeral: true);
    }

    private async Task OnSelectMenuAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (id != CancelMenuId && id != EditMenuId) return;

        try
        {
            if (component.User is not SocketGuildUser guildUser || !HasPermission(guildUser))
            {
                await component.RespondAsync("❌ You don't have permission to manage reminders.", ephemeral: true);
                return;
            }

            if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var reminderId))
            {
                await component.RespondAsync("That selection wasn't valid. Try the command again.", ephemeral: true);
                return;
            }

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var reminder = await db.ClanReminders.FirstOrDefaultAsync(r => r.Id == reminderId);

            if (reminder is null || reminder.Status != ClanReminderStatus.Scheduled)
            {
                await component.UpdateAsync(m =>
                {
                    m.Content    = "That reminder is no longer available (it may have already posted or been cancelled).";
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            if (id == CancelMenuId)
            {
                reminder.Status = ClanReminderStatus.Cancelled;
                reminder.CancelledAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await component.UpdateAsync(m =>
                {
                    m.Content    = $"🗑️ Cancelled **{reminder.Title}** — it won't post.";
                    m.Components = new ComponentBuilder().Build();
                });
                _logger.LogInformation("Reminder {Id} '{Title}' cancelled by {User}", reminder.Id, reminder.Title, component.User.Id);
            }
            else // edit
            {
                var started = await _wizard.StartEditAsync(component.User, reminder);
                await component.UpdateAsync(m =>
                {
                    m.Content = started
                        ? $"✏️ Editing **{reminder.Title}** — check your DMs to make changes."
                        : "I couldn't DM you. Enable **Direct Messages** from server members and try again.";
                    m.Components = new ComponentBuilder().Build();
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling reminder select menu {CustomId}", id);
            try { await component.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private async Task<List<ClanReminder>> GetUpcomingAsync(ulong guildId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.ClanReminders
            .Where(r => r.GuildId == guildId && r.Status == ClanReminderStatus.Scheduled)
            .OrderBy(r => r.NextFireUtc)
            .ToListAsync();
    }

    private string ResolveChannelName(ulong guildId, ulong channelId)
    {
        var client = _services.GetRequiredService<DiscordSocketClient>();
        return client.GetGuild(guildId)?.GetTextChannel(channelId)?.Name ?? channelId.ToString();
    }

    /// <summary>
    /// SyncWithHandlers: EventCommandHandler.HasEventPermission — Administrator /
    /// Manage Roles bypass, else any role at or above ReminderCommandMinRank in
    /// the RankRoles ladder.
    /// </summary>
    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.ReminderCommandMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "ReminderCommandMinRank '{MinRank}' not found in RankRoles — /reminder will be admin-only",
                _config.ReminderCommandMinRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
