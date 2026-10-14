using System.Collections.Concurrent;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Clip posts in the clip channels (<see cref="BotConfig.VideoRepostChannelIds"/>)
/// get an "Upload to YouTube" button in the private upload channel
/// (<see cref="BotConfig.VideoUploadChannelId"/>).
///
/// ── What it does ──
/// When a member posts a message carrying at least one video attachment, the
/// bot leaves the post as it is and forwards it to the upload channel. A
/// native Discord forward re-uploads nothing, so no size limit applies. It
/// then replies to the forward with who posted it, a jump link and one
/// "Upload" button per video. Messages without a video are ignored, and with
/// no upload channel set nothing happens at all.
///
/// Pressing "Upload" (holders of <see cref="BotConfig.VideoUploadRoleId"/>, plus
/// Administrators) downloads that video from the member's post and sends it to
/// the clan's YouTube channel through <see cref="YouTubeUploadService"/>. The
/// button then turns into a "Watch on YouTube" link.
///
/// ── Original deleted ──
/// A forward is a snapshot, so it stays when the member deletes their post.
/// Videos not uploaded yet are marked OriginalDeleted: their buttons are
/// disabled and the reply says so. A deletion missed while the bot was offline
/// is caught by the next click, which finds the post gone.
///
/// ── Older messages ──
/// Before forwarding, the bot reposted clips in the clip channel (deleting the
/// original) or, for posts too big to repost, replied to the original in place.
/// Those rows have no OriginalChannelId. Their buttons keep working, and an
/// in-place reply is still withdrawn when its original is deleted.
///
/// ── Upscaling ──
/// Before the upload, <see cref="VideoUpscaler"/> scales sub-1440p clips up to
/// 1440p so YouTube gives them its better encode. It never blocks an upload:
/// on any skip or failure the original file goes up instead.
///
/// ── Upload state / simultaneous clicks ──
/// One <see cref="VideoSubmission"/> row per video. When several media-team
/// members press Upload at nearly the same moment, each click runs the same
/// Pending → Uploading UPDATE with the Pending state in its WHERE clause.
/// SQLite applies writes one at a time, so exactly one click affects the row
/// and wins; every other click affects 0 rows and is told privately who is
/// already uploading it. No second upload is ever started.
///
/// The button redraw is the other half of the race: each click (and each
/// finished upload) re-reads the rows and edits the message, and two of those
/// edits landing out of order would let a stale "Uploading…" overwrite a newer
/// "Watch on YouTube". Redraws are therefore serialized per message, with the
/// read taken inside the lock, so the last edit always reflects the latest
/// state. The same applies to two videos on one post uploading in parallel.
///
/// An upload dies with the process, so on the first Ready after
/// a start any row still Uploading is put back to Pending and its button
/// re-enabled (a disabled button can't be clicked to recover it).
///
/// ── Threading ──
/// Forwards, downloads and uploads can take seconds to minutes, so both gateway callbacks
/// hand the work to the thread pool and return at once rather than blocking
/// Discord.Net's gateway task. The message is edited through the bot's own
/// REST session, not the interaction token, so an upload that outlives the
/// 15-minute interaction window still updates the button.
/// </summary>
public sealed partial class VideoRepostHandler
{
    // Fallback for attachments Discord didn't give a content type.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".mkv", ".m4v", ".avi",
    };

    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _httpFactory;
    private readonly YouTubeUploadService _youtube;
    private readonly VideoUpscaler _upscaler;
    private readonly ShortRenderer _shorts;
    private readonly ILogger<VideoRepostHandler> _logger;
    private readonly BotConfig _config;

    // Guards against the gateway delivering the same message twice.
    private readonly ConcurrentDictionary<ulong, byte> _inFlight = new();

    // One lock per repost message, serializing button redraws. Entries are a
    // few bytes each and only created for reposts someone clicked, so they
    // are kept for the life of the process rather than evicted.
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _redrawLocks = new();

    private DiscordSocketClient? _client;

    // Ready fires on every reconnect, but a reconnect doesn't kill in-flight
    // uploads; only a process start does. 0 = recovery not yet run.
    private int _recoveredStuckUploads;

    // 0 = the "VideoUploadChannelId not set" warning hasn't been logged yet.
    private int _warnedNoUploadChannel;

    private const string OriginalDeletedMessage = "The original post was deleted, so this clip can't be uploaded.";

    /// <summary>
    /// The member's original post (or this clip in it) is gone, so there is nothing
    /// left to upload. The message is what the clicker is told.
    /// </summary>
    private sealed class OriginalDeletedException(string message = OriginalDeletedMessage) : Exception(message);

    private const string YouTubeNotConfiguredMessage =
        "YouTube uploading isn't set up yet — the bot is missing its YouTube credentials.";

    private string NotAllowedMessage()
    {
        var who = _config.VideoUploadRoleId != 0 ? $"members with the <@&{_config.VideoUploadRoleId}> role" : "Administrators";
        return $"Only {who} can upload videos to YouTube.";
    }

    public VideoRepostHandler(
        IServiceProvider services,
        IHttpClientFactory httpFactory,
        YouTubeUploadService youtube,
        VideoUpscaler upscaler,
        ShortRenderer shorts,
        ILogger<VideoRepostHandler> logger,
        IOptions<BotConfig> config)
    {
        _services    = services;
        _httpFactory = httpFactory;
        _youtube     = youtube;
        _upscaler    = upscaler;
        _shorts      = shorts;
        _logger      = logger;
        _config      = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        _client = client;
        client.MessageReceived += OnMessageReceived;
        client.ButtonExecuted  += OnButtonExecuted;
        client.ModalSubmitted  += OnModalSubmitted;
        client.MessageDeleted  += OnMessageDeleted;
        client.MessagesBulkDeleted += OnMessagesBulkDeleted;
        client.Ready           += OnReady;
    }

    private Task OnReady()
    {
        if (Interlocked.Exchange(ref _recoveredStuckUploads, 1) == 1) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverStuckUploadsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover video uploads interrupted by a restart");
            }
        });
        return Task.CompletedTask;
    }

    private async Task RecoverStuckUploadsAsync()
    {
        List<VideoSubmission> stuck;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            stuck = await db.VideoSubmissions.AsNoTracking()
                .Where(v => v.Status == VideoSubmissionStatus.Uploading)
                .ToListAsync();
            if (stuck.Count == 0) return;

            await db.VideoSubmissions
                .Where(v => v.Status == VideoSubmissionStatus.Uploading)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Status,    VideoSubmissionStatus.Pending)
                    .SetProperty(v => v.LastError, "Bot restarted during the upload."));
        }

        _logger.LogWarning("Reset {Count} video upload(s) interrupted by a restart", stuck.Count);

        foreach (var group in stuck.GroupBy(v => (v.ChannelId, v.MessageId)))
        {
            try
            {
                if (_client?.GetChannel(group.Key.ChannelId) is not ITextChannel channel) continue;
                if (await channel.GetMessageAsync(group.Key.MessageId) is IUserMessage message)
                    await RefreshButtonsAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-enable upload button on repost {MessageId}", group.Key.MessageId);
            }
        }
    }

    // ─── Forward ─────────────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!_config.VideoRepostEnabled) return Task.CompletedTask;
        if (message is not SocketUserMessage msg) return Task.CompletedTask;
        if (msg.Author.IsBot || msg.Author.IsWebhook) return Task.CompletedTask;
        if (msg.Channel is not SocketTextChannel channel) return Task.CompletedTask;
        if (!_config.VideoRepostChannelIds.Contains(channel.Id)) return Task.CompletedTask;
        if (!msg.Attachments.Any(IsVideo)) return Task.CompletedTask;

        if (_config.VideoUploadChannelId == 0)
        {
            if (Interlocked.Exchange(ref _warnedNoUploadChannel, 1) == 0)
                _logger.LogWarning(
                    "VideoUploadChannelId is not set, so clip posts are left alone and get no Upload button. " +
                    "Set BotConfig__VideoUploadChannelId to the upload channel's ID.");
            return Task.CompletedTask;
        }

        if (!_inFlight.TryAdd(msg.Id, 0)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await ForwardAsync(msg, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Forwarding video post {MessageId} from #{Channel} to the upload channel failed",
                    msg.Id, channel.Name);
            }
            finally
            {
                _inFlight.TryRemove(msg.Id, out _);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forwards the member's post to the upload channel, replies to the forward
    /// with the Upload button(s) and saves one row per video. The member's post is
    /// never touched; if a later step fails, the bot's own messages are removed.
    /// </summary>
    private async Task ForwardAsync(SocketUserMessage msg, SocketTextChannel channel)
    {
        if (_client?.GetChannel(_config.VideoUploadChannelId) is not ITextChannel uploadChannel)
        {
            _logger.LogError(
                "Upload channel {UploadChannelId} not found or not a text channel; video post {MessageId} in #{Channel} not forwarded",
                _config.VideoUploadChannelId, msg.Id, channel.Name);
            return;
        }

        // The gateway can deliver a message twice; the in-flight guard only
        // covers overlapping deliveries.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            if (await db.VideoSubmissions.AnyAsync(v => v.OriginalMessageId == msg.Id)) return;
        }

        var attachments = msg.Attachments.ToList();
        var posterName  = (msg.Author as SocketGuildUser)?.DisplayName ?? msg.Author.GlobalName ?? msg.Author.Username;
        var now         = DateTime.UtcNow;
        var rows = Enumerable.Range(0, attachments.Count)
            .Where(i => IsVideo(attachments[i]))
            .SelectMany(i => (_config.VideoShortsEnabled
                    ? new[] { VideoSubmissionKind.Video, VideoSubmissionKind.Short }
                    : new[] { VideoSubmissionKind.Video })
                .Select(kind => new VideoSubmission
                {
                    GuildId           = channel.Guild.Id,
                    ChannelId         = uploadChannel.Id,
                    AttachmentIndex   = i,
                    Kind              = kind,
                    KeptOriginal      = true,
                    FileName          = attachments[i].Filename,
                    OriginalChannelId = channel.Id,
                    OriginalMessageId = msg.Id,
                    PosterUserId      = msg.Author.Id,
                    PosterName        = posterName,
                    OriginalContent   = msg.CleanContent ?? string.Empty,
                    PostedUtc         = now,
                    Width             = attachments[i].Width,
                    Height            = attachments[i].Height,
                }))
            .ToList();

        var forward = await uploadChannel.SendMessageAsync(
            messageReference: new MessageReference(msg.Id, channel.Id, channel.Guild.Id, true, MessageReferenceType.Forward));

        IUserMessage reply;
        try
        {
            reply = await uploadChannel.SendMessageAsync(
                VideoUploadCard.BuildText(rows[0], originalDeleted: false),
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(forward.Id),
                components: VideoUploadCard.BuildComponents(rows));
        }
        catch
        {
            await TryDeleteAsync(forward);
            throw;
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            foreach (var row in rows) row.MessageId = reply.Id;
            db.VideoSubmissions.AddRange(rows);
            await db.SaveChangesAsync();
        }
        catch
        {
            await TryDeleteAsync(reply);
            await TryDeleteAsync(forward);
            throw;
        }

        // The member may have deleted the post between the forward and the save,
        // when the deletion handler still found no rows to mark. Check once now.
        if (await OriginalConfirmedGoneAsync(channel.Id, msg.Id))
            await MarkOriginalDeletedAsync(msg.Id);

        _logger.LogInformation(
            "Forwarded {Count} video(s) from {User} in #{Channel} to #{UploadChannel} (original {Original} → reply {Reply})",
            rows.Count(r => r.Kind == VideoSubmissionKind.Video), posterName, channel.Name, uploadChannel.Name, msg.Id, reply.Id);
    }

    /// <summary>
    /// The member's post is gone: marks its forwarded videos that weren't uploaded
    /// yet as OriginalDeleted and redraws their reply.
    /// </summary>
    private async Task MarkOriginalDeletedAsync(ulong originalMessageId)
    {
        IReadOnlyList<(ulong ChannelId, ulong MessageId)> replies;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            replies = await VideoSubmissionStore.MarkOriginalDeletedAsync(db, originalMessageId);
        }

        foreach (var (channelId, messageId) in replies)
        {
            if (await FetchMessageAsync(channelId, messageId) is IUserMessage reply)
                await RefreshButtonsAsync(reply);
        }
    }

    /// <summary>True only when Discord confirms the message is gone; a failed check counts as still there.</summary>
    private async Task<bool> OriginalConfirmedGoneAsync(ulong channelId, ulong messageId)
    {
        try
        {
            return await FetchMessageAsync(channelId, messageId) is null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check whether original {MessageId} still exists", messageId);
            return false;
        }
    }

    private async Task TryDeleteAsync(IMessage message)
    {
        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete bot message {MessageId}", message.Id);
        }
    }

    private Task OnMessageDeleted(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        if (!_config.VideoRepostChannelIds.Contains(channel.Id)) return Task.CompletedTask;

        _ = Task.Run(() => HandleOriginalDeletedAsync(message.Id));
        return Task.CompletedTask;
    }

    /// <summary>Bulk deletes (e.g. /purge-user) don't fire MessageDeleted.</summary>
    private Task OnMessagesBulkDeleted(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages, Cacheable<IMessageChannel, ulong> channel)
    {
        if (!_config.VideoRepostChannelIds.Contains(channel.Id)) return Task.CompletedTask;

        var ids = messages.Select(m => m.Id).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var id in ids) await HandleOriginalDeletedAsync(id);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// A member's post in a clip channel was deleted. Its forwarded videos that
    /// weren't uploaded are marked "Original deleted" (the forward stays). An
    /// in-place reply from before forwarding is removed as before, unless a video
    /// from it already went to YouTube or is going now: then the reply holds the
    /// only link to it.
    /// </summary>
    private async Task HandleOriginalDeletedAsync(ulong messageId)
    {
        try
        {
            List<VideoSubmission> rows;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                rows = await db.VideoSubmissions.AsNoTracking()
                    .Where(v => v.OriginalMessageId == messageId && v.KeptOriginal)
                    .ToListAsync();
            }
            if (rows.Count == 0) return;

            if (rows.Any(v => v.OriginalChannelId is not null))
            {
                await MarkOriginalDeletedAsync(messageId);
                _logger.LogInformation("Original {Original} of a forwarded clip was deleted; marked its Upload button(s)", messageId);
                return;
            }

            if (rows.Any(v => v.Status != VideoSubmissionStatus.Pending)) return;

            var replyId = rows[0].MessageId;
            if (await FetchMessageAsync(rows[0].ChannelId, replyId) is IUserMessage reply)
                await RollBackRepostAsync(reply);
            else
                await DeleteRowsAsync(replyId);

            _logger.LogInformation(
                "Removed in-place Upload button {Reply} after its original {Original} was deleted", replyId, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not handle the deletion of clip post {MessageId}", messageId);
        }
    }

    private async Task RollBackRepostAsync(IUserMessage repost)
    {
        await TryDeleteAsync(repost);
        await DeleteRowsAsync(repost.Id);
    }

    private async Task DeleteRowsAsync(ulong buttonMessageId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            await db.VideoSubmissions.Where(v => v.MessageId == buttonMessageId).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear VideoSubmission rows for withdrawn message {MessageId}", buttonMessageId);
        }
    }

    // ─── Upload button ───────────────────────────────────────────────

    private Task OnButtonExecuted(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId ?? string.Empty;
        Func<Task> handle;
        if (customId.StartsWith(VideoUploadCard.CustomIdPrefix, StringComparison.Ordinal))
            handle = () => HandleUploadClickAsync(component, customId);
        else if (customId.StartsWith(VideoUploadCard.ShortCustomIdPrefix, StringComparison.Ordinal))
            handle = () => HandleShortClickAsync(component, customId);
        else if (customId.StartsWith(ShortFramingPrefix, StringComparison.Ordinal))
            handle = () => HandleShortFramingAsync(component, customId);
        else
            return Task.CompletedTask;

        _ = Task.Run(() => RunInteractionAsync(component, customId, handle));
        return Task.CompletedTask;
    }

    private Task OnModalSubmitted(SocketModal modal)
    {
        var customId = modal.Data.CustomId ?? string.Empty;
        if (!customId.StartsWith(ShortFormPrefix, StringComparison.Ordinal)) return Task.CompletedTask;

        _ = Task.Run(() => RunInteractionAsync(modal, customId, () => HandleShortFormAsync(modal, customId)));
        return Task.CompletedTask;
    }

    /// <summary>Runs one button/form handler; on an unexpected error, tells the clicker privately.</summary>
    private async Task RunInteractionAsync(SocketInteraction interaction, string customId, Func<Task> handle)
    {
        try
        {
            await handle();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling video button or form (customId={CustomId})", customId);
            try
            {
                if (interaction.HasResponded)
                    await interaction.FollowupAsync("Something went wrong handling that click.", ephemeral: true);
                else
                    await interaction.RespondAsync("Something went wrong handling that click.", ephemeral: true);
            }
            catch { /* interaction expired */ }
        }
    }

    private async Task HandleUploadClickAsync(SocketMessageComponent component, string customId)
    {
        if (!int.TryParse(customId[VideoUploadCard.CustomIdPrefix.Length..], out var index) || index < 0)
        {
            _logger.LogWarning("Malformed video upload customId: {CustomId}", customId);
            return;
        }

        if (component.User is not SocketGuildUser user || !CanUpload(user))
        {
            await component.RespondAsync(NotAllowedMessage(), ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (!_youtube.IsConfigured)
        {
            await component.RespondAsync(YouTubeNotConfiguredMessage, ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);

        var message    = component.Message;
        var messageId  = message.Id;
        var uploaderName = user.DisplayName;
        var now        = DateTime.UtcNow;

        bool claimed;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            claimed = await VideoSubmissionStore.TryClaimAsync(db, messageId, index, VideoSubmissionKind.Video, user.Id, uploaderName, now);
        }

        if (!claimed)
        {
            var existing = await LoadAsync(messageId, index, VideoSubmissionKind.Video);
            await RefreshButtonsAsync(message);
            var reply = existing?.Status switch
            {
                null => "I have no record of this video, so it can't be uploaded from here.",
                VideoSubmissionStatus.Uploaded => $"Already on YouTube: https://youtu.be/{existing.YouTubeVideoId}",
                VideoSubmissionStatus.Uploading when existing.UploadedByUserId == user.Id
                    => "You're already uploading this one — the button will change to a link when it's done.",
                VideoSubmissionStatus.Uploading
                    => $"{existing.UploadedByName ?? "Someone"} already started uploading this one — it only goes up once.",
                VideoSubmissionStatus.OriginalDeleted => OriginalDeletedMessage,
                _ => "That video can't be uploaded right now.",
            };
            await component.FollowupAsync(reply, ephemeral: true);
            return;
        }

        await RefreshButtonsAsync(message);
        await component.FollowupAsync("Working on it… the button will change to a link when it's on YouTube.", ephemeral: true);

        var tempDir = Path.Combine(Path.GetTempPath(), "clanguard-video", $"up-{messageId}-{index}");
        Directory.CreateDirectory(tempDir);
        VideoSubmission? submission = null;
        try
        {
            submission = await LoadAsync(messageId, index, VideoSubmissionKind.Video)
                ?? throw new InvalidOperationException("Submission row vanished after claim.");

            var attachment = await ResolveSourceAttachmentAsync(message, submission);

            var path = Path.Combine(tempDir, "video" + Path.GetExtension(attachment.Filename));
            await DownloadAsync(attachment.Url, path);

            var upscaled = await _upscaler.TryUpscaleAsync(
                path, tempDir,
                onEncodeStarting: () => TryFollowupAsync(component,
                    $"Making a {_config.VideoUploadUpscaleTargetSize}p version first so YouTube gives it a better encode. This can take a few minutes."));

            var videoCount = await CountVideosAsync(messageId);
            var guildName  = (message.Channel as SocketGuildChannel)?.Guild.Name ?? "clan";
            var youtubeId  = await _youtube.UploadAsync(
                upscaled ?? path,
                BuildTitle(submission, videoCount),
                BuildDescription(submission, guildName));

            await MarkUploadedAsync(submission.Id, youtubeId);

            await RefreshButtonsAsync(message);
            await TryFollowupAsync(component, $"✅ Uploaded: https://youtu.be/{youtubeId}");
        }
        catch (Exception ex)
        {
            // A forwarded clip's original may have been deleted while it uploaded;
            // then there's nothing to retry from.
            var originalDeleted = ex is OriginalDeletedException
                || (submission is { OriginalChannelId: { } originalChannelId }
                    && await OriginalConfirmedGoneAsync(originalChannelId, submission.OriginalMessageId));

            if (originalDeleted)
                _logger.LogInformation(
                    "Upload of message {MessageId} attachment {Index} stopped: the original post was deleted", messageId, index);
            else
                _logger.LogError(ex, "YouTube upload failed for message {MessageId} attachment {Index}", messageId, index);

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                await VideoSubmissionStore.ReleaseFailedUploadAsync(db, messageId, index, VideoSubmissionKind.Video, ex.Message, originalDeleted);
            }

            await RefreshButtonsAsync(message);
            await TryFollowupAsync(component, originalDeleted
                ? (ex as OriginalDeletedException)?.Message ?? OriginalDeletedMessage
                : $"⚠️ The YouTube upload failed: {ex.Message}\nThe button is live again if you want to retry.");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// The attachment to upload, with a freshly signed CDN URL. For an older repost
    /// it's on component.Message, which arrives with the click. Otherwise it's on
    /// the member's message, fetched over REST rather than from the gateway cache:
    /// a cached copy's URLs can be past their ~24h expiry. A deleted original
    /// throws <see cref="OriginalDeletedException"/>.
    /// </summary>
    private async Task<IAttachment> ResolveSourceAttachmentAsync(IUserMessage? buttonMessage, VideoSubmission submission)
    {
        if (!submission.KeptOriginal)
        {
            return buttonMessage?.Attachments.ElementAtOrDefault(submission.AttachmentIndex)
                ?? throw new InvalidOperationException($"The repost no longer has attachment #{submission.AttachmentIndex}.");
        }

        var original = await FetchMessageAsync(submission.OriginalChannelId ?? submission.ChannelId, submission.OriginalMessageId)
            ?? throw new OriginalDeletedException();

        var names = original.Attachments.Select(a => a.Filename).ToList();
        if (ResolveAttachmentIndex(names, submission.AttachmentIndex, submission.FileName) is { } index)
            return original.Attachments.ElementAt(index);

        if (!names.Contains(submission.FileName))
            throw new OriginalDeletedException("This clip was removed from the original post, so it can't be uploaded.");
        throw new InvalidOperationException(
            "The original post was edited and this clip can't be told apart from another one with the same name.");
    }

    /// <summary>
    /// Where the stored video sits in the member's post now. The member can edit
    /// the post and remove attachments, which shifts the positions, so the stored
    /// file name has to match: by position when it still does, otherwise the one
    /// attachment with that name. Null when the clip is gone or can't be told
    /// apart from a same-named one, so a different clip is never uploaded.
    /// </summary>
    internal static int? ResolveAttachmentIndex(IReadOnlyList<string> fileNames, int storedIndex, string storedFileName)
    {
        if (storedIndex < fileNames.Count && fileNames[storedIndex] == storedFileName) return storedIndex;

        var matches = Enumerable.Range(0, fileNames.Count).Where(i => fileNames[i] == storedFileName).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>REST fetch (bypassing the gateway cache); null if the message is gone.</summary>
    private async Task<IMessage?> FetchMessageAsync(ulong channelId, ulong messageId)
    {
        if (_client is null) return null;
        if (await _client.Rest.GetChannelAsync(channelId) is not IMessageChannel channel) return null;
        try
        {
            return await channel.GetMessageAsync(messageId);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private bool CanUpload(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.VideoUploadRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.VideoUploadRoleId);
    }

    private static string BuildTitle(VideoSubmission s, int videoCount)
    {
        var firstLine = s.OriginalContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var title = string.IsNullOrEmpty(firstLine)
            ? $"{s.PosterName} — {s.PostedUtc:MMM d, yyyy}"
            : firstLine;
        return videoCount > 1 ? $"{title} (part {s.AttachmentIndex + 1})" : title;
    }

    private static string BuildDescription(VideoSubmission s, string guildName)
    {
        var credit = $"Clip by {s.PosterName}, shared in the {guildName} Discord on {s.PostedUtc:MMMM d, yyyy}.";
        var text = s.OriginalContent.Trim();
        return text.Length == 0 ? credit : $"{text}\n\n{credit}";
    }

    // ─── Shared helpers ──────────────────────────────────────────────

    private static bool IsVideo(IAttachment a) =>
        (a.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
        || VideoExtensions.Contains(Path.GetExtension(a.Filename));

    private async Task RefreshButtonsAsync(IUserMessage message)
    {
        var gate = _redrawLocks.GetOrAdd(message.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Read INSIDE the lock: a redraw that read before another click's
            // redraw finished could otherwise publish the older state last.
            List<VideoSubmission> rows;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                rows = await db.VideoSubmissions.AsNoTracking()
                    .Where(v => v.MessageId == message.Id)
                    .ToListAsync();
            }
            if (rows.Count == 0) return;

            // A forwarded clip's reply text carries the "original deleted" line,
            // so it's rebuilt along with the buttons. Older messages: buttons only.
            var forwarded = rows[0].OriginalChannelId is not null;
            var deleted   = rows.Any(r => r.Status == VideoSubmissionStatus.OriginalDeleted);
            await message.ModifyAsync(p =>
            {
                p.Components = VideoUploadCard.BuildComponents(rows);
                if (forwarded)
                {
                    p.Content         = VideoUploadCard.BuildText(rows[0], deleted);
                    p.AllowedMentions = AllowedMentions.None;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh upload buttons on message {MessageId}", message.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VideoSubmission?> LoadAsync(ulong messageId, int index, VideoSubmissionKind kind)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.VideoSubmissions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.MessageId == messageId && v.AttachmentIndex == index && v.Kind == kind);
    }

    private async Task MarkUploadedAsync(int rowId, string youtubeId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        await db.VideoSubmissions
            .Where(v => v.Id == rowId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,         VideoSubmissionStatus.Uploaded)
                .SetProperty(v => v.YouTubeVideoId, youtubeId)
                .SetProperty(v => v.UploadedUtc,    (DateTime?)DateTime.UtcNow)
                .SetProperty(v => v.LastError,      (string?)null));
    }

    private async Task<int> CountVideosAsync(ulong messageId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await VideoSubmissionStore.CountVideosAsync(db, messageId);
    }

    private async Task DownloadAsync(string url, string path)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
    }

    /// <summary>A followup after the 15-minute interaction window fails; the button edit already told everyone.</summary>
    private async Task TryFollowupAsync(SocketInteraction interaction, string text)
    {
        try
        {
            await interaction.FollowupAsync(text, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upload followup not delivered (interaction likely expired)");
        }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete temp directory {Dir}", dir);
        }
    }
}
