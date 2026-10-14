using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The Short button: a 9:16 YouTube Short made from one forwarded clip.
///
/// Short click → (landscape or unknown size) a private framing choice → a form
/// (start, end, title) → render (<see cref="ShortRenderer"/>) → upload. Opening a
/// form must be an interaction's first response, so nothing slow happens before
/// it; the clip's size comes from the row (Discord's attachment metadata).
/// Independent of the Upload button: each has its own row (<see cref="VideoSubmission.Kind"/>)
/// and the same claim / release / original-deleted handling.
/// </summary>
public sealed partial class VideoRepostHandler
{
    internal const string ShortFramingPrefix = "vidshortl:";
    internal const string ShortFormPrefix    = "vidshortf:";

    private const string StartInputId = "start";
    private const string EndInputId   = "end";
    private const string TitleInputId = "title";

    // ─── Custom ids ──────────────────────────────────────────────────

    /// <summary><c>{prefix}{channelId}:{messageId}:{index}:{z|c|v}</c> — the framing buttons and the form live outside the reply, so they carry where it is.</summary>
    internal static string BuildShortId(string prefix, ulong channelId, ulong messageId, int index, ShortLayout layout) =>
        $"{prefix}{channelId}:{messageId}:{index}:{layout switch { ShortLayout.CenterCrop => "c", ShortLayout.AsIs => "v", _ => "z" }}";

    internal static bool TryParseShortId(
        string customId, string prefix, out ulong channelId, out ulong messageId, out int index, out ShortLayout layout)
    {
        channelId = 0; messageId = 0; index = 0; layout = ShortLayout.FullViewZoom;
        if (!customId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = customId[prefix.Length..].Split(':');
        if (parts.Length != 4
            || !ulong.TryParse(parts[0], out channelId)
            || !ulong.TryParse(parts[1], out messageId)
            || !int.TryParse(parts[2], out index) || index < 0)
            return false;

        switch (parts[3])
        {
            case "z": layout = ShortLayout.FullViewZoom; return true;
            case "c": layout = ShortLayout.CenterCrop;   return true;
            case "v": layout = ShortLayout.AsIs;         return true;
            default:  return false;
        }
    }

    // ─── Short button ────────────────────────────────────────────────

    private async Task HandleShortClickAsync(SocketMessageComponent component, string customId)
    {
        if (!int.TryParse(customId[VideoUploadCard.ShortCustomIdPrefix.Length..], out var index) || index < 0)
        {
            _logger.LogWarning("Malformed Short customId: {CustomId}", customId);
            return;
        }
        if (await RefuseShortAsync(component)) return;

        var message = component.Message;
        var row = await LoadAsync(message.Id, index, VideoSubmissionKind.Short);
        if (row is not { Status: VideoSubmissionStatus.Pending })
        {
            await component.RespondAsync(ShortStatusReply(row, component.User.Id), ephemeral: true);
            return;
        }

        if (row is { Width: { } w, Height: { } h } && h >= w)
        {
            await component.RespondWithModalAsync(await BuildShortFormAsync(message.Channel.Id, message.Id, row, ShortLayout.AsIs));
            return;
        }

        var framing = new ComponentBuilder()
            .WithButton("Full view + zoom", BuildShortId(ShortFramingPrefix, message.Channel.Id, message.Id, index, ShortLayout.FullViewZoom), ButtonStyle.Primary)
            .WithButton("Center crop", BuildShortId(ShortFramingPrefix, message.Channel.Id, message.Id, index, ShortLayout.CenterCrop), ButtonStyle.Secondary)
            .Build();
        await component.RespondAsync("How should the Short be framed?", components: framing, ephemeral: true);
    }

    private async Task HandleShortFramingAsync(SocketMessageComponent component, string customId)
    {
        if (!TryParseShortId(customId, ShortFramingPrefix, out var channelId, out var messageId, out var index, out var layout))
        {
            _logger.LogWarning("Malformed Short framing customId: {CustomId}", customId);
            return;
        }
        if (await RefuseShortAsync(component)) return;

        var row = await LoadAsync(messageId, index, VideoSubmissionKind.Short);
        if (row is not { Status: VideoSubmissionStatus.Pending })
        {
            await component.RespondAsync(ShortStatusReply(row, component.User.Id), ephemeral: true);
            return;
        }

        await component.RespondWithModalAsync(await BuildShortFormAsync(channelId, messageId, row, layout));
    }

    private async Task<Modal> BuildShortFormAsync(ulong channelId, ulong messageId, VideoSubmission row, ShortLayout layout)
    {
        var title = BuildTitle(row, await CountVideosAsync(messageId));
        if (title.Length > 100) title = title[..100];

        return new ModalBuilder()
            .WithTitle("YouTube Short")
            .WithCustomId(BuildShortId(ShortFormPrefix, channelId, messageId, row.AttachmentIndex, layout))
            .AddTextInput(label: "Start", customId: StartInputId, style: TextInputStyle.Short,
                placeholder: "0:00", minLength: 1, maxLength: 8, required: true, value: "0:00")
            .AddTextInput(label: "End", customId: EndInputId, style: TextInputStyle.Short,
                placeholder: "empty = end of clip (max 3:00)", minLength: 0, maxLength: 8, required: false)
            .AddTextInput(label: "Title", customId: TitleInputId, style: TextInputStyle.Short,
                minLength: 1, maxLength: 100, required: true, value: title)
            .Build();
    }

    // ─── Form submit ─────────────────────────────────────────────────

    private async Task HandleShortFormAsync(SocketModal modal, string customId)
    {
        if (!TryParseShortId(customId, ShortFormPrefix, out var channelId, out var messageId, out var index, out var layout))
        {
            _logger.LogWarning("Malformed Short form customId: {CustomId}", customId);
            return;
        }
        if (await RefuseShortAsync(modal)) return;
        var user = (SocketGuildUser)modal.User;

        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? string.Empty);
        if (!ShortRequest.TryParse(
                fields.GetValueOrDefault(StartInputId, string.Empty), fields.GetValueOrDefault(EndInputId),
                fields.GetValueOrDefault(TitleInputId, string.Empty), layout, out var settings, out var error))
        {
            await modal.RespondAsync($"{error} The Short button is still live, so you can try again.", ephemeral: true);
            return;
        }

        await modal.DeferAsync(ephemeral: true);

        bool claimed;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            claimed = await VideoSubmissionStore.TryClaimAsync(
                db, messageId, index, VideoSubmissionKind.Short, user.Id, user.DisplayName, DateTime.UtcNow, settings);
        }

