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
/// Handles slash commands with full database integration for
/// checking activity stats, looking up other users, clearing AWOL status,
/// and manually triggering a roster export.
/// </summary>
public class SlashCommandHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SlashCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly RosterExportService _rosterExport;

    public SlashCommandHandler(
        IServiceProvider services,
        ILogger<SlashCommandHandler> logger,
        IOptions<BotConfig> config,
        RosterExportService rosterExport)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
        _rosterExport = rosterExport;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not ("awol-status" or "awol-check" or "clear-awol" or "roster-export"))
            return;

        try
        {
            switch (command.Data.Name)
            {
                case "awol-status":
                    await HandleStatus(command);
                    break;
                case "awol-check":
                    await HandleCheck(command);
                    break;
                case "clear-awol":
                    await HandleClearAwol(command);
                    break;
                case "roster-export":
                    await HandleRosterExport(command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    /// <summary>/awol-status — show your own activity for this server.</summary>
    private async Task HandleStatus(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        var userWindowDays = guildUser is not null
            ? _config.GetWindowDaysForRoles(guildUser.Roles.Select(r => r.Name))
            : _config.WindowDays;
        var windowStart = DateTime.UtcNow.AddDays(-userWindowDays);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var messageCount = await db.MessageEvents
            .CountAsync(m => m.GuildId == command.GuildId.Value
                          && m.UserId == command.User.Id
                          && m.Timestamp >= windowStart);

        var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, command.GuildId.Value, command.User.Id, windowStart,
            _config.MaxSingleSessionHours);
        var voiceHours = voiceSeconds / 3600.0;

        var msgStatus = messageCount >= _config.MinMessages ? "✅" : "⚠️";
        var voiceStatus = voiceHours >= _config.MinVoiceHours ? "✅" : "⚠️";

        var embed = new EmbedBuilder()
            .WithTitle("📊 Your Activity Stats")
            .WithColor(messageCount >= _config.MinMessages || voiceHours >= _config.MinVoiceHours
                ? Color.Green : Color.Orange)
            .AddField($"{msgStatus} Messages", $"{messageCount} / {_config.MinMessages} required", true)
            .AddField($"{voiceStatus} Voice Time", $"{voiceHours:F1}h / {_config.MinVoiceHours}h required", true)
            .AddField("Window", $"Last {userWindowDays} days")
            .WithFooter("Stay active to avoid the AWOL role!")
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task HandleCheck(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null) return;

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasElevatedPermissions(caller))
        {
            await command.FollowupAsync("You don't have permission to use this command.", ephemeral: true);
            return;
        }

        var targetUser = command.Data.Options.First().Value as SocketGuildUser;
        if (targetUser is null)
        {
            await command.FollowupAsync("Could not find that user.", ephemeral: true);
            return;
        }

        var userWindowDays = _config.GetWindowDaysForRoles(targetUser.Roles.Select(r => r.Name));
        var windowStart = DateTime.UtcNow.AddDays(-userWindowDays);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var messageCount = await db.MessageEvents
            .CountAsync(m => m.GuildId == command.GuildId.Value
                          && m.UserId == targetUser.Id
                          && m.Timestamp >= windowStart);

        var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, command.GuildId.Value, targetUser.Id, windowStart,
            _config.MaxSingleSessionHours);
        var voiceHours = voiceSeconds / 3600.0;

        var activity = await db.UserActivities
            .FirstOrDefaultAsync(a => a.GuildId == command.GuildId.Value
                                   && a.UserId == targetUser.Id);

        var embed = new EmbedBuilder()
            .WithTitle($"📊 Activity Report — {targetUser.Username}")
            .WithColor(Color.Blue)
            .AddField("Messages", messageCount.ToString(), true)
            .AddField("Voice Time", $"{voiceHours:F1} hours", true)
            .AddField("Window", $"Last {userWindowDays} days")
            .AddField("Currently in Voice", activity?.VoiceJoinedAt.HasValue == true ? "Yes" : "No", true)
            .AddField("Has AWOL Role",
                targetUser.Roles.Any(r => r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase))
                    ? "Yes" : "No", true)
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    /// <summary>/clear-awol — remove AWOL role and clear pending notifications (officer+ only).</summary>
    private async Task HandleClearAwol(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null) return;

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasElevatedPermissions(caller))
        {
            await command.FollowupAsync("You don't have permission to use this command.", ephemeral: true);
            return;
        }

        var targetUser = command.Data.Options.First().Value as SocketGuildUser;
        if (targetUser is null)
        {
            await command.FollowupAsync("Could not find that user.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        // Remove AWOL role if present
        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

        if (awolRole is not null && targetUser.Roles.Any(r => r.Id == awolRole.Id))
        {
            await targetUser.RemoveRoleAsync(awolRole);
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Reset the member's activity window so the periodic AWOL sweep gives
        // them a full fresh window before it can re-flag them. We stamp this
        // directly here rather than relying on the GuildMemberUpdated echo from
        // RemoveRoleAsync (RankTrackingHandler.HandleAwolRoleRemovedAsync) — that
        // handler bails if the gateway "before" state isn't cached, which would
        // otherwise leave a cleared member eligible for immediate re-flagging.
        // See UserActivity.WindowResetAt for the rationale.
        var activity = await db.UserActivities
            .FirstOrDefaultAsync(a => a.GuildId == command.GuildId.Value && a.UserId == targetUser.Id);
        if (activity is null)
        {
            activity = new UserActivity
            {
                GuildId = command.GuildId.Value,
                UserId = targetUser.Id,
                Username = targetUser.ToString() ?? targetUser.Username,
            };
            db.UserActivities.Add(activity);
        }
        activity.WindowResetAt = DateTime.UtcNow;

        // Close pending records AND clean up any HQ embed that was already
        // posted. The OR catches both: pending records (NotificationSent=false,
        // no message id) and posted records (NotificationSent=true with a
        // stored message id). Mirrors the automatic recovery path in
        // AwolCheckService so a manual clear leaves no stale notice behind.
        var awolRecords = await db.AwolRecords
            .Where(r => r.GuildId == command.GuildId.Value
                     && r.UserId == targetUser.Id
                     && (!r.NotificationSent || r.NotificationMessageId != null))
            .ToListAsync();

        foreach (var record in awolRecords)
        {
            if (record.NotificationMessageId.HasValue
                && record.NotificationChannelId.HasValue)
            {
                try
                {
                    var notifChannel = guild.GetTextChannel(record.NotificationChannelId.Value);
                    if (notifChannel is not null)
                    {
                        await notifChannel.DeleteMessageAsync(record.NotificationMessageId.Value);
                        _logger.LogInformation(
                            "Deleted AWOL embed for {User} in #{Channel} on /clear-awol",
                            targetUser.Username, notifChannel.Name);
                    }
                }
                catch (Discord.Net.HttpException ex)
                    when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone — nothing to do.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete AWOL embed for {User} on /clear-awol (message {MessageId})",
                        targetUser.Username, record.NotificationMessageId.Value);
                }

                record.NotificationChannelId = null;
                record.NotificationMessageId = null;
            }

            record.NotificationSent = true;
            record.NotificationSentAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        await command.FollowupAsync(
            $"✅ AWOL status cleared for {targetUser.Mention}. Role removed and pending notifications closed.",
            ephemeral: true);

        _logger.LogInformation("{Caller} cleared AWOL status for {Target} in {Guild}",
            caller.Username, targetUser.Username, guild.Name);
    }

    /// <summary>/roster-export — manually trigger a roster export (officer+ only).</summary>
    private async Task HandleRosterExport(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasElevatedPermissions(caller))
        {
            await command.FollowupAsync("You don't have permission to use this command.", ephemeral: true);
            return;
        }

        await command.FollowupAsync("⏳ Roster export started...", ephemeral: true);

        try
        {
            await _rosterExport.RunManualExportAsync();
            await command.FollowupAsync("✅ Roster export complete! Check the Google Sheet.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual roster export failed");
            await command.FollowupAsync($"❌ Export failed: {ex.Message}", ephemeral: true);
        }
    }

    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;

        var officerRoles = _config.GetOfficerRolesList();
        return user.Roles.Any(r => officerRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}