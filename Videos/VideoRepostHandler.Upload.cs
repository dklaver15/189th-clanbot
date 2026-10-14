using System.Diagnostics.CodeAnalysis;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The Upload button: Upload click → a private choice (YouTube video, or a Short
/// and its framing) → a form (start, end, title) → prepare (<see cref="VideoUpscaler"/>)
/// or render (<see cref="ShortRenderer"/>) → upload. Opening a form must be an
/// interaction's first response, so nothing slow happens before it; the clip's
/// size comes from the row (Discord's attachment metadata).
/// </summary>
public sealed partial class VideoRepostHandler
{
    internal const string ChoicePrefix = "vidupk:";
    internal const string FormPrefix   = "vidupf:";

    private const string StartInputId = "start";
    private const string EndInputId   = "end";
    private const string TitleInputId = "title";

    private const string OldMessageReply =
        "This clip was posted before uploads moved to the upload channel, so it can't be uploaded from here.";

    // ─── Custom ids ──────────────────────────────────────────────────

    /// <summary>
    /// <c>{prefix}{channelId}:{messageId}:{index}:{v|z|c|s}</c> — the choice buttons
    /// and the form live outside the reply, so they carry where it is.
    /// v = video, z = Short full view + zoom, c = Short center crop, s = Short as is.
    /// </summary>
    internal static string BuildUploadId(
        string prefix, ulong channelId, ulong messageId, int index, VideoSubmissionKind kind, ShortLayout layout)
    {
        var code = kind == VideoSubmissionKind.Video ? "v"
            : layout switch { ShortLayout.CenterCrop => "c", ShortLayout.AsIs => "s", _ => "z" };
        return $"{prefix}{channelId}:{messageId}:{index}:{code}";
    }

    internal static bool TryParseUploadId(
        string customId, string prefix, out ulong channelId, out ulong messageId, out int index,
        out VideoSubmissionKind kind, out ShortLayout layout)
    {
        channelId = 0; messageId = 0; index = 0; kind = VideoSubmissionKind.Video; layout = ShortLayout.FullViewZoom;
        if (!customId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = customId[prefix.Length..].Split(':');
        if (parts.Length != 4
            || !ulong.TryParse(parts[0], out channelId)
            || !ulong.TryParse(parts[1], out messageId)
            || !int.TryParse(parts[2], out index) || index < 0)
            return false;

        switch (parts[3])
        {
            case "v": kind = VideoSubmissionKind.Video;                                    return true;
            case "z": kind = VideoSubmissionKind.Short; layout = ShortLayout.FullViewZoom; return true;
            case "c": kind = VideoSubmissionKind.Short; layout = ShortLayout.CenterCrop;   return true;
            case "s": kind = VideoSubmissionKind.Short; layout = ShortLayout.AsIs;         return true;
            default:  return false;
        }
    }

    // ─── Upload button ───────────────────────────────────────────────

    private async Task HandleUploadClickAsync(SocketMessageComponent component, string customId)
    {
        if (!int.TryParse(customId[VideoUploadCard.CustomIdPrefix.Length..], out var index) || index < 0)
        {
            _logger.LogWarning("Malformed Upload customId: {CustomId}", customId);
            return;
        }
        if (await RefuseUploadAsync(component, VideoSubmissionKind.Video)) return;

        var message = component.Message;
        var row = await LoadAsync(message.Id, index);
        if (!CanStartUpload(row, component.User.Id, out var refusal))
        {
            await component.RespondAsync(refusal, ephemeral: true);
            return;
        }

        var channelId = message.Channel.Id;
        if (!_config.VideoShortsEnabled)
        {
            await component.RespondWithModalAsync(await BuildUploadFormAsync(
                channelId, message.Id, row, VideoSubmissionKind.Video, ShortLayout.FullViewZoom));
            return;
        }

        var choices = new ComponentBuilder().WithButton("YouTube video",
            BuildUploadId(ChoicePrefix, channelId, message.Id, index, VideoSubmissionKind.Video, ShortLayout.FullViewZoom),
            ButtonStyle.Primary);
        if (row is { Width: { } w, Height: { } h } && h >= w)
        {
            choices.WithButton("Short",
                BuildUploadId(ChoicePrefix, channelId, message.Id, index, VideoSubmissionKind.Short, ShortLayout.AsIs),
                ButtonStyle.Secondary);
        }
        else
        {
            choices.WithButton("Short: full view + zoom",
                    BuildUploadId(ChoicePrefix, channelId, message.Id, index, VideoSubmissionKind.Short, ShortLayout.FullViewZoom),
                    ButtonStyle.Secondary)
                .WithButton("Short: center crop",
                    BuildUploadId(ChoicePrefix, channelId, message.Id, index, VideoSubmissionKind.Short, ShortLayout.CenterCrop),
                    ButtonStyle.Secondary);
        }
        await component.RespondAsync("How should this clip go to YouTube?", components: choices.Build(), ephemeral: true);
    }

    private async Task HandleUploadChoiceAsync(SocketMessageComponent component, string customId)
    {
        if (!TryParseUploadId(customId, ChoicePrefix, out var channelId, out var messageId, out var index, out var kind, out var layout))
        {
            _logger.LogWarning("Malformed upload choice customId: {CustomId}", customId);
            return;
        }
        if (await RefuseUploadAsync(component, kind)) return;

        var row = await LoadAsync(messageId, index);
        if (!CanStartUpload(row, component.User.Id, out var refusal))
        {
            await component.RespondAsync(refusal, ephemeral: true);
            return;
        }

        await component.RespondWithModalAsync(await BuildUploadFormAsync(channelId, messageId, row, kind, layout));
    }

    private async Task<Modal> BuildUploadFormAsync(
        ulong channelId, ulong messageId, VideoSubmission row, VideoSubmissionKind kind, ShortLayout layout)
    {
        var title = BuildTitle(row, await CountVideosAsync(messageId));
        var isShort = kind == VideoSubmissionKind.Short;

        return new ModalBuilder()
            .WithTitle(isShort ? "YouTube Short" : "YouTube video")
            .WithCustomId(BuildUploadId(FormPrefix, channelId, messageId, row.AttachmentIndex, kind, layout))
            .AddTextInput(label: "Start", customId: StartInputId, style: TextInputStyle.Short,
                placeholder: "0:00", minLength: 1, maxLength: 8, required: true, value: "0:00")
            .AddTextInput(label: "End", customId: EndInputId, style: TextInputStyle.Short,
                placeholder: isShort ? "empty = end of clip (max 3:00)" : "empty = end of clip",
                minLength: 0, maxLength: 8, required: false)
            .AddTextInput(label: "Title", customId: TitleInputId, style: TextInputStyle.Short,
                minLength: 1, maxLength: 100, required: true, value: title)
            .Build();
    }

    // ─── Form submit ─────────────────────────────────────────────────

    private async Task HandleUploadFormAsync(SocketModal modal, string customId)
    {
        if (!TryParseUploadId(customId, FormPrefix, out var channelId, out var messageId, out var index, out var kind, out var layout))
        {
            _logger.LogWarning("Malformed upload form customId: {CustomId}", customId);
            return;
        }
        if (await RefuseUploadAsync(modal, kind)) return;
        var user = (SocketGuildUser)modal.User;

        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? string.Empty);
        if (!UploadRequest.TryParse(
                fields.GetValueOrDefault(StartInputId, string.Empty), fields.GetValueOrDefault(EndInputId),
                fields.GetValueOrDefault(TitleInputId, string.Empty), kind, layout, out var settings, out var error))
        {
            await modal.RespondAsync($"{error} The Upload button is still live, so you can try again.", ephemeral: true);
            return;
        }

        await modal.DeferAsync(ephemeral: true);

        // Fetched before the claim: a REST failure here then leaves nothing claimed.
        var reply = await FetchMessageAsync(channelId, messageId) as IUserMessage;

        var existing = await LoadAsync(messageId, index);
        if (existing is { OriginalChannelId: null })
        {
            await modal.FollowupAsync(OldMessageReply, ephemeral: true);
            return;
        }

        bool claimed;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            claimed = await VideoSubmissionStore.TryClaimAsync(
                db, messageId, index, user.Id, user.DisplayName, DateTime.UtcNow, settings);
        }

        var isShort = settings.Kind == VideoSubmissionKind.Short;
        if (!claimed)
        {
            existing = await LoadAsync(messageId, index);
            if (reply is not null) await RefreshButtonsAsync(reply);
            await modal.FollowupAsync(StatusReply(existing, user.Id), ephemeral: true);
            return;
        }

        if (reply is not null) await RefreshButtonsAsync(reply);
        await TryFollowupAsync(modal, isShort
            ? "Making your Short… the button will change to a link when it's on YouTube."
            : "Working on it… the button will change to a link when it's on YouTube.");

        var tempDir = Path.Combine(Path.GetTempPath(), "clanguard-video", $"up-{messageId}-{index}");
        VideoSubmission? submission = null;
        string? youtubeId = null;
        try
        {
            Directory.CreateDirectory(tempDir);
            submission = await LoadAsync(messageId, index)
                ?? throw new InvalidOperationException("Submission row vanished after claim.");

            var attachment = await ResolveSourceAttachmentAsync(submission);
            var path = Path.Combine(tempDir, "video" + Path.GetExtension(attachment.Filename));
            await DownloadAsync(attachment.Url, path);

            var guildName   = _client?.GetGuild(submission.GuildId)?.Name ?? "clan";
            var description = BuildDescription(submission, guildName);
            string fileToUpload;
            if (isShort)
            {
                fileToUpload = await _shorts.RenderAsync(path, tempDir, settings);
                description += "\n\n#Shorts";
            }
            else
            {
                fileToUpload = await _upscaler.PrepareAsync(
                    path, tempDir, settings.StartSeconds, settings.EndSeconds,
                    onEncodeStarting: text => TryFollowupAsync(modal, text));
            }

            youtubeId = await _youtube.UploadAsync(fileToUpload, settings.Title, description);
            await MarkUploadedAsync(submission.Id, youtubeId);
            if (reply is not null) await RefreshButtonsAsync(reply);
            await TryFollowupAsync(modal, isShort
                ? $"✅ Short uploaded: https://youtube.com/shorts/{youtubeId}"
                : $"✅ Uploaded: https://youtu.be/{youtubeId}");
        }
        catch (Exception ex) when (youtubeId is not null)
        {
            // On YouTube already: releasing the row would invite a duplicate upload.
            _logger.LogError(ex,
                "Marking message {MessageId} attachment {Index} uploaded failed after YouTube video {YouTubeId} was created",
                messageId, index, youtubeId);
            if (reply is not null) await RefreshButtonsAsync(reply);
            await TryFollowupAsync(modal, isShort
                ? $"⚠️ Your Short IS on YouTube: https://youtube.com/shorts/{youtubeId}\nI couldn't update the button, so don't upload it again."
                : $"⚠️ Your video IS on YouTube: https://youtu.be/{youtubeId}\nI couldn't update the button, so don't upload it again.");
        }
        catch (Exception ex)
        {
            var rejected = ex is ClipRejectedException;
            var originalDeleted = ex is OriginalDeletedException
                || (!rejected && submission is { OriginalChannelId: { } originalChannelId }
                    && await OriginalConfirmedGoneAsync(originalChannelId, submission.OriginalMessageId));

            if (originalDeleted || rejected)
                _logger.LogInformation("Upload of message {MessageId} attachment {Index} stopped: {Reason}", messageId, index, ex.Message);
            else
                _logger.LogError(ex, "Upload failed for message {MessageId} attachment {Index}", messageId, index);

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                await VideoSubmissionStore.ReleaseFailedUploadAsync(db, messageId, index, ex.Message, originalDeleted);
            }

            if (reply is not null) await RefreshButtonsAsync(reply);
            await TryFollowupAsync(modal,
                originalDeleted ? (ex as OriginalDeletedException)?.Message ?? OriginalDeletedMessage
                : rejected ? $"{ex.Message} The Upload button is live again."
                : $"⚠️ The upload failed: {ex.Message}\nThe button is live again if you want to retry.");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>Permission, Shorts switch and YouTube setup; responds privately and returns true when refused.</summary>
    private async Task<bool> RefuseUploadAsync(SocketInteraction interaction, VideoSubmissionKind kind)
    {
        var refusal =
            interaction.User is not SocketGuildUser user || !CanUpload(user) ? NotAllowedMessage()
            : kind == VideoSubmissionKind.Short && !_config.VideoShortsEnabled ? "Shorts are turned off right now."
            : !_youtube.IsConfigured ? YouTubeNotConfiguredMessage
            : null;
        if (refusal is null) return false;

        await interaction.RespondAsync(refusal, ephemeral: true, allowedMentions: AllowedMentions.None);
        return true;
    }

    /// <summary>
    /// Whether a click can go on to the choice or the form: the clip has a row from
    /// a forward and is still waiting to be uploaded. Otherwise
    /// <paramref name="refusal"/> is the private reply (an old message, or the clip's
    /// status: already up, going up, original deleted).
    /// </summary>
    private static bool CanStartUpload([NotNullWhen(true)] VideoSubmission? row, ulong userId, out string refusal)
    {
        refusal = row is { OriginalChannelId: null } ? OldMessageReply
            : row is not { Status: VideoSubmissionStatus.Pending } ? StatusReply(row, userId)
            : string.Empty;
        return refusal.Length == 0;
    }

    private static string StatusReply(VideoSubmission? row, ulong userId) => row?.Status switch
    {
        null => "I have no record of this clip, so it can't be uploaded from here.",
        VideoSubmissionStatus.Uploaded when row.Kind == VideoSubmissionKind.Short
            => $"Already a Short: https://youtube.com/shorts/{row.YouTubeVideoId}",
        VideoSubmissionStatus.Uploaded => $"Already on YouTube: https://youtu.be/{row.YouTubeVideoId}",
        VideoSubmissionStatus.Uploading when row.UploadedByUserId == userId
            => "You're already uploading this one — the button will change to a link when it's done.",
        VideoSubmissionStatus.Uploading
            => $"{row.UploadedByName ?? "Someone"} is already uploading this one — it only goes up once.",
        VideoSubmissionStatus.OriginalDeleted => OriginalDeletedMessage,
        _ => "That clip can't be uploaded right now.",
    };
}
