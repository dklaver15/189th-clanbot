using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /allow-new-account slash command (Administrator only).
///
/// ── What it does ──
/// Pre-clears one specific Discord user to pass through the account-age gate
/// (AccountAgeGateHandler) the next time they join, even though their account
/// is younger than AccountAgeGateMinDays. It does two things:
///   1. Writes a one-time AccountAgeGateExemption row (upsert on GuildId+UserId).
///      The gate consumes that row on the user's next join and lets them in
///      instead of banning them.
///   2. Lifts any existing ban on that user, since the gate BANS (not just
///      kicks) the accounts it catches. Without the unban the person couldn't
///      rejoin at all, so clearing the exemption alone would be useless.
///
/// ── Why this exists ──
/// The age gate is an anti-raid measure that bans brand-new Discord accounts on
/// join. Every so often a real recruit genuinely has a fresh account — the
/// motivating case was a member's spouse who created their first-ever Discord
/// account minutes before joining and got auto-banned for it. This command is
/// the "she's not a bot, let her in" escape hatch, kept behind Administrator so
/// the anti-raid control isn't casually widened.
///
/// ── Target resolution ──
/// Accepts EITHER a raw numeric user_id OR the user picker. The raw-ID path is
/// the important one: the person is typically already banned (and therefore not
/// selectable in the picker), so the admin copies their ID (Developer Mode →
/// right-click → Copy User ID) and passes it here. Mirrors /purge-user.
///
/// ── Alert button ──
/// The age gate's "Banned" alert carries an "Allow in + unban" button
/// (<see cref="AllowButtonPrefix"/>) that does the same thing for that user,
/// also Administrator only.
///
/// ── One-time semantics ──
/// The exemption is consumed (deleted) by the gate on first use, so it never
/// leaves a standing hole. Re-running the command just re-adds it (upsert).
/// </summary>
public sealed class AllowNewAccountCommandHandler
{
    public const string CommandName = "allow-new-account";
    public const string AllowButtonPrefix = "agegate:allow:";

    private const string OptionUserId = "user_id";
    private const string OptionUser   = "user";
    private const string OptionNote   = "note";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AllowNewAccountCommandHandler> _logger;

