using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Disambiguate from Discord.Net's own Poll type (Discord namespace) under
// `using Discord;`. PollProperties/PollMediaProperties below are Discord's.
using Poll = ClanGuardBot.Models.Poll;

namespace ClanGuardBot.Services;

/// <summary>
/// Turns a validated <see cref="PollDraft"/> into a live poll, picking the
/// renderer by <see cref="PollDraft.Kind"/>:
///
/// • <b>Native</b> — sends a real Discord poll (<see cref="PollProperties"/>) so
///   Discord draws the animated UI and owns the vote buttons. We persist the poll
///   and its options; votes are captured live by <c>PollGatewayVoteHandler</c>.
///   If the creator supplied a custom image we post it as a separate leading
///   message, since a native poll can't carry an attachment in its own message.
///
/// • <b>Anonymous</b> — posts our own embed + buttons (<see cref="PollEmbedBuilder"/>)
///   carrying the 189th flag banner, so individual votes are never shown. Mirrors
///   the event-publisher dance: post the embed to get a message id, persist the
///   poll + options, then edit the message to attach the vote buttons (which need
///   the now-known poll id).
///
/// Either renderer then cross-posts a "new poll" announcement embed to
/// <see cref="BotConfig.PollAnnounceChannelId"/> (the clan's general channel), so
/// a poll sitting in a quiet #polls channel still reaches the people who need to
/// vote. Best-effort: a failed cross-post never fails the poll itself.
/// </summary>
public sealed class PollPublisher
{
    public const int MaxOptions = 10;
    public const int MaxQuestionLength = 300;  // Discord native poll question cap
    public const int MaxMessageLength = 2000;  // Discord message-content cap (holds the overflow description)
    public const int MaxOptionLength = 55;     // Discord native poll answer cap (applied to both modes)
    public const int MinDurationHours = 1;
    public const int MaxDurationHours = 768;  // Discord native poll cap (32 days)

    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<PollPublisher> _logger;

    public PollPublisher(
        DiscordSocketClient client,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<PollPublisher> logger)
    {
        _client   = client;
        _services = services;
        _config   = config.Value;
        _logger   = logger;
    }

    /// <summary>
    /// Publishes the poll and returns the persisted <see cref="Poll"/> alongside
    /// whether the cross-post announcement actually went out, so the caller's
    /// confirmation can't promise an announcement that was skipped or failed.
    /// </summary>
    public async Task<PollPublishResult> PublishAsync(IMessageChannel channel, PollDraft d)
    {
        var now      = DateTime.UtcNow;
        var closesAt = now.AddHours(Math.Clamp(d.DurationHours, MinDurationHours, MaxDurationHours));

        // Native polls get their mention tokens resolved to readable names up front
        // (see ResolveMentions), before anything is posted, persisted or announced —
        // so the poll, the cross-post and every later echo all read identically.
        if (d.Kind != PollKind.Anonymous) d = ResolveMentions(d);

        var poll = d.Kind == PollKind.Anonymous
            ? await PublishAnonymousAsync(channel, d, now, closesAt)
            : await PublishNativeAsync(channel, d, now, closesAt);

        // Surfacing the poll elsewhere is a nicety — never let it take the poll
        // down with it.
        var crossPosted = false;
        try { crossPosted = await CrossPostAsync(poll, d); }
        catch (Exception ex) { _logger.LogWarning(ex, "Cross-post failed for poll {PollId}", poll.Id); }

        return new PollPublishResult(poll, crossPosted);
    }

    // ─── Native (Discord poll object) ──────────────────────────────────────

