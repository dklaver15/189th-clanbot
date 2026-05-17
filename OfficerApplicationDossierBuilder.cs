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
/// Assembles the dossier embed posted to the HQ channel when a member submits
/// an officer application. The dossier combines the applicant's three
/// modal answers with their ClanGuard service record so HQ can review a
/// single artifact instead of cross-referencing multiple commands.
///
/// ── Data sources ──
///   • Discord (live)         — display name, mention, user ID, avatar,
///                              server join date, role list (rank + AWOL +
///                              Reserve detection)
///   • RankHistory (via       — current-rank assigned-at date for "time in
///     PromotionService)        rank" computation
///   • MessageEvents          — windowed message counts (7 / 30 / 60 days)
///   • VoiceSessions (via     — windowed voice hours (7 / 30 / 60 days)
///     VoiceActivityHelper)
///   • EventAttendances       — windowed event-attendance counts
///                              (30 / 60 / 90 days)
///
/// ── Pure(-ish) builder ──
/// This class never sends a Discord message itself. The caller posts the
/// returned <see cref="Embed"/> and is responsible for AllowedMentions, HQ
/// channel routing, and persistence of the resulting message ID back onto
/// the OfficerApplication row.
///
/// In Phase 3, this builder also exposes BuildReviewedEmbedFromExisting
/// which the review handler uses to transform a pending-state dossier into
/// its approved or denied form while preserving the original activity
/// snapshot.
/// </summary>
public sealed class OfficerApplicationDossierBuilder
{
    /// <summary>Pending embed color — mustard / amber.</summary>
    public static readonly Color PendingColor = new(0xC9, 0xA2, 0x27);

    private readonly IServiceProvider _services;
    private readonly PromotionService _promotion;
    private readonly ILogger<OfficerApplicationDossierBuilder> _logger;
    private readonly BotConfig _config;

    public OfficerApplicationDossierBuilder(
        IServiceProvider services,
        PromotionService promotion,
        ILogger<OfficerApplicationDossierBuilder> logger,
        IOptions<BotConfig> config)
    {
        _services  = services;
        _promotion = promotion;
        _logger    = logger;
        _config    = config.Value;
    }

