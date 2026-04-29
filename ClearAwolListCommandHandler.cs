using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /clear-awol-list slash command. Deletes all messages from the
/// configured HQ channel (BotConfig.HqChannelName, e.g. "awol-list") so the
/// reviewing officer doesn't have to scroll through months of stale embeds.
///
/// Implementation notes:
///   • Discord's bulk-delete endpoint only accepts messages younger than 14 days.
///     Anything older has to be deleted one at a time, which is slow and
///     rate-limit-sensitive — but on a channel that's only ever posted to by
///     the bot, the volume is bounded.
///   • Pages through messages 100 at a time (Discord's max per request).
///   • Throttles slow-path deletions to 750ms each to stay well under Discord's
///     per-channel rate limit.
///   • Permission gate matches /kick-awols (BotConfig.AwolKickMinRank).
///   • Reports back with a count of bulk vs. individual deletions so the officer
///     knows roughly how long it took.
/// </summary>
public class ClearAwolListCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<ClearAwolListCommandHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>Discord's hard cutoff for bulk delete eligibility.</summary>
    private static readonly TimeSpan BulkDeleteWindow = TimeSpan.FromDays(14);

    /// <summary>Per-message delay on the slow (>14 day) deletion path.</summary>
    private static readonly TimeSpan SlowDeleteDelay = TimeSpan.FromMilliseconds(750);

    public ClearAwolListCommandHandler(
        DiscordSocketClient client,
        ILogger<ClearAwolListCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
    }

    private async Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != "clear-awol-list") return;

        await cmd.DeferAsync(ephemeral: true);

        try
        {
            await HandleClearAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in /clear-awol-list");
            await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleClearAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ── Permission check ─────────────────────────────────────────────
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await cmd.FollowupAsync(
                $"⛔ You need to be **{_config.AwolKickMinRank}** or higher to use this command.",
                ephemeral: true);
            return;
        }

        // ── Confirm flag — destructive action, no default ────────────────
        var confirm = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value ?? false);
        if (!confirm)
        {
            await cmd.FollowupAsync(
                $"ℹ️ This will permanently delete **all messages** in `#{_config.HqChannelName}`. " +
                "Pass `confirm:true` to proceed.",
                ephemeral: true);
            return;
        }

        // ── Resolve the channel ──────────────────────────────────────────
        var channel = guild.TextChannels.FirstOrDefault(c =>
            string.Equals(c.Name, _config.HqChannelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            await cmd.FollowupAsync(
                $"❌ HQ channel `{_config.HqChannelName}` not found in this server.",
                ephemeral: true);
            return;
        }

        // ── Sanity check: bot has permission to manage messages ──────────
        var perms = guild.CurrentUser.GetPermissions(channel);
        if (!perms.ManageMessages || !perms.ReadMessageHistory)
        {
            await cmd.FollowupAsync(
                $"❌ Bot is missing **Manage Messages** or **Read Message History** " +
                $"permission in `#{channel.Name}`. Grant those permissions and try again.",
                ephemeral: true);
            return;
        }

        // ── Initial progress message so the officer knows it's working ──
        await cmd.FollowupAsync(
            $"🧹 Clearing `#{channel.Name}`… this may take a moment if there are many old messages.",
            ephemeral: true);

        var bulkDeleted = 0;
        var slowDeleted = 0;
        var failedSlow  = 0;
        // Use a 1-minute buffer below Discord's 14-day cutoff to avoid the
        // edge case where a message is 13d 23h 59m old at fetch time but
        // crosses the threshold by the time bulk delete is called.
        var bulkCutoff = DateTimeOffset.UtcNow - BulkDeleteWindow + TimeSpan.FromMinutes(1);

        // Page through history oldest-newest doesn't matter — we're nuking
        // everything. Default direction (newer → older) is fine.
        ulong? cursor = null;
        var safetyIterations = 0;
        const int MaxIterations = 1000; // hard cap so a runaway loop can't lock up the bot

        while (safetyIterations++ < MaxIterations)
        {
            IEnumerable<IMessage> batch;
            try
            {
                batch = cursor is null
                    ? await channel.GetMessagesAsync(100).FlattenAsync()
                    : await channel.GetMessagesAsync(cursor.Value, Direction.Before, 100).FlattenAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch messages from #{Channel}", channel.Name);
                break;
            }

            var batchList = batch.ToList();
            if (batchList.Count == 0) break;

            // Update cursor BEFORE we start deleting — we're iterating from
            // newest to oldest, so the next page is "before the oldest one
            // we've seen so far."
            cursor = batchList.Min(m => m.Id);

            var recent = batchList.Where(m => m.Timestamp > bulkCutoff).ToList();
            var old    = batchList.Where(m => m.Timestamp <= bulkCutoff).ToList();

            // ── Bulk-delete recent messages ──────────────────────────────
            if (recent.Count >= 2)
            {
                try
                {
                    await channel.DeleteMessagesAsync(recent);
                    bulkDeleted += recent.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Bulk delete failed for {Count} messages in #{Channel}; " +
                        "falling back to individual deletes",
                        recent.Count, channel.Name);

                    foreach (var msg in recent)
                    {
                        try
                        {
                            await msg.DeleteAsync();
                            bulkDeleted++;
                            await Task.Delay(SlowDeleteDelay);
                        }
                        catch (Exception delEx)
                        {
                            failedSlow++;
                            _logger.LogWarning(delEx,
                                "Failed to delete message {MessageId} in #{Channel}",
                                msg.Id, channel.Name);
                        }
                    }
                }
            }
            else if (recent.Count == 1)
            {
                // Bulk delete requires 2+ messages; just delete the singleton.
                try
                {
                    await recent[0].DeleteAsync();
                    bulkDeleted++;
                }
                catch (Exception ex)
                {
                    failedSlow++;
                    _logger.LogWarning(ex,
                        "Failed to delete singleton recent message {MessageId} in #{Channel}",
                        recent[0].Id, channel.Name);
                }
            }

            // ── Slow-delete old messages (>14d) ──────────────────────────
            foreach (var msg in old)
            {
                try
                {
                    await msg.DeleteAsync();
                    slowDeleted++;
                    await Task.Delay(SlowDeleteDelay);
                }
                catch (Exception ex)
                {
                    failedSlow++;
                    _logger.LogWarning(ex,
                        "Failed to delete old message {MessageId} in #{Channel}",
                        msg.Id, channel.Name);
                }
            }

            // If Discord returned fewer than 100, we're at the end of history
            if (batchList.Count < 100) break;
        }

        if (safetyIterations >= MaxIterations)
        {
            _logger.LogWarning(
                "/clear-awol-list hit safety iteration cap ({Cap}) in #{Channel}. " +
                "Re-run if more messages remain.",
                MaxIterations, channel.Name);
        }

        // ── Final summary ────────────────────────────────────────────────
        var total = bulkDeleted + slowDeleted;
        var summary = $"✅ Cleared **{total}** messages from <#{channel.Id}>.\n" +
                      $"• Bulk-deleted (≤14d old): {bulkDeleted}\n" +
                      $"• Individually deleted (>14d old): {slowDeleted}";

        if (failedSlow > 0)
            summary += $"\n• Failed: {failedSlow} (see bot logs for details)";

        if (safetyIterations >= MaxIterations)
            summary += $"\n\n⚠️ Hit safety iteration cap. There may be more messages — run the command again to continue.";

        _logger.LogInformation(
            "/clear-awol-list completed in #{Channel}: bulk={Bulk}, slow={Slow}, failed={Failed}, invoker={Invoker}",
            channel.Name, bulkDeleted, slowDeleted, failedSlow, cmd.User.Username);

        await cmd.FollowupAsync(summary, ephemeral: true);
    }

    /// <summary>
    /// True if the invoker has Administrator OR a rank role at or above AwolKickMinRank.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;

        var rankList = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.AwolKickMinRank, StringComparison.OrdinalIgnoreCase));

        if (minRankIdx < 0)
        {
            _logger.LogWarning(
                "AwolKickMinRank '{Rank}' not found in RankRoles list. " +
                "Permission check will deny everyone except Admins.",
                _config.AwolKickMinRank);
            return false;
        }

        foreach (var role in invoker.Roles)
        {
            var idx = rankList.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= minRankIdx) return true;
        }
        return false;
    }
}