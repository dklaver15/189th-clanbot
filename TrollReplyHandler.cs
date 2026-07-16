using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Fun feature: Troll Reply.
///
/// ── What it does ──
/// Watches for messages from one specific, good-natured victim
/// (<see cref="BotConfig.TrollReplyTargetUserId"/>) and, whenever they
/// post in a guild channel, the bot replies to that message with a
/// randomly-chosen sassy line from <see cref="BotConfig.TrollReplyLines"/>
/// — all riffing on the running "he's gonna cheat on you" gag.
///
/// ── Guardrails (so it's a joke, not a menace) ──
///   • Master switch: <see cref="BotConfig.TrollReplyEnabled"/> — default
///     OFF. Nothing fires until it's explicitly turned on.
///   • Target gate: only the configured user ID is trolled. A missing /
///     zero ID disables the feature.
///   • Cooldown: <see cref="BotConfig.TrollReplyCooldownSeconds"/>
///     (default 60s) throttles replies to the same target so the bot
///     doesn't carpet-bomb a channel or trip Discord's rate limits when
///     the victim is mid-conversation. 0 = reply to every message.
///   • Bots / webhooks / DMs are ignored — the target has to be a real
///     person posting in a real guild channel.
///
/// ── Mentions ──
/// The reply is sent as a Discord reply (MessageReference) so it threads
/// under the victim's message. AllowedMentions is set so the reply ping
/// still notifies the target (that's half the fun) but the bot can never
/// accidentally @everyone/@here or mass-ping roles from a config line.
///
/// ── Failure handling ──
/// Fire-and-forget from the gateway callback, same pattern as
/// InviteLinkFilterHandler / AccountAgeGateHandler. All work is wrapped
/// in try/catch — a Discord hiccup on a joke reply must never take down
/// the gateway listener.
/// </summary>
public sealed class TrollReplyHandler
{
    private readonly ILogger<TrollReplyHandler> _logger;
    private readonly BotConfig _config;
    private readonly Random _random = new();

    // In-memory cooldown tracker. Keyed by user ID (only ever one entry in
    // practice, but keyed so retuning the target mid-run behaves sanely).
    // Resets on restart — fine for a gag.
    private readonly object _cooldownLock = new();
    private DateTime _lastReplyUtc = DateTime.MinValue;

    public TrollReplyHandler(
        ILogger<TrollReplyHandler> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
    }

    private Task OnMessageReceived(SocketMessage message)
    {
        _ = HandleAsync(message);
        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketMessage message)
    {
        try
        {
            // ── Master switch + target gate ───────────────────────────────
            if (!_config.TrollReplyEnabled) return;
            if (_config.TrollReplyTargetUserId == 0) return;

            // ── Filter to a real user in a guild channel ──────────────────
            if (message is not SocketUserMessage userMsg) return;
            if (userMsg.Author.IsBot) return;
            if (userMsg.Author.IsWebhook) return;
            if (userMsg.Channel is not SocketGuildChannel) return;

            // ── Is this our victim? ───────────────────────────────────────
            if (userMsg.Author.Id != _config.TrollReplyTargetUserId) return;

            // ── Cooldown ──────────────────────────────────────────────────
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _config.TrollReplyCooldownSeconds));
            lock (_cooldownLock)
            {
                if (cooldown > TimeSpan.Zero &&
                    DateTime.UtcNow - _lastReplyUtc < cooldown)
                {
                    return;
                }
                _lastReplyUtc = DateTime.UtcNow;
            }

            // ── Pick a line ───────────────────────────────────────────────
            var lines = _config.TrollReplyLines;
            if (lines is null || lines.Count == 0) return;
            string line;
            lock (_random)
            {
                line = lines[_random.Next(lines.Count)];
            }

            // ── Reply ─────────────────────────────────────────────────────
            // MessageReference threads the reply under the victim's message.
            // AllowedMentions.Users lets the reply ping the target (the point)
            // but blocks @everyone/@here/role pings from ever leaking out of a
            // config line.
            await userMsg.Channel.SendMessageAsync(
                text: line,
                messageReference: new MessageReference(userMsg.Id),
                allowedMentions: new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users });

            _logger.LogDebug(
                "Troll reply sent to {UserId} in channel {ChannelId} (message {MessageId})",
                userMsg.Author.Id, userMsg.Channel.Id, userMsg.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Troll reply handler crashed for message {MessageId}", message.Id);
        }
    }
}
