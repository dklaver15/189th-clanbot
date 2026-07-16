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

    // ── Line selection: shuffle bag ──────────────────────────────────────
    // Naive Random.Next(count) picks WITH replacement, so it happily
    // repeats the same line several times in a row. Instead we deal from a
    // shuffled "bag": every line is used exactly once before any repeats,
    // and on reshuffle we make sure the new first line isn't the one we
    // just played, so there's no repeat across bag boundaries either.
    // The bag is rebuilt whenever the configured line list changes (count
    // or content), so editing TrollReplyLines takes effect cleanly.
    private readonly object _bagLock = new();
    private readonly List<string> _bag = new();
    private List<string> _bagSource = new();
    private string? _lastLine;

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

            // ── Pick a line (shuffle bag — no immediate repeats) ──────────
            var lines = _config.TrollReplyLines;
            if (lines is null || lines.Count == 0) return;
            var line = NextLine(lines);

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

    /// <summary>
    /// Returns the next line using a shuffle-bag: every line is dealt once
    /// before any repeats. When the bag empties it's reshuffled, and the
    /// reshuffle guarantees the new first line differs from the one just
    /// played (when there's more than one line) so we never repeat across
    /// bag boundaries. The bag is rebuilt if the source list changed, so
    /// config edits to TrollReplyLines take effect immediately.
    /// </summary>
    private string NextLine(List<string> lines)
    {
        lock (_bagLock)
        {
            // Rebuild if the configured lines changed (count or content).
            if (!_bagSource.SequenceEqual(lines))
            {
                _bagSource = new List<string>(lines);
                _bag.Clear();
            }

            if (_bag.Count == 0)
            {
                Refill(lines);
            }

            // Deal from the end (cheap removal).
            var line = _bag[^1];
            _bag.RemoveAt(_bag.Count - 1);
            _lastLine = line;
            return line;
        }
    }

    /// <summary>
    /// Refills and Fisher-Yates shuffles the bag. If there's more than one
    /// distinct line, ensures the line dealt next (the last element, since
    /// we deal from the end) isn't the one we just played.
    /// </summary>
    private void Refill(List<string> lines)
    {
        _bag.Clear();
        _bag.AddRange(lines);

        // Fisher-Yates.
        for (int i = _bag.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }

        // Avoid a cross-bag repeat: if the next line to be dealt (last
        // element) matches the previous line, swap it with another slot.
        if (_lastLine is not null && _bag.Count > 1 && _bag[^1] == _lastLine)
        {
            int swap = _random.Next(_bag.Count - 1); // any slot but the last
            (_bag[^1], _bag[swap]) = (_bag[swap], _bag[^1]);
        }
    }
}
