using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /promo-eligibility slash command. Returns a verdict on whether
/// the named member is eligible for auto-promotion at the next nightly run,
/// and if not, what specifically is gating them.
///
/// Rank-gated (PromoEligibilityMinRank+, default CPT) — designed for officers
/// fielding "when is my next promotion?" questions from members. The user
/// parameter is required and accepts any guild member.
///
/// ── Source of truth ──
/// All inputs to the verdict are computed from the same primitives the
/// nightly AutoPromotionService uses:
///   • Tier definitions → AutoPromotionService.GetTierForRank(rank)
///   • AssignedAt + seed → PromotionService.GetRankInfoAsync(...)
///   • Event count → EventAttendanceHelper.CountEventsAttendedAsync(...)
///   • Voice seconds → VoiceActivityHelper.GetVoiceSecondsAsync(...) with the
///     same MaxSingleSessionHours cap as the production decision path
/// This guarantees the command's verdict matches the decision the nightly
/// job will reach. SyncWithHandlers: AutoPromotionService.ProcessGuildAsync.
///
/// ── Senior role mismatch warning ──
/// AutoPromotionService skips members whose Discord role hierarchy says they
/// hold a role above their name-detected current rank (modulo organizational
/// allowlist). This usually indicates a stale junior rank role lingering on a
/// senior member. We surface the same check here as a warning field so the
/// invoking officer sees why the bot would skip even an otherwise-eligible
/// member. SyncWithHandlers: the senior-role-check block in
/// AutoPromotionService.ProcessGuildAsync.
///
/// Ephemeral so promo-status lookups don't clutter channel.
/// </summary>
public class PromoEligibilityCommandHandler
{
    private readonly ILogger<PromoEligibilityCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;
    private readonly IServiceProvider _services;

