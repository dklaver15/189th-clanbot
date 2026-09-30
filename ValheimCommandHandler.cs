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
/// Owns the Valheim slash commands against the clan's Shockbyte-hosted server.
///
///   • /valheim-status      — who's on right now, and whether the server is up   (everyone)
///   • /valheim-playtime    — one player's total hours, sessions and deaths      (everyone)
///   • /valheim-leaderboard — most playtime, public                              (everyone)
///   • /valheim-link        — bind a Discord account to a character              (everyone; others = officer)
///
/// ── Two data sources, and which one wins ──
/// <see cref="ValheimQueryService"/> is the Steam A2S query protocol — no mod
/// required, but it reports a bare player COUNT and never a name, and on a
/// CROSSPLAY server it reports nothing at all (crossplay registers with PlayFab
/// instead of Steam, so the query port is open but unanswered).
///
/// The DiscordConnector ingest (<see cref="ValheimEventIngestHandler"/>) is the
/// real source: join/leave/death events carrying a stable platform id, which is
/// what makes sessions, playtime, the leaderboard and the link table possible at
/// all. <see cref="HandleStatusAsync"/> prefers it and falls back to A2S only when
/// the ingest isn't configured.
///
/// There is still no /valheim-admin: neither source has a write side. Vanilla
/// Valheim has no RCON, and the mod is one-way.
///
/// ── Gateway safety ──
/// The status query is a UDP round-trip with a 5s timeout, and Discord.NET runs
/// handlers ON the gateway task — awaiting that inline would stall the heartbeat
/// every time the server is down. So the handler dispatches to Task.Run and
/// returns immediately, same rule as the other game-server handlers.
///
/// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (permission labels).
/// </summary>
public class ValheimCommandHandler
{
    private static readonly string[] CommandNames =
    {
        "valheim-status",
        "valheim-playtime",
        "valheim-leaderboard",
        "valheim-link",
    };

    /// <summary>Cap on leaderboard rows, so the embed can't blow the 4096-char description limit.</summary>
    private const int MaxLeaderboardRows = 15;

    /// <summary>Valheim's steel-blue UI accent, to distinguish the embed at a glance.</summary>
    private static readonly Color ValheimBlue = new(0x38607C);

    private readonly ILogger<ValheimCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly ValheimQueryService _query;
    private readonly IServiceProvider _services;