    /// <summary>
    /// Builds the dossier embed for the given submitted application.
    /// </summary>
    public async Task<Embed> BuildAsync(
        SocketGuildUser member,
        OfficerApplication application,
        CancellationToken ct = default)
    {
        var guild = member.Guild;
        var now   = DateTime.UtcNow;

        // ── Identity ────────────────────────────────────────────────
        var joinedAtUtc = member.JoinedAt?.UtcDateTime;
        var tenureDays  = joinedAtUtc is { } j ? Math.Max(0, (int)(now - j).TotalDays) : (int?)null;

        // ── Rank ────────────────────────────────────────────────────
        var rankInfo    = await _promotion.GetRankInfoAsync(guild.Id, member, ct);
        var currentRank = GetCurrentRank(member);

        // ── Status flags ────────────────────────────────────────────
        var isAwol = member.Roles.Any(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        var isReserve = !string.IsNullOrWhiteSpace(_config.ReserveRoleName)
            && member.Roles.Any(r =>
                r.Name.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));

        // ── Activity windows ────────────────────────────────────────
        var (msg7, msg30, msg60)   = await GetMessageCountsAsync(guild.Id, member.Id, ct);
        var (vh7,  vh30,  vh60)    = await GetVoiceHoursAsync(guild.Id, member.Id, ct);
        var (ev30, ev60,  ev90)    = await GetEventAttendanceAsync(guild.Id, member.Id, ct);
        var lastVoice              = await GetLastVoiceSessionAsync(guild.Id, member.Id, ct);

        // ── Compose ─────────────────────────────────────────────────
        var builder = new EmbedBuilder()
            .WithTitle("📋 Officer Application — Pending Review")
            .WithColor(PendingColor)
            .WithThumbnailUrl(member.GetDisplayAvatarUrl(size: 256) ?? member.GetDefaultAvatarUrl())
            .WithTimestamp(application.SubmittedAt);

        // Member identity — same display pattern as the AWOL HQ embed
        // (bold display name → username in parens → mention pill for tap).
        builder.AddField("Applicant",
            $"**[{member.DisplayName}](https://discord.com/users/{member.Id})** "
            + $"({member.Username}) — {member.Mention}\n"
            + $"User ID: `{member.Id}`");

        // Service record
        var serviceRecord = new System.Text.StringBuilder();
        serviceRecord.AppendLine($"**Current Rank:** {currentRank ?? "_(none detected)_"}");
        if (rankInfo.AssignedAt is { } assignedAt)
        {
            var daysInRank = Math.Max(0, (int)(now - assignedAt).TotalDays);
            serviceRecord.AppendLine(
                $"**Time in Rank:** {daysInRank} day{(daysInRank == 1 ? "" : "s")} "
                + $"(since <t:{new DateTimeOffset(assignedAt, TimeSpan.Zero).ToUnixTimeSeconds()}:D>)");
        }
        if (joinedAtUtc is { } joined)
        {
            serviceRecord.AppendLine(
                $"**Joined Server:** <t:{new DateTimeOffset(joined, TimeSpan.Zero).ToUnixTimeSeconds()}:D> "
                + $"({tenureDays} day{(tenureDays == 1 ? "" : "s")} ago)");
        }
        else
        {
            serviceRecord.AppendLine("**Joined Server:** _(unknown)_");
        }
        serviceRecord.AppendLine($"**Currently AWOL:** {(isAwol ? "⚠️ Yes" : "No")}");
        serviceRecord.AppendLine($"**Reserve:** {(isReserve ? "Yes" : "No")}");
        builder.AddField("🎖️ Service Record", serviceRecord.ToString());

        // Activity table — three windows side by side per metric for quick scan
        builder.AddField("📅 Event Attendance",
            $"`30d:` **{ev30}**  ·  `60d:` **{ev60}**  ·  `90d:` **{ev90}**", inline: true);
        builder.AddField("🎙️ Voice Activity",
            $"`7d:` **{vh7:F1}h**  ·  `30d:` **{vh30:F1}h**  ·  `60d:` **{vh60:F1}h**", inline: true);
        builder.AddField("💬 Messages",
            $"`7d:` **{msg7}**  ·  `30d:` **{msg30}**  ·  `60d:` **{msg60}**", inline: true);

        if (lastVoice is { } lv)
        {
            builder.AddField("Last Voice Session",
                $"<t:{new DateTimeOffset(lv, TimeSpan.Zero).ToUnixTimeSeconds()}:R>", inline: true);
        }

        // Application answers
        builder.AddField("📝 Q1 — Where do you want to contribute?",
            Truncate(application.Q1AreaOfInterest, 1024));
        builder.AddField("📝 Q2 — Ideas you'd bring to the 189th",
            Truncate(application.Q2Ideas, 1024));
        builder.AddField("📝 Q3 — Handling conflict during an event",
            Truncate(application.Q3Conflict, 1024));

        builder.WithFooter($"Application #{application.Id} • Submitted");

        return builder.Build();
    }

