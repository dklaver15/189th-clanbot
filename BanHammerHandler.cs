using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The "Ban Hammer" — a workplace-safety-sign style counter that tracks how
/// many days it's been since a member of the team last banned a real person.
///
/// ── What it does ──
/// One channel (<see cref="BotConfig.BanHammerChannelId"/>) holds a single
/// auto-updating embed reading "N days since the last ban". Every time a human
/// (non-bot) member is banned by a human moderator, the counter resets to 0,
/// the lifetime total ticks up, and — if the gap just ended was the longest
/// ever — the "record peace" updates. Between bans, a low-frequency timer
/// re-renders the embed so the day count climbs on its own.
///
/// ── Why the audit log, and what counts ──
/// We hook the gateway <c>AuditLogCreated</c> event (same source the
/// AuditLogWatcherHandler uses) rather than the bare <c>UserBanned</c> event,
/// because only the audit entry tells us WHO did the banning. A ban counts
/// toward the reset only when BOTH:
///   • the banned user is a real person (Target.IsBot == false), and
///   • the executor is NOT ClanGuard itself.
/// That deliberately excludes the honeypot / spam-trap auto-bans (bot accounts
/// banned by the bot) — this sign is about people the team chooses to ban, not
/// bots the bot catches.
///
/// ── Persistence ──
/// <see cref="BotState.BanHammerMessageId"/> stores the embed's message ID so a
/// restart edits the existing message instead of posting a duplicate.
/// <see cref="BotState.BanHammerLastBanUtc"/> is the timestamp the day count is
/// measured from; <see cref="BotState.BanHammerRecordDays"/> and
/// <see cref="BotState.BanHammerTotalBans"/> back the two stat fields. The
/// banner image is whatever URL is set in <see cref="BotConfig.BanHammerImageUrl"/>
/// (a placeholder until the server owner supplies the real gif).
/// </summary>
public sealed class BanHammerHandler
{
    /// <summary>Slash command name for the manual repost / self-heal command.</summary>
    public const string CommandName = "banhammer";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<BanHammerHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>Serializes counter resets + embed edits during bursts.</summary>
    private readonly SemaphoreSlim _embedLock = new(1, 1);

    /// <summary>One-shot guard so the ensure runs once per process, not per reconnect.</summary>
    private int _ensured;

    /// <summary>Last day count we rendered, so the timer only edits on a change.</summary>
    private int _lastRenderedDays = int.MinValue;

    /// <summary>Ticks the "days since" display upward between bans.</summary>
    private Timer? _refreshTimer;

    /// <summary>How often we re-check whether the day count rolled over.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    public BanHammerHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<BanHammerHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    /// <summary>
    /// Slash-command definition for <c>/banhammer</c>. Registered in
    /// DiscordBotService.OnReadyAsync alongside the other commands; keep
    /// CommandsCommandHandler.BuildCatalog in sync with its permission gate.
    /// </summary>
    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Repost the Ban Hammer counter embed (Officer+ only)")
            .AddOption("resync", ApplicationCommandOptionType.Boolean,
                "Re-pull the last ban date from Discord's audit log before reposting",
                isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        // The command is always wired so it can respond even when the feature
        // is disabled (with a "disabled" notice) — but the live ban tracking,
        // embed ensure, and climb timer only run when enabled.
        client.SlashCommandExecuted += OnSlashCommand;

        if (!_config.BanHammerEnabled || _config.BanHammerChannelId == 0)
            return;

        client.AuditLogCreated += OnAuditLogCreated;
        client.Ready           += OnReady;

        // Climb the day count without waiting for a ban. Cheap (one possible
        // ModifyAsync per day) — RefreshIfChangedAsync only edits when the
        // integer day count actually rolls over.
        _refreshTimer = new Timer(_ => _ = RefreshIfChangedAsync(),
            null, RefreshInterval, RefreshInterval);
    }

    // ── Slash command: manual repost / self-heal ───────────────────────

    private Task OnSlashCommand(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName)
            return Task.CompletedTask;