    private async Task<Poll> PublishNativeAsync(IMessageChannel channel, PollDraft d, DateTime now, DateTime closesAt)
    {
        // NOTE: the draft arrives already mention-resolved (PublishAsync does it
        // before the branch, so the cross-post sees the same text).

        // A native poll can't share its message with an attachment, so a creator-
        // supplied banner rides a separate leading message. The default flag GIF
        // is deliberately NOT auto-posted here — native polls stay clean.
        if (d.ImageBytes is { Length: > 0 } banner && !string.IsNullOrWhiteSpace(d.ImageFileName))
        {
            try
            {
                using var fa = new FileAttachment(new MemoryStream(banner), d.ImageFileName);
                await channel.SendFileAsync(fa, text: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post leading banner for native poll '{Q}'", d.Question);
            }
        }

        // The poll question itself is capped at Discord's 300 chars; any longer
        // context the creator wants lives in the optional Description field, which
        // rides as ordinary message text above the poll (Discord renders content
        // over the poll object). Null/blank description → just the poll, nothing above.
        var description = string.IsNullOrWhiteSpace(d.Description) ? null : Trim(d.Description, MaxMessageLength);

        var pollProps = new PollProperties
        {
            Question = new PollMediaProperties { Text = Trim(d.Question, MaxQuestionLength) },
            Answers = d.Options.Select(o => new PollMediaProperties
            {
                Text  = Trim(o.Label, MaxOptionLength),
                Emoji = PollEmbedBuilder.ParseEmote(o.Emoji),
            }).ToList(),
            Duration         = (uint)Math.Clamp(d.DurationHours, MinDurationHours, MaxDurationHours),
            AllowMultiselect = d.AllowMultiselect,
            // Must be set explicitly: PollProperties.LayoutType defaults to 0, but
            // Discord only accepts the Default(=1) layout and rejects layout_type:0.
            LayoutType       = PollLayout.Default,
        };

        var posted = await channel.SendMessageAsync(text: description, poll: pollProps);

        var poll = await PersistAsync(d, channel, posted.Id, now, closesAt);
        _logger.LogInformation("Posted native poll {PollId} '{Q}' (msg {MsgId}), closes {Close:o}",
            poll.Id, poll.Question, posted.Id, closesAt);
        return poll;
    }

    // ─── Anonymous (custom embed + buttons) ────────────────────────────────

    private async Task<Poll> PublishAnonymousAsync(IMessageChannel channel, PollDraft d, DateTime now, DateTime closesAt)
    {
        // Resolve the banner (custom or default flag GIF) up front so the embed can
        // reference attachment://<name> and we attach the bytes exactly once.
        var probe = new Poll { ImageBytes = d.ImageBytes, ImageFileName = d.ImageFileName };
        var banner = PollImage.Resolve(probe);
        var fileName = banner?.FileName;

        // 1) Build a zero-state preview poll just for rendering the initial embed.
        var preview = new Poll
        {
            Question         = d.Question,
            CreatorName      = d.CreatorName,
            AllowMultiselect = d.AllowMultiselect,
            Status           = PollStatus.Open,
            ClosesAtUtc      = closesAt,
        };
        var previewOptions = d.Options
            .Select((o, i) => new PollOption { Id = -(i + 1), Position = i, Label = o.Label, Emoji = o.Emoji })
            .ToList();
        var embed = PollEmbedBuilder.BuildEmbed(
            preview, previewOptions, new Dictionary<int, int>(), totalVoters: 0, fileName);

        // 2) Post the embed (no buttons yet) to obtain a real message id. Any
        //    optional description rides as message text above the embed; later
        //    vote/close re-renders only touch the embed + components, so it stays.
        var description = string.IsNullOrWhiteSpace(d.Description) ? null : Trim(d.Description, MaxMessageLength);
        IUserMessage posted;
        if (banner is { } b)
        {
            using var fa = new FileAttachment(new MemoryStream(b.Bytes), b.FileName);
            posted = await channel.SendFileAsync(fa, text: description, embed: embed);
        }
        else
        {
            posted = await channel.SendMessageAsync(text: description, embed: embed);
        }

        // 3) Persist poll + options now that we have the message id.
        var poll = await PersistAsync(d, channel, posted.Id, now, closesAt, fileName);

        // 4) Attach the vote buttons (need the poll id) and re-render the embed
        //    against the real persisted option ids.
        List<PollOption> options;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            options = await db.PollOptions.Where(o => o.PollId == poll.Id).OrderBy(o => o.Position).ToListAsync();
        }

        try
        {
            var finalEmbed = PollEmbedBuilder.BuildEmbed(
                poll, options, new Dictionary<int, int>(), totalVoters: 0, fileName);
            await posted.ModifyAsync(m =>
            {
                m.Embed      = finalEmbed;
                m.Components = PollEmbedBuilder.BuildComponents(poll, options, locked: false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anonymous poll {PollId} posted (msg {MsgId}) but attaching buttons failed",
                poll.Id, posted.Id);
        }

        _logger.LogInformation("Posted anonymous poll {PollId} '{Q}' (msg {MsgId}), closes {Close:o}",
            poll.Id, poll.Question, posted.Id, closesAt);
        return poll;
    }

    // ─── Mention resolution (native only) ──────────────────────────────────

    /// <summary>
    /// Returns a copy of the draft with mention tokens in the question and option
    /// labels rewritten to readable names (<c>&lt;#123&gt;</c> → <c>#palworld</c>).
    /// Description, emoji and banner are carried through untouched.
    ///
    /// Native polls only. Discord renders a native poll's question and answers as
    /// plain strings — no markdown, no mention resolution — so a question typed
    /// with a channel picker would post showing the raw snowflake. Anonymous polls
    /// render through an embed, where Discord resolves the real token into a
    /// clickable link, so they keep the token as typed. The description is left
    /// alone either way: it rides as ordinary message content, which also renders
    /// the real mention.
    /// </summary>
    private PollDraft ResolveMentions(PollDraft d)
    {
        var question = PollMentionResolver.Resolve(_client, d.GuildId, d.Question) ?? d.Question;
        var options  = d.Options
            .Select(o => new PollOptionDraft(
                PollMentionResolver.Resolve(_client, d.GuildId, o.Label) ?? o.Label, o.Emoji))
            .ToList();

        // Nothing to rewrite → hand back the original object rather than a clone.
        var changed = question != d.Question
                   || options.Where((o, i) => o.Label != d.Options[i].Label).Any();
        if (!changed) return d;

        return new PollDraft
        {
            GuildId          = d.GuildId,
            CreatorId        = d.CreatorId,
            CreatorName      = d.CreatorName,
            Question         = question,
            Description      = d.Description,
            Kind             = d.Kind,
            AllowMultiselect = d.AllowMultiselect,
            AnnounceOnClose  = d.AnnounceOnClose,
            CrossPost        = d.CrossPost,
            DurationHours    = d.DurationHours,
            Options          = options,
            ImageBytes       = d.ImageBytes,
            ImageFileName    = d.ImageFileName,
        };
    }

    // ─── Cross-post announcement ───────────────────────────────────────────

    /// <summary>
    /// Posts a "new poll" embed to <see cref="BotConfig.PollAnnounceChannelId"/>
    /// with a jump link back to the real poll. Returns false (doing nothing) when
    /// cross-posting is off, unconfigured, opted out of for this poll, or when the
    /// poll already lives in that channel (no point announcing it to itself).
    /// </summary>
    private async Task<bool> CrossPostAsync(Poll poll, PollDraft d)
    {
        if (!poll.CrossPost || !_config.PollAnnounceEnabled) return false;

        var targetId = _config.PollAnnounceChannelId;
        if (targetId == 0 || targetId == poll.ChannelId) return false;

        if (_client.GetChannel(targetId) is not IMessageChannel target)
        {
            _logger.LogWarning("Poll announce channel {ChannelId} not found — skipping cross-post for poll {PollId}",
                targetId, poll.Id);
            return false;
        }

        var close = new DateTimeOffset(poll.ClosesAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();

        var eb = new EmbedBuilder()
            .WithTitle("🗳️ New poll")
            .WithColor(new Color(0x5865F2))
            .WithDescription($"**{Trim(poll.Question, 250)}**")
            .AddField("Where", MentionUtils.MentionChannel(poll.ChannelId), inline: true)
            .AddField("Closes", $"<t:{close}:R>", inline: true)
            .WithFooter($"Started by {poll.CreatorName}" +
                        (poll.Kind == PollKind.Anonymous ? " · 🔒 anonymous — individual votes stay hidden" : ""));

        if (_config.PollAnnounceShowOptions && d.Options.Count > 0)
        {
            var lines = d.Options.Select(o =>
                $"{(string.IsNullOrWhiteSpace(o.Emoji) ? "•" : o.Emoji)} {Trim(o.Label, 80)}");
            eb.AddField(d.AllowMultiselect ? "Options (pick as many as you like)" : "Options",
                Trim(string.Join("\n", lines), 1000));
        }

        eb.AddField("​",
            $"[Vote here](https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId})");

        // No pings: the question may legitimately contain an @role, and an
        // announcement should never be the thing that mass-mentions the server.
        await target.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);

        _logger.LogInformation("Cross-posted poll {PollId} announcement to channel {ChannelId}", poll.Id, targetId);
        return true;
    }

    // ─── Shared persistence ────────────────────────────────────────────────

    private async Task<Poll> PersistAsync(
        PollDraft d, IMessageChannel channel, ulong messageId,
        DateTime now, DateTime closesAt, string? imageFileName = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var poll = new Poll
        {
            GuildId          = d.GuildId,
            ChannelId        = channel.Id,
            MessageId        = messageId,
            CreatorId        = d.CreatorId,
            CreatorName      = d.CreatorName,
            Question         = Trim(d.Question, MaxQuestionLength),
            Kind             = d.Kind,
            AllowMultiselect = d.AllowMultiselect,
            Status           = PollStatus.Open,
            AnnounceOnClose  = d.AnnounceOnClose,
            CrossPost        = d.CrossPost,
            CreatedAt        = now,
            ClosesAtUtc      = closesAt,
            // Single reminder at the midpoint of the poll's life (now → close),
            // so a 24h poll reminds at hour 12 and a 2h poll at hour 1.
            RemindAtUtc      = now.AddTicks((closesAt - now).Ticks / 2),
            // Banner persisted only for anonymous polls. Store the custom blob (so a
            // future re-post could re-attach it); the default flag carries only its
            // file name (bytes come from the bundled asset).
            ImageBytes       = d.Kind == PollKind.Anonymous ? d.ImageBytes : null,
            ImageFileName    = d.Kind == PollKind.Anonymous ? imageFileName : null,
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync(); // materialize poll.Id

        for (var i = 0; i < d.Options.Count; i++)
        {
            db.PollOptions.Add(new PollOption
            {
                PollId   = poll.Id,
                Position = i,
                AnswerId = i + 1, // Discord numbers native answers 1..N in submission order
                Label    = Trim(d.Options[i].Label, MaxOptionLength),
                Emoji    = d.Options[i].Emoji,
            });
        }
        await db.SaveChangesAsync();

        return poll;
    }

    private static string Trim(string? s, int max)
    {
        s = (s ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>
/// What <see cref="PollPublisher.PublishAsync"/> hands back: the persisted poll,
/// plus whether its announcement actually reached the cross-post channel (false
/// if cross-posting was off/skipped for this poll, or if the post failed).
/// </summary>
public sealed record PollPublishResult(Poll Poll, bool CrossPosted);

/// <summary>Validated input handed from <c>PollCommandHandler</c> to the publisher.</summary>
public sealed class PollDraft
{
    public ulong GuildId { get; init; }
    public ulong CreatorId { get; init; }
    public string CreatorName { get; init; } = string.Empty;

    public string Question { get; init; } = string.Empty;

    /// <summary>
    /// Optional longer context posted as ordinary message text above the poll
    /// (the creator's own words, from the <c>/poll</c> <c>description</c> field).
    /// Null/blank means no leading message — just the poll itself.
    /// </summary>
    public string? Description { get; init; }

    public PollKind Kind { get; init; } = PollKind.Native;
    public bool AllowMultiselect { get; init; }
    public bool AnnounceOnClose { get; init; } = true;

    /// <summary>
    /// Announce this poll in the configured cross-post channel (and send its
    /// midpoint reminder there). Default true; <c>/poll cross_post:false</c> keeps
    /// the poll confined to the channel it was posted in.
    /// </summary>
    public bool CrossPost { get; init; } = true;

    public int DurationHours { get; init; } = 24;

    public List<PollOptionDraft> Options { get; init; } = new();

    public byte[]? ImageBytes { get; set; }
    public string? ImageFileName { get; set; }
}

public sealed record PollOptionDraft(string Label, string? Emoji);