    public ValheimCommandHandler(
        ILogger<ValheimCommandHandler> logger,
        IOptions<BotConfig> config,
        ValheimQueryService query,
        IServiceProvider services)
    {
        _logger = logger;
        _config = config.Value;
        _query = query;
        _services = services;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStatusCommand() =>
        new SlashCommandBuilder()
            .WithName("valheim-status")
            .WithDescription("Live status of the clan's Valheim server — who's on, world, version, join address")
            .Build();

    public static SlashCommandProperties BuildPlaytimeCommand() =>
        new SlashCommandBuilder()
            .WithName("valheim-playtime")
            .WithDescription("How long someone has spent on the clan's Valheim server")
            .AddOption("member", ApplicationCommandOptionType.User,
                "Whose playtime to show (defaults to you)", isRequired: false)
            .AddOption("name", ApplicationCommandOptionType.String,
                "Look up by in-game character name instead", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildLeaderboardCommand() =>
        new SlashCommandBuilder()
            .WithName("valheim-leaderboard")
            .WithDescription("Who's put the most hours into the clan's Valheim server")
            .Build();

    public static SlashCommandProperties BuildLinkCommand() =>
        new SlashCommandBuilder()
            .WithName("valheim-link")
            .WithDescription("Link your Discord account to your Valheim character")
            .AddOption("name", ApplicationCommandOptionType.String,
                "Your in-game character name", isRequired: true)
            .AddOption("user", ApplicationCommandOptionType.User,
                "Link on someone else's behalf (officers only)", isRequired: false)
            .Build();

    // ─── Dispatch ────────────────────────────────────────────────────────────

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!CommandNames.Contains(command.Data.Name)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling /{Command} for {User}",
                    command.Data.Name, command.User.Username);
                try
                {
                    if (command.HasResponded)
                        await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                    else
                        await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                }
                catch { /* interaction already expired */ }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "valheim-status":      await HandleStatusAsync(command); break;
            case "valheim-playtime":    await HandlePlaytimeAsync(command); break;
            case "valheim-leaderboard": await HandleLeaderboardAsync(command); break;
            case "valheim-link":        await HandleLinkAsync(command); break;
        }
    }

    // ─── /valheim-status ─────────────────────────────────────────────────────

    /// <summary>
    /// Live server status.
    ///
    /// <para>Reads the DiscordConnector ingest when it's configured, and only falls
    /// back to the A2S query otherwise. That ordering is deliberate: A2S was the
    /// original source here and is now the WORSE one wherever the mod is running.
    /// It reports a bare player count and nothing else, and on a crossplay server it
    /// reports nothing at all — crossplay registers with PlayFab rather than Steam,
    /// so the Steam query port is allocated and open but never answers. The ingest
    /// gives names, per-player session durations, and an authoritative up/down with a
    /// timestamp.</para>
    /// </summary>
    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (IngestConfigured)
        {
            await RespondFromSessionsAsync(command);
            return;
        }

        if (!await EnsureEnabledAsync(command)) return;
        await RespondFromQueryAsync(command);
    }

    private bool IngestConfigured =>
        _config.ValheimEnabled && _config.ValheimIngestEnabled && _config.ValheimRawChannelId != 0;

    private async Task RespondFromSessionsAsync(SocketSlashCommand command)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var lastEvent = await db.ValheimServerEvents
            .OrderByDescending(e => e.OccurredUtc)
            .FirstOrDefaultAsync();

        var open = await db.ValheimSessions
            .Where(s => s.EndedUtc == null)
            .OrderBy(s => s.StartedUtc)
            .ToListAsync();

        var embed = new EmbedBuilder()
            .WithTitle("🛡️ Valheim server")
            .WithCurrentTimestamp();

        // Anyone mid-session is proof the server is up, and outranks a stale
        // lifecycle row — if the bot missed a server_start, live players still
        // settle the question.
        var online = open.Count > 0 || lastEvent?.Online == true;

        // No lifecycle event AND nobody on: the bot has simply never observed this
        // server. That is genuinely different from "offline" — it is the normal state
        // between deploying the integration and the game server's next restart — and
        // reporting it as offline would be indistinguishable from a real outage.
        var known = lastEvent is not null || open.Count > 0;

        if (!known)
        {
            embed.WithColor(Color.LightGrey)
                 .AddField("Status", "❓ Unknown — no server events recorded yet", false);
        }
        else
        {
            embed.WithColor(online ? Color.Green : Color.Red);

            // Phrased as a DURATION ("up 2h 14m"), not a relative timestamp.
            // "🟢 Online 6 hours ago" was reported as the status being six hours
            // stale, when it actually meant the server had been up that long — the
            // reading is a fair one, and no amount of correct data fixes a label that
            // says the opposite of what it means.
            var elapsed = lastEvent is null
                ? null
                : $" — {(online ? "up" : "down")} {ValheimStatusService.Humanize(DateTime.UtcNow - lastEvent.OccurredUtc)}";

            embed.AddField("Status", online ? $"🟢 Online{elapsed}" : $"🔴 Offline{elapsed}", false);
        }

        // "—" rather than 0 while unknown: we have no basis for the number, and
        // printing a figure we haven't observed is worse than admitting the gap.
        embed.AddField("Players", known ? (online ? $"{open.Count}" : "0") : "—", true);
        embed.AddField("Join", $"`{_query.JoinAddress}`", true);

        if (open.Count > 0)
        {
            var links = await LinksForAsync(db, command.GuildId, open);
            var now = DateTime.UtcNow;

            var lines = open.Select(s =>
            {
                var who = links.TryGetValue(KeyOf(s), out var discordId)
                    ? $"**{ValheimStatusService.Escape(s.PlayerName)}** (<@{discordId}>)"
                    : $"**{ValheimStatusService.Escape(s.PlayerName)}**";

                // Measured from StartedUtc against the wall clock rather than via
                // Duration, which clamps to LastSeenUtc — correct for crediting
                // playtime, but it would under-report someone who is on right now and
                // simply hasn't generated an event since joining.
                var elapsed = now - s.StartedUtc;
                return $"• {who} — {ValheimStatusService.Humanize(elapsed)}";
            });

            embed.AddField("Who's on", string.Join("\n", lines), false);
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    /// <summary>Discord links for the identities in these sessions, keyed as sessions are.</summary>
    private static async Task<Dictionary<string, ulong>> LinksForAsync(
        BotDbContext db, ulong? guildId, List<ValheimSession> sessions)
    {
        if (guildId is not ulong gid) return new Dictionary<string, ulong>(StringComparer.Ordinal);

        var ids = sessions.Select(KeyOf).Distinct(StringComparer.Ordinal).ToList();

        return (await db.ValheimLinks
                .Where(l => l.GuildId == gid && ids.Contains(l.ValheimPlayerId))
                .Select(l => new { l.ValheimPlayerId, l.DiscordUserId })
                .ToListAsync())
            .GroupBy(l => l.ValheimPlayerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DiscordUserId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The original A2S path, kept for a server where the query port actually
    /// answers — i.e. one not using crossplay, or a future host where it works.
    /// Only reached when the ingest isn't configured.
    /// </summary>
    private async Task RespondFromQueryAsync(SocketSlashCommand command)
    {
        var info = await _query.QueryAsync();

        if (info is null)
        {
            await command.FollowupAsync(
                $"🔴 The Valheim server isn't answering on `{_config.ValheimHost}:{_query.QueryPort}`. " +
                "It's most likely offline or restarting.\n\n" +
                OfflineHint(),
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🛡️ {ValheimStatusService.Escape(string.IsNullOrWhiteSpace(info.Name) ? info.World : info.Name)}")
            .WithColor(ValheimBlue)
            .WithCurrentTimestamp();

        embed.AddField("Players", $"{info.Players}/{info.MaxPlayers}", true);

        if (!string.IsNullOrWhiteSpace(info.World))
            embed.AddField("World", ValheimStatusService.Escape(info.World), true);

        if (info.DisplayVersion is { } version)
            embed.AddField("Version", ValheimStatusService.Escape(version), true);

        embed.AddField("Password", info.PasswordProtected ? "🔒 Required" : "🔓 Open", true);
        embed.AddField("Query ping", $"{(int)info.RoundTrip.TotalMilliseconds} ms", true);
        embed.AddField("Join", $"`{_query.JoinAddress}`", false);

        embed.WithFooter("Valheim's query protocol reports a player count only — names need a server-side mod.");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /valheim-playtime ───────────────────────────────────────────────────

    private async Task HandlePlaytimeAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await EnsureIngestEnabledAsync(command)) return;
        if (command.GuildId is not ulong guildId)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var name = (command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string ?? "").Trim();
        var member = command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value as SocketUser;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Two ways in: a character name, or a Discord member resolved through the
        // link table. Name wins when both are given — it's the more specific ask.
        string? key;
        string label;

        if (name.Length > 0)
        {
            key = await ResolveKeyByNameAsync(db, name);
            label = ValheimStatusService.Escape(name);

            if (key is null)
            {
                await command.FollowupAsync(
                    $"❌ I've never seen a character called **{label}** on the server.",
                    ephemeral: true);
                return;
            }
        }
        else
        {
            var target = member ?? command.User;
            var link = await db.ValheimLinks
                .FirstOrDefaultAsync(l => l.GuildId == guildId && l.DiscordUserId == target.Id);

            if (link is null)
            {
                var who = member is null
                    ? "You haven't linked a character yet — run `/valheim-link` with your in-game name."
                    : $"{target.Username} hasn't linked a Valheim character yet.";
                await command.FollowupAsync(who, ephemeral: true);
                return;
            }

            key = link.ValheimPlayerId;
            label = $"<@{target.Id}>";
        }

        var sessions = await SessionsForKeyAsync(db, key);
        if (sessions.Count == 0)
        {
            await command.FollowupAsync($"No recorded sessions for {label} yet.", ephemeral: true);
            return;
        }

        var total = sessions.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
        var deaths = await CountDeathsForKeyAsync(db, key);
        var latest = sessions.MaxBy(s => s.LastSeenUtc)!;
        var first = sessions.MinBy(s => s.StartedUtc)!;
        var open = sessions.Any(s => s.EndedUtc is null);

        var embed = new EmbedBuilder()
            .WithTitle($"🛡️ {ValheimStatusService.Escape(latest.PlayerName)}")
            .WithColor(ValheimBlue)
            .AddField("Total playtime", ValheimStatusService.Humanize(total), true)
            .AddField("Sessions", sessions.Count.ToString(), true)
            .AddField("Deaths", deaths.ToString(), true)
            .AddField("First seen", $"<t:{ToUnix(first.StartedUtc)}:R>", true)
            .AddField(open ? "Online since" : "Last seen",
                      open ? $"<t:{ToUnix(latest.StartedUtc)}:R>" : $"<t:{ToUnix(latest.LastSeenUtc)}:R>", true)
            .WithCurrentTimestamp();

        if (name.Length == 0) embed.WithDescription($"Linked to {label}");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /valheim-leaderboard ────────────────────────────────────────────────

    private async Task HandleLeaderboardAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: false);
        if (!await EnsureIngestEnabledAsync(command)) return;
        if (command.GuildId is not ulong guildId)
        {
            await command.FollowupAsync("This command can only be used in a server.");
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Loaded and summed in memory rather than in SQL: ValheimSession.Duration is
        // a computed C# property (deliberately — see the entity for why it clamps to
        // LastSeenUtc), so it has no SQL translation. Volume is a clan server's worth
        // of sessions, which is trivial to hold.
        var sessions = await db.ValheimSessions.ToListAsync();
        if (sessions.Count == 0)
        {
            await command.FollowupAsync("No Valheim sessions recorded yet.");
            return;
        }

        var links = (await db.ValheimLinks
                .Where(l => l.GuildId == guildId)
                .Select(l => new { l.ValheimPlayerId, l.DiscordUserId })
                .ToListAsync())
            .GroupBy(l => l.ValheimPlayerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DiscordUserId, StringComparer.Ordinal);

        var ranked = sessions
            .GroupBy(KeyOf, StringComparer.Ordinal)
            .Select(g => new
            {
                Key = g.Key,
                Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration),
                Count = g.Count(),
                // Newest name wins: a rename should show as the player calls
                // themselves now, while the session rows keep their own snapshots.
                Name = g.MaxBy(s => s.LastSeenUtc)!.PlayerName,
            })
            .Where(r => r.Total > TimeSpan.Zero)
            .OrderByDescending(r => r.Total)
            .Take(MaxLeaderboardRows)
            .ToList();

        if (ranked.Count == 0)
        {
            await command.FollowupAsync("No Valheim playtime recorded yet.");
            return;
        }

        var lines = ranked.Select((r, i) =>
        {
            var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"`{i + 1}.`" };
            var who = links.TryGetValue(r.Key, out var discordId)
                ? $"**{ValheimStatusService.Escape(r.Name)}** (<@{discordId}>)"
                : $"**{ValheimStatusService.Escape(r.Name)}**";
            return $"{medal} {who} — {ValheimStatusService.Humanize(r.Total)} · {r.Count} session{(r.Count == 1 ? "" : "s")}";
        });

        var embed = new EmbedBuilder()
            .WithTitle("🛡️ Valheim playtime")
            .WithColor(ValheimBlue)
            .WithDescription(string.Join("\n", lines))
            .WithCurrentTimestamp()
            .Build();

        await command.FollowupAsync(embed: embed, allowedMentions: AllowedMentions.None);
    }

    // ─── /valheim-link ───────────────────────────────────────────────────────

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await EnsureIngestEnabledAsync(command)) return;

        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var name = (command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string ?? "").Trim();
        var onBehalfOf = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;

        // Linking someone else decides whose playtime is whose, so it's gated —
        // otherwise anyone could bind a teammate's character to their own account.
        if (onBehalfOf is not null && onBehalfOf.Id != caller.Id && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                "❌ Only officers can link someone else. Leave `user` blank to link yourself.",
                ephemeral: true);
            return;
        }

        var targetId = onBehalfOf?.Id ?? caller.Id;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Verification is the session history rather than a live player list: the mod
        // reports a player id on every join, so by the time anyone links, the id is
        // already on file. That's why this works offline, unlike /satisfactory-link,
        // which requires the player to be in game.
        var match = await db.ValheimSessions
            .Where(s => s.PlayerName == name)
            .OrderByDescending(s => s.LastSeenUtc)
            .FirstOrDefaultAsync();

        if (match is null)
        {
            var seen = await db.ValheimSessions
                .OrderByDescending(s => s.LastSeenUtc)
                .Select(s => s.PlayerName)
                .Take(40)
                .ToListAsync();

            var known = seen.Distinct(StringComparer.Ordinal).Take(15).ToList();
            var hint = known.Count == 0
                ? "_No characters recorded yet — someone needs to join the server first._"
                : string.Join(", ", known.Select(n => $"`{n}`"));

            await command.FollowupAsync(
                $"❌ No character called **{ValheimStatusService.Escape(name)}** has been seen on the server. " +
                $"The name is case-sensitive.\n\nRecently seen: {hint}",
                ephemeral: true);
            return;
        }

        // Without a platform id there is nothing durable to link TO: a name-keyed
        // link would silently transfer to whoever renamed into it next. Refuse
        // rather than create a claim that can quietly become wrong.
        if (match.ValheimPlayerId.Length == 0)
        {
            await command.FollowupAsync(
                "❌ Linking needs DiscordConnector to report `%PLAYER_ID%`, and it isn't — sessions are " +
                "currently keyed on character name only. An admin needs to check the Player Join/Leave " +
                "message templates in the mod's messages config.",
                ephemeral: true);
            return;
        }

        // Seeing a character in the session history proves it exists, not who owns
        // it, so a character already linked to someone else can only be moved by an
        // officer.
        var ownerId = await db.ValheimLinks
            .Where(l => l.GuildId == guildId && l.ValheimPlayerId == match.ValheimPlayerId && l.DiscordUserId != targetId)
            .Select(l => (ulong?)l.DiscordUserId)
            .FirstOrDefaultAsync();
        if (ownerId is ulong owner && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                $"❌ **{ValheimStatusService.Escape(match.PlayerName)}** is already linked to <@{owner}>. " +
                "If that's wrong, ask an officer to relink it with the `user` option.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);
            return;
        }

        // Re-linking overwrites in BOTH directions, so two members can't both claim
        // one character and one member can't hold two.
        var existing = await db.ValheimLinks
            .Where(l => l.GuildId == guildId
                     && (l.DiscordUserId == targetId || l.ValheimPlayerId == match.ValheimPlayerId))
            .ToListAsync();
        if (existing.Count > 0) db.ValheimLinks.RemoveRange(existing);

        db.ValheimLinks.Add(new ValheimLink
        {
            GuildId = guildId,
            DiscordUserId = targetId,
            ValheimPlayerId = match.ValheimPlayerId,
            ValheimName = match.PlayerName,
            LinkedUtc = DateTime.UtcNow,
            LinkedByUserId = caller.Id,
        });

        await db.SaveChangesAsync();

        _logger.LogInformation("Valheim link: Discord {Target} → {PlayerName} ({PlayerId}) by {Caller}",
            targetId, match.PlayerName, match.ValheimPlayerId, caller.Id);

        await command.FollowupAsync(
            $"✅ Linked <@{targetId}> to **{ValheimStatusService.Escape(match.PlayerName)}**.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    // ─── Identity helpers ────────────────────────────────────────────────────

    /// <summary>
    /// The column a session is identified by: the platform id when present, the
    /// character name otherwise. Mirrors <see cref="ValheimEvent.IdentityKey"/> — the
    /// ingest side and the query side must agree on this or totals split in two.
    /// </summary>
    private static string KeyOf(ValheimSession s) =>
        s.ValheimPlayerId.Length > 0 ? s.ValheimPlayerId : s.PlayerName;

    /// <summary>Most recent identity used by a given character name, or null if unseen.</summary>
    private static async Task<string?> ResolveKeyByNameAsync(BotDbContext db, string name)
    {
        var session = await db.ValheimSessions
            .Where(s => s.PlayerName == name)
            .OrderByDescending(s => s.LastSeenUtc)
            .FirstOrDefaultAsync();

        return session is null ? null : KeyOf(session);
    }

    /// <summary>
    /// Sessions belonging to an identity key. Name-keyed rows are matched only when
    /// they carry no id, so a player who later gained an id can't double-count.
    /// </summary>
    private static async Task<List<ValheimSession>> SessionsForKeyAsync(BotDbContext db, string key) =>
        await db.ValheimSessions
            .Where(s => s.ValheimPlayerId == key || (s.ValheimPlayerId == "" && s.PlayerName == key))
            .ToListAsync();

    private static async Task<int> CountDeathsForKeyAsync(BotDbContext db, string key) =>
        await db.ValheimDeaths
            .CountAsync(d => d.ValheimPlayerId == key || (d.ValheimPlayerId == "" && d.PlayerName == key));

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private bool HasAdminRole(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.ValheimAdminRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.ValheimAdminRoleId);
    }

    private async Task<bool> EnsureIngestEnabledAsync(SocketSlashCommand command)
    {
        if (_config.ValheimEnabled && _config.ValheimIngestEnabled && _config.ValheimRawChannelId != 0)
            return true;

        await command.FollowupAsync(
            "Valheim session tracking isn't set up yet (`ValheimIngestEnabled` / `ValheimRawChannelId`). Ask an admin.",
            ephemeral: true);
        return false;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<bool> EnsureEnabledAsync(SocketSlashCommand command)
    {
        if (_config.ValheimEnabled && _query.IsConfigured) return true;

        await command.FollowupAsync(
            "The Valheim integration isn't set up yet (`ValheimEnabled` / `ValheimHost`). Ask an admin.",
            ephemeral: true);
        return false;
    }

    /// <summary>
    /// Where a human can go when the bot can't help. A2S is read-only, so the bot
    /// structurally cannot restart the server — every failure message has to point
    /// at the panel instead of implying a fix that doesn't exist.
    /// SyncWithHandlers: BotConfig.ValheimHostPanelName / ValheimHostPanelUrl.
    /// </summary>
    private string OfflineHint()
    {
        var name = string.IsNullOrWhiteSpace(_config.ValheimHostPanelName)
            ? "the host's control panel"
            : _config.ValheimHostPanelName;

        var where = string.IsNullOrWhiteSpace(_config.ValheimHostPanelUrl)
            ? name
            : $"[{name}]({_config.ValheimHostPanelUrl})";

        return $"The bot can only read the server's status, never start it — someone with access will need to " +
               $"bring it up from {where}, or from the game server card in this Discord.";
    }
}
