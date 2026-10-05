using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #4: Nickname Impersonation Check.
///
/// ── What it does ──
/// When a member joins, or when an existing member's display name
/// changes, we compare their new display name against every CPT+
/// officer in the guild. If the new name is suspiciously similar —
/// exact match, one or two character typo, contains the officer's
/// name as a substring, or matches after Unicode lookalikes are
/// normalized — we post an alert to the security-alerts channel.
///
/// ── Why this exists ──
/// The classic Discord scam: someone joins the server, sets their
/// nickname to look identical or near-identical to a real officer,
/// then DMs members pretending to be that officer asking for
/// gamertags, account info, money, or trying to push malicious links.
/// Members see the nickname in chat and the member list — not the
/// underlying username or ID — so by the time the deception is
/// noticed real damage may already be done. The mitigation has to
/// run within seconds of the nickname change.
///
/// Examples this catches:
///   • "MAJ.Klaver"     vs  "MAJ.Klaverr"          (typo)
///   • "Officer Bob"    vs  "0fficer Bob"          (digit-for-letter)
///   • "CPT.Smith"      vs  "СPT.Smith"            (Cyrillic С)
///   • "Klaver"         vs  "Definitely Not Klaver" (substring contain)
///   • "MAJ.Klaver"     vs  "Klaver"               (rank prefix dropped)
///
/// ── Modes ──
///   "Off"        — Feature disabled.
///   "AlertOnly"  — Default. Post an alert + write an audit row for
///                  every match. No automatic action.
/// Auto-revert mode (rename the suspect back to their plain username)
/// is intentionally out of scope for v1. A legitimate new recruit
/// happening to share a last name with an officer should not have
/// their nickname wiped on first contact; leadership eyeballing the
/// alert is the right call until we've seen what the signal looks
/// like in practice.
///
/// ── Protected officers ──
/// Anyone holding a rank at or above
/// <see cref="BotConfig.NicknameImpersonationMinRank"/> (default
/// <c>CPT</c>). Below that floor, ranks are dense enough that fuzzy
/// matching false-positives constantly — a new "CPL.Smith" joining
/// when there's already a "CPL.Smithson" is a coincidence, not a
/// scam. CPT and up are also the ranks members are most likely to
/// trust on sight, which makes them the highest-value impersonation
/// targets.
///
/// ── Events watched ──
///   • <c>UserJoined</c>           — catches impersonation set at the
///                                    moment of joining (some attackers
///                                    rename within milliseconds of
///                                    landing in the server).
///   • <c>GuildMemberUpdated</c>   — catches nickname changes by
///                                    existing members. We only fire
///                                    the check when DisplayName
///                                    actually changed; the event also
///                                    fires for role changes,
///                                    timeouts, etc. and running the
///                                    full comparison on every one
///                                    would be wasteful.
///
/// ── Match logic ──
/// Both names go through the same normalization pipeline before
/// comparison:
///   1. Lowercase.
///   2. Map Unicode lookalike characters (Cyrillic а/е/о/р/с/у/х,
///      Greek α/ο/ν/ε, digits 0/1/3/4/5/7) to their Latin equivalents.
///   3. Strip Unicode diacritics (FormD + remove non-spacing marks).
///   4. If the name starts with a known rank token from
///      <see cref="BotConfig.RankRoles"/> followed by a separator,
///      strip it. This lets "MAJ.Klaver" → "klaver" so an impersonator
///      dropping the rank prefix is still caught, while "Colton" keeps
///      its "col".
///   5. Drop everything that isn't a letter or digit.
/// After normalization we apply three match tests in order:
///   a) Exact equality                   → <c>Exact</c>
///   b) Levenshtein distance ≤ threshold → <c>NearTypo</c>
///      Threshold: 1 for names ≤5 chars, 2 for longer. Tighter on
///      short names because there's less signal to be confident.
///   c) Substring containment            → <c>Contains</c>
///      Requires the officer's normalized name to be ≥5 chars. Below
///      that we'd get false positives like "mike" inside many random
///      gamertags.
///
/// ── Why we skip officers as suspects ──
/// If a CPT+ member changes their own nickname to something that
/// happens to look like another officer's, we don't alert. The
/// threat model is impersonation BY a non-officer of an officer;
/// rank-on-rank false positives would add noise without proportionate
/// signal. (A genuinely compromised officer account would more likely
/// surface via the audit-log watcher than via a nickname tweak.)
///
/// ── Failure handling ──
/// Fire-and-forget from the gateway callback. Exceptions are
/// swallowed and logged so a bad name comparison can never deadlock
/// the gateway thread. Per-phase isolation between the audit write
/// and the alert post follows the AccountAgeGate / AuditLogWatcher
/// pattern.
/// </summary>
public sealed class NicknameImpersonationHandler
{
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";

