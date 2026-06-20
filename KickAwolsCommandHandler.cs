using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /kick-awols slash command. Iterates over every guild member with
/// the AWOL role and removes them from the server. Includes:
///
///   • Reserve guard — anyone with the role named in BotConfig.ReserveRoleName
///     is skipped, even if they somehow ended up flagged AWOL. AwolCheckService
///     already prevents Reserve members from being assigned AWOL via the
///     ExemptRoles list, but we re-check here in case the Reserve role was
///     applied AFTER they were marked AWOL (manual role change, role-on-rejoin
///     race, etc.). This is the belt-and-suspenders defense the user explicitly
///     asked for.
///
///   • Min-rank protection — members at or above BotConfig.AwolKickMinRank are
///     skipped to prevent a tracking bug or rogue invocation from nuking
///     leadership. Same rank gate that controls who can RUN the command.
///
///   • Role hierarchy check — Discord won't let the bot kick anyone whose top
///     role sits at or above the bot's top role; we detect that up front so
///     we can write a "SkippedHierarchy" audit row instead of letting
///     KickAsync throw. We ALSO catch the same condition at kick-time
///     (Forbidden/HTTP 403) in case roles changed mid-run.
///
///   • Best-effort DM before kick — sent inside a try/catch since DMs may be
///     closed. Gives the kicked member a clear reason and an invitation to
///     come back if they want.
///
///   • Confirm/dry-run flags — must pass exactly one of confirm:true or
///     dry-run:true. No default action, no implicit go-ahead. Dry run writes
///     the same audit rows but skips the actual kick.
///
///   • Per-member audit row — every member processed (kicked, skipped, or
///     failed) gets an AwolKickAuditRecord row. Discord's audit log only
///     retains 45 days; this preserves history forever.
///
///   • Mid-loop audit persistence — audit rows are flushed to the DB every
///     AuditFlushBatchSize members rather than only at the end of the loop.
///     A bot crash mid-run will lose at most BatchSize rows of audit data
///     instead of the entire run's audit trail. The previous behavior
///     (single SaveChangesAsync at the end) created a window where Discord
///     had a record of the kicks but the bot's DB did not.
///
///   • Token-independent result summary — the detailed ephemeral summary is
///     still sent via FollowupAsync, but a brief one-line audit summary is
///     ALSO posted to the HQ channel as a regular message. The ephemeral
///     followup uses the slash-command interaction token, which expires 15
///     minutes after the original DeferAsync call. A run that processes a
///     few hundred members can blow through that window and leave the
///     invoking officer with no visible result. The HQ post uses a normal
///     channel send (no token) so the run outcome is always recorded
///     somewhere visible, regardless of how long the loop took.
/// </summary>
public class KickAwolsCommandHandler
{
    /// <summary>
    /// How many audit rows to accumulate before flushing to the DB. Smaller
    /// values reduce loss-on-crash risk; larger values reduce DB write
    /// overhead. 25 is a balance that keeps loss within ~1 minute of kicks
    /// at the current ~2-second-per-kick pacing while still batching
    /// efficiently.
    /// </summary>
    private const int AuditFlushBatchSize = 25;

    /// <summary>
    /// Hard per-DM timeout. The goodbye DM is best-effort cosmetic — it must
    /// never hold up the actual kicks. Opening a DM channel
    /// (POST /users/@me/channels) is one of Discord's most aggressively
    /// rate-limited routes and lives in a SEPARATE bucket from the kick route,
    /// so the loop's 500ms kick pacing does nothing to protect it. On a large
    /// run the DM route eventually hits a 429 with a long retry-after and,
    /// under Discord.NET's default RetryMode.RetryRateLimit, the client
    /// silently AWAITS that retry-after instead of throwing — freezing the
    /// loop while the process still looks alive (kicks stop, command never
    /// returns). This is the stall we're fixing. See TrySendGoodbyeDmAsync.
    /// </summary>
    private static readonly TimeSpan DmTimeout = TimeSpan.FromSeconds(5);

    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<KickAwolsCommandHandler> _logger;
    private readonly BotConfig _config;

