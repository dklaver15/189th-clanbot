using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Background service that periodically:
///   1. Resets expired activity windows (rolling 28 days)
///   2. Checks for users below activity thresholds and assigns the AWOL role
///   3. Posts HQ notifications for users who've been AWOL for 2+ days
/// </summary>
public class AwolCheckService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AwolCheckService> _logger;
    private readonly BotConfig _config;

    public AwolCheckService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<AwolCheckService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the Discord client is fully connected and has guild data
        while (_client.ConnectionState != ConnectionState.Connected ||
               !_client.Guilds.Any())
        {
            _logger.LogInformation("Waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Give guilds a moment to finish downloading members
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("AWOL check service started. Running every {Interval} minutes.",
            _config.CheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AWOL check cycle");
            }

            await Task.Delay(TimeSpan.FromMinutes(_config.CheckIntervalMinutes), stoppingToken);
        }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting AWOL check cycle at {Time}", DateTime.UtcNow);

        foreach (var guild in _client.Guilds)
        {
            try
            {
                await ProcessGuildAsync(guild, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing guild {GuildName} ({GuildId})",
                    guild.Name, guild.Id);
            }
        }

        _logger.LogInformation("AWOL check cycle complete");
    }

    private async Task ProcessGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        // Ensure we have fresh member data
        await guild.DownloadUsersAsync();

        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

        if (awolRole is null)
        {
            _logger.LogWarning("AWOL role '{RoleName}' not found in guild {Guild}. Skipping.",
                _config.AwolRoleName, guild.Name);
            return;
        }

        var hqChannel = guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));

        if (hqChannel is null)
        {
            _logger.LogWarning("HQ channel '{ChannelName}' not found in guild {Guild}. Skipping notifications.",
                _config.HqChannelName, guild.Name);
        }

        var exemptRoles = _config.GetExemptRolesList();
        var minVoiceSeconds = (long)(_config.MinVoiceHours * 3600);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ------------------------------------------------------------------
        // Step 1: Reset windows that have expired (check per-user window)
        // ------------------------------------------------------------------
        var allActivities = await db.UserActivities
            .Where(a => a.GuildId == guild.Id)
            .ToListAsync(ct);

        foreach (var activity in allActivities)
        {
            var member = guild.GetUser(activity.UserId);
            var userWindowDays = member is not null
                ? _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name))
                : _config.WindowDays;

            var userCutoff = DateTime.UtcNow.AddDays(-userWindowDays);
            if (activity.WindowStart < userCutoff)
            {
                activity.MessageCount = 0;
                activity.VoiceSeconds = 0;
                activity.WindowStart = DateTime.UtcNow;
                _logger.LogDebug("Reset activity window ({WindowDays}d) for user {Username} in guild {Guild}",
                    userWindowDays, activity.Username, guild.Name);
            }
        }
        await db.SaveChangesAsync(ct);

        // ------------------------------------------------------------------
        // Step 2: Ensure all guild members have an activity record
        // ------------------------------------------------------------------
        var existingUserIds = (await db.UserActivities
                .Where(a => a.GuildId == guild.Id)
                .Select(a => a.UserId)
                .ToListAsync(ct))
                .ToHashSet();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (IsExempt(member, exemptRoles)) continue;

            if (!existingUserIds.Contains(member.Id))
            {
                db.UserActivities.Add(new UserActivity
                {
                    GuildId = guild.Id,
                    UserId = member.Id,
                    Username = member.ToString() ?? member.Username,
                    MessageCount = 0,
                    VoiceSeconds = 0,
                    WindowStart = DateTime.UtcNow
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // ------------------------------------------------------------------
        // Step 3a: Check for users below thresholds and assign AWOL
        // ------------------------------------------------------------------
        // Only check users whose window has been open for at least their full window period
        var allActivitiesForCheck = await db.UserActivities
            .Where(a => a.GuildId == guild.Id
                        && (a.MessageCount < _config.MinMessages && a.VoiceSeconds < minVoiceSeconds))
            .ToListAsync(ct);

        // Filter to those whose window has actually expired, based on their role-specific window
        var inactiveUsers = allActivitiesForCheck.Where(a =>
        {
            var member = guild.GetUser(a.UserId);
            if (member is null) return false;
            var userWindowDays = _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name));
            return a.WindowStart <= DateTime.UtcNow.AddDays(-userWindowDays);
        }).ToList();

        foreach (var inactive in inactiveUsers)
        {
            var member = guild.GetUser(inactive.UserId);
            if (member is null || member.IsBot) continue;
            if (IsExempt(member, exemptRoles)) continue;

            // Skip if they already have the AWOL role
            if (member.Roles.Any(r => r.Id == awolRole.Id)) continue;

            try
            {
                await member.AddRoleAsync(awolRole);
                _logger.LogInformation(
                    "Assigned AWOL role to {Username} ({UserId}) in {Guild} — " +
                    "Messages: {Messages}, Voice: {VoiceHours:F1}h",
                    inactive.Username, inactive.UserId, guild.Name,
                    inactive.MessageCount, inactive.VoiceSeconds / 3600.0);

                // Create AWOL record for the grace period
                var existingRecord = await db.AwolRecords
                    .FirstOrDefaultAsync(r => r.GuildId == guild.Id
                                           && r.UserId == inactive.UserId
                                           && !r.NotificationSent, ct);

                if (existingRecord is null)
                {
                    db.AwolRecords.Add(new AwolRecord
                    {
                        GuildId = guild.Id,
                        UserId = inactive.UserId,
                        Username = inactive.Username,
                        AssignedAt = DateTime.UtcNow,
                        NotificationSent = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign AWOL role to {Username} in {Guild}",
                    inactive.Username, guild.Name);
            }
        }
        await db.SaveChangesAsync(ct);
        
        // ------------------------------------------------------------------
            // Step 3b: Remove AWOL role from users who are now active
            // ------------------------------------------------------------------
            var awolMembers = guild.Users
                .Where(m => !m.IsBot && m.Roles.Any(r => r.Id == awolRole.Id));

            foreach (var member in awolMembers)
            {
                var activity = await db.UserActivities
                    .FirstOrDefaultAsync(a => a.GuildId == guild.Id && a.UserId == member.Id, ct);

                if (activity is null) continue;

                var meetsMessages = activity.MessageCount >= _config.MinMessages;
                var meetsVoice = activity.VoiceSeconds >= minVoiceSeconds;

                if (meetsMessages || meetsVoice)
                {
                    try
                    {
                        await member.RemoveRoleAsync(awolRole);
                        _logger.LogInformation(
                            "Removed AWOL role from {Username} ({UserId}) in {Guild} — " +
                            "Messages: {Messages}, Voice: {VoiceHours:F1}h",
                            member.Username, member.Id, guild.Name,
                            activity.MessageCount, activity.VoiceSeconds / 3600.0);

                        // Mark any pending AWOL records as resolved
                        var pendingRecords = await db.AwolRecords
                            .Where(r => r.GuildId == guild.Id
                                     && r.UserId == member.Id
                                     && !r.NotificationSent)
                            .ToListAsync(ct);

                        foreach (var record in pendingRecords)
                        {
                            record.NotificationSent = true;
                            record.NotificationSentAt = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove AWOL role from {Username} in {Guild}",
                            member.Username, guild.Name);
                    }
                }
            }

            await db.SaveChangesAsync(ct);

        // ------------------------------------------------------------------
        // Step 4: Post HQ notifications for users past the grace period
        // ------------------------------------------------------------------
        if (hqChannel is null) return;

        var graceCutoff = DateTime.UtcNow.AddDays(-_config.AwolGraceDays);
        var pendingNotifications = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id
                     && !r.NotificationSent
                     && r.AssignedAt <= graceCutoff)
            .ToListAsync(ct);

        foreach (var record in pendingNotifications)
        {
            var member = guild.GetUser(record.UserId);

            // Build the notification embed
            var embed = new EmbedBuilder()
                .WithTitle("⚠️ AWOL Member — Ready for Review")
                .WithColor(Color.Red)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("User", member is not null
                    ? $"{member.Mention} ({member.Username})"
                    : $"{record.Username} (ID: {record.UserId})")
                .AddField("AWOL Since", record.AssignedAt.ToString("yyyy-MM-dd HH:mm UTC"), inline: true)
                .AddField("Grace Period Expired",
                    record.AssignedAt.AddDays(_config.AwolGraceDays).ToString("yyyy-MM-dd HH:mm UTC"),
                    inline: true);

            // Pull their activity stats for context
            var activity = await db.UserActivities
                .FirstOrDefaultAsync(a => a.GuildId == guild.Id && a.UserId == record.UserId, ct);

            if (activity is not null)
            {
                var activityWindowDays = member is not null
                    ? _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name))
                    : _config.WindowDays;
                embed.AddField($"Messages ({activityWindowDays}d)", activity.MessageCount.ToString(), inline: true);
                embed.AddField($"Voice Time ({activityWindowDays}d)",
                    $"{activity.VoiceSeconds / 3600.0:F1} hours", inline: true);
            }

            if (member is not null)
            {
                embed.AddField("Joined Server",
                    member.JoinedAt?.ToString("yyyy-MM-dd") ?? "Unknown", inline: true);
                embed.AddField("Roles",
                    string.Join(", ", member.Roles
                        .Where(r => !r.IsEveryone)
                        .Select(r => r.Name)));
            }

            embed.WithFooter("ClanGuard Bot • Use server moderation tools to take action");

            try
            {
                await hqChannel.SendMessageAsync(embed: embed.Build());
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;

                _logger.LogInformation("Posted AWOL notification for {Username} in {Guild} #{Channel}",
                    record.Username, guild.Name, hqChannel.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send AWOL notification for {Username}", record.Username);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsExempt(SocketGuildUser member, List<string> exemptRoles)
    {
        return member.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
