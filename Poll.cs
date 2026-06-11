namespace ClanGuardBot.Models;

/// <summary>
/// One poll authored through <c>/poll</c>. The 189th runs polls in two distinct
/// renderers behind a single command — a deliberate hybrid:
///
/// ── Native (<see cref="PollKind.Native"/>) ──
/// A real Discord native poll (<c>PollProperties</c> on the message). Discord
/// draws the sleek animated UI, owns the vote buttons, and auto-finalizes at its
/// own duration. We still persist the poll + every vote (captured live from the
/// <c>PollVoteAdded</c>/<c>PollVoteRemoved</c> gateway events, keyed by
/// <see cref="MessageId"/> and <see cref="PollOption.AnswerId"/>) so analytics
/// survive the poll — Discord throws the vote breakdown away once the post ages
/// out. Native polls are NOT anonymous (anyone can see who voted what).
///
/// ── Anonymous (<see cref="PollKind.Anonymous"/>) ──
/// The one thing native polls structurally can't do. We render our own embed
/// (<c>PollEmbedBuilder</c>) with our own vote buttons, so individual choices are
/// never shown in Discord — only live tallies. Per-user vote rows are still
/// persisted (for participation / non-voter analytics) but never displayed.
///
/// Either way the poll posts to the channel <c>/poll</c> was run in, carries the
/// waving 189th flag banner by default (overridable per poll with an
/// image/GIF/Giphy link, exactly like event creation — see <c>PollImage</c>),
/// and is closed + announced by <c>PollCloseService</c> at <see cref="ClosesAtUtc"/>.
///
/// ── Times ──
/// All ...Utc columns are UTC. The embed/announcement render them as Discord
/// &lt;t:unix&gt; markdown so each viewer sees their own local clock.
/// </summary>
public class Poll
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Channel the poll was posted to (where <c>/poll</c> was run).</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// The poll message. For native polls this is the message carrying the
    /// Discord poll object (matched against the <c>PollVoteAdded</c> gateway
    /// event's message id). For anonymous polls it's the bot's custom embed,
    /// edited in place on each vote and locked when the poll closes.
    /// </summary>
    public ulong MessageId { get; set; }

    public ulong CreatorId { get; set; }
    public string CreatorName { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public PollKind Kind { get; set; } = PollKind.Native;

    /// <summary>When true, members may pick more than one option.</summary>
    public bool AllowMultiselect { get; set; }

    public PollStatus Status { get; set; } = PollStatus.Open;

    /// <summary>Post a winner announcement to the channel when the poll closes.</summary>
    public bool AnnounceOnClose { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the poll auto-closes. For native polls this is also handed to Discord
    /// as the poll duration (it finalizes the native UI itself); for anonymous
    /// polls <c>PollCloseService</c> enforces it by locking the buttons.
    /// </summary>
    public DateTime ClosesAtUtc { get; set; }

    /// <summary>Stamped when <see cref="Status"/> flips to <see cref="PollStatus.Closed"/>.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>
    /// Guards the close announcement against double-posting if the close sweep
    /// overlaps a restart. Set once the winner announcement has been posted.
    /// </summary>
    public bool ResultsAnnounced { get; set; }

    /// <summary>The posted winner-announcement message, if any (for idempotency / cleanup).</summary>
    public ulong? ResultsMessageId { get; set; }

    /// <summary>
    /// Optional custom banner bytes (the creator's own image/GIF/Giphy pick),
    /// attached to the poll post as <c>attachment://</c><see cref="ImageFileName"/>.
    /// Null means "use the default waving-flag banner asset" (resolved by
    /// <c>PollImage</c> from <c>Assets/189th_banner.gif</c>), so the common case
    /// stores no blob. Only meaningful for anonymous polls — native polls can't
    /// carry an attachment in the same message as the poll object.
    /// </summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>Attachment file name for the custom banner; null = default flag banner.</summary>
    public string? ImageFileName { get; set; }
}

public enum PollKind
{
    /// <summary>Discord native poll. Public votes, captured for analytics via gateway events.</summary>
    Native = 1,

    /// <summary>Custom embed + buttons. Votes hidden in Discord, persisted for analytics.</summary>
    Anonymous = 2,
}

public enum PollStatus
{
    Open = 1,
    Closed = 2,
}
