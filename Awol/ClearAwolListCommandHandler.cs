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
///   • Pinned messages are PRESERVED — the channel's top-of-list instructions
///     and bot-command reference are pinned, and clearing the channel should
///     leave them intact. Protection keys off pin status, not position, so it
///     survives reordering. (Discord's delete endpoints don't spare pinned
///     messages on their own, so we filter them out explicitly.)
///   • RECENT messages are PRESERVED — anything posted within the last
///     BotConfig.AwolKickMinListedDays stays. That's the same threshold
///     /kick-awols uses to decide who is old enough on the list to remove:
///     members flagged too recently aren't kicked, so wiping their listing
///     would erase the review window they haven't had yet (and, since the
///     embed is only ever posted once per AWOL spell, it would never come
///     back). A listing's message timestamp IS its listing time, so message
///     age is the exact same clock the kick gate runs on. Set the config
///     value to 0 to go back to clearing everything unpinned.
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
            var keepDays = _config.AwolKickMinListedDays;
            var recentNote = keepDays > 0
                ? $" and anything posted in the last **{keepDays} days** " +
                  "(those members haven't been on the list long enough to be kicked yet)"
                : string.Empty;

            await cmd.FollowupAsync(
                $"ℹ️ This will permanently delete all messages in `#{_config.HqChannelName}` " +
                $"**except pinned messages** (the instructions stay){recentNote}. " +
                "Pass `confirm:true` to proceed.",
                ephemeral: true);
            return;
        }

        // ── Resolve the channel ──────────────────────────────────────────
        // ID first (rename-proof), then name fallback. The name lookup is
        // brittle: adding an emoji/separator to the channel name breaks the
        // exact match, which is exactly what happened here. Mirrors the
        // resolution in AwolCheckService.
        SocketTextChannel? channel = null;
        if (_config.HqChannelId != 0)
        {
            channel = guild.GetTextChannel(_config.HqChannelId);
            if (channel is null)
            {
                _logger.LogWarning(
                    "HqChannelId={ChannelId} did not resolve in guild {Guild}; " +
                    "falling back to HqChannelName='{ChannelName}'.",
                    _config.HqChannelId, guild.Name, _config.HqChannelName);
            }
        }

        channel ??= guild.TextChannels.FirstOrDefault(c =>
            string.Equals(c.Name, _config.HqChannelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            await cmd.FollowupAsync(
                $"❌ HQ channel not found in this server " +
                $"(HqChannelId=`{_config.HqChannelId}`, HqChannelName=`{_config.HqChannelName}`).",
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
        var skippedPinned = 0;
        var skippedRecent = 0;

        // Messages newer than this are left alone: the members they list
        // haven't been on the list long enough for /kick-awols to remove them,
        // so their listing has to survive the wipe. 0 disables the guard.
        var recentCutoff = _config.AwolKickMinListedDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-_config.AwolKickMinListedDays)
            : DateTimeOffset.MinValue;
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
            // we've seen so far." Pinned messages still advance the cursor so
            // we keep paging past them; we simply never delete them.
            cursor = batchList.Min(m => m.Id);

            // ── Protect pinned messages ──────────────────────────────────
            // The top-of-channel instructions + bot-command reference are
            // pinned. We key off pin status (not position) so the protection
            // holds even if message ordering shifts. Note: Discord's bulk and
            // individual delete endpoints WILL remove pinned messages, so we
            // have to exclude them explicitly here.
            skippedPinned += batchList.Count(m => m.IsPinned);

            // ── Protect recent listings ──────────────────────────────────
            // Same threshold /kick-awols uses. Deleting these would wipe the
            // review window of members who are deliberately NOT being kicked
            // yet, and the embed is never re-posted for the same AWOL spell.
            skippedRecent += batchList.Count(m => !m.IsPinned && m.Timestamp > recentCutoff);

            var deletable = batchList
                .Where(m => !m.IsPinned && m.Timestamp <= recentCutoff)
                .ToList();

            var recent = deletable.Where(m => m.Timestamp > bulkCutoff).ToList();
            var old    = deletable.Where(m => m.Timestamp <= bulkCutoff).ToList();

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

        if (skippedPinned > 0)
            summary += $"\n• Kept (pinned): {skippedPinned}";

        if (skippedRecent > 0)
            summary += $"\n• Kept (posted in the last {_config.AwolKickMinListedDays}d, " +
                       $"not on the list long enough to be kicked yet): {skippedRecent}";

        if (failedSlow > 0)
            summary += $"\n• Failed: {failedSlow} (see bot logs for details)";

        if (safetyIterations >= MaxIterations)
            summary += $"\n\n⚠️ Hit safety iteration cap. There may be more messages — run the command again to continue.";

        _logger.LogInformation(
            "/clear-awol-list completed in #{Channel}: bulk={Bulk}, slow={Slow}, failed={Failed}, " +
            "keptPinned={Pinned}, keptRecent={Recent}, invoker={Invoker}",
            channel.Name, bulkDeleted, slowDeleted, failedSlow, skippedPinned, skippedRecent,
            cmd.User.Username);

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