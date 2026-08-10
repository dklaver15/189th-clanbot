using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Posts a short notice to HQ when a meeting recording ends in
/// <see cref="MeetingRecordingState.Failed"/>.
///
/// ── Why this exists ────────────────────────────────────────────────────────
/// Every terminal failure in the recording pipeline used to be a log line and
/// nothing else. /health says nothing about meetings and there is no meeting
/// command, so the only symptom anyone saw was "the bot sat in the channel and
/// never posted anything". That blind spot hid a transcriber sidecar that was
/// being OOM-killed on every attempt from 2026-06-28 to 2026-08-10: six weeks
/// and several meetings, none of them noticed until someone went looking.
///
/// Best-effort by design. A failure to announce a failure must never take down
/// the service that is already handling a failure, so everything here is
/// swallowed and logged.
/// </summary>
public static class MeetingFailureNotice
{
    public static async Task PostAsync(
        DiscordSocketClient client,
        BotConfig config,
        ILogger logger,
        MeetingRecording rec,
        string stage,
        CancellationToken ct)
    {
        try
        {
            var channelId = config.MeetingFailureAlertChannelId != 0
                ? config.MeetingFailureAlertChannelId
                : config.HqChannelId;

            if (channelId == 0)
            {
                logger.LogWarning(
                    "Recording '{Title}' (#{Id}) failed at {Stage} but neither " +
                    "MeetingFailureAlertChannelId nor HqChannelId is set, so no notice was posted.",
                    rec.MeetingTitle, rec.Id, stage);
                return;
            }

            if (client.GetChannel(channelId) is not IMessageChannel channel)
            {
                logger.LogWarning(
                    "Could not resolve meeting failure channel {ChannelId}; " +
                    "recording '{Title}' (#{Id}) failed at {Stage} with no notice posted.",
                    channelId, rec.MeetingTitle, rec.Id, stage);
                return;
            }

            var recorded = rec.RecordingStartedUtc is { } started && rec.RecordingStoppedUtc is { } stopped
                ? $"{(stopped - started).TotalMinutes:0} min on {started:yyyy-MM-dd HH:mm} UTC"
                : "not recorded";

            // Whether there is anything left to retry from is the single most
            // useful thing to say here, so it goes in its own field.
            var audio = string.IsNullOrWhiteSpace(rec.AudioDirPath)
                ? "No audio on disk. This meeting cannot be recovered."
                : $"Audio kept at `{rec.AudioDirPath}` for {Math.Max(0, config.MeetingFailedAudioRetentionDays)} " +
                  "day(s), so this can be retried once the cause is fixed.";

            var embed = new EmbedBuilder()
                .WithTitle("Meeting minutes failed")
                .WithDescription(
                    $"**{Trunc(rec.MeetingTitle, 200)}** (recording #{rec.Id}) was captured but no minutes were posted.")
                .AddField("Failed at", stage, inline: true)
                .AddField("Recording", recorded, inline: true)
                .AddField("Reason", Trunc(string.IsNullOrWhiteSpace(rec.ErrorMessage)
                    ? "(no detail recorded)" : rec.ErrorMessage, 1000))
                .AddField("Audio", Trunc(audio, 1000))
                .WithColor(new Color(0xE74C3C))
                .WithFooter($"{rec.MeetingStartUtc:yyyy-MM-dd} • ClanGuard")
                .Build();

            await channel.SendMessageAsync(embed: embed, options: new RequestOptions { CancelToken = ct });
            logger.LogInformation(
                "Posted meeting failure notice for '{Title}' (#{Id}) to channel {ChannelId}.",
                rec.MeetingTitle, rec.Id, channelId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not post the meeting failure notice for '{Title}' (#{Id}); the failure itself is unaffected.",
                rec.MeetingTitle, rec.Id);
        }
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..(max - 1)] + "…";
}
