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
/// Handles slash commands with full database integration for
/// checking activity stats, looking up other users, and exempting users from AWOL.
/// </summary>
public class SlashCommandHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SlashCommandHandler> _logger;
    private readonly BotConfig _config;

    public SlashCommandHandler(
        IServiceProvider services,
        ILogger<SlashCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
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
                case "awol-exempt":
                    await HandleExempt(command);
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

        var voiceSeconds = await GetVoiceSecondsAsync(db, command.GuildId.Value, command.User.Id, windowStart);
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

        var voiceSeconds = await GetVoiceSecondsAsync(db, command.GuildId.Value, targetUser.Id, windowStart);
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

    /// <summary>/awol-exempt — remove AWOL and reset a user's window (officer+ only).</summary>
    private async Task HandleExempt(SocketSlashCommand command)
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

        // Mark any pending AWOL records as handled
        var awolRecords = await db.AwolRecords
            .Where(r => r.GuildId == command.GuildId.Value
                     && r.UserId == targetUser.Id
                     && !r.NotificationSent)
            .ToListAsync();

        foreach (var record in awolRecords)
        {
            record.NotificationSent = true;
            record.NotificationSentAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        await command.FollowupAsync(
            $"✅ {targetUser.Mention} has been exempted. AWOL role removed.",
            ephemeral: true);

        _logger.LogInformation("{Caller} exempted {Target} from AWOL in {Guild}",
            caller.Username, targetUser.Username, guild.Name);
    }
    
    private static async Task<long> GetVoiceSecondsAsync(
        BotDbContext db, ulong guildId, ulong userId, DateTime windowStart, CancellationToken ct = default)
    {
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt >= windowStart)
            .ToListAsync(ct);

        long totalSeconds = 0;
        foreach (var session in sessions)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            var start = session.JoinedAt < windowStart ? windowStart : session.JoinedAt;
            totalSeconds += (long)(end - start).TotalSeconds;
        }

        var overlapping = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt < windowStart
                        && (v.LeftAt == null || v.LeftAt > windowStart))
            .ToListAsync(ct);

        foreach (var session in overlapping)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            totalSeconds += (long)(end - windowStart).TotalSeconds;
        }

        return totalSeconds;
    }

    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
