using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #1: Account-Age Gate on Join.
///
/// ── What it does ──
/// On every UserJoined gateway event, computes the new member's Discord
/// account age (now − user.CreatedAt) and compares it against the configured
/// minimum. Accounts younger than the threshold are surfaced to leadership
/// and, depending on mode, kicked immediately.
///
/// ── Why this exists ──
/// Brand-new Discord accounts are the dominant signal for trolls, raid
/// alts, ban-evading returnees, and scam-DM bots. Real recruits almost
/// always have months- or years-old accounts. A simple age floor filters
/// out the bulk of the noise before any human has to look at it.
///
/// ── Modes ──
///   "Off"        — Feature disabled. UserJoined still fires but this
///                  handler short-circuits immediately. Default value;
///                  flipping the switch is an explicit opt-in.
///   "AlertOnly"  — Post an embed to the security-alerts channel for any
///                  account under the threshold but DO NOT kick. Intended
///                  for the run-in period when leadership wants to watch
///                  what the rule WOULD catch before enforcing it.
///   "Kick"       — Best-effort DM the joiner with a templated message,
///                  then kick, then post the alert embed with the outcome.
///
/// ── Why account age is the signal we trust ──
/// Discord exposes the account creation timestamp on every user object
/// via the snowflake ID, so it cannot be spoofed by the joiner and does
/// not require any privileged intent beyond GuildMembers (already enabled
/// for AutoPromotion + AWOL tracking). It survives nickname changes,
/// avatar changes, and rejoins.
///
/// ── Concurrency / coexistence with InviteAttributionService ──
/// Both handlers subscribe to UserJoined independently. Discord.NET
/// dispatches gateway events to each subscriber concurrently. The kick
/// path does NOT interfere with attribution because:
///   • Kicking a member does not decrement the invite use counter, so
///     InviteAttributionService still observes a clean +1 diff.
///   • The attribution row records WHICH link the troll used — which is
///     the data leadership wants for forensics after a raid wave.
/// We deliberately do NOT block on attribution before kicking; speed of
/// removal is more valuable than embedding the invite label in the alert.
/// The label will surface in /invite stats and the weekly briefing.
///
/// ── Failure handling ──
/// All work is fire-and-forget from the gateway callback (same pattern as
/// MemberLifecycleHandler). Internal exceptions are caught and logged;
/// a Discord API hiccup must never bring down the gateway listener.
/// HttpException 404 on kick is treated as "user already left" — the
/// outcome we wanted, not a failure.
///
/// ── Out of scope (v1) ──
///   • Quarantine / waiting-room mode (requires a configured holding role
///     and channel-permission setup; tracked as a follow-on once leadership
///     decides whether they want that workflow at all).
///   • Persistent SecurityAuditRecord table. Serilog logs + the alert
///     channel are the audit trail today. If we later want
///     "show me every gate kick this month" without scraping logs, that
///     becomes a separate migration + entity.
///   • Per-rank exemptions. On UserJoined the member has only @everyone,
///     so role-based exemption doesn't help. If we ever need a user-ID
///     allowlist (for testing with mod alt accounts), it goes here.
/// </summary>
public sealed class AccountAgeGateHandler
{
    // Mode string constants — single source of truth for the config values.
    // Comparisons against the configured mode are case-insensitive so a
    // misconfigured "kick" / "ALERTONLY" still works.
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";
    public const string ModeKick      = "Kick";

    private readonly IServiceProvider _services;
    private readonly ILogger<AccountAgeGateHandler> _logger;
    private readonly BotConfig _config;

    public AccountAgeGateHandler(
        IServiceProvider services,
        ILogger<AccountAgeGateHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    /// <summary>Register the UserJoined event handler on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.UserJoined += OnUserJoined;
    }

    private Task OnUserJoined(SocketGuildUser member)
    {
        // Fire-and-forget; gateway handlers must not block the event loop.
        _ = ProcessJoinAsync(member);
        return Task.CompletedTask;
    }

