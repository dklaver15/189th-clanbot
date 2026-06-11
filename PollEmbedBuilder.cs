using System.Text;
using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Renders the custom embed + buttons for an ANONYMOUS poll. (Native polls use
/// Discord's own UI and never come through here.) The embed shows the question,
/// each option with a live tally bar, and a banner — but never who voted what.
/// Used by the publisher to post and by the vote handler to re-render on each
/// click, so both stay in sync.
///
/// Times render as Discord &lt;t:unix&gt; markdown so each viewer sees the close
/// time in their own zone.
/// </summary>
public static class PollEmbedBuilder
{
    public const string VotePrefix  = "poll:vote:";   // poll:vote:<pollId>:<optionId>
    public const string ClosePrefix = "poll:close:";  // poll:close:<pollId>

    private static readonly Color Blurple = new(0x5865F2);

    private const int BarWidth = 12;

    public static string VoteButtonId(int pollId, int optionId) => $"{VotePrefix}{pollId}:{optionId}";
    public static string CloseButtonId(int pollId) => $"{ClosePrefix}{pollId}";

    /// <summary>
    /// Builds the poll embed. <paramref name="countsByOptionId"/> maps PollOption.Id →
    /// number of votes; <paramref name="totalVoters"/> is the distinct member count
    /// (for the multiselect case where votes &gt; voters).
    /// </summary>
    public static Embed BuildEmbed(
        Poll poll,
        IReadOnlyList<PollOption> options,
        IReadOnlyDictionary<int, int> countsByOptionId,
        int totalVoters,
        string? imageFileName)
    {
        var closed = poll.Status == PollStatus.Closed;
        var totalVotes = countsByOptionId.Values.Sum();

        var eb = new EmbedBuilder()
            .WithTitle($"📊 {Trim(poll.Question, 250)}")
            .WithColor(closed ? Color.DarkGrey : Blurple);

        var sb = new StringBuilder();
        var maxCount = options.Count == 0 ? 0 : options.Max(o => countsByOptionId.GetValueOrDefault(o.Id));

        foreach (var opt in options.OrderBy(o => o.Position))
        {
            var count = countsByOptionId.GetValueOrDefault(opt.Id);
            var pct = totalVotes == 0 ? 0 : (int)Math.Round(count * 100.0 / totalVotes);
            var leader = closed && count > 0 && count == maxCount;

            var head = string.IsNullOrWhiteSpace(opt.Emoji) ? "" : opt.Emoji + " ";
            var label = Trim(opt.Label, 80);
            sb.Append(leader ? "👑 " : "").Append(head).Append("**").Append(label).Append("**\n");
            sb.Append('`').Append(Bar(pct)).Append("` ")
              .Append(count).Append(count == 1 ? " vote" : " votes")
              .Append(" · ").Append(pct).Append("%\n\n");
        }

        if (options.Count == 0) sb.Append("*(no options)*");
        eb.WithDescription(sb.ToString().TrimEnd());

        // Status / meta line.
        var meta = new List<string>
        {
            "🔒 Anonymous",
            poll.AllowMultiselect ? "multiple choice" : "single choice",
            $"{totalVoters} {(totalVoters == 1 ? "voter" : "voters")}",
        };
        eb.AddField("​",
            string.Join(" · ", meta) + "\n" +
            (closed
                ? $"🛑 Closed {EventTimeParser.Stamp(poll.ClosedAtUtc ?? poll.ClosesAtUtc, 'R')}"
                : $"⏳ Closes {EventTimeParser.Stamp(poll.ClosesAtUtc, 'F')} ({EventTimeParser.Stamp(poll.ClosesAtUtc, 'R')})"),
            inline: false);

        var footer = new List<string> { $"Poll by {poll.CreatorName}" };
        if (!closed) footer.Add("votes are hidden — only totals are shown");
        eb.WithFooter(string.Join(" • ", footer));

        if (!string.IsNullOrWhiteSpace(imageFileName))
            eb.WithImageUrl($"attachment://{imageFileName}");

        return eb.Build();
    }

    /// <summary>
    /// Option buttons (up to 5 per row → 10 options span 2 rows), then a Close row
    /// for the creator/officers. When <paramref name="locked"/> (poll closed),
    /// every button is disabled and the Close button is dropped.
    /// </summary>
    public static MessageComponent BuildComponents(Poll poll, IReadOnlyList<PollOption> options, bool locked)
    {
        var cb = new ComponentBuilder();

        var ordered = options.OrderBy(o => o.Position).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var opt = ordered[i];
            var emote = ParseEmote(opt.Emoji);
            var label = Trim(string.IsNullOrWhiteSpace(opt.Label) ? $"Option {i + 1}" : opt.Label, 80);
            cb.WithButton(label, VoteButtonId(poll.Id, opt.Id), ButtonStyle.Secondary,
                emote: emote, disabled: locked, row: i / 5);
        }

        if (!locked)
        {
            var closeRow = (ordered.Count - 1) / 5 + 1;
            cb.WithButton("Close poll", CloseButtonId(poll.Id), ButtonStyle.Danger, row: closeRow);
        }

        return cb.Build();
    }

    /// <summary>Unicode or custom (&lt;:name:id&gt;) emoji → IEmote, or null if unparseable/absent.</summary>
    public static IEmote? ParseEmote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Emote.TryParse(raw, out var custom)) return custom;
        try { return new Emoji(raw); } catch { return null; }
    }

    private static string Bar(int pct)
    {
        var filled = (int)Math.Round(pct / 100.0 * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        return new string('█', filled) + new string('░', BarWidth - filled);
    }

    private static string Trim(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
