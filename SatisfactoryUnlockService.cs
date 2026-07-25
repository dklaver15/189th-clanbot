using System.Text.RegularExpressions;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>Values stored in <see cref="SatisfactoryUnlock.Kind"/>.</summary>
public static class SatisfactoryUnlockKinds
{
    public const string Milestone = "Milestone";
    public const string Research = "Research";
}

/// <summary>
/// Announces progress on the clan's Satisfactory server: milestones (schematics)
/// and M.A.M. research, posted the first time each is seen complete.
///
/// ── Why the two halves poll at different rates ──
/// Measured against the live server on 2026-07-25:
///   • getResearchTrees — ~72 KB, 97 nodes
///   • getSchematics    — ~1.1 MB, 575 entries
/// Both run on the GAME THREAD, so the schematic poll is the expensive one and
/// runs on its own much slower timer. A milestone announced twenty minutes late
/// is still good news; a stuttering server is not.
///
/// ── State lives in the database, not in memory ──
/// Unlike the power alerts, these are one-time events that can't be re-derived
/// after the fact. <see cref="SatisfactoryUnlock"/> rows are the record of what
/// we've already announced, which is what makes this survive a redeploy without
/// either losing or repeating anything.
///
/// ── First run on a save is silent ──
/// If a save has no rows yet, everything currently complete is inserted WITHOUT
/// announcing — otherwise switching this on would dump forty milestones into the
/// channel at once. Because rows are scoped by world seed, starting a fresh save
/// re-seeds silently rather than re-announcing tier 1.
///
/// ── Gating ──
/// Idle unless Satisfactory + FRM are configured and
/// <see cref="BotConfig.SatisfactoryUnlockFeedEnabled"/> is on.
/// </summary>
public sealed class SatisfactoryUnlockService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

    private readonly DiscordSocketClient _client;
    private readonly FrmApiService _frm;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryUnlockService> _logger;

    /// <summary>When the expensive schematic poll last ran.</summary>
    private DateTime _lastMilestonePollUtc = DateTime.MinValue;

    public SatisfactoryUnlockService(
        DiscordSocketClient client,
        FrmApiService frm,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryUnlockService> logger)
    {
        _client = client;
        _frm = frm;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan ResearchInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryResearchPollSeconds, 60, 3600));

    private TimeSpan MilestoneInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryMilestonePollSeconds, 300, 21600));

    private bool Active =>
        _config.SatisfactoryEnabled && _config.FrmEnabled && _frm.IsConfigured
        && _config.SatisfactoryUnlockFeedEnabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!Active)
        {
            _logger.LogInformation("SatisfactoryUnlockService idle (enabled={Enabled})",
                _config.SatisfactoryUnlockFeedEnabled);
            return;
        }

        _logger.LogInformation(
            "SatisfactoryUnlockService started; research every {Research}s, milestones every {Milestone}s",
            (int)ResearchInterval.TotalSeconds, (int)MilestoneInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SatisfactoryUnlockService tick failed; will retry on next poll");
            }

            try { await Task.Delay(ResearchInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // The seed identifies the save. Without it we can't tell "nothing new"
        // from "different world", so skip the tick rather than risk writing rows
        // against the wrong save.
        var session = await _frm.GetSessionInfoAsync(ct);
        if (session is null) return;

        await CheckResearchAsync(session.Seed, ct);

        if (DateTime.UtcNow - _lastMilestonePollUtc >= MilestoneInterval)
        {
            _lastMilestonePollUtc = DateTime.UtcNow;
            await CheckMilestonesAsync(session.Seed, ct);
        }
    }

    // ─── Research (cheap poll) ───────────────────────────────────────────────

    private async Task CheckResearchAsync(long seed, CancellationToken ct)
    {
        var trees = await _frm.GetResearchTreesAsync(ct);
        if (trees is null) return;   // unreachable — hold, don't infer anything

        var completed = trees
            .SelectMany(t => t.Nodes ?? Array.Empty<FrmResearchNode>())
            .Where(IsComplete)
            .Select(n => new Candidate(
                UnlockId: n.Id,
                Name: DisplayName(n.Name, n.Id),
                TechTier: n.TechTier,
                Detail: string.IsNullOrWhiteSpace(n.Category) ? null : n.Category))
            .ToList();

        await ReconcileAsync(seed, SatisfactoryUnlockKinds.Research, completed, ct);
    }

    /// <summary>
    /// Observed states on the live server are "Purchased", "Available" and
    /// "Locked" — only the first means done. Compared case-insensitively because
    /// the value is a display-ish string, not an enum.
    /// </summary>
    private static bool IsComplete(FrmResearchNode n) =>
        string.Equals(n.State, "Purchased", StringComparison.OrdinalIgnoreCase);

    // ─── Milestones (expensive poll) ─────────────────────────────────────────

    private async Task CheckMilestonesAsync(long seed, CancellationToken ct)
    {
        var schematics = await _frm.GetSchematicsAsync(ct);
        if (schematics is null) return;

        var completed = schematics
            .Where(s => s.Purchased)
            .Select(s => new Candidate(
                UnlockId: s.Id,
                Name: DisplayName(s.Name, s.Id),
                TechTier: s.TechTier,
                Detail: string.IsNullOrWhiteSpace(s.Type) ? null : s.Type))
            .ToList();

        await ReconcileAsync(seed, SatisfactoryUnlockKinds.Milestone, completed, ct);
    }

    // ─── Shared diff + announce ──────────────────────────────────────────────

    private sealed record Candidate(string UnlockId, string Name, int TechTier, string? Detail);

    /// <summary>
    /// Compares what the server says is complete against what we've already
    /// recorded, writes the difference, and announces it — unless this is the
    /// first time we've seen this save, in which case it records silently.
    /// </summary>
    private async Task ReconcileAsync(long seed, string kind, List<Candidate> completed, CancellationToken ct)
    {
        if (completed.Count == 0) return;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var known = await db.SatisfactoryUnlocks
            .Where(u => u.Seed == seed && u.Kind == kind)
            .Select(u => u.UnlockId)
            .ToListAsync(ct);

        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        var fresh = completed.Where(c => !knownSet.Contains(c.UnlockId)).ToList();
        if (fresh.Count == 0) return;

        // No rows for this save yet: record everything, announce nothing. This is
        // both the first-ever run and the first run after a new save.
        var seeding = known.Count == 0;

        var now = DateTime.UtcNow;
        foreach (var c in fresh)
        {
            db.SatisfactoryUnlocks.Add(new SatisfactoryUnlock
            {
                Seed = seed,
                Kind = kind,
                UnlockId = c.UnlockId,
                Name = c.Name,
                TechTier = c.TechTier,
                CompletedUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);

        if (seeding)
        {
            _logger.LogInformation(
                "Satisfactory {Kind}: seeded {Count} already-complete entr(ies) for seed {Seed} — not announced",
                kind, fresh.Count, seed);
            return;
        }

        _logger.LogInformation("Satisfactory {Kind}: {Count} newly completed", kind, fresh.Count);
        await AnnounceAsync(kind, fresh, ct);
    }

    /// <summary>
    /// Posts the completions. A burst gets ONE embed rather than a message each:
    /// finishing a tier unlocks several schematics at once, and ten separate
    /// posts would read as spam.
    /// </summary>
    private async Task AnnounceAsync(string kind, List<Candidate> fresh, CancellationToken ct)
    {
        var isResearch = kind == SatisfactoryUnlockKinds.Research;

        var embed = new EmbedBuilder()
            .WithColor(isResearch ? new Color(0x9B59B6) : new Color(0xE59344))
            .WithCurrentTimestamp();

        embed.WithTitle(fresh.Count == 1
            ? (isResearch ? "🔬 Research complete" : "🎉 Milestone complete")
            : (isResearch ? $"🔬 {fresh.Count} research nodes complete" : $"🎉 {fresh.Count} milestones complete"));

        var lines = fresh
            .OrderBy(c => c.TechTier)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(c =>
            {
                var bits = new List<string>();
                if (c.TechTier > 0) bits.Add($"tier {c.TechTier}");
                if (c.Detail is not null) bits.Add(c.Detail);
                var suffix = bits.Count > 0 ? $" _({string.Join(" · ", bits)})_" : "";
                return $"• **{Escape(c.Name)}**{suffix}";
            })
            .ToList();

        if (fresh.Count > 15) lines.Add($"…and {fresh.Count - 15} more");

        embed.WithDescription(string.Join("\n", lines));

        await PostAsync(embed.Build(), ct);
    }

    // ─── Naming ──────────────────────────────────────────────────────────────

    /// <summary>
    /// FRM returns an EMPTY Name for some schematics — confirmed on the live
    /// server, where <c>Schematic_XMassTree_C</c> has <c>"Name": ""</c>. Falling
    /// back to a prettified id turns that into "XMass Tree" rather than
    /// announcing a milestone with a blank name.
    /// </summary>
    private static string DisplayName(string? reported, string id)
    {
        if (!string.IsNullOrWhiteSpace(reported)) return reported.Trim();
        if (string.IsNullOrWhiteSpace(id)) return "(unnamed)";

        // Schematic_XMassTree_C → XMassTree → "XMass Tree"
        // BPD_ResearchTreeNode_C_33 → keeps its shape; there's nothing better to
        // derive, and it's at least identifiable.
        var s = Regex.Replace(id, @"^(Schematic|BPD|Research)_", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"_C(_\d+)?$", "", RegexOptions.IgnoreCase);
        s = s.Replace('_', ' ').Trim();

        // Split camel case so "XMassTree" reads as words.
        s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");

        return string.IsNullOrWhiteSpace(s) ? id : s;
    }

    // ─── Posting ─────────────────────────────────────────────────────────────

    private async Task PostAsync(Embed embed, CancellationToken ct)
    {
        var channelId = _config.SatisfactoryUnlockChannelId != 0
            ? _config.SatisfactoryUnlockChannelId
            : _config.SatisfactoryFeedChannelId;

        if (channelId == 0) return;

        var channel = _client.GetChannel(channelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(channelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

        if (channel is null)
        {
            _logger.LogWarning("SatisfactoryUnlockService: could not resolve channel {ChannelId}", channelId);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cts.Token });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryUnlockService: failed to post announcement");
        }
    }

    /// <summary>
    /// Names come from game data, but they're still text landing in a Discord
    /// message — neutralize markdown so a mod-supplied name can't forge
    /// formatting or a mass-ping.
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
