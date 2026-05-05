using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Rebuilds a Discord.Net IEmbed from an ApolloEmbedSnapshot so that the
/// existing ApolloEmbedParser.Parse(IEmbed) function can be reused unchanged
/// against captured payloads.
///
/// ── Why this approach ──
/// ApolloEmbedParser only reads a small subset of IEmbed: Title, Description,
/// Author.Name, Footer.Text, and Fields (Name/Value/Inline). EmbedBuilder.Build
/// produces an Embed (which implements IEmbed) that round-trips all of those
/// faithfully. So we can replay parsing against a snapshot with zero changes
/// to the parser itself.
///
/// ── Validation caveat ──
/// EmbedBuilder.Build enforces Discord's structural limits (≤ 6000 chars total,
/// ≤ 25 fields, ≤ 256 char title, etc.). A snapshot of a real Apollo post
/// trivially satisfies these. If reconstruction ever throws — e.g. a future
/// Apollo format pushes a limit — we treat it as a parse failure and stamp
/// ApolloMessageLog.ParseError. The raw payload is still preserved, so the
/// fix-and-replay loop continues to work.
///
/// ── What gets reconstructed ──
/// Title, Description, URL, Color, Timestamp, Author (name/url/iconUrl),
/// Footer (text/iconUrl), Thumbnail, Image, and all Fields. Provider/Video
/// are not used by the parser and are skipped.
/// </summary>
public static class ApolloEmbedReconstructor
{
    public static IEmbed Reconstruct(ApolloEmbedSnapshot snapshot)
    {
        var builder = new EmbedBuilder();

        if (!string.IsNullOrEmpty(snapshot.Title))
            builder.WithTitle(snapshot.Title);

        if (!string.IsNullOrEmpty(snapshot.Description))
            builder.WithDescription(snapshot.Description);

        if (!string.IsNullOrEmpty(snapshot.Url))
            builder.WithUrl(snapshot.Url);

        if (snapshot.Color is not null)
            builder.WithColor(new Color(snapshot.Color.Value));

        if (snapshot.Timestamp is not null)
            builder.WithTimestamp(snapshot.Timestamp.Value);

        if (!string.IsNullOrEmpty(snapshot.AuthorName))
        {
            builder.WithAuthor(
                snapshot.AuthorName,
                iconUrl: snapshot.AuthorIconUrl,
                url: snapshot.AuthorUrl);
        }

        if (!string.IsNullOrEmpty(snapshot.FooterText))
        {
            builder.WithFooter(snapshot.FooterText, snapshot.FooterIconUrl);
        }

        if (!string.IsNullOrEmpty(snapshot.ThumbnailUrl))
            builder.WithThumbnailUrl(snapshot.ThumbnailUrl);

        if (!string.IsNullOrEmpty(snapshot.ImageUrl))
            builder.WithImageUrl(snapshot.ImageUrl);

        foreach (var field in snapshot.Fields)
        {
            // Defensive: EmbedBuilder rejects empty field values. Apollo
            // always populates Value, but a future format change shouldn't
            // make reconstruction blow up.
            if (string.IsNullOrEmpty(field.Name) || string.IsNullOrEmpty(field.Value))
                continue;

            builder.AddField(field.Name, field.Value, field.Inline);
        }

        return builder.Build();
    }

    /// <summary>
    /// Convenience: pulls the first embed out of a message snapshot and
    /// reconstructs it. Returns null if the snapshot has no embeds — Apollo
    /// posts always carry exactly one embed, so a missing embed is a strong
    /// signal that this isn't an event message and parsing should be skipped.
    /// </summary>
    public static IEmbed? FirstEmbedOrNull(ApolloMessageSnapshot snapshot)
    {
        if (snapshot.Embeds.Count == 0) return null;
        return Reconstruct(snapshot.Embeds[0]);
    }
}
