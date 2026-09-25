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
/// Video repost + "Upload to YouTube" button for the clip channels
/// (<see cref="BotConfig.VideoRepostChannelIds"/>).
///
/// ── What it does ──
/// When a member posts a message carrying at least one video attachment, the
/// bot reposts every attachment with the message text quoted under a
/// "Posted by @member" line, adds one "Upload" button per video, and deletes
/// the original. Messages without a video (plain chat, image-only posts) are
/// left alone, since members talk in these channels too.
///
/// Pressing "Upload" (holders of <see cref="BotConfig.VideoUploadRoleId"/>, plus
/// Administrators) re-downloads that video from the repost and sends it to the
/// clan's YouTube channel through <see cref="YouTubeUploadService"/>. The button
/// then turns into a "Watch on YouTube" link.
///
/// ── Order: repost, THEN delete ──
/// The repost is sent before the original is deleted, so a repost that fails
/// (file over the guild's upload limit, missing Attach Files) never loses the
/// member's video. If the original can't be deleted afterwards, the repost is
/// rolled back instead. That includes a 404: the moderation handlers
/// (invite filter, token-grabber scanner, honeypot) may have removed the
/// original while it was downloading, and the bot must not resurrect it.
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
/// Downloads and uploads take seconds to minutes, so both gateway callbacks
/// hand the work to the thread pool and return at once rather than blocking
/// Discord.Net's gateway task. The message is edited through the bot's own
/// REST session, not the interaction token, so an upload that outlives the
/// 15-minute interaction window still updates the button.
/// </summary>
public sealed class VideoRepostHandler
{
    public const string CustomIdPrefix = "vidup:";

