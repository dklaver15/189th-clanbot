using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ClanGuardBot.Services;

/// <summary>
/// Subscribes to Discord gateway events and writes attribution rows
/// (InviteJoin) for every observed UserJoined. Pairs with InviteCacheService —
/// this class owns the gateway wiring and the diff logic; the cache class
/// is a pure state container.
///
/// ── Sentinel labels written on the join row ──
/// See InviteJoin.cs for the full enumeration. The string constants here
/// are the source of truth and are referenced from /invite list / /invite
/// stats output, the briefing, and any future report.
///
/// ── Why we serialize per-guild ──
/// The fundamental attribution mechanism is "diff cached use counts against
/// fresh fetched use counts." If two UserJoined events arrive in rapid
/// succession and process concurrently, both handlers fetch the list and
/// see the same +N uses on the same codes — neither can tell which join
/// caused which increment, so both end up Ambiguous. Serializing per-guild
/// with a SemaphoreSlim lets handler #1 finish (cache rolled forward to
/// post-join state) before handler #2 fetches, so handler #2 sees a clean
/// +1 diff. The pathological case where both joins happen faster than a
/// single fetch + DB write cycle still surfaces as Ambiguous — Discord
/// gives us no better signal — but normal-paced joins resolve cleanly.
/// </summary>
public sealed class InviteAttributionService
{
    // ── Sentinel label values ────────────────────────────────────────
    public const string LabelVanity       = "Vanity";
    public const string LabelUnknown      = "Unknown";
    public const string LabelAmbiguous    = "Ambiguous";
    public const string LabelUnattributed = "Unattributed";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly InviteCacheService _cache;
    private readonly ILogger<InviteAttributionService> _logger;

    // Per-guild semaphore so UserJoined attribution serializes within a guild
    // while still letting other guild events run in parallel. The bot is
    // currently single-guild; this future-proofs for multi-guild without cost.
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();

    public InviteAttributionService(
        IServiceProvider services,
        DiscordSocketClient client,
        InviteCacheService cache,
        ILogger<InviteAttributionService> logger)
    {
        _services = services;
        _client   = client;
        _cache    = cache;
        _logger   = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.Ready          += OnReady;
        client.JoinedGuild    += OnJoinedGuild;
        client.UserJoined     += OnUserJoined;
        client.InviteCreated  += OnInviteCreated;
        client.InviteDeleted  += OnInviteDeleted;
    }

    // ── Cache lifecycle ─────────────────────────────────────────────

    private async Task OnReady()
    {
        // Hydrate every connected guild. Keeps the bot working if it's ever
        // run against more than one guild without any further wiring.
        foreach (var guild in _client.Guilds)
        {
            await _cache.HydrateAsync(guild);
        }
    }

    private async Task OnJoinedGuild(SocketGuild guild)
    {
        // Bot was just added to a new guild; hydrate so attribution starts
        // working from the next UserJoined onward.
        await _cache.HydrateAsync(guild);
    }

    private Task OnInviteCreated(SocketInvite invite)
    {
        // Keep the cache in sync. We deliberately do NOT auto-create an
        // InviteSource row for invites created via the Discord UI — only
        // /invite create and /invite assign produce labeled rows. UI-created
        // invites get LabelSnapshot="Unknown" on their first attribution and
        // can be retroactively labeled with /invite assign.
        //
        // SocketInvite.Uses is non-nullable int (unlike IInviteMetadata.Uses
        // which is int? on the interface) — see SocketInvite.cs:75.
        _cache.SetUses(invite.Code, invite.Uses);
        _logger.LogDebug(
            "Invite created: {Code} (uses={Uses}, channel={Channel})",
            invite.Code, invite.Uses, invite.Channel?.Name ?? "?");
        return Task.CompletedTask;
    }

    private async Task OnInviteDeleted(SocketGuildChannel channel, string code)
    {
        // Same path for both manual revocation (/invite revoke or Discord UI
        // delete) and natural expiration (Discord enforced ExpiresAt or
        // MaxUses). Single deactivation flow keeps the data model honest.
        _cache.Remove(code);

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var source = await db.InviteSources.FirstOrDefaultAsync(s =>
                s.GuildId == channel.Guild.Id && s.Code == code);

            if (source is not null && source.IsActive)
            {
                source.IsActive = false;
                source.DeactivatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogInformation(
                    "Invite source {Code} ({Label}) deactivated (Discord deleted invite)",
                    source.Code, source.Label);
            }
            else
            {
                // Untracked invite (created via Discord UI, never labeled) —
                // nothing in our DB to update. Cache removal is sufficient.
                _logger.LogDebug("Untracked invite {Code} deleted; no source row to deactivate", code);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deactivate InviteSource for code {Code}", code);
        }
    }

    // ── Attribution ─────────────────────────────────────────────────

    private Task OnUserJoined(SocketGuildUser user)
    {
        if (user.IsBot) return Task.CompletedTask;
        // Fire-and-forget; gateway handlers must not block the event loop.
        _ = AttributeAsync(user);
        return Task.CompletedTask;
    }