    /// <summary>
    /// How the suspect's name relates to the matched officer's.
    /// Surfaced in the alert so officers can tell at a glance whether
    /// the match is exact (high signal), a typo (medium), or just a
    /// substring (lower — judgement call).
    /// </summary>
    private enum MatchType
    {
        Exact,
        NearTypo,
        Contains,
    }

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<NicknameImpersonationHandler> _logger;
    private readonly BotConfig _config;

    public NicknameImpersonationHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<NicknameImpersonationHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.UserJoined        += OnUserJoined;
        client.GuildMemberUpdated += OnGuildMemberUpdated;
    }

    // ── Event entry points ─────────────────────────────────────────────

    private Task OnUserJoined(SocketGuildUser user)
    {
        _ = InspectAsync(user, trigger: "user joined");
        return Task.CompletedTask;
    }

    private Task OnGuildMemberUpdated(
        Cacheable<SocketGuildUser, ulong> beforeCacheable,
        SocketGuildUser after)
    {
        // Skip if we have the before-state and the display name didn't
        // change. GuildMemberUpdated fires for role changes, timeouts,
        // pending-flag flips, etc.; we only care about identity-facing
        // changes. If we DON'T have the before state cached, run the
        // check defensively — missing a real impersonation is worse
        // than running an extra comparison.
        if (beforeCacheable.HasValue)
        {
            var before = beforeCacheable.Value;
            if (string.Equals(before.DisplayName, after.DisplayName, StringComparison.Ordinal))
                return Task.CompletedTask;
        }

        _ = InspectAsync(after, trigger: "nickname change");
        return Task.CompletedTask;
    }

    // ── Core inspection ────────────────────────────────────────────────

    private async Task InspectAsync(SocketGuildUser suspect, string trigger)
    {
        try
        {
            var mode = (_config.NicknameImpersonationMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown NicknameImpersonationMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly.",
                    mode);
            }

            // Bots get a pass: webhooks and integration bots are
            // identified by their badge in the Discord UI; impersonation
            // by a bot account is a categorically different attack
            // covered by feature #5 (Webhook Audit) and our existing
            // BotAdded audit watch.
            if (suspect.IsBot) return;

            // If the suspect IS a protected officer, the threat model
            // doesn't apply (see class doc).
            if (IsProtectedOfficer(suspect)) return;

            var suspectName = suspect.DisplayName ?? suspect.Username;
            if (string.IsNullOrWhiteSpace(suspectName)) return;

            // Snapshot the rank list once per inspection — used both
            // for the officer floor check and for rank-prefix stripping
            // inside Normalize.
            var rankRoles = _config.GetRankRolesList();

            var matches = new List<(SocketGuildUser officer, MatchType type)>();
            foreach (var officer in suspect.Guild.Users)
            {
                if (officer.Id == suspect.Id) continue;          // never compare to self
                if (officer.IsBot) continue;                     // bot officers shouldn't exist, but defensive
                if (!IsProtectedOfficer(officer)) continue;      // only CPT+ are protected

                var officerName = officer.DisplayName ?? officer.Username;
                if (string.IsNullOrWhiteSpace(officerName)) continue;

                var matchType = CompareNames(suspectName, officerName, rankRoles);
                if (matchType.HasValue)
                    matches.Add((officer, matchType.Value));
            }

            if (matches.Count == 0) return;

            _logger.LogInformation(
                "Nickname impersonation candidate: {Suspect} ({SuspectId}) matched {Count} officer(s) — trigger: {Trigger}",
                suspectName, suspect.Id, matches.Count, trigger);

            await WriteAuditAsync(suspect, suspectName, matches, trigger);
            await PostAlertAsync(suspect, suspectName, matches, trigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "NicknameImpersonationHandler crashed for suspect {Suspect} ({SuspectId})",
                suspect.Username, suspect.Id);
        }
    }

    /// <summary>
    /// True if the member holds a rank role at or above
    /// <see cref="BotConfig.NicknameImpersonationMinRank"/>. Same
    /// index-walk pattern InviteLinkFilterHandler.IsRankExempt uses.
    /// </summary>
    private bool IsProtectedOfficer(SocketGuildUser member)
    {
        var minRank = _config.NicknameImpersonationMinRank;
        if (string.IsNullOrWhiteSpace(minRank)) return false;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "NicknameImpersonationMinRank '{Rank}' not found in RankRoles list — " +
                "no members will be treated as protected officers.",
                minRank);
            return false;
        }

        return member.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    // ── Name comparison ────────────────────────────────────────────────

    private static MatchType? CompareNames(string suspectRaw, string officerRaw, List<string> rankRoles)
    {
        var suspect = Normalize(suspectRaw, rankRoles);
        var officer = Normalize(officerRaw, rankRoles);

        // Below this threshold, normalization stripped so much that
        // anything would match anything. Bail out rather than spam
        // false positives. (e.g. an officer named just "AB" — too thin
        // to be a unique identifier post-normalization.)
        if (suspect.Length < 3 || officer.Length < 3) return null;

        if (suspect == officer) return MatchType.Exact;

        // Levenshtein threshold scales with length. Short names get
        // distance ≤ 1 only (one typo); longer get up to 2. This stops
        // "klaver" matching "kraken" (distance 3) while still catching
        // "klaver" vs "klavers" (distance 1).
        int threshold = officer.Length <= 5 ? 1 : 2;
        if (Levenshtein(suspect, officer) <= threshold) return MatchType.NearTypo;

        // Substring containment. Tightened to officer names ≥5 chars to
        // avoid alerting on every gamertag that incidentally contains a
        // 3- or 4-letter officer name as a fragment.
        if (officer.Length >= 5 && suspect.Contains(officer))
            return MatchType.Contains;

        return null;
    }

    /// <summary>
    /// Normalization pipeline. See class-level doc, "Match logic"
    /// section, for the full sequence of steps and why each exists.
    /// </summary>
    private static string Normalize(string s, List<string> rankRoles)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        s = Fold(s);

        // Strip a rank prefix only when a separator follows it, so "MAJ.Klaver"
        // and "MAJ Klaver" become "klaver" but "Colton" keeps its "col". Longest
        // first so "1stLT" wins over "LT"; rank tokens are folded the same way,
        // so "1SG" still matches after 1 → l.
        var start = 0;
        while (start < s.Length && !char.IsLetterOrDigit(s[start])) start++;
        foreach (var rank in rankRoles.OrderByDescending(r => r.Length))
        {
            var rankKey = AlnumOnly(Fold(rank));
            if (rankKey.Length == 0) continue;
            var end = start + rankKey.Length;
            if (end < s.Length
                && string.CompareOrdinal(s, start, rankKey, 0, rankKey.Length) == 0
                && !char.IsLetterOrDigit(s[end]))
            {
                s = s[end..];
                break;
            }
        }

        // "MAJ.Klaver" and "majklaver" compare the same from here.
        return AlnumOnly(s);
    }

    /// <summary>Lowercases, maps lookalike characters to Latin, and strips diacritics.</summary>
    private static string Fold(string s)
    {
        s = s.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(MapConfusable(ch));
        return StripDiacritics(sb.ToString());
    }

    private static string AlnumOnly(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>
    /// Hand-curated Unicode confusable map. Covers the lookalikes seen
    /// most often in social-engineering attacks. We deliberately stay
    /// short and explicit rather than pulling Unicode's full
    /// confusables.txt (~10k entries) — the long tail is rarely worth
    /// the false-positive risk and ships its own corner cases.
    /// </summary>
    private static char MapConfusable(char c) => c switch
    {
        // Cyrillic lowercase that visually mirror Latin letters
        'а' => 'a', 'е' => 'e', 'о' => 'o', 'р' => 'p',
        'с' => 'c', 'у' => 'y', 'х' => 'x', 'к' => 'k',
        'і' => 'i', 'ѕ' => 's', 'ј' => 'j', 'ь' => 'b',
        // Cyrillic whose capitals mirror Latin (М, Т, Н, В); input is lowercased first
        'м' => 'm', 'т' => 't', 'н' => 'h', 'в' => 'b',
        // Greek lowercase
        'α' => 'a', 'ο' => 'o', 'ν' => 'v',
        'ε' => 'e', 'ρ' => 'p', 'τ' => 't',
        // Digits that double as letters
        '0' => 'o', '1' => 'l', '3' => 'e',
        '4' => 'a', '5' => 's', '7' => 't',
        _ => c,
    };

    private static string StripDiacritics(string s)
    {
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Standard two-row Levenshtein. With N officers ≈ a couple dozen
    /// and string lengths capped at Discord's 32-char nickname limit,
    /// runtime is trivial — kept compact rather than optimized.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    // ── Audit + alert ──────────────────────────────────────────────────

    private async Task WriteAuditAsync(
        SocketGuildUser suspect,
        string suspectName,
        List<(SocketGuildUser officer, MatchType type)> matches,
        string trigger)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var details = new StringBuilder();
            details.Append($"Trigger: {trigger}. ");
            details.Append($"Suspect display name: '{suspectName}'. ");
            details.Append($"Match count: {matches.Count}. ");
            details.Append("Matches: ");
            details.Append(string.Join("; ", matches.Select(m =>
                $"{(m.officer.DisplayName ?? m.officer.Username)}[{m.officer.Id}]:{m.type}")));
            details.Append('.');

            var record = new SecurityAuditRecord
            {
                GuildId      = suspect.Guild.Id,
                Feature      = "NicknameImpersonation",
                Action       = "Match",
                UserId       = suspect.Id,
                Username     = suspect.Username,
                DisplayName  = suspectName,
                ChannelId    = null,
                Details      = details.ToString(),
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = null,
            };

            db.SecurityAuditRecords.Add(record);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write SecurityAuditRecord for nickname-impersonation match " +
                "(suspect {SuspectId}, {Count} matches)",
                suspect.Id, matches.Count);
        }
    }

    private async Task PostAlertAsync(
        SocketGuildUser suspect,
        string suspectName,
        List<(SocketGuildUser officer, MatchType type)> matches,
        string trigger)
    {
        var channel = ResolveSecurityAlertsChannel(suspect.Guild);
        if (channel is null)
        {
            _logger.LogWarning(
                "Nickname-impersonation alert had no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        // Build the matched-officer list. Use Discord mentions so officers
        // can click through; include username + id as plain text so the
        // alert is readable if the user has since left.
        var matchLines = new StringBuilder();
        foreach (var (officer, type) in matches)
        {
            var officerName = officer.DisplayName ?? officer.Username;
            var label = type switch
            {
                MatchType.Exact    => "**exact match**",
                MatchType.NearTypo => "near-match (typo distance)",
                MatchType.Contains => "contains officer name",
                _ => type.ToString(),
            };
            matchLines.AppendLine($"• {officer.Mention} (`{officerName}`) — {label}");
        }

        var embed = new EmbedBuilder()
            .WithColor(Color.Orange)
            .WithTitle("🎭 Nickname Impersonation Alert")
            .AddField(
                "Suspect",
                $"{suspect.Mention} (`{suspect.Username}` / `{suspect.Id}`)",
                inline: false)
            .AddField("Display Name", $"`{suspectName}`", inline: true)
            .AddField("Trigger",      trigger,           inline: true)
            .AddField("Matched Officer(s)", matchLines.ToString().TrimEnd(), inline: false)
            .WithFooter("ClanGuard • Nickname Impersonation")
            .WithCurrentTimestamp();

        try
        {
            await channel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post nickname-impersonation alert to channel {Channel} ({ChannelId}) " +
                "for suspect {SuspectId}",
                channel.Name, channel.Id, suspect.Id);
        }
    }

    /// <summary>
    /// ID-first, name fallback. Same resolution pattern as the other
    /// server-protection handlers, kept identical so a single config
    /// change moves alerts for every feature at once.
    /// </summary>
    private SocketTextChannel? ResolveSecurityAlertsChannel(SocketGuild guild)
    {
        if (_config.SecurityAlertsChannelId != 0)
        {
            var byId = guild.GetTextChannel(_config.SecurityAlertsChannelId);
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(_config.HqChannelName))
        {
            return guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
