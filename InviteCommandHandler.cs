using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the full /invite command surface: create, assign (Phase 1),
/// list, info, stats, revoke (Phase 2), edit-notes (Phase 3). All
/// responses are ephemeral so neither links nor labels leak to public
/// channels. The /invite info command supports button-based pagination
/// for invites with many attributed joins; pagination buttons stay
/// clickable indefinitely (Discord doesn't expire button interactions).
///
/// ── Permission model ──
/// Per-subcommand, intentionally split:
///   • create  — Discord's native "Create Invite" channel permission only,
///               so anyone who can already make an invite via the Discord
///               UI can also make a labeled one.
///   • assign  — InviteManagementMinRank (default 2ndLT). Relabeling can
///               rewrite labels other officers depend on.
///   • list    — open to anyone. Read-only registry.
///   • info    — InviteManagementMinRank (default 2ndLT). Surfaces
///               individual recruit attribution which is more sensitive
///               than aggregate stats.
///   • stats   — open to anyone. Aggregate counts only, no individual
///               recruit names.
///   • revoke  — InviteManagementMinRank (default 2ndLT) AND requires
///               confirm:true. Destructive — kills the live link.
/// Admins bypass the rank gates entirely.
///
/// ── /invite create ──
/// Creates a fresh Discord invite via the Discord API in the channel the
/// command was run in. The optional `expires` and `max-uses` arguments
/// are passed straight through to Discord's API as max_age / max_uses;
/// Discord enforces both natively. We do NOT run any expiration timer on
/// our side. We cache the values for fast /invite list rendering only.
///
/// ── /invite assign ──
/// Retroactively labels an invite that already exists — the vanity URL
/// (which doesn't get auto-seeded), invites created via the Discord UI
/// before the bot was wired, or relabeling a previously-mislabeled link.
/// Doesn't change the invite itself, only our InviteSource row.
/// Reserved sentinel labels ("Vanity", "Unknown", "Ambiguous",
/// "Unattributed") are blocked for non-vanity invites to prevent
/// collision with attribution-time fallbacks; the vanity URL itself is
/// exempt from this check (it's the legitimate use of "Vanity").
///
/// ── /invite list ──
/// Tabular registry of every tracked invite plus any unlabeled Discord
/// invites the bot can see. Three-key sort: uses descending → label A→Z
/// → creation date oldest-first. Inactive (revoked/expired) sources are
/// included; vanity, unlabeled, and revoked rows get a trailing flag tag.
///
/// ── /invite info ──
/// Drill-down on a single code: source metadata + the most recent N
/// attributed joins from InviteJoin. Default 25 joins, max 50.
///
/// ── /invite stats ──
/// Aggregate counts in a chosen window (7d / 30d / 90d / all). Two
/// sections: joins by label, top member referrers. Mirrors what the
/// weekly briefing surfaces but on demand.
///
/// ── /invite revoke ──
/// Calls Discord's delete-invite API. The InviteDeleted gateway event
/// (handled by InviteAttributionService) flips InviteSource.IsActive to
/// false — single deactivation flow for both manual revocation here and
/// natural expiration. Historical InviteJoin rows keep their
/// LabelSnapshot, so attribution stays intact forever.
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
        // Pagination buttons on /invite info responses route through here.
        // We only act on customIds starting with our prefix; everything else
        // is silently ignored so other handlers' buttons aren't disturbed.
        client.ButtonExecuted += HandleButtonAsync;
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
            await command.RespondAsync(
                "Missing subcommand. Try `/invite create`, `/invite assign`, `/invite edit-notes`, `/invite list`, `/invite info`, `/invite stats`, or `/invite revoke`.",
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
                case "edit-notes":
                    await HandleEditNotes(command, sub);
                    break;
                case "list":
                    await HandleList(command, sub);
                    break;
                case "info":
                    await HandleInfo(command, sub);
                    break;
                case "stats":
                    await HandleStats(command, sub);
                    break;
                case "revoke":
                    await HandleRevoke(command, sub);
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

        // /invite create always produces a non-vanity invite, so the
        // reserved-label check applies unconditionally here. (The vanity
        // exemption lives in /invite assign — see the comment there.)
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

        // NOTE: reserved-label check moved below the vanity-detection block.
        // The vanity URL is the legitimate use of the label "Vanity" — it
        // must be exempt from the collision guard. For non-vanity invites
        // the check still applies (preventing a regular invite from being
        // mislabeled with a sentinel name that would scramble attribution
        // stats grouping).

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

        // Reserved-label check: blocked for non-vanity invites only. The
        // vanity URL legitimately *is* the "Vanity" label — exempting it
        // here lets officers label discord.gg/<slug> with the natural
        // name. For all other invites, blocking sentinel collisions still
        // matters.
        if (!isVanity && IsReservedLabel(label))
        {
            await command.FollowupAsync(
                $"`{label}` is a reserved label used internally for attribution. Pick something else.",
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
                    // Vanity URL belongs to the server, not a person — override
                    // the snapshot so /invite list and /invite info don't
                    // attribute it to whichever officer ran /invite assign
                    // first. Clearing the ID also prevents a <@id> mention in
                    // /invite info pointing at the wrong person.
                    CreatedByDiscordId  = isVanity ? null : (ulong?)caller.Id,
                    CreatedByUsername   = isVanity ? "DISCORD" : caller.Username,
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

                // Same vanity override as the create path. Also serves as a
                // self-heal for any vanity row that was snapshotted before
                // this rule was in place — re-running /invite assign on the
                // vanity URL will clean up the creator fields.
                if (isVanity)
                {
                    existing.CreatedByDiscordId = null;
                    existing.CreatedByUsername  = "DISCORD";
                }
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

    // ── /invite list ────────────────────────────────────────────────
    //
    // Open to anyone — it's a read-only registry. Renders a code-block
    // table for monospaced alignment so officers can scan rows quickly.
    // If the result exceeds Discord's 4096-character description budget,
    // we split across multiple followup messages.

    private const int InviteListDescriptionBudget = 3800; // headroom under 4096

    private async Task HandleList(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Couldn't resolve the guild.", ephemeral: true);
            return;
        }

        // Parse the filter choice. Default to "active" — the day-to-day
        // case is "what's live right now"; revoked invites are useful for
        // audits but rarely the thing you want to scan past every time.
        var filter = (sub.Options.FirstOrDefault(o => o.Name == "filter")?.Value as string)?.Trim().ToLowerInvariant()
                     ?? "active";
        if (filter is not ("active" or "inactive" or "all"))
        {
            await command.FollowupAsync(
                "`filter` must be `active`, `inactive`, or `all`.", ephemeral: true);
            return;
        }

        // ── Pull all the data in parallel ───────────────────────────
        IReadOnlyCollection<IInviteMetadata> liveInvites;
        IInviteMetadata? vanity = null;
        try
        {
            liveInvites = await guild.GetInvitesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch invites for /invite list");
            await command.FollowupAsync("Couldn't fetch the invite list from Discord. Try again.", ephemeral: true);
            return;
        }

        // Filter out invites created by the Disboard bot. Disboard's /bump
        // flow auto-creates a fresh invite each time the server is bumped
        // to its directory listing, which means up to ~12 Disboard-owned
        // invites can accumulate per day. They aren't real recruitment
        // links any officer would pick up and share, just operational
        // artifacts. Identified by Inviter.Id matching the well-known
        // Disboard bot ID we already track for BumpReminderHandler.
        // Pairs with the matching skip in
        // InviteAttributionService.AttributeAsync — joins via Disboard
        // codes get recorded as Unattributed rather than attributed
        // through to a Disboard-owned code as "Unknown".
        liveInvites = liveInvites
            .Where(i => i.Inviter?.Id != _config.DisboardBotId)
            .ToList();

        try { vanity = await guild.GetVanityInviteAsync(); }
        catch { /* no vanity URL configured — that's fine */ }

        List<InviteSource> sources;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            sources = await db.InviteSources
                .Where(s => s.GuildId == command.GuildId.Value)
                .ToListAsync();
        }

        // ── Merge into a unified row set ────────────────────────────
        // Each row represents one invite with whatever data we have:
        // - Real label from InviteSource if present, else "(unlabeled)"
        // - Live use count from Discord if invite still exists, else
        //   InviteSource's last-known state (for revoked/expired)
        // - Status flags: vanity, unlabeled, revoked
        var sourcesByCode = sources.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var rows = new List<InviteListRow>();

        // Pass 1: every live regular invite Discord knows about
        foreach (var live in liveInvites)
        {
            // Skip invites created by ignored bots (e.g. Disboard's /bump
            // flow). They're operational noise, not recruitment data, and
            // they'd otherwise clutter /invite list with rows officers
            // can't act on. Same filter that InviteAttributionService
            // applies on the attribution side, kept consistent here.
            if (live.Inviter is not null && _config.IsIgnoredInviter(live.Inviter.Id))
                continue;

            sourcesByCode.TryGetValue(live.Code, out var src);
            rows.Add(new InviteListRow(
                Code:        live.Code,
                Label:       src?.Label ?? "(unlabeled)",
                Uses:        live.Uses ?? 0,
                MaxUses:     (live.MaxUses ?? 0) > 0 ? live.MaxUses : null,
                ExpiresAt:   ComputeExpiresAt(live),
                IsVanity:    false,
                IsLabeled:   src is not null,
                IsRevoked:   false,
                CreatedAt:   src?.CreatedAt ?? live.CreatedAt?.UtcDateTime ?? DateTime.UtcNow,
                CreatedBy:   ResolveCreatorDisplayName(src, live.Inviter)));
        }

        // Pass 2: vanity (separate API; never appears in liveInvites)
        if (vanity is not null)
        {
            sourcesByCode.TryGetValue(vanity.Code, out var vanitySrc);
            rows.Add(new InviteListRow(
                Code:        vanity.Code,
                Label:       vanitySrc?.Label ?? "(unlabeled)",
                Uses:        vanity.Uses ?? 0,
                MaxUses:     null, // vanity has no use cap
                ExpiresAt:   null, // vanity never expires
                IsVanity:    true,
                IsLabeled:   vanitySrc is not null,
                IsRevoked:   false,
                CreatedAt:   vanitySrc?.CreatedAt ?? DateTime.UtcNow,
                // Vanity invites have no Inviter on the Discord API — they
                // belong to the server, not a user. Fall back to the
                // InviteSource snapshot (whoever ran /invite assign on the
                // vanity URL) or "(unknown)".
                CreatedBy:   ResolveCreatorDisplayName(vanitySrc, liveInviter: null)));
        }

        // Pass 3: InviteSource rows that no live invite matched (revoked/expired)
        var liveCodes = liveInvites.Select(i => i.Code)
            .Concat(vanity is null ? Array.Empty<string>() : new[] { vanity.Code })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var src in sources.Where(s => !liveCodes.Contains(s.Code)))
        {
            // Defensive: same ignored-inviter check as Pass 1, applied to
            // the source-row's stored creator ID. In practice this never
            // hits today (we don't auto-create source rows for Disboard's
            // bump invites) but it keeps the filter consistent if a future
            // /invite assign ever labels one.
            if (src.CreatedByDiscordId is ulong srcInviterId && _config.IsIgnoredInviter(srcInviterId))
                continue;

            rows.Add(new InviteListRow(
                Code:        src.Code,
                Label:       src.Label,
                Uses:        0, // we don't store cumulative uses; Discord's gone
                MaxUses:     src.MaxUses,
                ExpiresAt:   src.ExpiresAt,
                IsVanity:    src.IsVanity,
                IsLabeled:   true,
                IsRevoked:   true,
                CreatedAt:   src.CreatedAt,
                // No live invite to fetch Inviter from — only the snapshot.
                CreatedBy:   ResolveCreatorDisplayName(src, liveInviter: null)));
        }

        if (rows.Count == 0)
        {
            await command.FollowupAsync("No invites found in this server.", ephemeral: true);
            return;
        }

        // Apply the active/inactive filter. We capture the unfiltered total
        // first so the summary line can show "filtered from N total" — that
        // signals to the user that the default `active` filter may be hiding
        // rows they'd otherwise see, without them having to remember the
        // filter exists.
        int totalUnfiltered = rows.Count;
        rows = filter switch
        {
            "active"   => rows.Where(r => !r.IsRevoked).ToList(),
            "inactive" => rows.Where(r =>  r.IsRevoked).ToList(),
            _          => rows,
        };

        if (rows.Count == 0)
        {
            var emptyMsg = filter switch
            {
                "active"   => $"No active invites. ({totalUnfiltered} revoked — try `filter:inactive` or `filter:all`.)",
                "inactive" => $"No revoked invites yet. ({totalUnfiltered} active — try `filter:active` or `filter:all`.)",
                _          => "No invites found in this server.",
            };
            await command.FollowupAsync(emptyMsg, ephemeral: true);
            return;
        }

        // ── Sort: uses desc → label asc → creation asc ──────────────
        // Three keys because at zero-use volumes there's a long tail and
        // alphabetical-by-label is more useful than chronological as the
        // tiebreaker. Creation date is the final tiebreaker for invites
        // sharing both use count and label.
        rows.Sort((a, b) =>
        {
            int c = b.Uses.CompareTo(a.Uses);                                   // uses desc
            if (c != 0) return c;
            c = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase); // label asc
            if (c != 0) return c;
            return a.CreatedAt.CompareTo(b.CreatedAt);                          // creation asc
        });

        // ── Render to a code-block table ────────────────────────────
        // Compute column widths from the data so labels and codes don't
        // get truncated harder than needed. Cap label at 24 chars and
        // creator at 18 chars to keep the table from going extreme.
        const int LabelHardCap   = 24;
        const int CreatorHardCap = 18;
        int codeWidth    = Math.Max("CODE".Length,  rows.Max(r => r.Code.Length));
        int labelWidth   = Math.Max("LABEL".Length, Math.Min(rows.Max(r => r.Label.Length), LabelHardCap));
        int usesWidth    = Math.Max("USES".Length,  rows.Max(r => FormatUses(r).Length));
        int expWidth     = Math.Max("EXPIRES".Length, rows.Max(r => FormatExpires(r).Length));
        int creatorWidth = Math.Max("CREATED BY".Length, Math.Min(rows.Max(r => r.CreatedBy.Length), CreatorHardCap));

        var header = $"{"CODE".PadRight(codeWidth)}  {"LABEL".PadRight(labelWidth)}  {"USES".PadRight(usesWidth)}  {"EXPIRES".PadRight(expWidth)}  CREATED BY";
        var divider = new string('─', header.Length);

        var pages = new List<StringBuilder>();
        var current = new StringBuilder();
        current.Append("```\n").Append(header).Append('\n').Append(divider).Append('\n');

        foreach (var row in rows)
        {
            var labelTrunc = row.Label.Length > LabelHardCap
                ? row.Label[..(LabelHardCap - 1)] + "…"
                : row.Label;
            var creatorTrunc = row.CreatedBy.Length > CreatorHardCap
                ? row.CreatedBy[..(CreatorHardCap - 1)] + "…"
                : row.CreatedBy;

            var line =
                $"{row.Code.PadRight(codeWidth)}  " +
                $"{labelTrunc.PadRight(labelWidth)}  " +
                $"{FormatUses(row).PadRight(usesWidth)}  " +
                $"{FormatExpires(row).PadRight(expWidth)}  " +
                $"{creatorTrunc}\n";

            // If adding this line would push the page past Discord's
            // description limit, close the current code block and start a
            // new page. Each page must end with ``` and the next must
            // open with ``` to keep the monospace formatting.
            if (current.Length + line.Length + 4 > InviteListDescriptionBudget)
            {
                current.Append("```");
                pages.Add(current);
                current = new StringBuilder();
                current.Append("```\n").Append(header).Append('\n').Append(divider).Append('\n');
            }
            current.Append(line);
        }
        current.Append("```");
        pages.Add(current);

        // ── Send pages ──────────────────────────────────────────────
        // Summary line tailored to the filter so we don't show meaningless
        // sub-counts (e.g. "0 revoked" under filter:active is just noise).
        // When filtered, append "(filtered from N total)" so the user
        // knows there's more they're not seeing without expanding the filter.
        var summary = filter switch
        {
            "active" =>
                $"**{rows.Count}** active invite(s) — " +
                $"{rows.Count(r => r.IsLabeled)} labeled, " +
                $"{rows.Count(r => !r.IsLabeled)} unlabeled" +
                (totalUnfiltered > rows.Count ? $" _(filtered from {totalUnfiltered} total)_" : ""),
            "inactive" =>
                $"**{rows.Count}** revoked invite(s)" +
                (totalUnfiltered > rows.Count ? $" _(filtered from {totalUnfiltered} total)_" : ""),
            _ =>
                $"**{rows.Count}** total invite(s) — " +
                $"{rows.Count(r => r.IsLabeled && !r.IsRevoked)} labeled active, " +
                $"{rows.Count(r => !r.IsLabeled)} unlabeled, " +
                $"{rows.Count(r => r.IsRevoked)} revoked",
        };

        if (pages.Count == 1)
        {
            await command.FollowupAsync($"{summary}\n{pages[0]}", ephemeral: true);
        }
        else
        {
            await command.FollowupAsync($"{summary}\n_(showing across {pages.Count} pages)_\n{pages[0]}", ephemeral: true);
            for (int i = 1; i < pages.Count; i++)
            {
                await command.FollowupAsync($"_Page {i + 1}/{pages.Count}_\n{pages[i]}", ephemeral: true);
            }
        }

        _logger.LogInformation(
            "{Caller} ran /invite list filter={Filter} — {Total} rows ({Labeled} labeled, {Unlabeled} unlabeled, {Revoked} revoked, {Hidden} hidden by filter)",
            command.User.Username, filter, rows.Count,
            rows.Count(r => r.IsLabeled && !r.IsRevoked),
            rows.Count(r => !r.IsLabeled),
            rows.Count(r => r.IsRevoked),
            totalUnfiltered - rows.Count);
    }

    private static DateTime? ComputeExpiresAt(IInviteMetadata invite)
    {
        if ((invite.MaxAge ?? 0) <= 0 || !invite.CreatedAt.HasValue) return null;
        return invite.CreatedAt.Value.UtcDateTime.AddSeconds(invite.MaxAge!.Value);
    }

    private static string FormatUses(InviteListRow r) =>
        r.MaxUses is int cap ? $"{r.Uses}/{cap}" : $"{r.Uses}";

    private static string FormatExpires(InviteListRow r)
    {
        if (r.IsRevoked) return "—";
        if (r.ExpiresAt is null) return "never";

        var delta = r.ExpiresAt.Value - DateTime.UtcNow;
        if (delta.TotalSeconds <= 0) return "expired";
        if (delta.TotalDays    >= 1) return $"{(int)delta.TotalDays}d";
        if (delta.TotalHours   >= 1) return $"{(int)delta.TotalHours}h";
        return $"{(int)delta.TotalMinutes}m";
    }

    private static string FormatFlags(InviteListRow r)
    {
        var flags = new List<string>();
        if (r.IsVanity)       flags.Add("[vanity]");
        if (!r.IsLabeled)     flags.Add("[unlabeled]");
        if (r.IsRevoked)      flags.Add("[revoked]");
        return string.Join(" ", flags);
    }

    /// <summary>
    /// Resolve a display name for the invite creator. Prefers the snapshot
    /// captured on InviteSource (stable across username changes); falls back
    /// to the live Inviter from Discord's API when no source row exists
    /// (i.e. unlabeled invites). Returns "(unknown)" when neither source has
    /// a value — typically very old invites whose creator Discord no longer
    /// surfaces.
    /// </summary>
    private static string ResolveCreatorDisplayName(InviteSource? source, IUser? liveInviter)
    {
        if (source is not null && !string.IsNullOrWhiteSpace(source.CreatedByUsername))
            return source.CreatedByUsername;

        if (liveInviter is not null)
        {
            // GlobalName is the public display name (Discord's 2023+ display
            // name change); Username is the underlying handle. Prefer the
            // friendlier GlobalName when set.
            if (!string.IsNullOrWhiteSpace(liveInviter.GlobalName)) return liveInviter.GlobalName;
            if (!string.IsNullOrWhiteSpace(liveInviter.Username))   return liveInviter.Username;
        }

        return "(unknown)";
    }

    private sealed record InviteListRow(
        string Code,
        string Label,
        int Uses,
        int? MaxUses,
        DateTime? ExpiresAt,
        bool IsVanity,
        bool IsLabeled,
        bool IsRevoked,
        DateTime CreatedAt,
        string CreatedBy);

    // ── /invite info ────────────────────────────────────────────────
    //
    // Drill-down on one code: source metadata + paginated attributed
    // joins. Gated at InviteManagementMinRank because individual recruit
    // attribution is more sensitive than aggregate stats — you can see
    // exactly which member joined via which link.
    //
    // Pagination uses Discord button interactions. Page size is fixed at
    // 25 (replaced the older limit:N arg). Buttons stay clickable
    // indefinitely — Discord doesn't expire button interactions, only
    // the underlying ephemeral message can age out, which takes long
    // enough to not matter for officer use.

    private const int InfoPageSize = 25;

    /// <summary>
    /// CustomId prefix for our pagination buttons. The full format is
    /// "invite-info:&lt;code&gt;:&lt;page&gt;". Discord caps customId at
    /// 100 chars; even with a 32-char vanity slug and a 4-digit page
    /// number we stay well under that.
    /// </summary>
    private const string InfoButtonIdPrefix = "invite-info:";
    private const string InfoLabelButtonIdPrefix = "invite-info-label:";

    private async Task HandleInfo(SocketSlashCommand command, SocketSlashCommandDataOption sub)
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
                $"You need {_config.InviteManagementMinRank}+ to view invite details.",
                ephemeral: true);
            return;
        }

        var code = (sub.Options.FirstOrDefault(o => o.Name == "code")?.Value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            await command.FollowupAsync("`code` is required.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        var result = await BuildInfoResponseAsync(guild, command.GuildId.Value, code, page: 0);

        if (result.ErrorMessage is not null)
        {
            await command.FollowupAsync(result.ErrorMessage, ephemeral: true);
            return;
        }

        await command.FollowupAsync(
            embed:      result.Embed,
            components: result.Components,
            ephemeral:  true);

        _logger.LogInformation(
            "{Caller} ran /invite info code={Code} (totalJoins={Total})",
            caller.Username, code, result.TotalJoinCount);
    }

    /// <summary>
    /// Routes button-click events. Only acts on customIds with our
    /// pagination prefix; everything else is ignored so other handlers'
    /// buttons aren't disturbed. (Discord delivers ButtonExecuted to
    /// every subscribed handler, regardless of which message owns the
    /// button.)
    /// </summary>
    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (customId is null || !customId.StartsWith(InfoButtonIdPrefix, StringComparison.Ordinal))
            return;

        try
        {
            await HandleInfoButtonAsync(component, customId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /invite info pagination button (customId={CustomId})", customId);
            // Best-effort feedback — if the interaction was already deferred
            // and ModifyOriginalResponseAsync failed, this followup may also
            // fail; nothing we can do beyond logging at that point.
            try
            {
                await component.FollowupAsync("Something went wrong loading that page. Try the command again.",
                    ephemeral: true);
            }
            catch { /* swallowed */ }
        }
    }

    private async Task HandleInfoButtonAsync(SocketMessageComponent component, string customId)
    {
        // Acknowledge within Discord's 3-second window. After this we have
        // up to 15 minutes to update the original message.
        await component.DeferAsync(ephemeral: true);

        // Format: "invite-info:<code>:<page>" — split on ':' once and once
        // again. Codes never contain colons (Discord codes are alphanumeric
        // plus dashes/underscores).
        var rest = customId.Substring(InfoButtonIdPrefix.Length);
        var lastColon = rest.LastIndexOf(':');
        if (lastColon < 1 || lastColon == rest.Length - 1)
        {
            _logger.LogWarning("Malformed pagination customId: {CustomId}", customId);
            return;
        }
        var code = rest[..lastColon];
        if (!int.TryParse(rest[(lastColon + 1)..], out var page) || page < 0)
        {
            _logger.LogWarning("Malformed page number in customId: {CustomId}", customId);
            return;
        }

        if (component.GuildId is null)
        {
            // Edge: button was somehow clicked outside a guild context.
            return;
        }

        var guild = (component.Channel as SocketGuildChannel)?.Guild;
        var result = await BuildInfoResponseAsync(guild, component.GuildId.Value, code, page);

        if (result.ErrorMessage is not null)
        {
            // The underlying invite/source/joins were deleted between the
            // initial command and this click. Tell the user the data's gone
            // rather than leaving them staring at stale buttons.
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = result.ErrorMessage;
                m.Embed = null;
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = result.Embed;
            m.Components = result.Components;
        });
    }

    /// <summary>
    /// Builds the embed and pagination components for /invite info, given
    /// a guild + code + page (0-indexed). Same code path serves both the
    /// initial slash command and every subsequent button click — keeps
    /// the rendering logic in one place. Returns ErrorMessage non-null
    /// when there's nothing to display (caller chooses how to surface it).
    /// </summary>
    private async Task<InfoResponse> BuildInfoResponseAsync(
        SocketGuild? guild, ulong guildId, string code, int page)
    {
        // Clamp page to non-negative. Higher bound is checked after we
        // know totalJoinCount, since pageSize math depends on it.
        if (page < 0) page = 0;

        // ── DB queries ──────────────────────────────────────────────
        InviteSource? source;
        List<InviteJoin> pageJoins;
        int totalJoinCount;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            source = await db.InviteSources
                .FirstOrDefaultAsync(s => s.GuildId == guildId && s.Code == code);

            totalJoinCount = await db.InviteJoins
                .CountAsync(j => j.GuildId == guildId && j.InviteCode == code);

            pageJoins = await db.InviteJoins
                .Where(j => j.GuildId == guildId && j.InviteCode == code)
                .OrderByDescending(j => j.JoinedAt)
                .Skip(page * InfoPageSize)
                .Take(InfoPageSize)
                .ToListAsync();
        }

        // ── Live invite (regular or vanity) ─────────────────────────
        IInviteMetadata? live = null;
        bool liveIsVanity = false;
        if (guild is not null)
        {
            try
            {
                var v = await guild.GetVanityInviteAsync();
                if (v is not null && string.Equals(v.Code, code, StringComparison.Ordinal))
                {
                    live = v;
                    liveIsVanity = true;
                }
            }
            catch { /* no vanity URL configured — fine */ }

            if (live is null)
            {
                try
                {
                    var invites = await guild.GetInvitesAsync();
                    live = invites.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch live invite list during /invite info code={Code}", code);
                }
            }
        }

        // Bail only if we have nothing at all to show.
        if (source is null && totalJoinCount == 0 && live is null)
        {
            return new InfoResponse(
                Embed: null,
                Components: new ComponentBuilder().Build(),
                TotalJoinCount: 0,
                ErrorMessage: $"No data for code `{code}`. Discord doesn't recognize it, we have no source row, and no joins have ever been attributed to it.");
        }

        // ── Build the metadata embed ────────────────────────────────
        var isUnlabeled = source is null;
        var isRevoked   = source is not null ? !source.IsActive : (live is null && totalJoinCount > 0);
        var isVanity    = source?.IsVanity ?? liveIsVanity;

        var color = isRevoked   ? Color.DarkGrey
                  : isVanity    ? Color.Gold
                  : isUnlabeled ? Color.LightGrey
                  : Color.Blue;

        var titleLabel = source?.Label ?? "(unlabeled)";
        var title = $"📨 Invite info — {titleLabel}";

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color);

        string status = (source, live, totalJoinCount) switch
        {
            ({ IsActive: false }, _, _)   => "Revoked",
            (null, null, > 0)             => "Revoked (no source row)",
            ({ IsVanity: true }, _, _)    => "Active (vanity URL)",
            ({ IsActive: true }, _, _)    => "Active",
            (null, not null, _) when liveIsVanity => "Active (unlabeled, vanity URL)",
            (null, not null, _)           => "Active (unlabeled)",
            _                             => "Unknown",
        };
        embed.AddField("Status", status, inline: true);
        embed.AddField("Code",   $"`{code}`", inline: true);

        ulong? channelId = source?.ChannelId
                           ?? (live is not null && !liveIsVanity ? live.ChannelId : null);
        if (channelId is ulong chId)
            embed.AddField("Channel", $"<#{chId}>", inline: true);

        if (source?.CreatedByDiscordId is ulong creatorId)
            embed.AddField("Created by", $"<@{creatorId}>", inline: true);
        else if (live?.Inviter is IUser inviter)
            embed.AddField("Created by", $"<@{inviter.Id}>", inline: true);
        else if (source is not null && !string.IsNullOrWhiteSpace(source.CreatedByUsername))
            embed.AddField("Created by", source.CreatedByUsername, inline: true);

        DateTime? createdAt = source?.CreatedAt ?? live?.CreatedAt?.UtcDateTime;
        if (createdAt is DateTime ca)
            embed.AddField("Created", DiscordTimestamp(ca, 'R'), inline: true);

        DateTime? expiresAt = source?.ExpiresAt
                              ?? (live is not null && !liveIsVanity ? ComputeExpiresAt(live) : null);
        if (isVanity)
            embed.AddField("Expires", "never (vanity)", inline: true);
        else if (expiresAt is DateTime exp)
            embed.AddField("Expires", DiscordTimestamp(exp, 'R'), inline: true);
        else
            embed.AddField("Expires", "never", inline: true);

        int? maxUses = source?.MaxUses
                       ?? (live is not null && (live.MaxUses ?? 0) > 0 ? live.MaxUses : null);
        if (maxUses is int cap)
            embed.AddField("Max uses", cap.ToString(), inline: true);

        if (live is not null)
            embed.AddField("Current uses", (live.Uses ?? 0).ToString(), inline: true);

        if (source is not null && !source.IsActive && source.DeactivatedAt is DateTime deact)
            embed.AddField("Deactivated", DiscordTimestamp(deact, 'R'), inline: true);

        if (source is not null && !string.IsNullOrWhiteSpace(source.Notes))
            embed.AddField("Notes", source.Notes, inline: false);

        embed.AddField("Total joins attributed", totalJoinCount.ToString(), inline: true);

        if (isUnlabeled && live is not null)
        {
            embed.AddField(
                "💡 Suggestion",
                $"This invite has no label. Run `/invite assign code:{code} label:<name>` to start tracking it.",
                inline: false);
        }

        // ── Recent joins page ───────────────────────────────────────
        int totalPages = totalJoinCount == 0
            ? 1
            : (int)Math.Ceiling(totalJoinCount / (double)InfoPageSize);

        // If a button click somehow arrived for a page beyond the current
        // last page (e.g. joins were deleted), clamp to the last valid page
        // so we render something useful instead of an empty list.
        if (page >= totalPages) page = Math.Max(0, totalPages - 1);

        if (totalJoinCount == 0)
        {
            embed.AddField("Recent joins", "_None yet._", inline: false);
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var join in pageJoins)
            {
                var when = DiscordTimestamp(join.JoinedAt, 'R');
                var ambig = join.IsAmbiguous ? " *(ambiguous)*" : "";
                sb.AppendLine($"• {when} — **{join.Username}**{ambig}");

                // Discord embed field value max is 1024 chars. 25 rows of
                // ~50 chars each lands comfortably under that, but defensive.
                if (sb.Length > 950)
                {
                    sb.AppendLine("_…truncated to fit_");
                    break;
                }
            }

            int startIdx = page * InfoPageSize + 1;
            int endIdx   = Math.Min(startIdx + InfoPageSize - 1, totalJoinCount);
            var fieldName = totalPages > 1
                ? $"Recent joins (showing {startIdx}–{endIdx} of {totalJoinCount})"
                : $"Recent joins ({totalJoinCount})";

            embed.AddField(fieldName, sb.ToString(), inline: false);
        }

        // ── Build pagination buttons ────────────────────────────────
        // Only attach buttons when there's more than one page. A single
        // page of joins (or zero joins) shows the embed alone.
        var components = totalPages > 1
            ? BuildInfoComponents(code, page, totalPages)
            : new ComponentBuilder().Build();

        return new InfoResponse(
            Embed: embed.Build(),
            Components: components,
            TotalJoinCount: totalJoinCount,
            ErrorMessage: null);
    }

    /// <summary>
    /// Three-button row: ◀ Prev, "Page X / Y" (disabled label), Next ▶.
    /// Boundary buttons disable themselves so a click can't fall off the
    /// ends — Discord won't even send the event for a disabled button.
    /// </summary>
    private static MessageComponent BuildInfoComponents(string code, int currentPage, int totalPages)
    {
        var prevId  = $"{InfoButtonIdPrefix}{code}:{currentPage - 1}";
        var nextId  = $"{InfoButtonIdPrefix}{code}:{currentPage + 1}";
        var labelId = $"{InfoLabelButtonIdPrefix}{code}:{currentPage}:{totalPages}";

        return new ComponentBuilder()
            .WithButton(
                label:    "◀ Prev",
                customId: prevId,
                style:    ButtonStyle.Secondary,
                disabled: currentPage <= 0)
            .WithButton(
                // 1-indexed for users — easier to read.
                label:    $"Page {currentPage + 1} / {totalPages}",
                customId: labelId,
                style:    ButtonStyle.Secondary,
                disabled: true)
            .WithButton(
                label:    "Next ▶",
                customId: nextId,
                style:    ButtonStyle.Secondary,
                disabled: currentPage >= totalPages - 1)
            .Build();
    }

    /// <summary>
    /// Bundle returned by BuildInfoResponseAsync. Either Embed +
    /// Components are populated and ErrorMessage is null, OR
    /// ErrorMessage is populated and the embed/components are empty.
    /// Never both.
    /// </summary>
    private sealed record InfoResponse(
        Embed? Embed,
        MessageComponent Components,
        int TotalJoinCount,
        string? ErrorMessage);

    // ── /invite edit-notes ──────────────────────────────────────────
    //
    // Update the Notes field on an existing InviteSource row. Distinct
    // from /invite assign in two ways:
    //   1. Doesn't touch the label.
    //   2. Treats an empty/whitespace `notes` value as an explicit
    //      *clear*. The assign path preserves existing notes when the
    //      field is empty (defensive against accidentally wiping notes
    //      via re-assign); this path is for officers who deliberately
    //      want to edit, including erasing.
    //
    // Refuses on codes with no InviteSource row — labeling-then-noting
    // is the intended flow. Notes without a label is a weird state.

    private async Task HandleEditNotes(SocketSlashCommand command, SocketSlashCommandDataOption sub)
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
                $"You need {_config.InviteManagementMinRank}+ to edit invite notes.",
                ephemeral: true);
            return;
        }

        var code = (sub.Options.FirstOrDefault(o => o.Name == "code")?.Value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            await command.FollowupAsync("`code` is required.", ephemeral: true);
            return;
        }

        // notes is required at the schema level so it's always present;
        // we treat empty/whitespace as an explicit clear.
        var notesRaw = (sub.Options.FirstOrDefault(o => o.Name == "notes")?.Value as string) ?? string.Empty;
        var notes = notesRaw.Trim();

        // Defensive cap at 500 chars. Discord embed fields max at 1024;
        // capping below that leaves headroom for officers prefixing or
        // suffixing context later. The create/assign paths don't cap;
        // this is the editing surface so it's the right place to enforce.
        const int NotesMaxLength = 500;
        if (notes.Length > NotesMaxLength)
        {
            await command.FollowupAsync(
                $"`notes` must be {NotesMaxLength} characters or fewer (got {notes.Length}).",
                ephemeral: true);
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var existing = await db.InviteSources
                .FirstOrDefaultAsync(s => s.GuildId == command.GuildId.Value && s.Code == code);

            if (existing is null)
            {
                await command.FollowupAsync(
                    $"No source row exists for `{code}`. Run `/invite assign code:{code} label:<name>` to label it first, " +
                    "then come back here to set notes.",
                    ephemeral: true);
                return;
            }

            var oldNotes = existing.Notes ?? string.Empty;
            existing.Notes = notes; // empty string is an explicit clear
            await db.SaveChangesAsync();

            // Build a clear officer-facing diff so it's obvious what changed.
            // Using inline code blocks for the values so empty / whitespace
            // changes are visible.
            string Format(string s) => string.IsNullOrEmpty(s) ? "*(empty)*" : $"`{s}`";

            var diff = oldNotes == notes
                ? $"Notes on **{existing.Label}** (`{code}`) unchanged."
                : $"Updated notes on **{existing.Label}** (`{code}`):\n" +
                  $"• Before: {Format(oldNotes)}\n" +
                  $"• After:  {Format(notes)}";

            await command.FollowupAsync($"✅ {diff}", ephemeral: true);

            _logger.LogInformation(
                "{Caller} edited notes on invite {Code} (label='{Label}'): '{OldNotes}' -> '{NewNotes}'",
                caller.Username, code, existing.Label, oldNotes, notes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit notes on InviteSource code={Code}", code);
            await command.FollowupAsync("Failed to save the notes. Check the bot logs.", ephemeral: true);
        }
    }


    // ── /invite stats ───────────────────────────────────────────────
    //
    // Aggregate counts in a chosen window. Open to anyone — same data
    // shape the briefing surfaces, but on demand.

    private async Task HandleStats(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var windowOpt = (sub.Options.FirstOrDefault(o => o.Name == "window")?.Value as string) ?? "7d";
        if (!TryParseStatsWindow(windowOpt, out var since, out var windowLabel))
        {
            await command.FollowupAsync(
                "Invalid `window`. Use `7d`, `30d`, `90d`, or `all`.", ephemeral: true);
            return;
        }

        // ── Aggregate ────────────────────────────────────────────────
        List<(string Label, int Count)> bySource;
        List<(ulong InviterId, int Count)> topReferrers;
        int totalJoins;
        int ambiguousJoins;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var baseQuery = db.InviteJoins
                .Where(j => j.GuildId == command.GuildId.Value);
            if (since is DateTime sinceUtc)
                baseQuery = baseQuery.Where(j => j.JoinedAt >= sinceUtc);

            totalJoins = await baseQuery.CountAsync();
            ambiguousJoins = await baseQuery.CountAsync(j => j.IsAmbiguous);

            bySource = (await baseQuery
                    .GroupBy(j => j.LabelSnapshot)
                    .Select(g => new { Label = g.Key, Count = g.Count() })
                    .ToListAsync())
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Label, x.Count))
                .ToList();

            // Top referrers: only joins via regular invites with a known
            // inviter (vanity has no inviter, ambiguous joins can't be
            // attributed to one person).
            topReferrers = (await baseQuery
                    .Where(j => j.InviterDiscordId != null && !j.IsAmbiguous)
                    .GroupBy(j => j.InviterDiscordId!.Value)
                    .Select(g => new { InviterId = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .Take(10)
                    .ToListAsync())
                .Select(x => (x.InviterId, x.Count))
                .ToList();
        }

        if (totalJoins == 0)
        {
            await command.FollowupAsync(
                $"No joins in {windowLabel}.", ephemeral: true);
            return;
        }

        // ── Render embed ────────────────────────────────────────────
        var embed = new EmbedBuilder()
            .WithTitle($"📨 Invite stats — {windowLabel}")
            .WithColor(Color.Blue)
            .WithDescription($"**{totalJoins}** total join(s){(ambiguousJoins > 0 ? $" • {ambiguousJoins} ambiguous" : "")}");

        // Sources by label
        if (bySource.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var (label, count) in bySource)
            {
                var pct = totalJoins > 0 ? (count * 100.0 / totalJoins) : 0;
                sb.AppendLine($"• **{label}** — {count} ({pct:0}%)");
                if (sb.Length > 950) { sb.AppendLine("_…truncated_"); break; }
            }
            embed.AddField("Joins by source", sb.ToString(), inline: false);
        }

        // Top referrers
        if (topReferrers.Count > 0)
        {
            var sb = new StringBuilder();
            int rank = 1;
            foreach (var (inviterId, count) in topReferrers)
            {
                sb.AppendLine($"`{rank}.` <@{inviterId}> — {count}");
                rank++;
                if (sb.Length > 950) { sb.AppendLine("_…truncated_"); break; }
            }
            embed.AddField("Top referrers", sb.ToString(), inline: false);
        }
        else
        {
            embed.AddField("Top referrers",
                "_No personal-invite referrals in this window._ (Vanity, unknown, and ambiguous joins don't credit a referrer.)",
                inline: false);
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);

        _logger.LogInformation(
            "{Caller} ran /invite stats window={Window} — {Total} joins, {Sources} sources, {Referrers} referrers",
            command.User.Username, windowLabel, totalJoins, bySource.Count, topReferrers.Count);
    }

    /// <summary>
    /// Parse the four allowed window values. Returns the lower bound (or
    /// null for "all") and a human-readable label for the embed title.
    /// </summary>
    private static bool TryParseStatsWindow(string raw, out DateTime? since, out string label)
    {
        var now = DateTime.UtcNow;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "7d":  since = now.AddDays(-7);  label = "last 7 days";  return true;
            case "30d": since = now.AddDays(-30); label = "last 30 days"; return true;
            case "90d": since = now.AddDays(-90); label = "last 90 days"; return true;
            case "all": since = null;             label = "all time";     return true;
            default:    since = null;             label = "";             return false;
        }
    }

    // ── /invite revoke ──────────────────────────────────────────────
    //
    // Destructive. Two-step: without confirm:true we show a preview and
    // bail; with confirm:true we call Discord's delete-invite API. The
    // existing InviteDeleted handler in InviteAttributionService handles
    // the row-deactivation side — single code path for both manual and
    // natural revocation.

    private async Task HandleRevoke(SocketSlashCommand command, SocketSlashCommandDataOption sub)
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
                $"You need {_config.InviteManagementMinRank}+ to revoke tracked invites.",
                ephemeral: true);
            return;
        }

        var code = (sub.Options.FirstOrDefault(o => o.Name == "code")?.Value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            await command.FollowupAsync("`code` is required.", ephemeral: true);
            return;
        }

        var confirmRaw = sub.Options.FirstOrDefault(o => o.Name == "confirm")?.Value;
        bool confirmed = confirmRaw is bool b && b;

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Couldn't resolve the guild.", ephemeral: true);
            return;
        }

        // ── Find the invite (regular or vanity) ─────────────────────
        IInviteMetadata? matched = null;
        bool isVanity = false;
        try
        {
            var vanity = await guild.GetVanityInviteAsync();
            if (vanity is not null && string.Equals(vanity.Code, code, StringComparison.Ordinal))
            {
                matched = vanity;
                isVanity = true;
            }
        }
        catch { /* no vanity URL */ }

        if (matched is null)
        {
            try
            {
                var invites = await guild.GetInvitesAsync();
                matched = invites.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch invites for /invite revoke");
                await command.FollowupAsync("Couldn't fetch the invite list from Discord. Try again.", ephemeral: true);
                return;
            }
        }

        // ── Vanity URL guard ────────────────────────────────────────
        // The vanity URL can't be deleted via the invite API — it's
        // managed in Server Settings → Vanity URL. We could call
        // ModifyVanityUrlAsync to clear it, but that's a separate
        // destructive action with broader implications, so we punt.
        if (isVanity)
        {
            await command.FollowupAsync(
                $"`{code}` is the vanity URL. It can't be revoked via this command — remove it from " +
                "**Server Settings → Vanity URL** instead. The InviteSource row will keep historical attribution either way.",
                ephemeral: true);
            return;
        }

        // ── Look up our row for the preview / revoke confirmation ──
        InviteSource? source;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            source = await db.InviteSources
                .FirstOrDefaultAsync(s => s.GuildId == command.GuildId.Value && s.Code == code);
        }

        if (matched is null && source is null)
        {
            await command.FollowupAsync(
                $"No invite found for code `{code}` — Discord doesn't recognize it and we have no source row either.",
                ephemeral: true);
            return;
        }

        if (matched is null && source is not null && !source.IsActive)
        {
            await command.FollowupAsync(
                $"`{code}` is already revoked (deactivated <t:{new DateTimeOffset(source.DeactivatedAt ?? source.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds()}:R>). Nothing to do.",
                ephemeral: true);
            return;
        }

        // ── Preview path ────────────────────────────────────────────
        if (!confirmed)
        {
            var labelLine = source is not null ? $"**{source.Label}**" : "**(unlabeled)**";
            var usesLine = matched is not null
                ? $"{matched.Uses ?? 0}{((matched.MaxUses ?? 0) > 0 ? $"/{matched.MaxUses}" : "")} use(s)"
                : "(no live invite)";
            var expiresLine = matched is not null && (matched.MaxAge ?? 0) > 0 && matched.CreatedAt.HasValue
                ? DiscordTimestamp(matched.CreatedAt.Value.UtcDateTime.AddSeconds(matched.MaxAge!.Value), 'R')
                : "never";

            var sb = new StringBuilder();
            sb.AppendLine($"⚠️ **About to revoke** {labelLine} (`{code}`)");
            sb.AppendLine($"• Channel: {(source?.ChannelId is ulong chId ? $"<#{chId}>" : "(unknown)")}");
            sb.AppendLine($"• Current uses: {usesLine}");
            sb.AppendLine($"• Expires: {expiresLine}");
            sb.AppendLine();
            sb.AppendLine("Run again with `confirm:true` to proceed. Historical attribution rows will be preserved.");

            await command.FollowupAsync(sb.ToString(), ephemeral: true);
            return;
        }

        // ── Revoke path ─────────────────────────────────────────────
        // Discord's delete-invite endpoint is per-invite; we can call it
        // even if our InviteSource row is missing. The InviteDeleted
        // gateway event will fire and our handler will flip IsActive=false
        // (or no-op if there's no row).
        if (matched is null)
        {
            // We have a stale source row but no live invite — nothing to
            // delete on Discord's side. Just mark our row inactive so
            // /invite list stops surfacing it as a "still pending" row.
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            if (source is not null && source.IsActive)
            {
                source.IsActive = false;
                source.DeactivatedAt = DateTime.UtcNow;
                db.InviteSources.Update(source);
                await db.SaveChangesAsync();
            }
            await command.FollowupAsync(
                $"`{code}` was already gone from Discord. Marked our row inactive.",
                ephemeral: true);
            _logger.LogInformation("{Caller} marked stale InviteSource {Code} inactive", caller.Username, code);
            return;
        }

        try
        {
            await matched.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord rejected DeleteAsync for invite {Code}", code);
            await command.FollowupAsync(
                $"Discord refused to delete the invite: {ex.Message}",
                ephemeral: true);
            return;
        }

        // The InviteDeleted gateway event will arrive shortly and the
        // existing handler will flip IsActive=false + set DeactivatedAt.
        // We don't write that state ourselves to keep the deactivation
        // flow single-pathed.
        var labelForLog = source?.Label ?? "(unlabeled)";
        await command.FollowupAsync(
            $"✅ Revoked **{labelForLog}** (`{code}`). Historical attribution preserved.",
            ephemeral: true);

        _logger.LogInformation(
            "{Caller} revoked invite {Code} (label='{Label}')",
            caller.Username, code, labelForLog);
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
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("edit-notes")
                .WithDescription("Update the notes on an existing tracked invite (pass empty notes to clear)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("code", ApplicationCommandOptionType.String,
                    "The invite code whose notes you want to edit", isRequired: true)
                .AddOption("notes", ApplicationCommandOptionType.String,
                    "New notes (max 500 chars). Pass empty to clear existing notes.",
                    isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list")
                .WithDescription("List every tracked invite (and any unlabeled live invites in the server)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("filter")
                    .WithDescription("Which invites to show. Default: active.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .AddChoice("Active only",   "active")
                    .AddChoice("Inactive only", "inactive")
                    .AddChoice("All",           "all")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("info")
                .WithDescription("Show detailed info and paginated joins for a specific invite code")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("code", ApplicationCommandOptionType.String,
                    "The invite code to look up", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("stats")
                .WithDescription("Show join counts by label and top referrers in a time window")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("window")
                    .WithDescription("Time window. Default: 7d.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .AddChoice("Last 7 days",  "7d")
                    .AddChoice("Last 30 days", "30d")
                    .AddChoice("Last 90 days", "90d")
                    .AddChoice("All time",     "all")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("revoke")
                .WithDescription("Revoke a tracked invite. Pass confirm:true to actually delete it.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("code", ApplicationCommandOptionType.String,
                    "The invite code to revoke", isRequired: true)
                .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                    "Set true to actually revoke. Without this, you'll see a preview.",
                    isRequired: false))
            .Build();
    }
}