    private async Task ProcessJoinAsync(SocketGuildUser member)
    {
        try
        {
            // ── Mode check ────────────────────────────────────────────────
            var mode = (_config.AccountAgeGateMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Validate mode — fall back to AlertOnly and warn rather than
            // crash on a typo'd config. Erring toward observability over
            // silent enforcement.
            var enforce = string.Equals(mode, ModeKick, StringComparison.OrdinalIgnoreCase);
            if (!enforce && !string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown AccountAgeGateMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly, Kick.",
                    mode);
            }

            // ── Skip bots ─────────────────────────────────────────────────
            // Bots are added by server admins via OAuth invite, not via the
            // normal join flow we care about. Their account age is also a
            // separate concept (the bot user's snowflake, not a human user).
            if (member.IsBot)
            {
                return;
            }

            // ── Age check ─────────────────────────────────────────────────
            var minDays = Math.Max(0, _config.AccountAgeGateMinDays);
            var accountAge = DateTimeOffset.UtcNow - member.CreatedAt;
            var threshold = TimeSpan.FromDays(minDays);

            if (accountAge >= threshold)
            {
                // Account is older than the threshold. Nothing to do.
                return;
            }

            _logger.LogInformation(
                "Account-age gate fired for {Username} ({UserId}) in guild {Guild}: " +
                "account age {AgeDays:F1}d < threshold {MinDays}d (mode: {Mode})",
                member.Username, member.Id, member.Guild.Name,
                accountAge.TotalDays, minDays, mode);

            // ── Kick path: DM + kick first, then post the embed with the result ──
            // We post the alert embed AFTER attempting the kick so the embed
            // can faithfully report whether the kick actually succeeded.
            // AlertOnly mode skips DM + kick entirely.
            var outcome = AccountAgeGateOutcome.Alerted;
            string? kickFailureReason = null;

            if (enforce)
            {
                outcome = await TryKickAsync(member, accountAge, minDays);
                if (outcome == AccountAgeGateOutcome.KickFailed)
                {
                    kickFailureReason = "See bot logs for details.";
                }
            }

            // ── Write durable audit row ───────────────────────────────────
            // Done BEFORE the embed post so that even a Discord-side failure
            // (alerts channel deleted, permissions revoked) leaves us with a
            // queryable record. Wrapped in its own try/catch — a DB hiccup
            // must not stop the alert from posting.
            await WriteAuditAsync(member, accountAge, minDays, outcome, kickFailureReason);

            // ── Post alert embed ──────────────────────────────────────────
            await PostAlertAsync(member, accountAge, minDays, mode, outcome, kickFailureReason);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders — ProcessJoinAsync is fire-and-forget, so
            // an uncaught exception would just vanish into the void. Log it.
            _logger.LogError(ex,
                "Account-age gate handler crashed for user {UserId} in guild {GuildId}",
                member.Id, member.Guild.Id);
        }
    }

    /// <summary>
    /// Best-effort DM the joiner, then kick. Returns the outcome for embed
    /// rendering. Does not throw — Discord errors are logged and surfaced
    /// as KickFailed.
    /// </summary>
    private async Task<AccountAgeGateOutcome> TryKickAsync(
        SocketGuildUser member,
        TimeSpan accountAge,
        int minDays)
    {
        // ── Discord role-hierarchy guard ──────────────────────────────────
        // Same precondition KickAwolsCommandHandler enforces: the bot must
        // outrank the target or KickAsync throws. On UserJoined this should
        // never trip (new joiners only have @everyone) but check anyway for
        // the edge case where a "Sticky Roles"-style bot re-applies a role
        // before our handler runs.
        var botMember = member.Guild.CurrentUser;
        var botTopPos = botMember.Roles.Max(r => r.Position);
        var memberTopPos = member.Roles.Any() ? member.Roles.Max(r => r.Position) : 0;

        if (memberTopPos >= botTopPos)
        {
            _logger.LogWarning(
                "Account-age gate cannot kick {Username} ({UserId}) — member's top role " +
                "is at or above the bot's. Falling back to alert-only for this join.",
                member.Username, member.Id);
            return AccountAgeGateOutcome.KickSkippedHierarchy;
        }

        // ── Best-effort DM before the kick ────────────────────────────────
        // Mirrors KickAwolsCommandHandler: try to give the user context,
        // but don't let a closed DM block the kick itself.
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"Hey — your Discord account is too new to join the **189th** right now " +
                $"(we require accounts at least **{minDays}** days old as an anti-raid measure).\n\n" +
                "This is automated and not personal. Once your account is older than the threshold, " +
                "you're welcome to rejoin. If you believe this was a mistake, reach out to a member " +
                "of leadership.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DMs disabled / blocked the bot — fine, continue with the kick.
        }

