using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// </summary>
public sealed class PollPublisher
{
    public const int MaxOptions = 10;
    public const int MaxQuestionLength = 300; // Discord native poll question cap
    public const int MaxOptionLength = 55;    // Discord native poll answer cap (applied to both modes)
    public const int MinDurationHours = 1;
    public const int MaxDurationHours = 768;  // Discord native poll cap (32 days)

    private readonly IServiceProvider _services;
    private readonly ILogger<PollPublisher> _logger;

    public PollPublisher(IServiceProvider services, ILogger<PollPublisher> logger)
    {
        _services = services;
        _logger   = logger;
    }

    /// <summary>Publishes the poll and returns the persisted <see cref="Poll"/>.</summary>
    public async Task<Poll> PublishAsync(IMessageChannel channel, PollDraft d)
    {
        var now      = DateTime.UtcNow;
        var closesAt = now.AddHours(Math.Clamp(d.DurationHours, MinDurationHours, MaxDurationHours));

        return d.Kind == PollKind.Anonymous
            ? await PublishAnonymousAsync(channel, d, now, closesAt)
            : await PublishNativeAsync(channel, d, now, closesAt);
    }

    // ─── Native (Discord poll object) ──────────────────────────────────────

    private async Task<Poll> PublishNativeAsync(IMessageChannel channel, PollDraft d, DateTime now, DateTime closesAt)
    {
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
            // LayoutType left at its default (Discord only supports the default
            // layout today) to avoid pinning an enum member name.
        };

        var posted = await channel.SendMessageAsync(poll: pollProps);

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

        // 2) Post the embed (no buttons yet) to obtain a real message id.
        IUserMessage posted;
        if (banner is { } b)
        {
            using var fa = new FileAttachment(new MemoryStream(b.Bytes), b.FileName);
            posted = await channel.SendFileAsync(fa, embed: embed);
        }
        else
        {
            posted = await channel.SendMessageAsync(embed: embed);
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
            CreatedAt        = now,
            ClosesAtUtc      = closesAt,
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

/// <summary>Validated input handed from <c>PollCommandHandler</c> to the publisher.</summary>
public sealed class PollDraft
{
    public ulong GuildId { get; init; }
    public ulong CreatorId { get; init; }
    public string CreatorName { get; init; } = string.Empty;

    public string Question { get; init; } = string.Empty;
    public PollKind Kind { get; init; } = PollKind.Native;
    public bool AllowMultiselect { get; init; }
    public bool AnnounceOnClose { get; init; } = true;
    public int DurationHours { get; init; } = 24;

    public List<PollOptionDraft> Options { get; init; } = new();

    public byte[]? ImageBytes { get; set; }
    public string? ImageFileName { get; set; }
}

public sealed record PollOptionDraft(string Label, string? Emoji);