    public PromoEligibilityCommandHandler(
        ILogger<PromoEligibilityCommandHandler> logger,
        IOptions<BotConfig> config,
        PromotionService promotion,
        IServiceProvider services)
    {
        _logger = logger;
        _config = config.Value;
        _promotion = promotion;
        _services = services;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "promo-eligibility") return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /promo-eligibility for {User}", command.User.Username);
            try
            {
                await command.FollowupAsync(
                    "❌ Something went wrong. Check the bot logs.",
                    ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        // ── Permission check ──
        var caller = command.User as SocketGuildUser;
        if (caller is null || !InvokerHasPermission(caller))
        {
            await command.FollowupAsync(
                $"❌ You need to be **{_config.PromoEligibilityMinRank}** or higher to use this command.",
                ephemeral: true);
            return;
        }

        // ── Parse target user ──
        var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "user");
        if (userOption?.Value is not SocketGuildUser target)
        {
            await command.FollowupAsync("❌ You must select a clan member to look up.", ephemeral: true);
            return;
        }

        if (target.IsBot)
        {
            await command.FollowupAsync(
                "❌ That's a bot account — bots aren't promoted by the auto-promotion service.",
                ephemeral: true);
            return;
        }

        var guild = caller.Guild;

        // ── Detect current rank ──
        var currentRank = GetCurrentRank(target);
        if (currentRank is null)
        {
            await command.FollowupAsync(
                $"⚠️ Could not detect a rank role on {target.Mention}. " +
                $"They may not have any RankRoles-listed role assigned yet.",
                ephemeral: true);
            return;
        }

        // ── Look up the active tier ──
        var tier = AutoPromotionService.GetTierForRank(currentRank);
        if (tier is null)
        {
            // Terminal rank (CSM or above, or SMA) — no further auto-promotion path.
            var terminalEmbed = BuildTerminalRankEmbed(target, currentRank);
            await command.FollowupAsync(embed: terminalEmbed, ephemeral: true);
            return;
        }

        // ── Fetch rank info ──
        var rankInfo = await _promotion.GetRankInfoAsync(guild.Id, target);
        if (rankInfo.AssignedAt is null)
        {
            await command.FollowupAsync(
                $"⚠️ {target.Mention} has no rank-assigned date and no recorded join date. " +
                $"Cannot compute eligibility.",
                ephemeral: true);
            return;
        }

        // ── Single DB scope for the three lookups below ──
        bool inferredFromJoin;
        bool meetsActivity;
        string activityValue;

        var now = DateTime.UtcNow;
        var timeInRank = now - rankInfo.AssignedAt.Value;
        var requiredTime = TimeSpan.FromDays(tier.DaysInRank);
        bool meetsTime = timeInRank >= requiredTime;
        int daysInRank = (int)timeInRank.TotalDays;

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            inferredFromJoin = !await db.RankHistories
                .AnyAsync(r => r.GuildId == guild.Id && r.UserId == target.Id);

            // ── Activity check (mirrors AutoPromotionService.ProcessGuildAsync) ──
            if (tier.MinEvents > 0)
            {
                var events = await EventAttendanceHelper.CountEventsAttendedAsync(
                    db, guild.Id, target.Id,
                    rankInfo.AssignedAt.Value,
                    rankInfo.SeedAppliedAt,
                    rankInfo.SeedEvents);

                meetsActivity = events >= tier.MinEvents;
                activityValue = rankInfo.SeedEvents > 0
                    ? $"**{events}** / {tier.MinEvents} events (includes seed = {rankInfo.SeedEvents})"
                    : $"**{events}** / {tier.MinEvents} events";
            }
            else
            {
                var msgCount = await db.MessageEvents
                    .CountAsync(m => m.GuildId == guild.Id
                                  && m.UserId == target.Id
                                  && m.Timestamp >= rankInfo.AssignedAt.Value);

                var voiceSec = await VoiceActivityHelper.GetVoiceSecondsAsync(
                    db, guild.Id, target.Id,
                    rankInfo.AssignedAt.Value,
                    _config.MaxSingleSessionHours);
                var voiceHrs = voiceSec / 3600.0;

                var meetsMsgs  = msgCount >= tier.MinMessages;
                var meetsVoice = voiceHrs  >= tier.MinVoiceHours;
                meetsActivity = meetsMsgs || meetsVoice;

                var msgIcon   = meetsMsgs  ? "✅" : "❌";
                var voiceIcon = meetsVoice ? "✅" : "❌";
                activityValue =
                    $"{msgIcon} **{msgCount}** / {tier.MinMessages} messages\n" +
                    $"{voiceIcon} **{voiceHrs:F1}h** / {tier.MinVoiceHours:F1}h voice\n" +
                    $"*Either threshold qualifies.*";
            }
        }

        // ── Senior role mismatch (defensive backstop) ──
        var seniorRoleWarning = DetectSeniorRoleMismatch(guild, target, currentRank);

        // ── Dry-run state ──
        var perTierDryRun = _config.GetAutoPromotionDryRunRanksList()
            .Any(r => r.Equals(tier.FromRank, StringComparison.OrdinalIgnoreCase));
        var dryRunActive = _config.AutoPromotionDryRun || perTierDryRun;
        var dryRunReason = _config.AutoPromotionDryRun
            ? "global"
            : (perTierDryRun ? $"per-tier ({tier.FromRank})" : null);

        // ── Verdict ──
        // Senior-role mismatch trumps everything — auto-promo will refuse to act
        // regardless of time/activity, so the headline reflects that.
        Color color;
        string title;
        string verdict;

        if (seniorRoleWarning is not null)
        {
            color = Color.DarkRed;
            title = "🔴 Auto-promo will SKIP this member";
            verdict = "Senior-role mismatch detected — see warning below. " +
                      "The thresholds below are computed for reference, but no promotion will fire.";
        }
        else if (meetsTime && meetsActivity)
        {
            if (dryRunActive)
            {
                color = Color.Orange;
                title = "⚠️ Eligible — but dry-run is on";
                verdict = $"Meets all thresholds, but auto-promo is in dry-run mode ({dryRunReason}). " +
                          $"A manual `/promote` is required.";
            }
            else
            {
                color = Color.Green;
                title = "🟢 Eligible";
                verdict = "Will be promoted at the next nightly run.";
            }
        }
        else if (meetsActivity)
        {
            color = Color.Gold;
            title = "🟡 Waiting on time-in-rank";
            var remaining = Math.Max(1, tier.DaysInRank - daysInRank);
            verdict = $"Activity thresholds are met. ~{remaining} more day(s) of time-in-rank needed.";
        }
        else if (meetsTime)
        {
            color = Color.Red;
            title = "🔴 Waiting on activity";
            verdict = "Time-in-rank is met, but activity thresholds are not.";
        }
        else
        {
            color = Color.Red;
            title = "🔴 Waiting on both time and activity";
            verdict = "Neither threshold is met yet.";
        }