        // ── Actually kick ─────────────────────────────────────────────────
        var reason = $"Account-age gate: account {accountAge.TotalDays:F1} day(s) old, " +
                     $"threshold {minDays} day(s).";
        try
        {
            await member.KickAsync(
                reason: reason,
                options: new RequestOptions { AuditLogReason = reason });

            _logger.LogInformation(
                "Kicked {Username} ({UserId}) by account-age gate: {Reason}",
                member.Username, member.Id, reason);
            return AccountAgeGateOutcome.Kicked;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 means the member left between UserJoined and our KickAsync —
            // the outcome we wanted. Same classification as KickAwolsCommandHandler.
            _logger.LogInformation(
                "Account-age gate: {Username} ({UserId}) already left before we could kick them.",
                member.Username, member.Id);
            return AccountAgeGateOutcome.UserAlreadyLeft;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Account-age gate failed to kick {Username} ({UserId})",
                member.Username, member.Id);
            return AccountAgeGateOutcome.KickFailed;
        }
    }

    /// <summary>
    /// Writes one SecurityAuditRecord row for the join + action taken. Wrapped
    /// in its own try/catch — a DB failure here is logged but never
    /// propagated, so the alert post and the kick itself remain
    /// independent of database availability.
    /// </summary>
    private async Task WriteAuditAsync(
        SocketGuildUser member,
        TimeSpan accountAge,
        int minDays,
        AccountAgeGateOutcome outcome,
        string? errorMessage)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = new SecurityAuditRecord
            {
                GuildId      = member.Guild.Id,
                Feature      = "AccountAgeGate",
                Action       = outcome.ToString(),
                UserId       = member.Id,
                Username     = member.Username,
                DisplayName  = member.DisplayName ?? member.Username,
                ChannelId    = null, // Account-age gate is join-scoped, not channel-scoped.
                Details      = $"Account age {accountAge.TotalDays:F2}d, threshold {minDays}d. " +
                               $"Created: {member.CreatedAt:O}.",
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = errorMessage,
            };

            db.SecurityAuditRecords.Add(record);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write SecurityAuditRecord for account-age gate event " +
                "(user {UserId}, outcome {Outcome})",
                member.Id, outcome);
        }
    }

    /// <summary>
    /// Resolves the security-alerts channel and posts an embed describing the
    /// join + action taken. Falls back to the configured HqChannelName when
    /// SecurityAlertsChannelId is unset (0) so newly-deployed instances get
    /// a functional default before leadership configures a dedicated channel.
    /// </summary>
    private async Task PostAlertAsync(
        SocketGuildUser member,
        TimeSpan accountAge,
        int minDays,
        string mode,
        AccountAgeGateOutcome outcome,
        string? kickFailureReason)
    {
        var channel = ResolveSecurityAlertsChannel(member.Guild);
        if (channel is null)
        {
            _logger.LogWarning(
                "Account-age gate has no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var (color, title, statusLine) = outcome switch
        {
            AccountAgeGateOutcome.Kicked                 => (Color.Red,        "🛡️ Account-Age Gate — Kicked",          "Member was kicked automatically."),
            AccountAgeGateOutcome.Alerted                => (Color.Orange,     "🛡️ Account-Age Gate — Alert",           "Mode is **AlertOnly** — no action taken."),
            AccountAgeGateOutcome.UserAlreadyLeft        => (Color.LightGrey,  "🛡️ Account-Age Gate — User Left",       "Member left before kick could be issued."),
            AccountAgeGateOutcome.KickSkippedHierarchy   => (Color.Gold,       "🛡️ Account-Age Gate — Cannot Kick",     "Bot lacks role hierarchy to kick this member. Manual review required."),
            AccountAgeGateOutcome.KickFailed             => (Color.DarkRed,    "🛡️ Account-Age Gate — Kick FAILED",     "Kick call failed. " + (kickFailureReason ?? "")),
            _                                            => (Color.DarkerGrey, "🛡️ Account-Age Gate",                    "Unknown outcome."),
        };

        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithDescription(statusLine)
            .AddField("Member",            $"{member.Mention} (`{member.Username}` / `{member.Id}`)", inline: false)
            .AddField("Account Created",   FormatTimestamp(member.CreatedAt),                          inline: true)
            .AddField("Account Age",       FormatAge(accountAge),                                       inline: true)
            .AddField("Threshold",         $"{minDays} day(s)",                                         inline: true)
            .AddField("Mode",              mode,                                                        inline: true)
            .WithFooter("ClanGuard • Account-Age Gate")
            .WithCurrentTimestamp();

        try
        {
            await channel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post account-age gate alert to channel {Channel} ({ChannelId}) " +
                "for user {UserId}",
                channel.Name, channel.Id, member.Id);
        }
    }

    /// <summary>
    /// Resolves the alert destination. ID takes precedence; falls back to a
    /// name lookup against HqChannelName so a fresh install still gets alerts
    /// in the default HQ channel until leadership picks a dedicated one.
    /// </summary>
    private SocketTextChannel? ResolveSecurityAlertsChannel(SocketGuild guild)
    {
        if (_config.SecurityAlertsChannelId != 0)
        {
            var byId = guild.GetTextChannel(_config.SecurityAlertsChannelId);
            if (byId is not null) return byId;

            _logger.LogWarning(
                "SecurityAlertsChannelId={Id} did not resolve in guild {Guild}; " +
                "falling back to HqChannelName='{Hq}'.",
                _config.SecurityAlertsChannelId, guild.Name, _config.HqChannelName);
        }

        if (!string.IsNullOrWhiteSpace(_config.HqChannelName))
        {
            return guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static string FormatTimestamp(DateTimeOffset dt)
    {
        // Discord renders <t:UNIX:F> as the full date+time in the viewer's locale,
        // and <t:UNIX:R> as the relative ("3 days ago") form. Show both — exact
        // for forensics, relative for at-a-glance scanning.
        var unix = dt.ToUnixTimeSeconds();
        return $"<t:{unix}:F> (<t:{unix}:R>)";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{age.TotalDays:F1} day(s)";
        if (age.TotalHours >= 1)
            return $"{age.TotalHours:F1} hour(s)";
        return $"{age.TotalMinutes:F0} minute(s)";
    }
}

/// <summary>
/// Outcomes the gate can record for a single join event. Kept internal
/// to the handler — the alert embed is the only consumer today. Promote
/// to a public enum if/when a SecurityAuditRecord table is added.
/// </summary>
internal enum AccountAgeGateOutcome
{
    /// <summary>AlertOnly mode — no enforcement action taken.</summary>
    Alerted,

    /// <summary>Kick mode — member was successfully removed.</summary>
    Kicked,

    /// <summary>Member left the guild before we could kick them.</summary>
    UserAlreadyLeft,

    /// <summary>Bot lacks role hierarchy to kick this member.</summary>
    KickSkippedHierarchy,

    /// <summary>Kick call failed for an unexpected reason. See logs.</summary>
    KickFailed,
}