    /// <summary>
    /// Walks the member's roles top-down through the RankRoles list and
    /// returns the highest match. SyncWithHandlers: matches the
    /// GetCurrentRank implementations in PromoEligibilityCommandHandler
    /// and AutoPromotionService — keep them in sync.
    /// </summary>
    private string? GetCurrentRank(SocketGuildUser member)
    {
        var rankRoles = _config.GetRankRolesList();
        var memberRoleNames = member.Roles
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Contains(rankRoles[i]))
                return rankRoles[i];
        }
        return null;
    }

    private async Task<(int d7, int d30, int d60)> GetMessageCountsAsync(
        ulong guildId, ulong userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var c7  = await db.MessageEvents.CountAsync(m =>
            m.GuildId == guildId && m.UserId == userId && m.Timestamp >= now.AddDays(-7), ct);
        var c30 = await db.MessageEvents.CountAsync(m =>
            m.GuildId == guildId && m.UserId == userId && m.Timestamp >= now.AddDays(-30), ct);
        var c60 = await db.MessageEvents.CountAsync(m =>
            m.GuildId == guildId && m.UserId == userId && m.Timestamp >= now.AddDays(-60), ct);

        return (c7, c30, c60);
    }

    private async Task<(double h7, double h30, double h60)> GetVoiceHoursAsync(
        ulong guildId, ulong userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var s7  = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, guildId, userId, now.AddDays(-7),  _config.MaxSingleSessionHours, ct);
        var s30 = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, guildId, userId, now.AddDays(-30), _config.MaxSingleSessionHours, ct);
        var s60 = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, guildId, userId, now.AddDays(-60), _config.MaxSingleSessionHours, ct);

        return (s7 / 3600.0, s30 / 3600.0, s60 / 3600.0);
    }

    private async Task<(int d30, int d60, int d90)> GetEventAttendanceAsync(
        ulong guildId, ulong userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;

        // Per clan policy "attending a meeting counts as an event", the
        // dossier's 30/60/90 day windows include BOTH EventAttendance and
        // MeetingAttendance rows. Kept as separate per-table counts and
        // summed in-process rather than UNION'd in SQL because EF Core 8
        // doesn't lower set operations across DbSets cleanly on SQLite, and
        // these are tiny indexed counts — two queries each is fine.
        var c30e = await db.EventAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-30), ct);
        var c60e = await db.EventAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-60), ct);
        var c90e = await db.EventAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-90), ct);

        var c30m = await db.MeetingAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-30), ct);
        var c60m = await db.MeetingAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-60), ct);
        var c90m = await db.MeetingAttendances.CountAsync(a =>
            a.GuildId == guildId && a.UserId == userId && a.EventEndUtc >= now.AddDays(-90), ct);

        return (c30e + c30m, c60e + c60m, c90e + c90m);
    }

    private async Task<DateTime?> GetLastVoiceSessionAsync(
        ulong guildId, ulong userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // The most recent session by JoinedAt is "last session" — even if
        // LeftAt is null (currently active), that's still the latest one.
        var lastJoin = await db.VoiceSessions
            .Where(v => v.GuildId == guildId && v.UserId == userId)
            .OrderByDescending(v => v.JoinedAt)
            .Select(v => (DateTime?)v.JoinedAt)
            .FirstOrDefaultAsync(ct);

        return lastJoin;
    }

    /// <summary>
    /// Transforms a pending-state dossier embed into its reviewed form
    /// (approved or denied). Preserves all the original activity / identity
    /// fields exactly as they were at submission so the audit trail reflects
    /// the decision-making snapshot, then prepends a Review field showing
    /// who reviewed it and when (plus the denial reason, if denied) and
    /// flips the title + color to match.
    ///
    /// Called by OfficerApplicationReviewHandler.UpdateDossierAsync when the
    /// original dossier message is still available. If the original message
    /// was deleted, the review handler skips this path and posts a fallback
    /// summary instead — see Phase 3 decision #8.
    /// </summary>
    public Embed BuildReviewedEmbedFromExisting(
        IEmbed original,
        OfficerApplication application,
        SocketUser reviewer)
    {
        var builder = original.ToEmbedBuilder();

        if (application.Status == OfficerApplicationStatus.Approved)
        {
            builder.WithTitle("✅ Officer Application — Approved")
                   .WithColor(new Color(0x4A, 0xC9, 0x59));
        }
        else if (application.Status == OfficerApplicationStatus.Denied)
        {
            builder.WithTitle("❌ Officer Application — Denied")
                   .WithColor(new Color(0xC9, 0x42, 0x42));
        }

        var reviewedAtUnix = application.ReviewedAt is { } reviewedAt
            ? new DateTimeOffset(reviewedAt, TimeSpan.Zero).ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var reviewValue = $"{reviewer.Mention} · <t:{reviewedAtUnix}:f>";
        if (application.Status == OfficerApplicationStatus.Denied
            && !string.IsNullOrWhiteSpace(application.ReviewNotes))
        {
            reviewValue += $"\n\n**Reason:**\n{application.ReviewNotes}";
        }

        // Insert the Review field at index 1 — right after Applicant — so
        // reviewers and audit scrollers see the verdict before the rest of
        // the dossier.
        var insertIndex = Math.Min(1, builder.Fields.Count);
        builder.Fields.Insert(insertIndex, new EmbedFieldBuilder()
            .WithName("🔎 Review")
            .WithValue(reviewValue)
            .WithIsInline(false));

        return builder.Build();
    }

    /// <summary>
    /// Discord embed-field values are capped at 1024 chars; the modal allows
    /// up to ~1000 per paragraph input by config, so this is a defensive
    /// truncation rather than a typical case.
    /// </summary>
    private static string Truncate(string input, int max)
    {
        if (string.IsNullOrEmpty(input)) return "_(blank)_";
        return input.Length <= max ? input : input[..(max - 1)] + "…";
    }
}