    private async Task AttributeAsync(SocketGuildUser user)
    {
        var guild = user.Guild;
        var sem = _guildLocks.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync();
        try
        {
            // ── If the cache hasn't been hydrated yet, we have no baseline.
            // Better to write Unattributed and move on than to fabricate.
            if (!_cache.IsHydrated)
            {
                await WriteJoinAsync(guild.Id, user, code: null,
                    label: LabelUnattributed, inviterId: null, ambiguous: false);
                _logger.LogWarning(
                    "User {User} joined before invite cache was hydrated; recorded as Unattributed",
                    user.Username);
                return;
            }

            // ── Fetch fresh state ────────────────────────────────────
            IReadOnlyCollection<IInviteMetadata> currentInvites;
            try
            {
                currentInvites = await guild.GetInvitesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch invites for attribution of {User}", user.Username);
                await WriteJoinAsync(guild.Id, user, code: null,
                    label: LabelUnattributed, inviterId: null, ambiguous: false);
                return;
            }

            int? currentVanityUses = null;
            try
            {
                var vanity = await guild.GetVanityInviteAsync();
                currentVanityUses = vanity?.Uses;
            }
            catch
            {
                // Guild has no vanity URL — leave currentVanityUses null.
            }

            // ── Diff against cache ───────────────────────────────────
            var snapshot = _cache.Snapshot();
            var increased = new List<IInviteMetadata>();
            foreach (var inv in currentInvites)
            {
                var prior = snapshot.TryGetValue(inv.Code, out var p) ? p : 0;
                var now = inv.Uses ?? 0;
                if (now > prior) increased.Add(inv);
            }

            var cachedVanity = _cache.VanityUses;
            var vanityIncreased = currentVanityUses.HasValue
                                  && cachedVanity.HasValue
                                  && currentVanityUses.Value > cachedVanity.Value;

            // ── Resolve outcome ──────────────────────────────────────
            string? winningCode = null;
            ulong? inviterId = null;
            string label;
            bool ambiguous = false;

            int totalIncrements = increased.Count + (vanityIncreased ? 1 : 0);

            if (totalIncrements == 1)
            {
                if (vanityIncreased)
                {
                    label = LabelVanity;
                }
                else
                {
                    var winner = increased[0];
                    winningCode = winner.Code;
                    inviterId = winner.Inviter?.Id;

                    // Resolve label from the InviteSource row, if any.
                    label = await ResolveLabelAsync(guild.Id, winner.Code) ?? LabelUnknown;
                }
            }
            else if (totalIncrements == 0)
            {
                // Could be a single-use invite that was already revoked by
                // Discord between the join and our fetch — its row vanishes
                // from GetInvitesAsync without any visible diff. Could also
                // be server-discovery or any future Discord join path we
                // don't track. Either way, no signal.
                label = LabelUnattributed;
            }
            else
            {
                // 2+ counters bumped — multiple users joined within our
                // serialization window, or vanity AND a regular invite
                // happened to bump together (rare race). We can't tell
                // whose join was whose.
                label = LabelAmbiguous;
                ambiguous = true;
            }

            // ── Persist + roll cache forward ─────────────────────────
            await WriteJoinAsync(guild.Id, user, winningCode, label, inviterId, ambiguous);

            _cache.ReplaceAll(
                currentInvites.Select(i => (i.Code, i.Uses ?? 0)),
                currentVanityUses);

            _logger.LogInformation(
                "Attributed join: {User} → {Label}{CodeFragment}",
                user.Username, label,
                winningCode is null ? "" : $" (code={winningCode})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure attributing {User}", user.Username);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<string?> ResolveLabelAsync(ulong guildId, string code)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var source = await db.InviteSources
            .Where(s => s.GuildId == guildId && s.Code == code)
            .Select(s => s.Label)
            .FirstOrDefaultAsync();
        return source; // null if no row exists for this code
    }

    private async Task WriteJoinAsync(
        ulong guildId,
        SocketGuildUser user,
        string? code,
        string label,
        ulong? inviterId,
        bool ambiguous)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            db.InviteJoins.Add(new InviteJoin
            {
                GuildId          = guildId,
                UserDiscordId    = user.Id,
                Username         = ResolveDisplayName(user),
                InviteCode       = code,
                LabelSnapshot    = label,
                InviterDiscordId = inviterId,
                JoinedAt         = DateTime.UtcNow,
                IsAmbiguous      = ambiguous,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist InviteJoin row for {User}", user.Username);
        }
    }

    private static string ResolveDisplayName(SocketGuildUser user)
    {
        // Match the convention used by EventAttendance.Username — server
        // nickname → global name → underlying username, in that order.
        if (!string.IsNullOrWhiteSpace(user.Nickname))   return user.Nickname;
        if (!string.IsNullOrWhiteSpace(user.GlobalName)) return user.GlobalName;
        return user.Username ?? string.Empty;
    }
}
