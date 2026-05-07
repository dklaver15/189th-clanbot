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
/// Handles /invite create and /invite assign — the Phase 1 surface for
/// labeling Discord invites so InviteAttributionService can credit joins
/// to the right source ("Website", "Facebook", a recruiter's name, etc).
///
/// Both commands respond ephemerally so neither the link nor the label
/// gets posted to a public channel; only the invoking officer sees the
/// reply.
///
/// ── Permission model ──
/// Gated by BotConfig.InviteManagementMinRank, a separate config value
/// from PromoteDemoteMinRank so recruiters can manage their own invite
/// links without inheriting promote/demote privileges. Admins bypass.
///
/// ── /invite create ──
/// Creates a fresh Discord invite via the Discord API in the channel the
/// command was run in (sensible default — officers usually want recruits
/// to land where they were invited from). The optional `expires` and
/// `max-uses` arguments are passed straight through to Discord's API as
/// max_age / max_uses; Discord enforces both natively. We do NOT run any
/// expiration timer on our side. We cache the values for fast /invite
/// list rendering only.
///
/// ── /invite assign ──
/// Retroactively labels an invite that already exists — useful for the
/// vanity URL (which doesn't get auto-seeded), for invites created via
/// the Discord UI before the bot was wired, or for relabeling a
/// previously-mislabeled link. Doesn't change the invite itself, only
/// our InviteSource row.
/// </summary>
public class InviteCommandHandler
{
    private readonly IServiceProvider _services;
    private readonly InviteCacheService _cache;
    private readonly ILogger<InviteCommandHandler> _logger;
    private readonly BotConfig _config;

    public InviteCommandHandler(
        IServiceProvider services,
        InviteCacheService cache,
        ILogger<InviteCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _cache    = cache;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not "invite") return;

        // Subcommand routing. Discord slash-command groups deliver the
        // subcommand as the first option; its own options live one level
        // deeper. We unwrap that here and dispatch to the right handler.
        var sub = command.Data.Options.FirstOrDefault();
        if (sub is null)
        {
            await command.RespondAsync("Missing subcommand. Try `/invite create` or `/invite assign`.",
                ephemeral: true);
            return;
        }