        // ── Compose embed ──
        var assignedTs = ((DateTimeOffset)DateTime.SpecifyKind(rankInfo.AssignedAt.Value, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        var assignedDisplay = $"<t:{assignedTs}:F> (<t:{assignedTs}:R>)";

        var embed = new EmbedBuilder()
            .WithAuthor(target.GlobalName ?? target.Username, target.GetDisplayAvatarUrl())
            .WithColor(color)
            .WithTitle(title)
            .WithDescription(verdict)
            .AddField("Current Rank", currentRank, inline: true)
            .AddField("Next Tier",
                $"{tier.FromRank} → {tier.ToRank.ToUpperInvariant()}", inline: true)
            .AddField("Rank Assigned", assignedDisplay, inline: false)
            .AddField(
                $"{(meetsTime ? "✅" : "❌")} Time in Rank",
                $"**{daysInRank}** / {tier.DaysInRank} days",
                inline: false)
            .AddField(
                $"{(meetsActivity ? "✅" : "❌")} Activity",
                activityValue,
                inline: false);

        // Estimated eligible date — only meaningful when time is the sole blocker.
        if (!meetsTime && meetsActivity && seniorRoleWarning is null)
        {
            var eligibleMoment = DateTime.SpecifyKind(
                rankInfo.AssignedAt.Value.Add(requiredTime), DateTimeKind.Utc);
            var nextRun = ComputeNextRunAfter(eligibleMoment, _config.AutoPromotionRunHourUtc);
            var nextRunTs = ((DateTimeOffset)nextRun).ToUnixTimeSeconds();
            embed.AddField(
                "Estimated Eligible (next run)",
                $"<t:{nextRunTs}:F>",
                inline: false);
        }

        if (dryRunActive && !(meetsTime && meetsActivity))
        {
            embed.AddField(
                "⚠️ Dry-Run Active",
                $"Auto-promotion is in dry-run mode for this tier ({dryRunReason}). " +
                $"Even when this member becomes eligible, a manual `/promote` will be required.",
                inline: false);
        }

        if (inferredFromJoin)
        {
            embed.AddField(
                "ℹ️ Note",
                "No RankHistories row exists for this member — rank assignment date is inferred " +
                "from the server join date. A row will be created automatically on their next " +
                "promotion or demotion.",
                inline: false);
        }

        if (seniorRoleWarning is not null)
        {
            embed.AddField(
                "⚠️ Senior Role Mismatch",
                seniorRoleWarning,
                inline: false);
        }

        embed.WithFooter($"Auto-promotion runs daily at {_config.AutoPromotionRunHourUtc:D2}:00 UTC.");
        embed.WithCurrentTimestamp();

        _logger.LogInformation(
            "/promo-eligibility: {Caller} looked up {Target} → {Verdict} ({Rank}, {Days}d in rank)",
            caller.Username, target.Username, title, currentRank, daysInRank);

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    /// <summary>
    /// Embed for a member who's already at the top of the auto-promotion
    /// ladder (CSM and above, or any rank not in the tier table — e.g. SMA,
    /// officer ranks).
    /// </summary>
    private Embed BuildTerminalRankEmbed(SocketGuildUser target, string currentRank)
    {
        return new EmbedBuilder()
            .WithAuthor(target.GlobalName ?? target.Username, target.GetDisplayAvatarUrl())
            .WithColor(Color.LightGrey)
            .WithTitle("⚪ No further auto-promotion")
            .WithDescription(
                $"{target.Mention} currently holds **{currentRank}**, which is at or above the top of " +
                $"the auto-promotion ladder. Further rank changes are handled manually via `/promote`.")
            .WithCurrentTimestamp()
            .Build();
    }

    /// <summary>
    /// Returns the highest rank-list role name on the member, or null if they
    /// hold no configured rank role. SyncWithHandlers: identical pattern in
    /// AutoPromotionService.GetCurrentRank (both walk RankRoles top-down).
    /// </summary>
    private string? GetCurrentRank(SocketGuildUser member)
    {
        var rankRoles = _config.GetRankRolesList();
        var memberRoleNames = member.Roles
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Contains(rankRoles[i]))
                return rankRoles[i];
        }
        return null;
    }

