using ClanGuardBot.Data;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Builds the Satisfactory factory report embed — session age, power, top
/// production, AWESOME Sink progress, space-elevator phase, and who's been
/// playing.
///
/// ── Why this is its own class ──
/// Two callers want the same embed: <see cref="SatisfactoryFactoryService"/>
/// posts it on a daily schedule, and <c>/satisfactory-report</c> posts it on
/// demand. Putting it here avoids the alternative — resolving the hosted service
/// from DI to call a method on it — which quietly constructs a SECOND instance
/// unless the registration is written very carefully. A stateless builder can't
/// have that problem.
///
/// ── Failure semantics ──
/// Returns null when the server is unreachable, matching
/// <see cref="FrmApiService"/>. Individual sections are best-effort: a section
/// whose endpoint failed is omitted rather than rendered empty, so a partial
/// outage produces a shorter report instead of one full of zeroes.
/// </summary>
public sealed class SatisfactoryDigestBuilder
{
    /// <summary>How far back "recently active" looks for the player list.</summary>
    private static readonly TimeSpan PlayerWindow = TimeSpan.FromDays(7);

    private readonly FrmApiService _frm;
    private readonly IServiceProvider _services;
    private readonly ILogger<SatisfactoryDigestBuilder> _logger;

    public SatisfactoryDigestBuilder(
        FrmApiService frm,
        IServiceProvider services,
        ILogger<SatisfactoryDigestBuilder> logger)
    {
        _frm = frm;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Builds the report. Null means the server (or FRM) couldn't be reached at
    /// all — callers should say so rather than posting an empty embed.
    /// </summary>
    public async Task<Embed?> BuildAsync(CancellationToken ct = default)
    {
        // getSessionInfo is the anchor: if that fails there's nothing worth
        // posting, so bail before spending the other four requests.
        var session = await _frm.GetSessionInfoAsync(ct);
        if (session is null)
        {
            _logger.LogInformation("Satisfactory digest not built — server unreachable");
            return null;
        }

        var power = await _frm.GetPowerAsync(ct);
        var prod = await _frm.GetProdStatsAsync(ct);
        var sink = await _frm.GetResourceSinkAsync(ct);
        var elevators = await _frm.GetSpaceElevatorAsync(ct);

        var embed = new EmbedBuilder()
            .WithTitle($"🏭 Factory report — {Escape(session.SessionName)}")
            .WithColor(new Color(0xE59344))
            .WithCurrentTimestamp();

        embed.AddField("Day", session.PassedDays.ToString(), true);
        embed.AddField("Total playtime", session.TotalPlayDurationText, true);
        embed.AddField("Deaths", session.NumberOfDaysSinceLastDeath == 0
            ? "someone died today"
            : $"{session.NumberOfDaysSinceLastDeath} day(s) clean", true);

        AddPowerField(embed, power);
        AddProductionField(embed, prod);
        AddSinkField(embed, sink);
        AddElevatorField(embed, elevators);
        await AddPlayersFieldAsync(embed, ct);

        embed.WithFooter("Live figures from the server at the time of posting.");

        return embed.Build();
    }

    private static void AddPowerField(EmbedBuilder embed, IReadOnlyList<FrmPowerCircuit>? power)
    {
        if (power is null || power.Count == 0) return;

        var production = power.Sum(c => c.PowerProduction);
        var consumed = power.Sum(c => c.PowerConsumed);
        var capacity = power.Sum(c => c.PowerCapacity);
        var tripped = power.Count(c => c.FuseTriggered);

        var text = $"{consumed:0.#} / {capacity:0.#} MW used\n{production:0.#} MW produced across {power.Count} circuit(s)";
        if (tripped > 0) text += $"\n⚡ **{tripped} tripped fuse(s)**";

        embed.AddField("Power", text, false);
    }

    /// <summary>
    /// Top items by current output, with how hard each line is running.
    ///
    /// getProdStats lists every item the save knows about, and in an early-game
    /// factory most sit at zero — so this filters to things actually producing
    /// and says nothing rather than printing a wall of "0.0".
    /// </summary>
    private static void AddProductionField(EmbedBuilder embed, IReadOnlyList<FrmProdStat>? prod)
    {
        if (prod is null) return;

        var producing = prod
            .Where(p => p.CurrentProd > 0.01)
            .OrderByDescending(p => p.CurrentProd)
            .Take(5)
            .ToList();

        if (producing.Count == 0) return;

        var lines = producing.Select(p =>
        {
            // MaxProd is 0 for consume-only items (Leaves, for one) and can be 0
            // transiently — never divide without this guard.
            var pct = p.MaxProd > 0 ? $" ({100.0 * p.CurrentProd / p.MaxProd:0}% of max)" : "";
            return $"• **{Escape(p.Name)}** — {p.CurrentProd:0.#}/min{pct}";
        });

        embed.AddField("Top production", string.Join("\n", lines), false);
    }

    private static void AddSinkField(EmbedBuilder embed, IReadOnlyList<FrmResourceSink>? sink)
    {
        var s = sink?.FirstOrDefault();
        if (s is null) return;

        embed.AddField("AWESOME Sink",
            $"{s.NumCoupon} coupon(s) · {s.TotalPoints:N0} points\nNext coupon at {s.PointsToCoupon:N0} ({s.Percent:0.#}%)",
            false);
    }

    /// <summary>
    /// Space-elevator progress, when there is one. The clan had none as of
    /// 2026-07-25 (phase 0), so this is usually absent — and
    /// <see cref="FrmSpaceElevator"/> is the one model still unverified against
    /// real data, hence the defensive null checks.
    /// </summary>
    private static void AddElevatorField(EmbedBuilder embed, IReadOnlyList<FrmSpaceElevator>? elevators)
    {
        var e = elevators?.FirstOrDefault();
        if (e is null) return;

        if (e.CurrentPhase is null || e.CurrentPhase.Count == 0)
        {
            embed.AddField("Space elevator", e.FullyUpgraded ? "Fully upgraded" : "No phase in progress", false);
            return;
        }

        var lines = e.CurrentPhase
            .OrderByDescending(i => i.TotalCost == 0 ? 0 : (double)i.RemainingCost / i.TotalCost)
            .Take(5)
            .Select(i =>
            {
                var done = i.TotalCost - i.RemainingCost;
                var pct = i.TotalCost == 0 ? 100 : (int)(100.0 * done / i.TotalCost);
                return $"• {Escape(i.Name)} — {done:N0}/{i.TotalCost:N0} ({pct}%)";
            });

        embed.AddField("Space elevator phase", string.Join("\n", lines), false);
    }

    /// <summary>
    /// Who's been on lately, from our own session table rather than the live
    /// player list — the report is about the period, not the moment.
    ///
    /// Approximation: a session counts if it STARTED inside the window, so a
    /// marathon that began before the cutoff is excluded rather than
    /// part-counted. Good enough for a summary, and it never double-counts.
    /// </summary>
    private async Task AddPlayersFieldAsync(EmbedBuilder embed, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var cutoff = DateTime.UtcNow - PlayerWindow;
        var recent = await db.SatisfactorySessions
            .Where(s => s.StartedUtc >= cutoff)
            .ToListAsync(ct);

        if (recent.Count == 0) return;

        var lines = recent
            .GroupBy(s => s.PlayerName, StringComparer.Ordinal)
            .Select(g => new { Name = g.Key, Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration) })
            .OrderByDescending(p => p.Total)
            .Take(5)
            .Select(p => $"• **{Escape(p.Name)}** — {SatisfactoryPresenceService.Humanize(p.Total)}");

        embed.AddField("Most active (7 days)", string.Join("\n", lines), false);
    }

    /// <summary>
    /// Session and item names are builder-controlled text landing in a Discord
    /// message. Neutralize markdown so they can't forge formatting or a mass-ping.
    /// </summary>
    private static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "(unnamed)"
            : s.Replace("\\", "\\\\")
               .Replace("*", "\\*")
               .Replace("_", "\\_")
               .Replace("~", "\\~")
               .Replace("`", "\\`")
               .Replace("|", "\\|")
               .Replace("@", "@​")
               .Replace("\n", " ")
               .Replace("\r", " ");
}