        var reply = await FetchMessageAsync(channelId, messageId) as IUserMessage;
        if (!claimed)
        {
            var existing = await LoadAsync(messageId, index, VideoSubmissionKind.Short);
            if (reply is not null) await RefreshButtonsAsync(reply);
            await modal.FollowupAsync(ShortStatusReply(existing, user.Id), ephemeral: true);
            return;
        }

        if (reply is not null) await RefreshButtonsAsync(reply);
        await modal.FollowupAsync("Making your Short… the button will change to a link when it's on YouTube.", ephemeral: true);

        var tempDir = Path.Combine(Path.GetTempPath(), "clanguard-video", $"short-{messageId}-{index}");
        Directory.CreateDirectory(tempDir);
        VideoSubmission? submission = null;
        try
        {
            submission = await LoadAsync(messageId, index, VideoSubmissionKind.Short)
                ?? throw new InvalidOperationException("Short row vanished after claim.");

            var attachment = await ResolveSourceAttachmentAsync(reply, submission);
            var path = Path.Combine(tempDir, "video" + Path.GetExtension(attachment.Filename));
            await DownloadAsync(attachment.Url, path);

            var shortPath = await _shorts.RenderAsync(path, tempDir, settings);

            var guildName = _client?.GetGuild(submission.GuildId)?.Name ?? "clan";
            var youtubeId = await _youtube.UploadAsync(
                shortPath, settings.Title, BuildDescription(submission, guildName) + "\n\n#Shorts");

            await MarkUploadedAsync(submission.Id, youtubeId);
            if (reply is not null) await RefreshButtonsAsync(reply);
            await TryFollowupAsync(modal, $"✅ Short uploaded: https://youtube.com/shorts/{youtubeId}");
        }
        catch (Exception ex)
        {
            var tooLate = ex is ShortTimeOutOfRangeException;
            var originalDeleted = ex is OriginalDeletedException
                || (!tooLate && submission is { OriginalChannelId: { } originalChannelId }
                    && await OriginalConfirmedGoneAsync(originalChannelId, submission.OriginalMessageId));

            if (originalDeleted || tooLate)
                _logger.LogInformation("Short for message {MessageId} attachment {Index} stopped: {Reason}", messageId, index, ex.Message);
            else
                _logger.LogError(ex, "Short failed for message {MessageId} attachment {Index}", messageId, index);

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                await VideoSubmissionStore.ReleaseFailedUploadAsync(
                    db, messageId, index, VideoSubmissionKind.Short, ex.Message, originalDeleted);
            }

            if (reply is not null) await RefreshButtonsAsync(reply);
            await TryFollowupAsync(modal,
                originalDeleted ? (ex as OriginalDeletedException)?.Message ?? OriginalDeletedMessage
                : tooLate ? $"{ex.Message} The Short button is live again."
                : $"⚠️ Making the Short failed: {ex.Message}\nThe button is live again if you want to retry.");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>Permission, Shorts switch and YouTube setup; responds privately and returns true when refused.</summary>
    private async Task<bool> RefuseShortAsync(SocketInteraction interaction)
    {
        var refusal =
            interaction.User is not SocketGuildUser user || !CanUpload(user) ? NotAllowedMessage()
            : !_config.VideoShortsEnabled ? "Shorts are turned off right now."
            : !_youtube.IsConfigured ? YouTubeNotConfiguredMessage
            : null;
        if (refusal is null) return false;

        await interaction.RespondAsync(refusal, ephemeral: true, allowedMentions: AllowedMentions.None);
        return true;
    }

    private static string ShortStatusReply(VideoSubmission? row, ulong userId) => row?.Status switch
    {
        null => "I have no record of this clip, so a Short can't be made from here.",
        VideoSubmissionStatus.Uploaded => $"Already a Short: https://youtube.com/shorts/{row.YouTubeVideoId}",
        VideoSubmissionStatus.Uploading when row.UploadedByUserId == userId
            => "You're already making this Short — the button will change to a link when it's done.",
        VideoSubmissionStatus.Uploading
            => $"{row.UploadedByName ?? "Someone"} is already making this Short — it only goes up once.",
        VideoSubmissionStatus.OriginalDeleted => OriginalDeletedMessage,
        _ => "That Short can't be made right now.",
    };
}