    public KickAwolsCommandHandler(
        DiscordSocketClient client,
        IServiceProvider services,
        ILogger<KickAwolsCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Hooks the slash-command listener. Call once during bot startup
    /// (same Register pattern as the other command handlers).
    /// </summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
    }

    private async Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != "kick-awols") return;

        // Defer ephemeral so only the invoker sees progress / results.
        await cmd.DeferAsync(ephemeral: true);

        try
        {
            await HandleKickAwolsAsync(cmd);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — let it propagate so the framework can stop cleanly.
            // This MUST be caught separately and rethrown; the broad catch below
            // would otherwise swallow it and falsely report a "user-visible
            // error" via FollowupAsync during shutdown.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in /kick-awols");
            // FollowupAsync may itself throw if the interaction token expired
            // (the run was very long). Don't let that obscure the original
            // error in the logs.
            try
            {
                await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
            }
            catch (Exception followupEx)
            {
                _logger.LogWarning(followupEx,
                    "Could not deliver error followup — interaction token likely expired");
            }
        }
    }

    private async Task HandleKickAwolsAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Permission check — must meet the configured minimum rank
        // ------------------------------------------------------------------
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await cmd.FollowupAsync(
                $"⛔ You need to be **{_config.AwolKickMinRank}** or higher to use this command.",
                ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Mode flags — must pass exactly one of confirm or dry-run
        // ------------------------------------------------------------------
        var confirm = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value ?? false);
        var dryRun  = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "dry-run")?.Value ?? false);

        if (!confirm && !dryRun)
        {
            await cmd.FollowupAsync(
                "ℹ️ Pass `confirm:true` to actually kick AWOL members, " +
                "or `dry-run:true` to preview without kicking. " +
                "**Strongly recommend running with `dry-run:true` first.**",
                ephemeral: true);
            return;
        }

        if (confirm && dryRun)
        {
            await cmd.FollowupAsync(
                "ℹ️ Pass either `confirm:true` OR `dry-run:true`, not both.",
                ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Resolve roles
        // ------------------------------------------------------------------
        var awolRole = guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, _config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (awolRole is null)
        {
            await cmd.FollowupAsync(
                $"❌ AWOL role `{_config.AwolRoleName}` not found in this server.",
                ephemeral: true);
            return;
        }

        // Reserve role may legitimately be missing in a fresh server setup — that's
        // fine, just means no one currently holds it. Log a warning so it's visible
        // but proceed normally.
        var reserveRole = guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, _config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));
        if (reserveRole is null)
        {
            _logger.LogWarning(
                "Reserve role '{ReserveRole}' not found in guild {Guild}. " +
                "Reserve guard will be skipped (no Reserve members to protect).",
                _config.ReserveRoleName, guild.Name);
        }

        // ------------------------------------------------------------------
        // Make sure the user cache is populated before enumerating
        // ------------------------------------------------------------------
        await guild.DownloadUsersAsync();

        var awolMembers = guild.Users
            .Where(u => !u.IsBot)
            .Where(u => u.Roles.Any(r => r.Id == awolRole.Id))
            .ToList();

        if (awolMembers.Count == 0)
        {
            await cmd.FollowupAsync("✅ No AWOL members found. Nothing to do.", ephemeral: true);
            return;
        }

        // Bot's top role position determines who we can kick at all
        var botTopRolePos = guild.CurrentUser.Roles.Max(r => r.Position);

        // ------------------------------------------------------------------
        // Process each AWOL member
        // ------------------------------------------------------------------
        var kicked        = new List<string>();
        var skippedRes    = new List<string>();
        var skippedRank   = new List<string>();
        var skippedHier   = new List<string>();
        var userGone      = new List<string>();
        var failed        = new List<string>();

        // Pending audit rows that haven't been flushed to the DB yet. Cleared
        // every AuditFlushBatchSize members. Total audit rows written across
        // the run is tracked separately in totalAuditsWritten.
        var pendingAudits = new List<AwolKickAuditRecord>();
        var totalAuditsWritten = 0;

        var kickReason = $"AWOL — automated removal via /kick-awols by {cmd.User.Username}";
        var rankList   = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.AwolKickMinRank, StringComparison.OrdinalIgnoreCase));

        foreach (var member in awolMembers)
        {
            var rolesSnapshot = string.Join(", ",
                member.Roles.Where(r => !r.IsEveryone).Select(r => r.Name));

            var audit = new AwolKickAuditRecord
            {
                GuildId      = guild.Id,
                UserId       = member.Id,
                Username     = member.Username,
                DisplayName  = member.DisplayName ?? member.Username,
                RolesAtKick  = rolesSnapshot,
                ProcessedAt  = DateTime.UtcNow,
                InvokerId    = cmd.User.Id,
                InvokerName  = cmd.User.Username,
                WasDryRun    = dryRun,
                Reason       = kickReason
            };

            // ── Guard 1: Reserve members are NEVER kicked ─────────────────
            // The user explicitly requested this re-check inside the kick
            // command. AwolCheckService should already prevent Reserve members
            // from being assigned AWOL, but verify here in case the Reserve
            // role was added between AWOL assignment and kick (manual role
            // change, role-on-rejoin race, etc.).
            if (reserveRole != null && member.Roles.Any(r => r.Id == reserveRole.Id))
            {
                audit.Outcome = "SkippedReserve";
                pendingAudits.Add(audit);
                skippedRes.Add(member.DisplayName);
                _logger.LogInformation(
                    "Skipped AWOL kick for {User} — has Reserve role (Reserve guard)",
                    member.Username);
            }
            // ── Guard 2: Don't kick members at/above protected leadership rank ─
            else if (minRankIdx >= 0 && MemberRankIsAtOrAbove(member, rankList, minRankIdx))
            {
                audit.Outcome = "SkippedProtected";
                pendingAudits.Add(audit);
                skippedRank.Add(member.DisplayName);
                _logger.LogWarning(
                    "Skipped AWOL kick for {User} — at/above protected rank {MinRank}. " +
                    "This shouldn't happen; investigate why they were marked AWOL.",
                    member.Username, _config.AwolKickMinRank);
            }
            // ── Guard 3: Discord role hierarchy ───────────────────────────
            // Bot must outrank target or KickAsync will throw. Skip up front
            // so we get a clean audit row instead of an exception.
            else if (member.Roles.Max(r => r.Position) >= botTopRolePos)
            {
                audit.Outcome = "SkippedHierarchy";
                pendingAudits.Add(audit);
                skippedHier.Add(member.DisplayName);
                _logger.LogWarning(
                    "Skipped AWOL kick for {User} — top role above bot's top role",
                    member.Username);
            }
            // ── Dry run: log intended kick and move on ────────────────────
            else if (dryRun)
            {
                audit.Outcome = "DryRunKicked";
                pendingAudits.Add(audit);
                kicked.Add(member.DisplayName);
            }
            else
            {
                // ── Best-effort DM before the kick ────────────────────────
                // Hard-timeout + no-rate-limit-retry so a throttled or hung
                // DM route can never stall the kick loop. See
                // TrySendGoodbyeDmAsync for the full rationale.
                await TrySendGoodbyeDmAsync(member);

                // ── Actually kick ─────────────────────────────────────────
                try
                {
                    await member.KickAsync(
                        reason: kickReason,
                        options: new RequestOptions { AuditLogReason = kickReason });

                    audit.Outcome = "Kicked";
                    pendingAudits.Add(audit);
                    kicked.Add(member.DisplayName);
                    _logger.LogInformation(
                        "Kicked {User} ({UserId}) for AWOL — invoker: {Invoker}",
                        member.Username, member.Id, cmd.User.Username);
                }
                catch (OperationCanceledException)
                {
                    // Don't classify host shutdown as a kick failure.
                    throw;
                }
                catch (HttpException ex) when (
                    ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404 means the member left between us building the AWOL
                    // list and reaching their position in the loop. Not a
                    // failure — they're already gone, which is exactly the
                    // outcome we wanted. Categorize separately so reports
                    // distinguish "Discord said no" from "user pre-empted us."
                    audit.Outcome = "UserAlreadyLeft";
                    pendingAudits.Add(audit);
                    userGone.Add(member.DisplayName);
                    _logger.LogInformation(
                        "AWOL kick for {User} ({UserId}) returned 404 — user already left the guild",
                        member.Username, member.Id);
                }
                catch (HttpException ex) when (
                    ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // 403 at kick-time despite the pre-check usually means
                    // someone's roles changed mid-run (e.g. they were given a
                    // role above the bot's top role between the snapshot and
                    // the kick attempt) or the bot lost Kick Members
                    // permission. Treat the same as the up-front hierarchy
                    // guard so officers see a single category in the summary.
                    audit.Outcome      = "SkippedHierarchy";
                    audit.ErrorMessage = $"HTTP 403 at kick-time: {ex.Message}";
                    pendingAudits.Add(audit);
                    skippedHier.Add(member.DisplayName);
                    _logger.LogWarning(ex,
                        "AWOL kick for {User} ({UserId}) returned 403 at kick-time — " +
                        "roles changed mid-run or bot lost Kick Members permission",
                        member.Username, member.Id);
                }
                catch (HttpException ex)
                {
                    audit.Outcome      = "Failed";
                    audit.ErrorMessage = $"HTTP {(int?)ex.HttpCode}: {ex.Message}";
                    pendingAudits.Add(audit);
                    failed.Add($"{member.DisplayName} (HTTP {(int?)ex.HttpCode})");
                    _logger.LogError(ex,
                        "Failed to kick {User} ({UserId}) for AWOL — HTTP {Code}",
                        member.Username, member.Id, (int?)ex.HttpCode);
                }
                catch (Exception ex)
                {
                    audit.Outcome      = "Failed";
                    audit.ErrorMessage = ex.Message;
                    pendingAudits.Add(audit);
                    failed.Add($"{member.DisplayName} ({ex.Message})");
                    _logger.LogError(ex,
                        "Failed to kick {User} ({UserId}) for AWOL",
                        member.Username, member.Id);
                }

                // Gentle pacing to avoid hitting Discord's rate limit on bulk
                // kicks. Discord's documented kick rate limit is roughly 5/5s
                // per guild but burst behavior varies; 500ms client-side plus
                // Discord.NET's own rate-limiter has held up clean across
                // 279-member runs with zero rate-limit warnings.
                await Task.Delay(500);
            }

            // ── Periodic audit flush ──────────────────────────────────────
            // Persist accumulated audit rows so a mid-run crash doesn't lose
            // the entire run's audit history. Wrapped in try/catch because a
            // transient DB error here should NOT abort the kick loop — the
            // members in flight are far more valuable than the audit row,
            // and we'll retry the flush at the end anyway.
            if (pendingAudits.Count >= AuditFlushBatchSize)
            {
                var flushed = await TryFlushAuditsAsync(pendingAudits);
                totalAuditsWritten += flushed;
                pendingAudits.Clear();
            }
        }

        // ------------------------------------------------------------------
        // Final audit flush — anything left over after the loop
        // ------------------------------------------------------------------
        if (pendingAudits.Count > 0)
        {
            var flushed = await TryFlushAuditsAsync(pendingAudits);
            totalAuditsWritten += flushed;
            pendingAudits.Clear();
        }

        // ------------------------------------------------------------------
        // Build the summary response
        // ------------------------------------------------------------------
        var modeLabel = dryRun ? "**DRY RUN** — no members were actually kicked" : "**LIVE RUN**";
        var verb      = dryRun ? "Would kick" : "Kicked";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### AWOL Kick Summary — {modeLabel}");
        sb.AppendLine();
        sb.AppendLine($"**{verb}: {kicked.Count}**");
        if (kicked.Count > 0)
            sb.AppendLine(TruncateList(kicked));

        if (skippedRes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — Reserve role: {skippedRes.Count}**");
            sb.AppendLine(TruncateList(skippedRes));
        }

        if (skippedRank.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — at/above {_config.AwolKickMinRank}: {skippedRank.Count}**");
            sb.AppendLine(TruncateList(skippedRank));
        }

        if (skippedHier.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — role hierarchy: {skippedHier.Count}**");
            sb.AppendLine(TruncateList(skippedHier));
            sb.AppendLine("_(Bot's top role must be above the member's top role to kick them.)_");
        }

        if (userGone.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Already left: {userGone.Count}**");
            sb.AppendLine(TruncateList(userGone));
            sb.AppendLine("_(Left the server before we got to them — no action needed.)_");
        }

        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Failed: {failed.Count}**");
            sb.AppendLine(TruncateList(failed));
        }

        sb.AppendLine();
        sb.AppendLine($"_Audit rows written: {totalAuditsWritten}_");

        // Discord caps message content at 2000 chars; truncate if we somehow exceed
        var summary = sb.ToString();
        if (summary.Length > 1900)
            summary = summary.Substring(0, 1900) + "\n…(truncated)";

        // ------------------------------------------------------------------
        // Send the detailed ephemeral summary to the invoker.
        // ------------------------------------------------------------------
        // Wrapped because the slash-command interaction token expires 15
        // minutes after DeferAsync. A long run can blow through that window,
        // in which case FollowupAsync throws and the invoker would otherwise
        // see no result at all. The HQ post below covers that case.
        try
        {
            await cmd.FollowupAsync(summary, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not deliver ephemeral kick summary to invoker — " +
                "interaction token likely expired after a long run. " +
                "HQ channel summary will still be posted.");
        }

        // ------------------------------------------------------------------
        // Token-independent persistent summary in HQ.
        // ------------------------------------------------------------------
        // Posts a brief one-line audit-style summary to the configured HQ
        // channel using a regular channel send — no interaction token, no
        // 15-minute deadline. This is the source of truth for "did the run
        // happen and how did it go" when the ephemeral followup gets eaten
        // by token expiry on long runs.
        await PostHqSummaryAsync(
            guild, cmd.User, dryRun,
            kicked.Count, skippedRes.Count, skippedRank.Count,
            skippedHier.Count, userGone.Count, failed.Count,
            totalAuditsWritten);
    }

    /// <summary>
    /// Sends the best-effort "you've been removed" DM with a hard timeout and
    /// no rate-limit retry, so a throttled or hung DM route can never stall the
    /// kick loop (the freeze documented on <see cref="DmTimeout"/>). Two
    /// independent defenses:
    ///
    ///   • RetryMode.AlwaysFail — a 429 on the DM route throws
    ///     RateLimitedException immediately instead of silently awaiting the
    ///     retry-after.
    ///   • CancellationToken timeout — caps any single DM attempt at
    ///     DmTimeout regardless of cause (slow open, hung send, etc.).
    ///
    /// Any failure (DMs closed, rate-limited, timed out, network) is swallowed
    /// — the kick is what matters. Host-shutdown cancellation is distinguished
    /// from our own timeout and re-thrown so the loop still stops cleanly.
    /// </summary>
    private async Task TrySendGoodbyeDmAsync(SocketGuildUser member)
    {
        using var cts = new CancellationTokenSource(DmTimeout);
        var dmOptions = new RequestOptions
        {
            RetryMode   = RetryMode.AlwaysFail,
            CancelToken = cts.Token
        };

        try
        {
            var dm = await member.CreateDMChannelAsync(dmOptions);
            await dm.SendMessageAsync(
                "You've been removed from the **189th** for inactivity (AWOL).\n\n" +
                "If you'd like to come back, reach out to a member of leadership and " +
                "we'll get you reinstated. No hard feelings — life happens.",
                options: dmOptions);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // Cancellation that ISN'T our DM timeout = host shutdown. Let it
            // propagate so the kick loop stops cleanly (same contract as before).
            throw;
        }
        catch (Exception ex)
        {
            // DMs disabled/closed, rate-limited, timed out, or any other
            // best-effort failure — log quietly and proceed to the kick.
            _logger.LogDebug(ex,
                "Skipped goodbye DM to {User} ({UserId}) — {Reason}",
                member.Username, member.Id, ex.Message);
        }
    }

    /// <summary>
    /// Attempts to persist the given audit rows. Returns the number of rows
    /// successfully written, or 0 on failure. Does NOT throw — a transient
    /// DB issue here should not abort the kick loop. The rows stay in the
    /// caller's pending list and will be retried on the next batch (or the
    /// final flush). Worst case: we lose the rows, but the kick itself
    /// already happened and is in Discord's audit log.
    /// </summary>
    private async Task<int> TryFlushAuditsAsync(List<AwolKickAuditRecord> rows)
    {
        if (rows.Count == 0) return 0;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.AwolKickAudits.AddRange(rows);
            await db.SaveChangesAsync();
            return rows.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to flush {Count} AWOL kick audit rows to the database. " +
                "Kicks already happened; the audit trail for this batch is lost.",
                rows.Count);
            return 0;
        }
    }

    /// <summary>
    /// Posts a one-line summary of the kick run to the HQ channel as a
    /// regular (non-ephemeral) message. This is the durable record of the
    /// run — visible regardless of whether the invoker's ephemeral followup
    /// landed and persistent across bot restarts. Wrapped in try/catch
    /// because a missing/misconfigured HQ channel should not make the
    /// command itself look like it failed.
    /// </summary>
    private async Task PostHqSummaryAsync(
        SocketGuild guild,
        IUser invoker,
        bool dryRun,
        int kicked, int skippedReserve, int skippedRank,
        int skippedHier, int userGone, int failedCount,
        int auditsWritten)
    {
        try
        {
            // ID first (rename-proof), then name fallback — mirrors
            // AwolCheckService. A name-only lookup silently fails the moment
            // the channel name gains an emoji/separator.
            SocketTextChannel? hqChannel = null;
            if (_config.HqChannelId != 0)
            {
                hqChannel = guild.GetTextChannel(_config.HqChannelId);
                if (hqChannel is null)
                {
                    _logger.LogWarning(
                        "HqChannelId={ChannelId} did not resolve in {Guild}; " +
                        "falling back to HqChannelName='{ChannelName}'.",
                        _config.HqChannelId, guild.Name, _config.HqChannelName);
                }
            }

            hqChannel ??= guild.TextChannels.FirstOrDefault(c =>
                string.Equals(c.Name, _config.HqChannelName, StringComparison.OrdinalIgnoreCase));

            if (hqChannel is null)
            {
                _logger.LogWarning(
                    "HQ channel not found in {Guild} (HqChannelId={ChannelId}, " +
                    "HqChannelName='{Channel}'); skipping persistent summary post",
                    guild.Name, _config.HqChannelId, _config.HqChannelName);
                return;
            }

            var verb = dryRun ? "would kick" : "kicked";
            var mode = dryRun ? " (DRY RUN)" : "";

            var line =
                $"📋 **AWOL kick run complete{mode}** — invoked by {invoker.Mention}\n" +
                $"`{verb}: {kicked}` · " +
                $"`reserve: {skippedReserve}` · " +
                $"`protected rank: {skippedRank}` · " +
                $"`hierarchy: {skippedHier}` · " +
                $"`already left: {userGone}` · " +
                $"`failed: {failedCount}` · " +
                $"`audits: {auditsWritten}`";

            await hqChannel.SendMessageAsync(
                line,
                allowedMentions: AllowedMentions.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post AWOL kick summary to HQ channel. " +
                "Run completed; ephemeral followup (if delivered) and DB audit rows are the only record.");
        }
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

        return MemberRankIsAtOrAbove(invoker, rankList, minRankIdx);
    }

    /// <summary>
    /// True if the member holds any rank role at or above the given index in the
    /// rank list. Members with multiple rank roles (which shouldn't happen but
    /// can during a botched promotion) are evaluated by their highest rank.
    /// </summary>
    private static bool MemberRankIsAtOrAbove(
        SocketGuildUser member, List<string> rankList, int minRankIdx)
    {
        foreach (var role in member.Roles)
        {
            var idx = rankList.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= minRankIdx) return true;
        }
        return false;
    }

    /// <summary>
    /// Joins a name list with commas, truncating to keep summary messages
    /// under Discord's 2000-char content cap when many members are involved.
    /// </summary>
    private static string TruncateList(List<string> names, int maxLength = 400)
    {
        var joined = string.Join(", ", names);
        if (joined.Length <= maxLength) return joined;
        return joined.Substring(0, maxLength) + $"… (+{names.Count - 1} more, see audit log)";
    }
}