    // Fallback for attachments Discord didn't give a content type.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".mkv", ".m4v", ".avi",
    };

    private const int DiscordMessageLimit = 2000;

    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _httpFactory;
    private readonly YouTubeUploadService _youtube;
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

    public VideoRepostHandler(
        IServiceProvider services,
        IHttpClientFactory httpFactory,
        YouTubeUploadService youtube,
        ILogger<VideoRepostHandler> logger,
        IOptions<BotConfig> config)
    {
        _services    = services;
        _httpFactory = httpFactory;
        _youtube     = youtube;
        _logger      = logger;
        _config      = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        _client = client;
        client.MessageReceived += OnMessageReceived;
        client.ButtonExecuted  += OnButtonExecuted;
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

    // ─── Repost ──────────────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!_config.VideoRepostEnabled) return Task.CompletedTask;
        if (message is not SocketUserMessage msg) return Task.CompletedTask;
        if (msg.Author.IsBot || msg.Author.IsWebhook) return Task.CompletedTask;
        if (msg.Channel is not SocketTextChannel channel) return Task.CompletedTask;
        if (!_config.VideoRepostChannelIds.Contains(channel.Id)) return Task.CompletedTask;
        if (!msg.Attachments.Any(IsVideo)) return Task.CompletedTask;
        if (!_inFlight.TryAdd(msg.Id, 0)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await RepostAsync(msg, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video repost failed for message {MessageId} in #{Channel}", msg.Id, channel.Name);
            }
            finally
            {
                _inFlight.TryRemove(msg.Id, out _);
            }
        });
        return Task.CompletedTask;
    }

    private async Task RepostAsync(SocketUserMessage msg, SocketTextChannel channel)
    {
        var attachments = msg.Attachments.ToList();

        var totalBytes = attachments.Sum(a => (long)a.Size);
        if ((ulong)totalBytes > channel.Guild.MaxUploadLimit)
        {
            _logger.LogWarning(
                "Video post {MessageId} in #{Channel} is {Size:N0} bytes, over the guild's {Limit:N0}-byte bot upload limit; leaving it in place",
                msg.Id, channel.Name, totalBytes, channel.Guild.MaxUploadLimit);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "clanguard-video", msg.Id.ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Every attachment is carried over, not just the videos, so a
            // screenshot posted alongside a clip isn't lost with the original.
            var paths = new List<string>(attachments.Count);
            for (var i = 0; i < attachments.Count; i++)
            {
                var path = Path.Combine(tempDir, i.ToString());
                await DownloadAsync(attachments[i].Url, path);
                paths.Add(path);
            }

            var poster     = msg.Author as SocketGuildUser;
            var posterName = poster?.DisplayName ?? msg.Author.GlobalName ?? msg.Author.Username;
            var videoIndexes = Enumerable.Range(0, attachments.Count).Where(i => IsVideo(attachments[i])).ToList();

            var pending = videoIndexes
                .Select(i => new VideoSubmission { AttachmentIndex = i, Status = VideoSubmissionStatus.Pending })
                .ToList();

            // Keep a reply a reply.
            MessageReference? reference = null;
            if (msg.Type == MessageType.Reply && msg.Reference?.MessageId.IsSpecified == true)
                reference = new MessageReference(msg.Reference.MessageId.Value, failIfNotExists: false);

            IUserMessage repost;
            var files = paths
                .Select((p, i) => new FileAttachment(
                    File.OpenRead(p), attachments[i].Filename, attachments[i].Description, attachments[i].IsSpoiler()))
                .ToList();
            try
            {
                repost = await channel.SendFilesAsync(
                    files,
                    text: BuildRepostText(msg.Author.Id, msg.Content),
                    allowedMentions: AllowedMentions.None,
                    messageReference: reference,
                    components: BuildComponents(pending));
            }
            finally
            {
                foreach (var f in files) f.Dispose();
            }

            try
            {
                var now = DateTime.UtcNow;
                var repostAttachments = repost.Attachments.ToList();
                using (var scope = _services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                    foreach (var i in videoIndexes)
                    {
                        db.VideoSubmissions.Add(new VideoSubmission
                        {
                            GuildId           = channel.Guild.Id,
                            ChannelId         = channel.Id,
                            MessageId         = repost.Id,
                            AttachmentIndex   = i,
                            // Discord may rewrite the filename on upload; record what it kept.
                            FileName          = repostAttachments.ElementAtOrDefault(i)?.Filename ?? attachments[i].Filename,
                            OriginalMessageId = msg.Id,
                            PosterUserId      = msg.Author.Id,
                            PosterName        = posterName,
                            OriginalContent   = msg.CleanContent ?? string.Empty,
                            PostedUtc         = now,
                        });
                    }
                    await db.SaveChangesAsync();
                }

                await msg.DeleteAsync();
            }
            catch (Exception ex)
            {
                await RollBackRepostAsync(repost);

                if (ex is HttpException { HttpCode: System.Net.HttpStatusCode.NotFound })
                {
                    _logger.LogInformation(
                        "Video post {MessageId} in #{Channel} was deleted by something else mid-repost; repost withdrawn",
                        msg.Id, channel.Name);
                    return;
                }
                throw;
            }

            _logger.LogInformation(
                "Reposted {Count} video(s) from {User} in #{Channel} (original {Original} → repost {Repost})",
                videoIndexes.Count, posterName, channel.Name, msg.Id, repost.Id);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private async Task RollBackRepostAsync(IUserMessage repost)
    {
        try
        {
            await repost.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not withdraw video repost {MessageId}", repost.Id);
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            await db.VideoSubmissions.Where(v => v.MessageId == repost.Id).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear VideoSubmission rows for withdrawn repost {MessageId}", repost.Id);
        }
    }

    private static string BuildRepostText(ulong posterId, string content)
    {
        var header = $"🎬 Posted by <@{posterId}>";
        content = content.Trim();
        if (content.Length == 0) return header;

        const string quote = "\n>>> ";
        var room = DiscordMessageLimit - header.Length - quote.Length;
        if (content.Length > room) content = content[..(room - 1)].TrimEnd() + "…";
        return header + quote + content;
    }

    // ─── Upload button ───────────────────────────────────────────────

    private Task OnButtonExecuted(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (string.IsNullOrEmpty(customId) || !customId.StartsWith(CustomIdPrefix, StringComparison.Ordinal))
            return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleUploadClickAsync(component, customId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling video upload button (customId={CustomId})", customId);
                try
                {
                    if (component.HasResponded)
                        await component.FollowupAsync("Something went wrong handling that click.", ephemeral: true);
                    else
                        await component.RespondAsync("Something went wrong handling that click.", ephemeral: true);
                }
                catch { /* interaction expired */ }
            }
        });
        return Task.CompletedTask;
    }

    private async Task HandleUploadClickAsync(SocketMessageComponent component, string customId)
    {
        if (!int.TryParse(customId[CustomIdPrefix.Length..], out var index) || index < 0)
        {
            _logger.LogWarning("Malformed video upload customId: {CustomId}", customId);
            return;
        }

        if (component.User is not SocketGuildUser user || !CanUpload(user))
        {
            var who = _config.VideoUploadRoleId != 0 ? $"members with the <@&{_config.VideoUploadRoleId}> role" : "Administrators";
            await component.RespondAsync(
                $"Only {who} can upload videos to YouTube.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (!_youtube.IsConfigured)
        {
            await component.RespondAsync(
                "YouTube uploading isn't set up yet — the bot is missing its YouTube credentials.",
                ephemeral: true);
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
            claimed = await TryClaimAsync(db, messageId, index, user.Id, uploaderName, now);
        }

        if (!claimed)
        {
            var existing = await LoadAsync(messageId, index);
            await RefreshButtonsAsync(message);
            var reply = existing?.Status switch
            {
                null => "I have no record of this video, so it can't be uploaded from here.",
                VideoSubmissionStatus.Uploaded => $"Already on YouTube: https://youtu.be/{existing.YouTubeVideoId}",
                VideoSubmissionStatus.Uploading when existing.UploadedByUserId == user.Id
                    => "You're already uploading this one — the button will change to a link when it's done.",
                VideoSubmissionStatus.Uploading
                    => $"{existing.UploadedByName ?? "Someone"} already started uploading this one — it only goes up once.",
                _ => "That video can't be uploaded right now.",
            };
            await component.FollowupAsync(reply, ephemeral: true);
            return;
        }

        await RefreshButtonsAsync(message);
        await component.FollowupAsync("Uploading to YouTube… the button will change to a link when it's done.", ephemeral: true);

        var tempDir = Path.Combine(Path.GetTempPath(), "clanguard-video", $"up-{messageId}-{index}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var submission = await LoadAsync(messageId, index)
                ?? throw new InvalidOperationException("Submission row vanished after claim.");

            // component.Message comes with the click, so its attachment URLs are
            // freshly signed even if the repost is days old.
            var attachment = message.Attachments.ElementAtOrDefault(index)
                ?? throw new InvalidOperationException($"The repost no longer has attachment #{index}.");

            var path = Path.Combine(tempDir, "video" + Path.GetExtension(attachment.Filename));
            await DownloadAsync(attachment.Url, path);

            var videoCount = await CountVideosAsync(messageId);
            var guildName  = (message.Channel as SocketGuildChannel)?.Guild.Name ?? "clan";
            var youtubeId  = await _youtube.UploadAsync(
                path,
                BuildTitle(submission, videoCount),
                BuildDescription(submission, guildName));

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                await db.VideoSubmissions
                    .Where(v => v.Id == submission.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.Status,         VideoSubmissionStatus.Uploaded)
                        .SetProperty(v => v.YouTubeVideoId, youtubeId)
                        .SetProperty(v => v.UploadedUtc,    (DateTime?)DateTime.UtcNow)
                        .SetProperty(v => v.LastError,      (string?)null));
            }

            await RefreshButtonsAsync(message);
            await TryFollowupAsync(component, $"✅ Uploaded: https://youtu.be/{youtubeId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube upload failed for repost {MessageId} attachment {Index}", messageId, index);

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                var error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                await db.VideoSubmissions
                    .Where(v => v.MessageId == messageId && v.AttachmentIndex == index
                             && v.Status == VideoSubmissionStatus.Uploading)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.Status,    VideoSubmissionStatus.Pending)
                        .SetProperty(v => v.LastError, error));
            }

            await RefreshButtonsAsync(message);
            await TryFollowupAsync(component, $"⚠️ The YouTube upload failed: {ex.Message}\nThe button is live again if you want to retry.");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Atomically moves one video from Pending to Uploading for this uploader.
    /// A single UPDATE guarded on Status = Pending: of any number of
    /// simultaneous clicks, exactly one returns true.
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        BotDbContext db, ulong messageId, int index, ulong userId, string userName, DateTime now)
    {
        var rows = await db.VideoSubmissions
            .Where(v => v.MessageId == messageId && v.AttachmentIndex == index
                     && v.Status == VideoSubmissionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,           VideoSubmissionStatus.Uploading)
                .SetProperty(v => v.UploadStartedUtc, (DateTime?)now)
                .SetProperty(v => v.UploadedByUserId, (ulong?)userId)
                .SetProperty(v => v.UploadedByName,   userName));
        return rows == 1;
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

    /// <summary>
    /// One button per video, in attachment order. Numbered only when the post
    /// carries more than one video. Up to 5 buttons per row (Discord's cap);
    /// a message carries at most 10 attachments, so two rows at most.
    /// </summary>
    private static MessageComponent BuildComponents(IReadOnlyList<VideoSubmission> videos)
    {
        var builder  = new ComponentBuilder();
        var numbered = videos.Count > 1;
        var ordered  = videos.OrderBy(v => v.AttachmentIndex).ToList();

        for (var n = 0; n < ordered.Count; n++)
        {
            var v      = ordered[n];
            var suffix = numbered ? $" #{n + 1}" : "";
            var row    = n / 5;
            var id     = CustomIdPrefix + v.AttachmentIndex;

            switch (v.Status)
            {
                case VideoSubmissionStatus.Uploaded:
                    builder.WithButton("Watch on YouTube" + suffix, style: ButtonStyle.Link,
                        url: $"https://youtu.be/{v.YouTubeVideoId}", row: row);
                    break;
                case VideoSubmissionStatus.Uploading:
                    builder.WithButton("Uploading…" + suffix, id, ButtonStyle.Secondary,
                        disabled: true, row: row);
                    break;
                default:
                    builder.WithButton("Upload" + suffix, id, ButtonStyle.Primary,
                        emote: new Emoji("📤"), row: row);
                    break;
            }
        }

        return builder.Build();
    }

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

            await message.ModifyAsync(p => p.Components = BuildComponents(rows));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh upload buttons on repost {MessageId}", message.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VideoSubmission?> LoadAsync(ulong messageId, int index)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.VideoSubmissions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.MessageId == messageId && v.AttachmentIndex == index);
    }

    private async Task<int> CountVideosAsync(ulong messageId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.VideoSubmissions.CountAsync(v => v.MessageId == messageId);
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
    private async Task TryFollowupAsync(SocketMessageComponent component, string text)
    {
        try
        {
            await component.FollowupAsync(text, ephemeral: true);
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