    public AllowNewAccountCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<AllowNewAccountCommandHandler> logger)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Let a new account bypass the age gate and unban them if needed (Admin only)")
            .AddOption(OptionUserId, ApplicationCommandOptionType.String,
                "The member's Discord ID (Developer Mode → right-click → Copy User ID). Use this for banned members.",
                isRequired: false)
            .AddOption(OptionUser, ApplicationCommandOptionType.User,
                "Or pick a member who is still in the server", isRequired: false)
            .AddOption(OptionNote, ApplicationCommandOptionType.String,
                "Optional note for the record (e.g. \"Bravo's wife, brand-new account\")", isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
        client.ButtonExecuted       += OnButtonExecuted;
    }

    private async Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != CommandName) return;

        try
        {
            await cmd.DeferAsync(ephemeral: true);
            await HandleAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in /{Command}", CommandName);
            try
            {
                await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
            }
            catch (Exception followupEx)
            {
                _logger.LogWarning(followupEx,
                    "/{Command}: couldn't report the error back to the invoker.", CommandName);
            }
        }
    }

    private async Task HandleAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ── Permission check: Administrator only ─────────────────────────
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !invoker.GuildPermissions.Administrator)
        {
            await cmd.FollowupAsync(
                "⛔ This command is restricted to server **Administrators**.",
                ephemeral: true);
            return;
        }

        // ── Resolve the target (picker OR raw numeric ID) ────────────────
        var pickedUser = cmd.Data.Options.FirstOrDefault(o => o.Name == OptionUser)?.Value as SocketUser;
        var rawId      = cmd.Data.Options.FirstOrDefault(o => o.Name == OptionUserId)?.Value as string;
        var note       = (cmd.Data.Options.FirstOrDefault(o => o.Name == OptionNote)?.Value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(note)) note = null;

        ulong targetId;
        if (pickedUser is not null)
        {
            targetId = pickedUser.Id;
        }
        else if (string.IsNullOrWhiteSpace(rawId) || !ulong.TryParse(rawId.Trim(), out targetId))
        {
            await cmd.FollowupAsync(
                "⚠️ Pass a numeric `user_id`, or pick a `user`. For someone who's already been " +
                "banned by the age gate, use `user_id` (Developer Mode → right-click their name or " +
                "message → Copy User ID).",
                ephemeral: true);
            return;
        }

        if (targetId == guild.CurrentUser.Id)
        {
            await cmd.FollowupAsync("🤖 That's my own ID — nothing to do there.", ephemeral: true);
            return;
        }

        var targetName = pickedUser is not null
            ? ((pickedUser as IGuildUser)?.DisplayName ?? pickedUser.GlobalName ?? pickedUser.Username)
            : $"User {targetId}";

        var result = await AllowAsync(guild, targetId, targetName, invoker, note);
        if (result is not { } r)
        {
            await cmd.FollowupAsync(
                "❌ Couldn't save the exemption (database error — see bot logs). Nothing was changed.",
                ephemeral: true);
            return;
        }
        var (exemptionAlreadyExisted, banState, banError) = r;

        // ── Report ───────────────────────────────────────────────────────
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"✅ **{targetName}** (`{targetId}`) is now cleared to bypass the account-age gate on their next join.");
        if (exemptionAlreadyExisted)
            sb.AppendLine("ℹ️ An exemption for them already existed — I refreshed it.");

        sb.Append(banState switch
        {
            BanState.Lifted    => "🔓 Their existing ban has been **lifted**.\n",
            BanState.NotBanned => "ℹ️ They weren't banned, so there was nothing to unban.\n",
            _                  => $"⚠️ I couldn't lift their ban ({banError ?? "see bot logs"}). " +
                                  "You may need to remove it manually in **Server Settings → Bans**.\n",
        });

        sb.AppendLine();
        sb.AppendLine("**Next step:** send them a fresh invite link — when they rejoin, the gate will let them through and use up the exemption. It's a one-time pass, so if they don't rejoin it just sits harmlessly.");

        await cmd.FollowupAsync(sb.ToString(), ephemeral: true);
    }

    /// <summary>
    /// Upserts the one-time exemption, lifts any ban, and writes the audit row.
    /// Null when the exemption couldn't be saved (nothing was changed).
    /// </summary>
    private async Task<(bool AlreadyExisted, BanState Ban, string? BanError)?> AllowAsync(
        SocketGuild guild, ulong targetId, string targetName, SocketGuildUser invoker, string? note)
    {
        // ── 1) Upsert the one-time exemption ─────────────────────────────
        bool exemptionAlreadyExisted;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var existing = await db.AccountAgeGateExemptions
                .FirstOrDefaultAsync(e => e.GuildId == guild.Id && e.UserId == targetId);

            exemptionAlreadyExisted = existing is not null;

            if (existing is null)
            {
                db.AccountAgeGateExemptions.Add(new AccountAgeGateExemption
                {
                    GuildId         = guild.Id,
                    UserId          = targetId,
                    AddedByUserId   = invoker.Id,
                    AddedByUsername = invoker.Username,
                    Note            = note,
                    AddedAtUtc      = DateTime.UtcNow,
                });
            }
            else
            {
                // Refresh the audit fields on re-run so the record reflects the
                // latest admin/note rather than a stale first-add.
                existing.AddedByUserId   = invoker.Id;
                existing.AddedByUsername = invoker.Username;
                existing.Note            = note ?? existing.Note;
                existing.AddedAtUtc      = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "/{Command}: failed to write the exemption for {Target} in guild {Guild}.",
                CommandName, targetId, guild.Id);
            return null;
        }

        // ── 2) Lift any existing ban so they can actually rejoin ─────────
        var (banState, banError) = await TryUnbanAsync(guild, targetId);

        // ── 3) Durable audit row (mirrors the gate's own audit trail) ────
        await WriteAuditAsync(guild, targetId, targetName, invoker, note, banState);

        _logger.LogInformation(
            "/{Command} by {Invoker}: cleared {Target} ({TargetId}) through the age gate " +
            "(exemptionAlreadyExisted={Existed}, ban={BanState}). Note: {Note}",
            CommandName, invoker.Username, targetName, targetId, exemptionAlreadyExisted, banState, note ?? "(none)");

        return (exemptionAlreadyExisted, banState, banError);
    }

    private Task OnButtonExecuted(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(AllowButtonPrefix, StringComparison.Ordinal))
            return Task.CompletedTask;
        _ = HandleButtonAsync(component);
        return Task.CompletedTask;
    }

    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        try
        {
            if (!ulong.TryParse(component.Data.CustomId[AllowButtonPrefix.Length..], out var targetId)) return;

            if (component.User is not SocketGuildUser invoker || !invoker.GuildPermissions.Administrator)
            {
                await component.RespondAsync(
                    "⛔ Only server **Administrators** can let a new account in.", ephemeral: true);
                return;
            }

            await component.DeferAsync();

            var targetName = _client.GetUser(targetId)?.Username ?? $"User {targetId}";
            var result = await AllowAsync(invoker.Guild, targetId, targetName, invoker, "Allowed from the age-gate alert");
            if (result is not { } r)
            {
                await component.FollowupAsync(
                    "❌ Couldn't save the exemption (database error — see bot logs). Nothing was changed.",
                    ephemeral: true);
                return;
            }

            var status = r.Ban == BanState.Failed
                ? $"⚠️ {invoker.Mention} cleared them through the gate, but I couldn't lift the ban " +
                  $"({r.BanError ?? "see bot logs"}). Remove it in **Server Settings → Bans**, then send a fresh invite."
                : $"🔓 Allowed in by {invoker.Mention}: ban lifted, and their next join skips the age gate once. " +
                  "Send them a fresh invite.";

            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content         = status;
                p.AllowedMentions = AllowedMentions.None;
                if (r.Ban != BanState.Failed)
                    p.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Age-gate allow button {Id} failed.", component.Data.CustomId);
        }
    }

    /// <summary>
    /// Lifts a ban on the target if one exists. Returns NotBanned when there was
    /// no ban to lift (GetBanAsync returns null / RemoveBanAsync 404s), Lifted on
    /// success, and Failed with a message on any other error. Never throws.
    /// </summary>
    private async Task<(BanState State, string? Error)> TryUnbanAsync(SocketGuild guild, ulong targetId)
    {
        try
        {
            var ban = await guild.GetBanAsync(targetId);
            if (ban is null)
                return (BanState.NotBanned, null);

            await guild.RemoveBanAsync(targetId);
            return (BanState.Lifted, null);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // No such ban — the user was never banned (e.g. AlertOnly mode, or a
            // still-present member being pre-cleared). Not an error.
            return (BanState.NotBanned, null);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(ex,
                "/{Command}: missing Ban Members permission to unban {Target}.", CommandName, targetId);
            return (BanState.Failed, "I'm missing the **Ban Members** permission");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/{Command}: failed to unban {Target}.", CommandName, targetId);
            return (BanState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Writes a SecurityAuditRecord row so the exemption + unban shows up in
    /// /security-audit and /timeline alongside the gate's own ban/alert rows.
    /// Its own try/catch — a DB hiccup here must not fail the command, whose
    /// primary effects (exemption + unban) have already been applied.
    /// </summary>
    private async Task WriteAuditAsync(
        SocketGuild guild, ulong targetId, string targetName,
        SocketGuildUser invoker, string? note, BanState banState)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var details = $"Exemption added by {invoker.Username} ({invoker.Id}). " +
                          $"Unban: {banState}." +
                          (note is not null ? $" Note: {note}" : "");

            db.SecurityAuditRecords.Add(new SecurityAuditRecord
            {
                GuildId     = guild.Id,
                Feature     = "AccountAgeGate",
                Action      = "ExemptionAdded",
                UserId      = targetId,
                Username    = targetName,
                DisplayName = targetName,
                ChannelId   = null,
                Details     = details,
                OccurredAt  = DateTime.UtcNow,
                ErrorMessage = banState == BanState.Failed ? "Unban failed — see bot logs." : null,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "/{Command}: failed to write SecurityAuditRecord for exemption of {Target}.",
                CommandName, targetId);
        }
    }

    private enum BanState
    {
        /// <summary>An existing ban was found and removed.</summary>
        Lifted,
        /// <summary>No ban existed, so nothing was removed.</summary>
        NotBanned,
        /// <summary>An unban was attempted but failed (permissions / API error).</summary>
        Failed,
    }
}