        try
        {
            switch (sub.Name)
            {
                case "create":
                    await HandleCreate(command, sub);
                    break;
                case "assign":
                    await HandleAssign(command, sub);
                    break;
                default:
                    await command.RespondAsync($"Unknown subcommand `{sub.Name}`.", ephemeral: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /invite {Sub}", sub.Name);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    // ── /invite create ──────────────────────────────────────────────
    //
    // No rank gate. Discord's native "Create Invite" channel permission
    // does the gating: when the bot calls CreateInviteAsync below, Discord
    // checks that the *invoking user's* permissions allow invite creation
    // in the target channel and rejects the API call if not. So the
    // server's existing channel permissions ARE the access control here.
    // If you want to restrict who can run this, configure the channel's
    // permissions in Discord — don't add a second layer in code.

    private async Task HandleCreate(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null)
        {
            await command.FollowupAsync("Couldn't resolve your guild membership. Try again.", ephemeral: true);
            return;
        }

        // Channel resolution: invite is created in the channel the command
        // was run in. Recruits usually want to land where they were invited
        // from, and forcing the officer to pick a channel adds friction for
        // no real benefit on a clan-sized server.
        if (command.Channel is not SocketTextChannel sourceChannel)
        {
            await command.FollowupAsync(
                "I can only create invites for text channels. Run this command in the channel you want recruits to land in.",
                ephemeral: true);
            return;
        }

        // Belt-and-suspenders permission check: Discord's API will also
        // reject the CreateInviteAsync call below if the caller lacks the
        // permission, but checking up-front lets us return a clear error
        // message instead of bubbling Discord's HTTP exception up.
        var callerChannelPerms = caller.GetPermissions(sourceChannel);
        if (!callerChannelPerms.CreateInstantInvite && !caller.GuildPermissions.Administrator)
        {
            await command.FollowupAsync(
                $"You don't have permission to create invites in <#{sourceChannel.Id}>. Ask a channel admin if you think this is wrong.",
                ephemeral: true);
            return;
        }

        var label = sub.Options.FirstOrDefault(o => o.Name == "label")?.Value as string;
        if (string.IsNullOrWhiteSpace(label))
        {
            await command.FollowupAsync("`label` is required.", ephemeral: true);
            return;
        }
        label = label.Trim();
        if (label.Length > 80)
        {
            await command.FollowupAsync("`label` must be 80 characters or fewer.", ephemeral: true);
            return;
        }

        // Reject sentinel values to keep the data model honest. If a label
        // collides with an attribution sentinel, /invite stats can't tell
        // joins-via-Vanity from joins-via-some-link-the-officer-named-Vanity.
        if (IsReservedLabel(label))
        {
            await command.FollowupAsync(
                $"`{label}` is a reserved label used internally for attribution. Pick something else.",
                ephemeral: true);
            return;
        }

        // Optional: expiration. We accept either a friendly duration string
        // ("7d", "12h", "30m") OR a raw integer count of seconds. Discord's
        // API takes seconds. 0 = never expires.
        int? maxAgeSeconds = null;
        var expiresOpt = sub.Options.FirstOrDefault(o => o.Name == "expires")?.Value as string;
        if (!string.IsNullOrWhiteSpace(expiresOpt))
        {
            if (!TryParseDuration(expiresOpt, out var seconds))
            {
                await command.FollowupAsync(
                    "Couldn't parse `expires`. Use values like `7d`, `12h`, `30m`, or `0` for never.",
                    ephemeral: true);
                return;
            }
            maxAgeSeconds = seconds;
        }

        // Optional: max-uses. Discord cap is 100 (or 0 for unlimited).
        int? maxUses = null;
        var maxUsesOpt = sub.Options.FirstOrDefault(o => o.Name == "max-uses")?.Value;
        if (maxUsesOpt is not null)
        {
            // Discord delivers integer options as long; cast safely.
            var asLong = Convert.ToInt64(maxUsesOpt);
            if (asLong < 0 || asLong > 100)
            {
                await command.FollowupAsync(
                    "`max-uses` must be between 0 (unlimited) and 100. Discord caps single invites at 100 uses.",
                    ephemeral: true);
                return;
            }
            maxUses = (int)asLong;
        }

        var notes = sub.Options.FirstOrDefault(o => o.Name == "notes")?.Value as string;

        // ── Create on Discord ────────────────────────────────────────
        IInviteMetadata invite;
        try
        {
            // isUnique:true so we don't end up sharing a code with a previous
            // invite that happened to have the same parameters. isTemporary
            // is left false (default) — temporary invites kick members who
            // disconnect before being assigned a role, which would conflict
            // with our onboarding flow.
            invite = await sourceChannel.CreateInviteAsync(
                maxAge:    maxAgeSeconds,
                maxUses:   maxUses,
                isTemporary: false,
                isUnique:  true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord rejected CreateInviteAsync for {Channel}", sourceChannel.Name);
            await command.FollowupAsync(
                $"Discord refused to create the invite: {ex.Message}",
                ephemeral: true);
            return;
        }

        // ── Persist InviteSource row ─────────────────────────────────
        var now = DateTime.UtcNow;
        DateTime? expiresAt = (maxAgeSeconds.HasValue && maxAgeSeconds.Value > 0)
            ? now.AddSeconds(maxAgeSeconds.Value)
            : null;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.InviteSources.Add(new InviteSource
            {
                GuildId             = command.GuildId.Value,
                Code                = invite.Code,
                Label               = label,
                IsVanity            = false,
                IsActive            = true,
                CreatedByDiscordId  = caller.Id,
                CreatedByUsername   = caller.Username,
                CreatedAt           = now,
                DiscordCreatedAt    = invite.CreatedAt?.UtcDateTime,
                ExpiresAt           = expiresAt,
                MaxUses             = (maxUses.HasValue && maxUses.Value > 0) ? maxUses : null,
                ChannelId           = sourceChannel.Id,
                Notes               = notes ?? string.Empty,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist InviteSource row for {Code}", invite.Code);
            // We've already created the Discord invite at this point. Tell
            // the officer the link works but our tracking row didn't write,
            // and they should /invite assign it manually.
            await command.FollowupAsync(
                $"Created the Discord invite **{invite.Url}** but failed to save the label. " +
                $"Run `/invite assign code:{invite.Code} label:{label}` to retry the labeling.",
                ephemeral: true);
            return;
        }

        // Pre-seed the cache so the next UserJoined diff sees a baseline
        // for this code. Idempotent vs. the InviteCreated handler that may
        // run concurrently.
        _cache.SetUses(invite.Code, invite.Uses ?? 0);

        // ── Build a clear officer-facing response ────────────────────
        var expiresFragment = expiresAt is null ? "never" : DiscordTimestamp(expiresAt.Value, 'R');
        var usesFragment = (maxUses.HasValue && maxUses.Value > 0) ? maxUses.Value.ToString() : "unlimited";

        var embed = new EmbedBuilder()
            .WithTitle($"📨 Tracked invite created — {label}")
            .WithColor(Color.Blue)
            .WithDescription($"**{invite.Url}**")
            .AddField("Channel",  $"<#{sourceChannel.Id}>", inline: true)
            .AddField("Expires",  expiresFragment,         inline: true)
            .AddField("Max uses", usesFragment,            inline: true)
            .WithFooter($"Code: {invite.Code} • Created by {caller.Username}")
            .WithCurrentTimestamp()
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);

        _logger.LogInformation(
            "{Caller} created tracked invite {Code} (label='{Label}', channel={Channel}, expires={Expires}, maxUses={MaxUses})",
            caller.Username, invite.Code, label, sourceChannel.Name,
            expiresAt?.ToString("u") ?? "never", maxUses?.ToString() ?? "unlimited");
    }

    // ── /invite assign ──────────────────────────────────────────────

    private async Task HandleAssign(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasInvitePermission(caller))
        {
            await command.FollowupAsync(
                $"You need {_config.InviteManagementMinRank}+ to manage tracked invites.",
                ephemeral: true);
            return;
        }

        var code = (sub.Options.FirstOrDefault(o => o.Name == "code")?.Value as string)?.Trim();
        var label = (sub.Options.FirstOrDefault(o => o.Name == "label")?.Value as string)?.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            await command.FollowupAsync("`code` is required.", ephemeral: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(label))
        {
            await command.FollowupAsync("`label` is required.", ephemeral: true);
            return;
        }
        if (label.Length > 80)
        {
            await command.FollowupAsync("`label` must be 80 characters or fewer.", ephemeral: true);
            return;
        }
        if (IsReservedLabel(label))
        {
            await command.FollowupAsync(
                $"`{label}` is a reserved label used internally for attribution. Pick something else.",
                ephemeral: true);
            return;
        }

        var notes = sub.Options.FirstOrDefault(o => o.Name == "notes")?.Value as string;

        // ── Verify the invite actually exists ─────────────────────────
        // We support assigning to: (a) the vanity URL, (b) a regular invite
        // currently visible in GetInvitesAsync. Anything else is rejected
        // — no point in labeling a code that Discord doesn't recognize.
        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Couldn't resolve the guild. Try again from a guild channel.", ephemeral: true);
            return;
        }

        bool isVanity = false;
        IInviteMetadata? matched = null;

        try
        {
            var vanity = await guild.GetVanityInviteAsync();
            if (vanity is not null && string.Equals(vanity.Code, code, StringComparison.Ordinal))
            {
                isVanity = true;
                matched  = vanity;
            }
        }
        catch
        {
            // Guild has no vanity URL — ignore and check regular invites.
        }

        if (matched is null)
        {
            try
            {
                var invites = await guild.GetInvitesAsync();
                matched = invites.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch invites for /invite assign");
                await command.FollowupAsync("Couldn't fetch the invite list from Discord. Try again.", ephemeral: true);
                return;
            }
        }

        if (matched is null)
        {
            await command.FollowupAsync(
                $"Discord doesn't recognize invite code `{code}`. Make sure it's the part after `discord.gg/` and that the invite hasn't expired.",
                ephemeral: true);
            return;
        }

        // ── Upsert the InviteSource row ───────────────────────────────
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var existing = await db.InviteSources
                .FirstOrDefaultAsync(s => s.GuildId == command.GuildId.Value && s.Code == code);

            string oldLabel = existing?.Label ?? string.Empty;
            bool isUpdate = existing is not null;

            int? cachedMaxUses = (matched.MaxUses ?? 0) > 0 ? matched.MaxUses : null;
            DateTime? cachedExpiresAt =
                (matched.MaxAge ?? 0) > 0 && matched.CreatedAt.HasValue
                    ? matched.CreatedAt.Value.UtcDateTime.AddSeconds(matched.MaxAge!.Value)
                    : null;

            if (existing is null)
            {
                db.InviteSources.Add(new InviteSource
                {
                    GuildId             = command.GuildId.Value,
                    Code                = code,
                    Label               = label,
                    IsVanity            = isVanity,
                    IsActive            = true,
                    CreatedByDiscordId  = caller.Id,
                    CreatedByUsername   = caller.Username,
                    CreatedAt           = DateTime.UtcNow,
                    DiscordCreatedAt    = isVanity ? null : matched.CreatedAt?.UtcDateTime,
                    ExpiresAt           = cachedExpiresAt,
                    MaxUses             = cachedMaxUses,
                    ChannelId           = isVanity ? null : matched.ChannelId,
                    Notes               = notes ?? string.Empty,
                });
            }
            else
            {
                existing.Label    = label;
                existing.IsActive = true;
                existing.IsVanity = isVanity;
                if (!string.IsNullOrWhiteSpace(notes)) existing.Notes = notes!;
                // Refresh cached display copies in case Discord's values drifted.
                existing.MaxUses    = cachedMaxUses;
                existing.ExpiresAt  = cachedExpiresAt;
                existing.ChannelId  = isVanity ? existing.ChannelId : matched.ChannelId;
            }

            await db.SaveChangesAsync();

            // Make sure the cache knows about the code so the next UserJoined
            // diff sees a baseline. ReplaceAll-style hydration would be too
            // broad; SetUses with the current count is targeted.
            _cache.SetUses(code, matched.Uses ?? 0);

            var summary = isUpdate
                ? $"Relabeled `{code}` from **{oldLabel}** → **{label}**."
                : $"Labeled `{code}` as **{label}**.";

            await command.FollowupAsync(
                $"✅ {summary}{(isVanity ? " (vanity URL)" : "")}",
                ephemeral: true);

            _logger.LogInformation(
                "{Caller} {Action} invite {Code} as '{Label}'",
                caller.Username, isUpdate ? "relabeled" : "labeled", code, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert InviteSource for /invite assign");
            await command.FollowupAsync("Failed to save the label. Check the bot logs.", ephemeral: true);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private bool HasInvitePermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minRank = _config.InviteManagementMinRank;
        var minIndex = rankRoles.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));

        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    private static bool IsReservedLabel(string label) =>
        label.Equals(InviteAttributionService.LabelVanity,       StringComparison.OrdinalIgnoreCase) ||
        label.Equals(InviteAttributionService.LabelUnknown,      StringComparison.OrdinalIgnoreCase) ||
        label.Equals(InviteAttributionService.LabelAmbiguous,    StringComparison.OrdinalIgnoreCase) ||
        label.Equals(InviteAttributionService.LabelUnattributed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse simple duration strings: "7d", "12h", "30m", "3600s", or a raw
    /// integer count of seconds. "0" or "never" → 0 (Discord's "no expiration"
    /// sentinel). Returns false on any other format.
    /// </summary>
    private static bool TryParseDuration(string input, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim().ToLowerInvariant();
        if (trimmed == "0" || trimmed == "never")
        {
            seconds = 0;
            return true;
        }

        // Plain integer = seconds
        if (int.TryParse(trimmed, out var rawSeconds) && rawSeconds >= 0)
        {
            seconds = rawSeconds;
            return true;
        }

        // <number><unit> form
        if (trimmed.Length < 2) return false;
        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];
        if (!int.TryParse(numberPart, out var n) || n < 0) return false;

        seconds = unit switch
        {
            's' => n,
            'm' => n * 60,
            'h' => n * 3600,
            'd' => n * 86400,
            'w' => n * 604800,
            _   => -1,
        };

        return seconds >= 0;
    }

    private static string DiscordTimestamp(DateTime utc, char style) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:{style}>";

    /// <summary>
    /// Produces the slash-command shape for /invite. Lives on the handler so
    /// the option list stays next to the code that consumes it — same pattern
    /// as CleanupCalendarDupesCommandHandler.BuildCommand().
    /// </summary>
    public static SlashCommandProperties BuildCommand()
    {
        return new SlashCommandBuilder()
            .WithName("invite")
            .WithDescription("Manage tracked invite links for join attribution")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("create")
                .WithDescription("Create a new tracked invite link with a label")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("label", ApplicationCommandOptionType.String,
                    "Friendly label, e.g. \"Website\", \"Facebook\"", isRequired: true)
                .AddOption("expires", ApplicationCommandOptionType.String,
                    "How long the link lives. e.g. \"7d\", \"12h\", \"0\" for never. Default: never.",
                    isRequired: false)
                .AddOption("max-uses", ApplicationCommandOptionType.Integer,
                    "Cap on how many people can use it (0 = unlimited, max 100). Default: unlimited.",
                    isRequired: false)
                .AddOption("notes", ApplicationCommandOptionType.String,
                    "Optional officer note for context (\"Q2 recruitment push\")",
                    isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("assign")
                .WithDescription("Label an invite that already exists (e.g. the vanity URL or a UI-created invite)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("code", ApplicationCommandOptionType.String,
                    "The invite code (the part after discord.gg/)", isRequired: true)
                .AddOption("label", ApplicationCommandOptionType.String,
                    "Friendly label, e.g. \"Vanity\", \"Website\"", isRequired: true)
                .AddOption("notes", ApplicationCommandOptionType.String,
                    "Optional officer note", isRequired: false))
            .Build();
    }
}