        _ = HandleRepostCommandAsync(command);
        return Task.CompletedTask;
    }

    private async Task HandleRepostCommandAsync(SocketSlashCommand command)
    {
        try
        {
            await command.DeferAsync(ephemeral: true);

            if (!_config.BanHammerEnabled || _config.BanHammerChannelId == 0)
            {
                await command.FollowupAsync(
                    "The Ban Hammer is currently disabled in config.", ephemeral: true);
                return;
            }

            if (command.User is not SocketGuildUser caller || !HasElevatedPermissions(caller))
            {
                await command.FollowupAsync(
                    "You don't have permission to use this command.", ephemeral: true);
                return;
            }

            if (_client.GetChannel(_config.BanHammerChannelId) is not SocketTextChannel channel)
            {
                await command.FollowupAsync(
                    "The configured Ban Hammer channel could not be found.", ephemeral: true);
                return;
            }

            var resync = command.Data.Options
                .FirstOrDefault(o => o.Name == "resync")?.Value as bool? ?? false;

            string resyncNote = "";
            if (resync)
            {
                var found = await ResyncFromHistoryAsync(channel.Guild);
                resyncNote = found is { } ts
                    ? $"\nRe-pulled from audit log: last ban <t:{((DateTimeOffset)DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>."
                    : "\nNo ban found in the audit log's ~45-day window — counter left unchanged.";
            }

            var posted = await RepostAsync(channel);
            if (posted is null)
            {
                await command.FollowupAsync(
                    "Something went wrong reposting the embed. Check the bot logs.", ephemeral: true);
                return;
            }

            await command.FollowupAsync(
                $"🔨 Ban Hammer reposted in {channel.Mention}: {posted.GetJumpUrl()}{resyncNote}",
                ephemeral: true);

            _logger.LogInformation(
                "Ban Hammer: {Caller} reposted the counter embed (message {MessageId}, resync={Resync}).",
                caller.Username, posted.Id, resync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ban Hammer: /{Command} failed.", CommandName);
            try
            {
                await command.FollowupAsync(
                    "Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    /// <summary>
    /// Posts a fresh counter embed and removes the previously-stored one (if it
    /// still exists), guaranteeing exactly one embed. Used by /banhammer to
    /// recover from a manually-deleted message. Returns the new message, or null
    /// on failure.
    /// </summary>
    private async Task<IUserMessage?> RepostAsync(SocketTextChannel channel)
    {
        await _embedLock.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db);

            // Fresh DB / never-ensured: start the sign at day 0 from now.
            if (state.BanHammerLastBanUtc is null)
                state.BanHammerLastBanUtc = DateTime.UtcNow;

            // Best-effort delete of the old message so we don't leave a duplicate
            // when the previous one wasn't actually deleted.
            if (state.BanHammerMessageId is { } oldId)
            {
                try
                {
                    if (await channel.GetMessageAsync(oldId) is IUserMessage old)
                        await old.DeleteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Ban Hammer: could not delete prior embed {MessageId} during repost.", oldId);
                }
            }

            var embed = BuildEmbed(channel.Guild, state);
            var posted = await channel.SendMessageAsync(embed: embed);
            try { await posted.PinAsync(); } catch { /* pin is best-effort */ }

            state.BanHammerMessageId = posted.Id;
            await db.SaveChangesAsync();

            _lastRenderedDays = state.BanHammerLastBanUtc is { } s ? DaysSince(s) : 0;
            return posted;
        }
        finally
        {
            _embedLock.Release();
        }
    }

    /// <summary>
    /// Re-pulls the most recent qualifying ban from the audit log and, if one is
    /// found, updates the counter's anchor date. Returns the found timestamp, or
    /// null when nothing qualifying exists in the retention window (the date is
    /// left untouched in that case). Marks the state seeded so the startup seed
    /// won't second-guess a manual resync.
    /// </summary>
    private async Task<DateTime?> ResyncFromHistoryAsync(SocketGuild guild)
    {
        var found = await FindLastQualifyingBanUtcAsync(guild);

        await _embedLock.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db);

            if (found is { } ts)
                state.BanHammerLastBanUtc = ts;

            state.BanHammerSeededFromHistory = true;
            await db.SaveChangesAsync();
        }
        finally
        {
            _embedLock.Release();
        }

        return found;
    }

    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }

    // ── Ready: ensure the counter embed exists ─────────────────────────

    private Task OnReady()
    {
        // Ready fires on every reconnect; only ensure once per process. Restart
        // reconciliation is handled by the stored message ID.
        if (Interlocked.Exchange(ref _ensured, 1) == 1)
            return Task.CompletedTask;

        _ = EnsureEmbedAsync();
        return Task.CompletedTask;
    }

    private async Task EnsureEmbedAsync()
    {
        try
        {
            if (_client.GetChannel(_config.BanHammerChannelId) is not SocketTextChannel channel)
            {
                _logger.LogWarning(
                    "Ban Hammer: configured channel {ChannelId} is not a resolvable text channel; " +
                    "embed not posted.", _config.BanHammerChannelId);
                return;
            }

            await _embedLock.WaitAsync();
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                var state = await GetOrCreateBotStateAsync(db);

                // One-time seed: back-date the counter to the most recent real
                // ban in Discord's audit log (≈45-day retention) so the sign
                // reflects reality instead of starting at deploy time.
                if (!state.BanHammerSeededFromHistory)
                {
                    var found = await FindLastQualifyingBanUtcAsync(channel.Guild);
                    if (found is { } ts)
                    {
                        state.BanHammerLastBanUtc = ts;
                        _logger.LogInformation(
                            "Ban Hammer: seeded last-ban date from audit history ({Date:u}).", ts);
                    }
                    else if (state.BanHammerLastBanUtc is null)
                    {
                        // No qualifying ban in the audit window — start at day 0.
                        state.BanHammerLastBanUtc = DateTime.UtcNow;
                        _logger.LogInformation(
                            "Ban Hammer: no ban found in audit history; starting counter at today.");
                    }

                    state.BanHammerSeededFromHistory = true;
                    await db.SaveChangesAsync();
                }
                else if (state.BanHammerLastBanUtc is null)
                {
                    state.BanHammerLastBanUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }

                var embed = BuildEmbed(channel.Guild, state);

                // Try to edit the existing message first (refreshes the count
                // after a restart and avoids spawning a duplicate embed).
                if (state.BanHammerMessageId is { } existingId)
                {
                    try
                    {
                        if (await channel.GetMessageAsync(existingId) is IUserMessage existing)
                        {
                            await existing.ModifyAsync(m => m.Embed = embed);
                            _logger.LogInformation(
                                "Ban Hammer: refreshed existing embed {MessageId}.", existingId);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Ban Hammer: stored message {MessageId} could not be edited; " +
                            "posting a fresh one.", existingId);
                    }
                }

                var posted = await channel.SendMessageAsync(embed: embed);
                try { await posted.PinAsync(); } catch { /* pin is best-effort */ }

                state.BanHammerMessageId = posted.Id;
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Ban Hammer: posted counter embed as message {MessageId} in #{Channel}.",
                    posted.Id, channel.Name);
            }
            finally
            {
                _embedLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ban Hammer: failed to ensure counter embed.");
        }
    }

    // ── AuditLogCreated: detect human bans and reset the counter ───────

    private Task OnAuditLogCreated(SocketAuditLogEntry entry, SocketGuild guild)
    {
        // Fire-and-forget; gateway handlers must not block the event loop.
        _ = ProcessBanAsync(entry, guild);
        return Task.CompletedTask;
    }

    private async Task ProcessBanAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        try
        {
            if (entry.Action != ActionType.Ban)
                return;

            // Skip bans performed by ClanGuard itself (honeypot / spam-trap
            // enforcement, account-age gate, etc.). This counter is about
            // people the team chooses to ban, not bots the bot catches.
            if (entry.User is not null && entry.User.Id == _client.CurrentUser.Id)
                return;

            var data = await ResolveBanDataAsync(entry, guild);
            if (data is null)
            {
                _logger.LogDebug(
                    "Ban Hammer: could not resolve ban audit data for entry {EntryId}; " +
                    "not counting.", entry.Id);
                return;
            }

            var target = data.Target;
            if (target is null)
                return;

            // Exclude banned bot accounts — only real people reset the sign.
            if (target.IsBot)
            {
                _logger.LogDebug(
                    "Ban Hammer: banned target {User} ({Id}) is a bot; not counting.",
                    target.Username, target.Id);
                return;
            }

            await ResetCounterAsync(guild, entry.User);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ban Hammer: ban handler crashed for entry {EntryId}.", entry.Id);
        }
    }

    private async Task ResetCounterAsync(SocketGuild guild, SocketUser? executor)
    {
        await _embedLock.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db);

            // Close out the streak that just ended; promote it to the record if
            // it beats the current best.
            if (state.BanHammerLastBanUtc is { } since)
            {
                var streakDays = DaysSince(since);
                if (streakDays > state.BanHammerRecordDays)
                    state.BanHammerRecordDays = streakDays;
            }

            state.BanHammerLastBanUtc = DateTime.UtcNow;
            state.BanHammerTotalBans += 1;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Ban Hammer: counter reset (total {Total}, record {Record}d) — ban by {Actor}.",
                state.BanHammerTotalBans, state.BanHammerRecordDays,
                executor?.Username ?? "(unknown)");

            await RenderAsync(guild, state);
        }
        finally
        {
            _embedLock.Release();
        }
    }

    // ── Periodic climb ─────────────────────────────────────────────────

    private async Task RefreshIfChangedAsync()
    {
        try
        {
            if (_client.GetChannel(_config.BanHammerChannelId) is not SocketTextChannel channel)
                return;

            await _embedLock.WaitAsync();
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                var state = await GetOrCreateBotStateAsync(db);

                var days = state.BanHammerLastBanUtc is { } since ? DaysSince(since) : 0;
                if (days == _lastRenderedDays)
                    return; // No visible change — skip the edit.

                await RenderAsync(channel.Guild, state);
            }
            finally
            {
                _embedLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ban Hammer: periodic refresh failed.");
        }
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>
    /// Edits the stored message with a freshly-built embed. Caller must hold
    /// <see cref="_embedLock"/>. Updates <see cref="_lastRenderedDays"/>.
    /// </summary>
    private async Task RenderAsync(SocketGuild guild, BotState state)
    {
        if (state.BanHammerMessageId is not { } msgId
            || _client.GetChannel(_config.BanHammerChannelId) is not SocketTextChannel channel)
            return;

        try
        {
            if (await channel.GetMessageAsync(msgId) is IUserMessage existing)
            {
                var embed = BuildEmbed(guild, state);
                await existing.ModifyAsync(m => m.Embed = embed);
                _lastRenderedDays = state.BanHammerLastBanUtc is { } s ? DaysSince(s) : 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ban Hammer: could not edit counter embed (message {MessageId}).", msgId);
        }
    }

    private Embed BuildEmbed(SocketGuild guild, BotState state)
    {
        var days = state.BanHammerLastBanUtc is { } since ? DaysSince(since) : 0;

        var builder = new EmbedBuilder()
            .WithColor(ColorForDays(days))
            .WithTitle("🔨 THE BAN HAMMER 🔨")
            .WithDescription(
                $"# 🛑 {days} {Plural(days, "day", "days")}\n" +
                "since the last ban\n\n" +
                FlavorLine(days))
            .AddField("📈 Record peace",
                $"{state.BanHammerRecordDays} {Plural(state.BanHammerRecordDays, "day", "days")}",
                inline: true)
            .AddField("⚰️ Total bans", state.BanHammerTotalBans.ToString(), inline: true)
            .WithFooter("189th • Ban Hammer • resets on every ban")
            .WithCurrentTimestamp();

        // Thumbnail: explicit override, else the guild icon (the 189th logo).
        var thumb = !string.IsNullOrWhiteSpace(_config.BanHammerThumbnailUrl)
            ? _config.BanHammerThumbnailUrl
            : guild.IconUrl;
        if (!string.IsNullOrWhiteSpace(thumb))
            builder.WithThumbnailUrl(thumb);

        // Banner image / gif: placeholder until the owner supplies the real one.
        if (!string.IsNullOrWhiteSpace(_config.BanHammerImageUrl))
            builder.WithImageUrl(_config.BanHammerImageUrl);

        return builder.Build();
    }

    private static string FlavorLine(int days) => days switch
    {
        0 => "*The hammer has fallen. 💥*",
        1 => "*One whole day. Easy now.*",
        <= 6 => "*So far, so peaceful. 🕊️*",
        _ => "*A new era of peace. 🏆*",
    };

    private static Color ColorForDays(int days) => days switch
    {
        0      => Color.Red,
        <= 2   => Color.Orange,
        <= 6   => Color.Gold,
        _      => Color.Green,
    };

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Whole days elapsed since <paramref name="sinceUtc"/>, never negative.</summary>
    private static int DaysSince(DateTime sinceUtc)
        => Math.Max(0, (int)Math.Floor((DateTime.UtcNow - sinceUtc).TotalDays));

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>
    /// Upgrades <c>entry.Data</c> to <see cref="BanAuditLogData"/>, re-fetching
    /// via REST when the gateway payload arrives untyped — the same reliability
    /// quirk AuditLogWatcherHandler.ResolveDataAsync handles for bans.
    /// </summary>
    private async Task<BanAuditLogData?> ResolveBanDataAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        if (entry.Data is BanAuditLogData typed)
            return typed;

        try
        {
            await foreach (var page in guild.GetAuditLogsAsync(limit: 10, actionType: ActionType.Ban))
            {
                foreach (var rest in page)
                {
                    if (rest.Id == entry.Id && rest.Data is BanAuditLogData restTyped)
                        return restTyped;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Ban Hammer: REST fallback fetch failed for ban entry {EntryId}.", entry.Id);
        }

        return null;
    }

    /// <summary>
    /// Scans Discord's audit log (newest-first) for the most recent ban that
    /// counts toward the sign — a real person, banned by someone other than
    /// ClanGuard — and returns its UTC timestamp. Returns null if no qualifying
    /// ban exists within the audit log's ≈45-day retention window. Mirrors the
    /// same target.IsBot / executor-is-bot filters the live counter uses, so a
    /// seed agrees with what live events would have recorded.
    /// </summary>
    private async Task<DateTime?> FindLastQualifyingBanUtcAsync(SocketGuild guild)
    {
        // Cap the scan so a guild with a huge ban history can't spin forever.
        // Entries are newest-first, so the first qualifying hit is the answer.
        const int maxToScan = 500;
        var scanned = 0;

        try
        {
            await foreach (var page in guild.GetAuditLogsAsync(limit: maxToScan, actionType: ActionType.Ban))
            {
                foreach (var entry in page)
                {
                    scanned++;

                    // Skip bans performed by ClanGuard itself (honeypot / spam
                    // trap / age gate) — same rule as the live counter.
                    if (entry.User is not null && entry.User.Id == _client.CurrentUser.Id)
                        continue;

                    if (entry.Data is not BanAuditLogData ban || ban.Target is null)
                        continue;

                    // Exclude banned bot accounts — only real people count.
                    if (ban.Target.IsBot)
                        continue;

                    return entry.CreatedAt.UtcDateTime;
                }

                if (scanned >= maxToScan)
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ban Hammer: audit-log history scan failed.");
        }

        return null;
    }

    /// <summary>Singleton BotState load — same pattern the honeypot uses.</summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db)
    {
        var state = await db.BotStates.FirstOrDefaultAsync();
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync();
        }
        return state;
    }
}
