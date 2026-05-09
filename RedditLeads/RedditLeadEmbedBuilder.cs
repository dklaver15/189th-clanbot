using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Builds the embed and button rows for a RedditLead. Centralising both
/// here keeps the polling service and the button handler from drifting
/// — both render the same lead the same way regardless of which path
/// triggered the rebuild.
///
/// ── customId format ──
/// "rl:{leadId}:{action}" — short prefix to stay well under Discord's
/// 100-char customId cap, integer leadId for stable lookup. Actions:
/// claim, contacted, joined, declined, noresp, skip, unclaim. The
/// integer lead ID is the DB primary key, which is stable across
/// embed re-renders.
/// </summary>
public static class RedditLeadEmbedBuilder
{
    /// <summary>Prefix the button handler uses to filter customIds it owns.</summary>
    public const string CustomIdPrefix = "rl:";

    private const int ExcerptLimit  = 350;

    /// <summary>
    /// Discord embed field-name max is 256 chars. We keep one in reserve for
    /// the bold-formatting and any trailing punctuation we tack on.
    /// </summary>
    private const int TitleLimit = 240;

    public static Embed Build(RedditLead lead)
    {
        var color = lead.Status switch
        {
            LeadStatus.New        => Color.Blue,
            LeadStatus.Claimed    => Color.Gold,
            LeadStatus.Contacted  => Color.Orange,
            LeadStatus.Joined     => Color.Green,
            LeadStatus.Declined   => Color.DarkGrey,
            LeadStatus.NoResponse => Color.LightGrey,
            LeadStatus.Skipped    => Color.DarkerGrey,
            _                     => Color.Default,
        };

        var statusBadge = lead.Status switch
        {
            LeadStatus.New        => "🎯 New Lead",
            LeadStatus.Claimed    => "✋ Claimed",
            LeadStatus.Contacted  => "✉️ Contacted",
            LeadStatus.Joined     => "🎉 Joined",
            LeadStatus.Declined   => "👋 Declined",
            LeadStatus.NoResponse => "💤 No Response",
            LeadStatus.Skipped    => "⏭️ Skipped",
            _                     => lead.Status.ToString(),
        };

        var ageText   = FormatAccountAge(lead.AuthorAccountAgeDays);
        var karmaText = FormatKarma(lead.AuthorKarma);

        var meta =
            $"**u/{lead.AuthorUsername}**  ·  account {ageText}  ·  {karmaText} karma\n" +
            $"Posted: {DiscordTimestampRelative(lead.PostedAtUtc)}";

        var excerpt = string.IsNullOrWhiteSpace(lead.Excerpt)
            ? "_(no body text)_"
            : Quoted(Truncate(lead.Excerpt, ExcerptLimit));

        var builder = new EmbedBuilder()
            .WithColor(color)
            .WithAuthor($"{statusBadge} — r/{lead.Subreddit}")
            .WithTitle(Truncate(lead.Title, TitleLimit))
            .WithUrl(lead.Url)
            .WithDescription(meta + "\n\n" + excerpt)
            .AddField("Matched", string.IsNullOrWhiteSpace(lead.MatchedKeywords) ? "_n/a_" : lead.MatchedKeywords, inline: false);

        var footer = BuildFooter(lead);
        if (!string.IsNullOrEmpty(footer))
            builder.WithFooter(footer);

        return builder.Build();
    }

