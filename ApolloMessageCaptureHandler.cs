using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Phase 1 of the Apollo sync rework: lossless capture.
///
/// Subscribes to MessageReceived / MessageUpdated / MessageDeleted for the
/// configured #events channel, filters to Apollo's bot, and writes a row to
/// ApolloMessageLog. No parsing, no Google Calendar — pure observation.
/// Runs in PARALLEL with ApolloEventHandler; the existing inline pipeline is
/// untouched. The captured rows feed Phase 2's parser worker.
///
/// ── Why this lives separately from ApolloEventHandler ──
/// Capture has different failure semantics than the inline path. ApolloEvent-
/// Handler bails out on parse failure (correct for the live path — nothing to
/// sync). This handler MUST NOT bail on parse failure — the whole point is
/// preserving the raw payload precisely so we can fix the parser later and
/// replay. Splitting capture into its own class keeps that contract clear and
/// avoids accidentally entangling the two concerns.
///
/// ── Per-message serialization ──
/// Same race that bit ApolloEventHandler (MessageReceived + MessageUpdated for
/// the same Apollo post arriving within milliseconds, both opening their own
/// DbContext and computing the same nextRevision) applies here too. We use the
/// same per-message-id semaphore pattern.
///
/// The unique index on (DiscordMessageId, RevisionNumber) is the structural
/// backstop in case a code path ever bypasses the lock.
/// </summary>
public class ApolloMessageCaptureHandler
{
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _messageLocks = new();

    private readonly IServiceProvider _services;
    private readonly ILogger<ApolloMessageCaptureHandler> _logger;
    private readonly BotConfig _config;

    public ApolloMessageCaptureHandler(
        IServiceProvider services,
        ILogger<ApolloMessageCaptureHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.MessageUpdated  += OnMessageUpdated;
        client.MessageDeleted  += OnMessageDeleted;
    }

    // ─── Event Handlers ──────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!IsApolloMessage(message)) return Task.CompletedTask;
        if (message.Channel is not SocketTextChannel textChannel) return Task.CompletedTask;
        if (!IsEventsChannel(textChannel)) return Task.CompletedTask;

        _ = CaptureRevisionAsync(message, ApolloMessageEventType.Created);
        return Task.CompletedTask;
    }

    private Task OnMessageUpdated(
        Cacheable<IMessage, ulong> _before,
        SocketMessage after,
        ISocketMessageChannel channel)
    {
        if (!IsApolloMessage(after)) return Task.CompletedTask;
        if (channel is not SocketTextChannel textChannel) return Task.CompletedTask;
        if (!IsEventsChannel(textChannel)) return Task.CompletedTask;

        _ = CaptureRevisionAsync(after, ApolloMessageEventType.Updated);
        return Task.CompletedTask;
    }

    private Task OnMessageDeleted(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> _channel)
    {
        // We can't filter by author or channel here (the message body may be
        // gone from cache by deletion time). Same approach as
        // ApolloEventHandler: look up by DiscordMessageId in our table; if
        // we have prior revisions, write a tombstone. If not, ignore — almost
        // certainly a non-Apollo message we never cared about.
        _ = CaptureDeletionAsync(message.Id);
        return Task.CompletedTask;
    }

    // ─── Core Capture ────────────────────────────────────────────────

    private async Task CaptureRevisionAsync(IMessage message, ApolloMessageEventType eventType)
    {
        var gate = _messageLocks.GetOrAdd(message.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var nextRevision = (await db.ApolloMessageLogs
                .Where(x => x.DiscordMessageId == message.Id)
                .Select(x => (int?)x.RevisionNumber)
                .MaxAsync()) ?? 0;

            var snapshot = ApolloMessageSnapshot.From(message);

            var row = new ApolloMessageLog
            {
                DiscordMessageId = message.Id,
                ChannelId        = message.Channel.Id,
                GuildId          = (message.Channel as IGuildChannel)?.GuildId ?? 0,
                AuthorId         = message.Author.Id,
                RevisionNumber   = nextRevision + 1,
                EventType        = eventType,
                PayloadJson      = snapshot.ToJson(),
                CapturedAt       = DateTime.UtcNow,
            };

            db.ApolloMessageLogs.Add(row);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Captured Apollo message {MessageId} rev {Revision} ({EventType})",
                message.Id, row.RevisionNumber, eventType);
        }
        catch (Exception ex)
        {
            // Capture must NEVER take down the bot. The reconciler is the safety net.
            _logger.LogError(ex,
                "Failed to capture Apollo message {MessageId} ({EventType}); row not written",
                message.Id, eventType);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CaptureDeletionAsync(ulong messageId)
    {
        var gate = _messageLocks.GetOrAdd(messageId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Find the most recent revision so we (a) know if this message is
            // even one of ours, and (b) can carry forward channel/guild/author
            // values that aren't available on a deletion event.
            var latest = await db.ApolloMessageLogs
                .Where(x => x.DiscordMessageId == messageId)
                .OrderByDescending(x => x.RevisionNumber)
                .FirstOrDefaultAsync();

            if (latest is null)
            {
                _logger.LogDebug(
                    "Ignoring deletion of unknown message {MessageId} (no prior capture)", messageId);
                return;
            }

            // Skip if the latest revision is ALREADY a Deleted tombstone —
            // Discord can occasionally re-deliver a delete event after a
            // reconnect, and the unique index would reject it anyway.
            if (latest.EventType == ApolloMessageEventType.Deleted)
            {
                _logger.LogDebug(
                    "Skipping duplicate deletion event for message {MessageId} (already tombstoned)",
                    messageId);
                return;
            }

            var row = new ApolloMessageLog
            {
                DiscordMessageId = messageId,
                ChannelId        = latest.ChannelId,
                GuildId          = latest.GuildId,
                AuthorId         = latest.AuthorId,
                RevisionNumber   = latest.RevisionNumber + 1,
                EventType        = ApolloMessageEventType.Deleted,
                PayloadJson      = null,
                CapturedAt       = DateTime.UtcNow,
            };

            db.ApolloMessageLogs.Add(row);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Captured Apollo message {MessageId} deletion (rev {Revision})",
                messageId, row.RevisionNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to capture deletion of Apollo message {MessageId}; row not written",
                messageId);
        }
        finally
        {
            gate.Release();
        }
    }

    // ─── Helpers (mirror ApolloEventHandler) ─────────────────────────

    private bool IsApolloMessage(SocketMessage message)
    {
        if (!message.Author.IsBot) return false;
        return message.Author.Username.Contains(
            _config.ApolloBotName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEventsChannel(SocketTextChannel channel) =>
        (_config.EventsTextChannelId != 0 && channel.Id == _config.EventsTextChannelId) ||
        channel.Name.Equals(_config.EventsTextChannelName, StringComparison.OrdinalIgnoreCase);
}