    /// <summary>
    /// Mirrors the defensive ceiling check in AutoPromotionService.ProcessGuildAsync.
    /// Returns a human-readable warning string if the member holds a Discord
    /// role positioned above their name-detected current rank that isn't in the
    /// organizational allowlist (platoons, exempt, ShortWindow, AWOL). Returns
    /// null if no mismatch is detected. SyncWithHandlers: AutoPromotionService
    /// .ProcessGuildAsync senior-role block.
    /// </summary>
    private string? DetectSeniorRoleMismatch(
        SocketGuild guild, SocketGuildUser member, string currentRank)
    {
        var currentRankRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(currentRank, StringComparison.OrdinalIgnoreCase));

        if (currentRankRole is null) return null;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(_config.GetExemptRolesList());
        allowed.UnionWith(_config.GetPlatoonRolesList());
        allowed.UnionWith(_config.GetShortWindowRolesList());
        allowed.Add(_config.AwolRoleName);

        var seniorRole = member.Roles
            .Where(r => r.Position > currentRankRole.Position
                     && !r.IsManaged
                     && !r.IsEveryone
                     && r.Tags?.IsPremiumSubscriberRole != true
                     && !allowed.Contains(r.Name))
            .OrderByDescending(r => r.Position)
            .FirstOrDefault();

        if (seniorRole is null) return null;

        return
            $"Member holds role **{seniorRole.Name}** (position {seniorRole.Position}) which sits " +
            $"above their detected current rank **{currentRank}** (position {currentRankRole.Position}). " +
            $"Auto-promotion will skip this member to avoid promoting someone who is functionally " +
            $"senior. Likely a role-name mismatch with the `RankRoles` config — verify and clean up " +
            $"stale junior rank roles before this member can be auto-promoted.";
    }

    /// <summary>
    /// Returns the next nightly run time strictly AFTER the given moment,
    /// given the configured AutoPromotionRunHourUtc. Mirrors the scheduling
    /// logic in AutoPromotionService.GetDelayUntilNextRun, with a strict
    /// inequality so a member whose required-time mark falls exactly on the
    /// run hour gets reported as eligible on the FOLLOWING night (matches
    /// the production behavior, where the time check is `&lt;` not `&lt;=`).
    /// </summary>
    private static DateTime ComputeNextRunAfter(DateTime moment, int runHourUtc)
    {
        var clamped = Math.Clamp(runHourUtc, 0, 23);
        var candidate = new DateTime(
            moment.Year, moment.Month, moment.Day, clamped, 0, 0, DateTimeKind.Utc);
        if (candidate <= moment) candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>
    /// True if the invoker has Administrator OR a rank role at or above
    /// PromoEligibilityMinRank. Same pattern as PromoteCommandHandler /
    /// KickAwolsCommandHandler / etc. SyncWithHandlers: the catalog-table
    /// MinRank closure in CommandsCommandHandler.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minRank = _config.PromoEligibilityMinRank;
        var minIdx = rankRoles.FindIndex(r =>
            r.Equals(minRank, StringComparison.OrdinalIgnoreCase));

        if (minIdx < 0)
        {
            _logger.LogWarning(
                "PromoEligibilityMinRank '{Rank}' not found in RankRoles list. " +
                "Permission check will deny everyone except Admins.",
                minRank);
            return false;
        }

        return user.Roles.Any(role =>
        {
            var idx = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return idx >= minIdx;
        });
    }
}