    /// <summary>
    /// Builds the action-row layout for a lead. Returns null for terminal
    /// states — Discord renders the embed without any component row, which
    /// is exactly what we want once the lead is closed out.
    /// </summary>
    public static MessageComponent? BuildComponents(RedditLead lead)
    {
        var components = new ComponentBuilder();

        // The "View Post" link button is always present (except on terminal
        // states where we strip everything to keep the channel quiet).
        // Link buttons have no customId and don't fire button events.
        switch (lead.Status)
        {
            case LeadStatus.New:
                components
                    .WithButton("View Post", style: ButtonStyle.Link, url: lead.Url, emote: new Emoji("🔗"))
                    .WithButton("Claim",     customId: CustomId(lead.Id, "claim"), style: ButtonStyle.Primary, emote: new Emoji("✋"))
                    .WithButton("Skip",      customId: CustomId(lead.Id, "skip"),  style: ButtonStyle.Secondary, emote: new Emoji("⏭️"));
                break;

            case LeadStatus.Claimed:
                components
                    .WithButton("View Post", style: ButtonStyle.Link, url: lead.Url, emote: new Emoji("🔗"))
                    .WithButton("Contacted",   customId: CustomId(lead.Id, "contacted"), style: ButtonStyle.Primary,   emote: new Emoji("✉️"))
                    .WithButton("Joined",      customId: CustomId(lead.Id, "joined"),    style: ButtonStyle.Success,   emote: new Emoji("🎉"))
                    .WithButton("Declined",    customId: CustomId(lead.Id, "declined"),  style: ButtonStyle.Secondary, emote: new Emoji("👋"))
                    .WithButton("No Response", customId: CustomId(lead.Id, "noresp"),    style: ButtonStyle.Secondary, emote: new Emoji("💤"))
                    // Unclaim drops to row 2 (5 buttons already on row 1).
                    .WithButton("Unclaim",     customId: CustomId(lead.Id, "unclaim"),   style: ButtonStyle.Secondary, emote: new Emoji("↩️"), row: 1);
                break;

            case LeadStatus.Contacted:
                components
                    .WithButton("View Post", style: ButtonStyle.Link, url: lead.Url, emote: new Emoji("🔗"))
                    .WithButton("Joined",      customId: CustomId(lead.Id, "joined"),    style: ButtonStyle.Success,   emote: new Emoji("🎉"))
                    .WithButton("Declined",    customId: CustomId(lead.Id, "declined"),  style: ButtonStyle.Secondary, emote: new Emoji("👋"))
                    .WithButton("No Response", customId: CustomId(lead.Id, "noresp"),    style: ButtonStyle.Secondary, emote: new Emoji("💤"));
                break;

            // Terminal states: link button only so officers can still jump
            // to the post for retrospectives, but no action buttons.
            case LeadStatus.Joined:
            case LeadStatus.Declined:
            case LeadStatus.NoResponse:
            case LeadStatus.Skipped:
                components.WithButton("View Post", style: ButtonStyle.Link, url: lead.Url, emote: new Emoji("🔗"));
                break;

            default:
                return null;
        }

        return components.Build();
    }

    /// <summary>Format-string for a customId. Public so the button handler can mirror it when parsing.</summary>
    public static string CustomId(int leadId, string action) => $"{CustomIdPrefix}{leadId}:{action}";

    // ── Internal formatting helpers ──────────────────────────────────

    private static string BuildFooter(RedditLead lead)
    {
        // Build the audit trail incrementally. New leads have no footer
        // until someone takes an action; from there each transition adds
        // a segment.
        var segments = new List<string>();

        if (!string.IsNullOrEmpty(lead.ClaimedByUsername) && lead.ClaimedAtUtc.HasValue)
            segments.Add($"Claimed by {lead.ClaimedByUsername} at {lead.ClaimedAtUtc.Value:HH:mm} UTC");

        if (lead.ContactedAtUtc.HasValue)
            segments.Add($"Contacted at {lead.ContactedAtUtc.Value:HH:mm} UTC");

        if (lead.OutcomeAtUtc.HasValue)
        {
            var outcomeIcon = lead.Status switch
            {
                LeadStatus.Joined     => "🎉 Joined",
                LeadStatus.Declined   => "👋 Declined",
                LeadStatus.NoResponse => "💤 No Response",
                LeadStatus.Skipped    => "⏭️ Skipped",
                _                     => lead.Status.ToString(),
            };
            segments.Add($"{outcomeIcon} at {lead.OutcomeAtUtc.Value:HH:mm} UTC");
        }

        return string.Join(" • ", segments);
    }

    private static string FormatAccountAge(int days)
    {
        if (days < 30)  return $"{days}d";
        if (days < 365) return $"{days / 30}mo";
        var years = days / 365.0;
        return years < 10 ? $"{years:0.#}y" : $"{(int)years}y";
    }

    private static string FormatKarma(int karma)
    {
        if (karma < 1000)    return karma.ToString("N0");
        if (karma < 10000)   return (karma / 1000.0).ToString("0.#") + "k";
        if (karma < 1_000_000) return (karma / 1000) + "k";
        return (karma / 1_000_000.0).ToString("0.#") + "M";
    }

    private static string DiscordTimestampRelative(DateTime utc) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>";

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)].TrimEnd() + "…";

    /// <summary>
    /// Convert plain text into a Discord-flavoured markdown blockquote by
    /// prefixing each line with "> ". Done line-by-line so multi-paragraph
    /// excerpts render as a single visual quote block.
    /// </summary>
    private static string Quoted(string s) =>
        string.Join("\n", s.Split('\n').Select(line => "> " + line));
}
