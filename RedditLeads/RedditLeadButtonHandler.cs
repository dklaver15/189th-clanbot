using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Handles button clicks on RedditLead embeds. Subscribed to the global
/// ButtonExecuted event; filters by customId prefix so other handlers'
/// buttons are left alone (Discord delivers ButtonExecuted to every
/// registered handler regardless of which message owns the button —
/// same pattern InviteCommandHandler uses).
///
/// ── Race-safe transitions ──
/// Every state change goes through ExecuteUpdateAsync with the
/// "from" state in the WHERE clause. If two officers click Claim on
/// the same New lead at nearly the same moment, exactly one update
/// affects 1 row and the other affects 0. The "0 rows" path is
/// surfaced as an ephemeral "already handled — refreshed view"
/// notice and the embed is re-rendered from current state.
///
/// ── Always re-render after a click ──
/// Whether the click landed or lost the race, we always rebuild the
/// embed from the post-update DB state and call UpdateAsync. That way
/// the officer never sees stale state, even on a lost race.
/// </summary>
public sealed class RedditLeadButtonHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RedditLeadButtonHandler> _logger;

    public RedditLeadButtonHandler(
        IServiceProvider services,
        ILogger<RedditLeadButtonHandler> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.ButtonExecuted += HandleButtonAsync;
    }

    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (string.IsNullOrEmpty(customId) ||
            !customId.StartsWith(RedditLeadEmbedBuilder.CustomIdPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await DispatchAsync(component, customId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling RedditLead button (customId={CustomId})", customId);
            try
            {
                if (component.HasResponded)
                    await component.FollowupAsync("Something went wrong handling that click.", ephemeral: true);
                else
                    await component.RespondAsync("Something went wrong handling that click.", ephemeral: true);
            }
            catch { /* swallowed */ }
        }
    }

    private async Task DispatchAsync(SocketMessageComponent component, string customId)
    {
        // Format: "rl:{leadId}:{action}" — strip the prefix, split once on ':'.
        var rest = customId[RedditLeadEmbedBuilder.CustomIdPrefix.Length..];
        var sep = rest.IndexOf(':');
        if (sep <= 0 || sep == rest.Length - 1)
        {
            _logger.LogWarning("Malformed RedditLead customId: {CustomId}", customId);
            return;
        }
        if (!int.TryParse(rest[..sep], out var leadId) || leadId <= 0)
        {
            _logger.LogWarning("Bad lead id in customId: {CustomId}", customId);
            return;
        }
        var action = rest[(sep + 1)..];

        var clicker   = component.User;
        var clickerId = clicker.Id;
        // Username (no discriminator) — preferred for the audit footer because
        // it stays readable even after Discord's username migration.
        var clickerName = clicker.GlobalName ?? clicker.Username;
        var now = DateTime.UtcNow;

        // Acknowledge fast — Discord gives us 3 seconds before the click
        // visibly times out. UpdateAsync (used below) consumes the response
        // slot, so we DEFER as an update so any DB work has 15 minutes to
        // finish before we have to edit the message.
        await component.DeferAsync();

        var (rowsAffected, raceMessage) = await ApplyTransitionAsync(
            leadId, action, clickerId, clickerName, now);

        // Reload the lead from current state, rebuild the embed, push it.
        RedditLead? fresh = null;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            fresh = await db.RedditLeads.FirstOrDefaultAsync(l => l.Id == leadId);
        }

        if (fresh is null)
        {
            // The lead was hard-deleted between click and dispatch. Vanishingly
            // rare; we don't expose any deletion path. Best we can do is tell
            // the clicker.
            await component.FollowupAsync("That lead no longer exists.", ephemeral: true);
            return;
        }

        var embed = RedditLeadEmbedBuilder.Build(fresh);
        var components = RedditLeadEmbedBuilder.BuildComponents(fresh);

        await component.ModifyOriginalResponseAsync(props =>
        {
            props.Embed = embed;
            props.Components = components ?? new ComponentBuilder().Build();
        });

        // Toast: confirm or explain the race-loss to the clicker only.
        if (rowsAffected > 0)
        {
            await component.FollowupAsync(BuildSuccessToast(action, fresh), ephemeral: true);
        }
        else
        {
            await component.FollowupAsync(
                raceMessage ?? "That action wasn't valid for this lead's current state. Embed refreshed.",
                ephemeral: true);
        }
    }

    /// <summary>
    /// Apply the requested transition with a from-state guard. Returns the
    /// number of rows affected (0 on a lost race or invalid action) and an
    /// optional human-readable message describing why a 0-row result happened.
    /// </summary>
    private async Task<(int Rows, string? Message)> ApplyTransitionAsync(
        int leadId, string action, ulong clickerId, string clickerName, DateTime now)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        switch (action)
        {
            case "claim":
            {
                var rows = await db.RedditLeads
                    .Where(l => l.Id == leadId && l.Status == LeadStatus.New)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,                 LeadStatus.Claimed)
                        .SetProperty(l => l.ClaimedByDiscordUserId, (ulong?)clickerId)
                        .SetProperty(l => l.ClaimedByUsername,      (string?)clickerName)
                        .SetProperty(l => l.ClaimedAtUtc,           (DateTime?)now));
                return (rows, rows == 0 ? "Already claimed by another officer — embed refreshed." : null);
            }

            case "unclaim":
            {
                // Unclaim is only valid from Claimed (not Contacted — once we've
                // reached out, "I take it back" doesn't really make sense; the
                // recruit might have already heard from us).
                var rows = await db.RedditLeads
                    .Where(l => l.Id == leadId && l.Status == LeadStatus.Claimed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,                 LeadStatus.New)
                        .SetProperty(l => l.ClaimedByDiscordUserId, (ulong?)null)
                        .SetProperty(l => l.ClaimedByUsername,      (string?)null)
                        .SetProperty(l => l.ClaimedAtUtc,           (DateTime?)null));
                return (rows, rows == 0 ? "Can't unclaim — lead is no longer in Claimed state." : null);
            }

            case "skip":
            {
                // Skip is only valid from New — once someone claims, the
                // outcome buttons (Joined/Declined/NoResponse) are how you
                // close the lead out, not Skip.
                var rows = await db.RedditLeads
                    .Where(l => l.Id == leadId && l.Status == LeadStatus.New)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,       LeadStatus.Skipped)
                        .SetProperty(l => l.OutcomeAtUtc, (DateTime?)now));
                return (rows, rows == 0 ? "Can't skip — lead has already been claimed or closed." : null);
            }

            case "contacted":
            {
                // Only from Claimed. Marking Contacted preserves the claim
                // metadata (who/when claimed) untouched.
                var rows = await db.RedditLeads
                    .Where(l => l.Id == leadId && l.Status == LeadStatus.Claimed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,         LeadStatus.Contacted)
                        .SetProperty(l => l.ContactedAtUtc, (DateTime?)now));
                return (rows, rows == 0 ? "Can't mark contacted from current state." : null);
            }

            case "joined":
            case "declined":
            case "noresp":
            {
                var terminal = action switch
                {
                    "joined"   => LeadStatus.Joined,
                    "declined" => LeadStatus.Declined,
                    "noresp"   => LeadStatus.NoResponse,
                    _          => LeadStatus.NoResponse,
                };

                // Valid from either Claimed or Contacted. We don't require a
                // Contacted stop along the way — sometimes you decide before
                // reaching out (e.g. you spot they're a known griefer).
                var rows = await db.RedditLeads
                    .Where(l => l.Id == leadId &&
                               (l.Status == LeadStatus.Claimed || l.Status == LeadStatus.Contacted))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,       terminal)
                        .SetProperty(l => l.OutcomeAtUtc, (DateTime?)now));
                return (rows, rows == 0 ? "Lead must be Claimed or Contacted before recording an outcome." : null);
            }

            default:
                _logger.LogWarning("Unknown RedditLead action: {Action} (lead {Id})", action, leadId);
                return (0, "Unknown action.");
        }
    }

    private static string BuildSuccessToast(string action, RedditLead lead) => action switch
    {
        "claim"     => $"✋ Claimed. Lead is yours — go get 'em.",
        "unclaim"   => $"↩️ Unclaimed. Back in the pool.",
        "skip"      => $"⏭️ Skipped. Lead closed.",
        "contacted" => $"✉️ Marked contacted at {lead.ContactedAtUtc:HH:mm} UTC.",
        "joined"    => $"🎉 Recorded as Joined. Nice work.",
        "declined"  => $"👋 Recorded as Declined.",
        "noresp"    => $"💤 Recorded as No Response.",
        _           => "Done.",
    };
}
