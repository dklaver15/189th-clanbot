using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Immutable, JSON-serializable snapshot of a Discord message + its embeds.
///
/// Goal: lossless preservation so Phase 2's parser worker, Phase 4's reconciler,
/// and Phase 6's replay command can all reconstruct what Apollo posted without
/// needing to refetch the live message from Discord (which may have been edited
/// or deleted in the meantime).
///
/// Round-trip pairing: ApolloEmbedReconstructor rebuilds an IEmbed from the
/// snapshot's embed records, so ApolloEmbedParser.Parse can run against historical
/// payloads exactly as it would against a live message.
/// </summary>
public sealed record ApolloMessageSnapshot(
    ulong MessageId,
    ulong ChannelId,
    ulong GuildId,
    ulong AuthorId,
    string AuthorUsername,
    string? Content,
    DateTimeOffset Timestamp,
    DateTimeOffset? EditedTimestamp,
    IReadOnlyList<ApolloEmbedSnapshot> Embeds)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ApolloMessageSnapshot From(IMessage message) => new(
        message.Id,
        message.Channel.Id,
        (message.Channel as IGuildChannel)?.GuildId ?? 0,
        message.Author.Id,
        message.Author.Username,
        message.Content,
        message.Timestamp,
        message.EditedTimestamp,
        message.Embeds.Select(ApolloEmbedSnapshot.From).ToList());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ApolloMessageSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<ApolloMessageSnapshot>(json, JsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize ApolloMessageSnapshot");
}

public sealed record ApolloEmbedSnapshot(
    string? Title,
    string? Description,
    string? Url,
    DateTimeOffset? Timestamp,
    uint? Color,
    string? AuthorName,
    string? AuthorUrl,
    string? AuthorIconUrl,
    string? FooterText,
    string? FooterIconUrl,
    string? ThumbnailUrl,
    string? ImageUrl,
    IReadOnlyList<ApolloEmbedFieldSnapshot> Fields)
{
    public static ApolloEmbedSnapshot From(IEmbed embed) => new(
        embed.Title,
        embed.Description,
        embed.Url,
        embed.Timestamp,
        embed.Color?.RawValue,
        embed.Author?.Name,
        embed.Author?.Url,
        embed.Author?.IconUrl,
        embed.Footer?.Text,
        embed.Footer?.IconUrl,
        embed.Thumbnail?.Url,
        embed.Image?.Url,
        embed.Fields.Select(f => new ApolloEmbedFieldSnapshot(f.Name, f.Value, f.Inline)).ToList());
}

public sealed record ApolloEmbedFieldSnapshot(string Name, string Value, bool Inline);
