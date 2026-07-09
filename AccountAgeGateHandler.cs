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
/// Server-protection feature #1: Account-Age Gate on Join.
///
/// ── What it does ──
/// On every UserJoined gateway event, computes the new member's Discord
/// account age (now − user.CreatedAt) and compares it against the configured
/// minimum. Accounts younger than the threshold are surfaced to leadership
/// and, depending on mode, banned immediately.
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
///   "Kick"/"Ban"  — Best-effort DM the joiner with a templated message,
///                  then BAN, then post the alert embed with the outcome.
///                  Both strings are accepted as the enforcement trigger:
///                  "Ban" is the preferred name; "Kick" is retained as a
///                  back-compat alias so existing deployments keep enforcing
///                  without a config edit. (The action is a ban either way.)
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
/// HttpException 404 on ban is treated as "user already left" — the
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
    // misconfigured "ban" / "ALERTONLY" still works.
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";
    public const string ModeKick      = "Kick"; // Back-compat alias for ModeBan; still triggers enforcement.
    public const string ModeBan       = "Ban";

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
            // silent enforcement. Both "Ban" and the legacy "Kick" alias
            // trigger enforcement (the action is a ban in both cases).
            var enforce =
                string.Equals(mode, ModeBan, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, ModeKick, StringComparison.OrdinalIgnoreCase);
            if (!enforce && !string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown AccountAgeGateMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly, Ban (or legacy Kick).",
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

            // ── One-time exemption check ──────────────────────────────────
            // /allow-new-account (Administrator only) can pre-clear a specific
            // brand-new account — e.g. a member's spouse whose Discord account
            // is genuinely minutes old. If a matching exemption exists, consume
            // it (so it can't be reused), then alert-only and stop — never ban.
            // Checked only for under-threshold joins, so the common case (an
            // old account joining) still does zero DB work.
            if (await TryConsumeExemptionAsync(member.Guild.Id, member.Id))
            {
                _logger.LogInformation(
                    "Account-age gate: {Username} ({UserId}) allowed in via a one-time exemption " +
                    "(account age {AgeDays:F1}d < threshold {MinDays}d). Exemption consumed.",
                    member.Username, member.Id, accountAge.TotalDays, minDays);

                await WriteAuditAsync(member, accountAge, minDays, AccountAgeGateOutcome.AllowedByExemption, null);
                await PostAlertAsync(member, accountAge, minDays, mode, AccountAgeGateOutcome.AllowedByExemption, null);
                return;
            }

            _logger.LogInformation(
                "Account-age gate fired for {Username} ({UserId}) in guild {Guild}: " +
                "account age {AgeDays:F1}d < threshold {MinDays}d (mode: {Mode})",
                member.Username, member.Id, member.Guild.Name,
                accountAge.TotalDays, minDays, mode);

            // ── Ban path: DM + ban first, then post the embed with the result ──
            // We post the alert embed AFTER attempting the ban so the embed
            // can faithfully report whether the ban actually succeeded.
            // AlertOnly mode skips DM + ban entirely.
            var outcome = AccountAgeGateOutcome.Alerted;
            string? banFailureReason = null;

            if (enforce)
            {
                outcome = await TryBanAsync(member, accountAge, minDays);
                if (outcome == AccountAgeGateOutcome.BanFailed)
                {
                    banFailureReason = "See bot logs for details.";
                }
            }

            // ── Write durable audit row ───────────────────────────────────
            // Done BEFORE the embed post so that even a Discord-side failure
            // (alerts channel deleted, permissions revoked) leaves us with a
            // queryable record. Wrapped in its own try/catch — a DB hiccup
            // must not stop the alert from posting.
            await WriteAuditAsync(member, accountAge, minDays, outcome, banFailureReason);

            // ── Post alert embed ──────────────────────────────────────────
            await PostAlertAsync(member, accountAge, minDays, mode, outcome, banFailureReason);
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
    /// Best-effort DM the joiner, then ban. Returns the outcome for embed
    /// rendering. Does not throw — Discord errors are logged and surfaced
    /// as BanFailed.
    /// </summary>
    private async Task<AccountAgeGateOutcome> TryBanAsync(
        SocketGuildUser member,
        TimeSpan accountAge,
        int minDays)
    {
        // ── Discord role-hierarchy guard ──────────────────────────────────
        // The bot must outrank the target or BanAsync throws. On UserJoined
        // this should never trip (new joiners only have @everyone) but check
        // anyway for the edge case where a "Sticky Roles"-style bot re-applies
        // a role before our handler runs.
        var botMember = member.Guild.CurrentUser;
        var botTopPos = botMember.Roles.Max(r => r.Position);
        var memberTopPos = member.Roles.Any() ? member.Roles.Max(r => r.Position) : 0;

        if (memberTopPos >= botTopPos)
        {
            _logger.LogWarning(
                "Account-age gate cannot ban {Username} ({UserId}) — member's top role " +
                "is at or above the bot's. Falling back to alert-only for this join.",
                member.Username, member.Id);
            return AccountAgeGateOutcome.BanSkippedHierarchy;
        }

        // ── Best-effort DM before the ban ─────────────────────────────────
        // Try to give the user context, but don't let a closed DM block the
        // ban itself.
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"Hey — your Discord account is too new to join the **189th** right now " +
                $"(we require accounts at least **{minDays}** days old as an anti-raid measure), " +
                "so you've been removed and banned from the server.\n\n" +
                "This is automated and not personal. If you believe this was a mistake, reach out " +
                "to a member of leadership and they can lift the ban.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DMs disabled / blocked the bot — fine, continue with the ban.
        }

        // ── Actually ban ──────────────────────────────────────────────────
        var reason = $"Account-age gate: account {accountAge.TotalDays:F1} day(s) old, " +
                     $"threshold {minDays} day(s).";
        try
        {
            // pruneDays = 0 — a join-time ban means the account has had no
            // chance to post anything to delete. Bump it if you later want to
            // scrub messages from accounts that slip through a gap.
            // Using guild.AddBanAsync(IUser, ...) rather than member.BanAsync:
            // it has a single unambiguous signature and bans by user object,
            // so it still applies even if the member left in the interim.
            await member.Guild.AddBanAsync(
                member,
                pruneDays: 0,
                reason: reason,
                options: new RequestOptions { AuditLogReason = reason });

            _logger.LogInformation(
                "Banned {Username} ({UserId}) by account-age gate: {Reason}",
                member.Username, member.Id, reason);
            return AccountAgeGateOutcome.Banned;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // A ban applies even to a user who already left, so a 404 here is
            // unusual. Treat it the same as "user already gone" — the outcome
            // we wanted — rather than a failure.
            _logger.LogInformation(
                "Account-age gate: {Username} ({UserId}) already left before we could ban them.",
                member.Username, member.Id);
            return AccountAgeGateOutcome.UserAlreadyLeft;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Account-age gate failed to ban {Username} ({UserId})",
                member.Username, member.Id);
            return AccountAgeGateOutcome.BanFailed;
        }
    }

    /// <summary>
    /// Writes one SecurityAuditRecord row for the join + action taken. Wrapped
    /// in its own try/catch — a DB failure here is logged but never
    /// propagated, so the alert post and the ban itself remain
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
    /// Looks for a one-time age-gate exemption for this (guild, user) and, if
    /// found, deletes it and returns true. The delete IS the consumption — a
    /// single row can only be claimed once, so even the (extremely unlikely)
    /// double-join race can't be waved through twice. Wrapped in its own
    /// try/catch: a DB failure here must fall through to the normal gate path
    /// (fail closed — better to ban a pre-cleared account and have an admin
    /// re-run /allow-new-account than to silently let everyone through on a DB
    /// hiccup), so it returns false on any error.
    /// </summary>
    private async Task<bool> TryConsumeExemptionAsync(ulong guildId, ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var exemption = await db.AccountAgeGateExemptions
                .FirstOrDefaultAsync(e => e.GuildId == guildId && e.UserId == userId);

            if (exemption is null) return false;

            db.AccountAgeGateExemptions.Remove(exemption);
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Account-age gate: failed to check/consume exemption for user {UserId} in guild {GuildId}. " +
                "Falling through to the normal gate path (fail closed).",
                userId, guildId);
            return false;
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
        string? banFailureReason)
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
            AccountAgeGateOutcome.Banned                 => (Color.Red,        "🛡️ Account-Age Gate — Banned",          "Member was banned automatically."),
            AccountAgeGateOutcome.AllowedByExemption     => (Color.Green,      "🛡️ Account-Age Gate — Allowed In",      "Account is under the threshold but was pre-cleared via `/allow-new-account`. No action taken; the one-time exemption has been used up."),
            AccountAgeGateOutcome.Alerted                => (Color.Orange,     "🛡️ Account-Age Gate — Alert",           "Mode is **AlertOnly** — no action taken."),
            AccountAgeGateOutcome.UserAlreadyLeft        => (Color.LightGrey,  "🛡️ Account-Age Gate — User Left",       "Member left before the ban could be issued."),
            AccountAgeGateOutcome.BanSkippedHierarchy    => (Color.Gold,       "🛡️ Account-Age Gate — Cannot Ban",      "Bot lacks role hierarchy to ban this member. Manual review required."),
            AccountAgeGateOutcome.BanFailed              => (Color.DarkRed,    "🛡️ Account-Age Gate — Ban FAILED",      "Ban call failed. " + (banFailureReason ?? "")),
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

    /// <summary>Enforcement mode — member was successfully banned.</summary>
    Banned,

    /// <summary>Account was under the threshold but had a one-time exemption (added via /allow-new-account), so it was allowed in and the exemption consumed.</summary>
    AllowedByExemption,

    /// <summary>Member left the guild before we could ban them.</summary>
    UserAlreadyLeft,

    /// <summary>Bot lacks role hierarchy to ban this member.</summary>
    BanSkippedHierarchy,

    /// <summary>Ban call failed for an unexpected reason. See logs.</summary>
    BanFailed,
}
