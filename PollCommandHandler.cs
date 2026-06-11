using System.Text.RegularExpressions;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/poll</c> slash command — open to all members. One command, two
/// renderers (the hybrid): a normal poll posts as a native Discord poll; an
/// <c>anonymous:true</c> poll posts as our custom embed where individual votes
/// are hidden. Validates input, optionally captures a banner image (upload, or a
/// Tenor/Giphy/direct link — same flow as event creation), then hands a
/// <see cref="PollDraft"/> to <see cref="PollPublisher"/>.
/// </summary>
public sealed class PollCommandHandler
{
    public const string CommandName = "poll";

    private const int MaxOptions = PollPublisher.MaxOptions;

    // Shared client for pulling an uploaded banner off the Discord CDN.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly PollPublisher _publisher;
    private readonly ILogger<PollCommandHandler> _logger;

    public PollCommandHandler(PollPublisher publisher, ILogger<PollCommandHandler> logger)
    {
        _publisher = publisher;
        _logger    = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public static SlashCommandProperties BuildCommand()
    {
        var builder = new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Create a poll — native by default, or anonymous")
            .AddOption("question", ApplicationCommandOptionType.String, "The poll question", isRequired: true)
            .AddOption("option1", ApplicationCommandOptionType.String, "First choice (optional leading emoji, e.g. 🔥 Build a base)", isRequired: true)
            .AddOption("option2", ApplicationCommandOptionType.String, "Second choice", isRequired: true);

        for (var i = 3; i <= MaxOptions; i++)
            builder.AddOption($"option{i}", ApplicationCommandOptionType.String, $"Choice {i}", isRequired: false);

        builder
            .AddOption("anonymous", ApplicationCommandOptionType.Boolean,
                "Hide who voted for what (custom embed instead of a native poll). Default: false", isRequired: false)
            .AddOption("multiselect", ApplicationCommandOptionType.Boolean,
                "Let members pick more than one option. Default: false", isRequired: false)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("hours")
                .WithDescription("How long the poll stays open, in hours (1–768, default 24)")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(PollPublisher.MinDurationHours)
                .WithMaxValue(PollPublisher.MaxDurationHours)
                .WithRequired(false))
            .AddOption("image", ApplicationCommandOptionType.Attachment,
                "Banner for an anonymous poll (PNG/JPG/GIF/WebP, max 8 MB). Defaults to the 189th flag", isRequired: false)
            .AddOption("image_url", ApplicationCommandOptionType.String,
                "Banner link instead of an upload (Tenor, Giphy, or direct)", isRequired: false)
            .AddOption("announce", ApplicationCommandOptionType.Boolean,
                "Post a winner announcement when the poll closes. Default: true", isRequired: false);

        return builder.Build();
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /poll");
            try { await command.FollowupAsync("Something went wrong creating the poll. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null || command.Channel is not IMessageChannel channel)
        {
            await command.FollowupAsync("This command can only be used in a server channel.", ephemeral: true);
            return;
        }

        string? Opt(string name) => command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as string;
        bool Flag(string name) => command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as bool? ?? false;

        var question = (Opt("question") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            await command.FollowupAsync("The poll needs a question.", ephemeral: true);
            return;
        }

        // Collect option1..optionN in order, parsing an optional leading emoji.
        var options = new List<PollOptionDraft>();
        for (var i = 1; i <= MaxOptions; i++)
        {
            var raw = Opt($"option{i}");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (emoji, label) = ExtractLeadingEmoji(raw.Trim());
            if (string.IsNullOrWhiteSpace(label)) continue;
            options.Add(new PollOptionDraft(label, emoji));
        }

        if (options.Count < 2)
        {
            await command.FollowupAsync("A poll needs at least **2** options.", ephemeral: true);
            return;
        }

        var anonymous   = Flag("anonymous");
        var multiselect = Flag("multiselect");
        var announce    = command.Data.Options.FirstOrDefault(o => o.Name == "announce")?.Value as bool? ?? true;
        var hours       = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "hours")?.Value as long? ?? 24);
        hours = Math.Clamp(hours, PollPublisher.MinDurationHours, PollPublisher.MaxDurationHours);

        var draft = new PollDraft
        {
            GuildId          = command.GuildId.Value,
            CreatorId        = command.User.Id,
            CreatorName      = (command.User as SocketGuildUser)?.DisplayName ?? command.User.GlobalName ?? command.User.Username,
            Question         = question,
            Kind             = anonymous ? PollKind.Anonymous : PollKind.Native,
            AllowMultiselect = multiselect,
            AnnounceOnClose  = announce,
            DurationHours    = hours,
            Options          = options,
        };

        // Optional banner: upload takes precedence over a link.
        var attachment = command.Data.Options.FirstOrDefault(o => o.Name == "image")?.Value as IAttachment;
        var imageUrl   = Opt("image_url");
        var imageError = await TryAttachBannerAsync(draft, attachment, imageUrl);
        if (imageError is not null)
        {
            await command.FollowupAsync($"🖼️ {imageError}", ephemeral: true);
            return;
        }

        var poll = await _publisher.PublishAsync(channel, draft);

        var kindNote = poll.Kind == PollKind.Anonymous
            ? "🔒 **Anonymous** — individual votes are hidden; only totals show."
            : "📊 **Native** Discord poll.";
        var imgNote = poll.Kind == PollKind.Native && draft.ImageBytes is { Length: > 0 }
            ? "\n*(Native polls can't show a banner in the poll itself, so I posted your image just above it.)*"
            : "";
        await command.FollowupAsync(
            $"✅ Poll posted in {MentionUtils.MentionChannel(channel.Id)}.\n{kindNote}{imgNote}",
            ephemeral: true);
    }

