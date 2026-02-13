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
/// One-time startup service that backfills MessageEvents from Discord channel history.
/// Runs once, then stops. Skips messages already recorded.
/// </summary>
public class HistoryBackfillService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<HistoryBackfillService> _logger;
    private readonly BotConfig _config;

    public HistoryBackfillService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<HistoryBackfillService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Check if we've already backfilled (simple flag in the DB)
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            if (await db.MessageEvents.AnyAsync(stoppingToken))
            {
                _logger.LogInformation("Message history already has data — skipping backfill.");
                return;
            }
        }

        _logger.LogInformation("Starting message history backfill...");

        // Go back as far as the longest window
        var cutoff = DateTime.UtcNow.AddDays(-_config.WindowDays);

        foreach (var guild in _client.Guilds)
        {
            _logger.LogInformation("Backfilling guild {Guild}...", guild.Name);

            foreach (var channel in guild.TextChannels)
            {
                try
                {
                    await BackfillChannelAsync(channel, guild.Id, cutoff, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Permission errors, etc. — just skip the channel
                    _logger.LogWarning(ex, "Could not backfill #{Channel} in {Guild}",
                        channel.Name, guild.Name);
                }
            }
        }

        _logger.LogInformation("Message history backfill complete!");
    }

    private async Task BackfillChannelAsync(
        SocketTextChannel channel, ulong guildId, DateTime cutoff, CancellationToken ct)
    {
        var count = 0;
        var batch = new List<MessageEvent>();

        // Discord returns messages newest-first
        var messages = channel.GetMessagesAsync(limit: int.MaxValue).Flatten();

        await foreach (var msg in messages.WithCancellation(ct))
        {
            // Stop once we've gone past the window
            if (msg.Timestamp.UtcDateTime < cutoff)
                break;

            // Skip bots and system messages
            if (msg.Author.IsBot) continue;

            batch.Add(new MessageEvent
            {
                GuildId = guildId,
                UserId = msg.Author.Id,
                Timestamp = msg.Timestamp.UtcDateTime
            });
            count++;

            // Save in batches of 500 to avoid memory buildup
            if (batch.Count >= 500)
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                db.MessageEvents.AddRange(batch);
                await db.SaveChangesAsync(ct);
                batch.Clear();
            }
        }

        // Save any remaining
        if (batch.Count > 0)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.MessageEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }

        if (count > 0)
            _logger.LogInformation("  #{Channel}: backfilled {Count} messages", channel.Name, count);
    }
}