    /// <summary>
    /// Downloads/fetches the banner into the draft. Returns null on success or a
    /// user-facing error string on failure (the poll is then not posted).
    /// </summary>
    private async Task<string?> TryAttachBannerAsync(PollDraft draft, IAttachment? attachment, string? imageUrl)
    {
        if (attachment is not null)
        {
            var looksImage = (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                          || EventImage.IsAllowedExtension(attachment.Filename);
            if (!looksImage)
                return "That attachment isn't a PNG/JPG/GIF/WebP. Posting without a custom banner — try again to add one.";
            if (attachment.Size > EventImage.MaxBytes)
                return $"That image is too large (max {EventImage.MaxBytes / (1024 * 1024)} MB).";

            byte[] bytes;
            try { bytes = await Http.GetByteArrayAsync(attachment.Url); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download poll banner for {User}", draft.CreatorId);
                return "Couldn't download that image. Try again.";
            }
            if (bytes.Length > EventImage.MaxBytes)
                return $"That image is too large (max {EventImage.MaxBytes / (1024 * 1024)} MB).";

            var name = EventImage.Sanitize(attachment.Filename);
            draft.ImageFileName = name;
            draft.ImageBytes    = EventImage.Downscale(bytes, name);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var res = await EventImageFetcher.FromUrlAsync(imageUrl);
            if (!res.Ok)
                return $"{res.Error}";
            draft.ImageFileName = res.FileName;
            draft.ImageBytes    = EventImage.Downscale(res.Bytes!, res.FileName);
            return null;
        }

        return null; // no banner supplied — default flag (anonymous) or none (native)
    }

    // ─── Emoji extraction ──────────────────────────────────────────────────

    private static readonly Regex CustomEmojiPrefix = new(@"^<a?:\w{2,32}:\d{15,25}>", RegexOptions.Compiled);

    /// <summary>
    /// Splits an optional leading emoji off an option string. A custom emoji
    /// (<c>&lt;:name:id&gt;</c>) or a single unicode emoji followed by a space is
    /// taken as the option's emoji; everything else stays in the label. Predictable
    /// by design — when in doubt the whole string is the label.
    /// </summary>
    public static (string? Emoji, string Label) ExtractLeadingEmoji(string raw)
    {
        var m = CustomEmojiPrefix.Match(raw);
        if (m.Success)
        {
            var rest = raw[m.Length..].TrimStart();
            return rest.Length > 0 ? (m.Value, rest) : (null, raw);
        }

        var sp = raw.IndexOf(' ');
        if (sp > 0)
        {
            var first = raw[..sp];
            var rest  = raw[(sp + 1)..].TrimStart();
            if (rest.Length > 0 && LooksLikeEmoji(first))
                return (first, rest);
        }

        return (null, raw);
    }

    private static bool LooksLikeEmoji(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8) return false;
        var hasEmojiScalar = false;
        foreach (var rune in token.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x80)
            {
                if (char.IsLetterOrDigit((char)v)) return false; // contains plain text → not an emoji token
                continue;
            }
            if (v >= 0x1F000
                || (v is >= 0x2190 and <= 0x2BFF)   // arrows, misc symbols
                || (v is >= 0x2600 and <= 0x27BF)   // misc symbols + dingbats
                || (v is >= 0xFE00 and <= 0xFE0F)   // variation selectors
                || (v is >= 0x1F1E6 and <= 0x1F1FF) // regional indicators
                || v == 0x303D || v == 0x2B50 || v == 0x2B55)
                hasEmojiScalar = true;
        }
        return hasEmojiScalar;
    }
}
