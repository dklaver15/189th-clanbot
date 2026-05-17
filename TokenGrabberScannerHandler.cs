using System.Text;
using System.Text.RegularExpressions;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Server-protection feature #6: Anti-Token-Grabber Link Scanner.
///
/// ── What it does ──
/// Scans every user-authored message (and every edit) for URLs that
/// look like token-grabber / phishing destinations. Three detection
/// channels run in parallel for each URL:
///
///   1. <b>Officer blocklist</b>
///      <see cref="BotConfig.TokenGrabberScannerBlocklist"/>, a
///      comma-separated list of domains officers have manually flagged.
///      Highest confidence — explicit human "yes this is bad" judgment.
///
///   2. <b>Sinking Yachts feed</b>
///      A community-maintained list of ~thousands of phishing domains,
///      refreshed every 6 hours by
///      <see cref="PhishingDomainFeedService"/>. Covers the long tail
///      of fake Nitro / Steam gift / Discord-lookalike domains that
///      rotate constantly.
///
///   3. <b>Static IP-address URL pattern</b>
///      Any URL pointing to a raw IPv4 address (e.g.
///      <c>http://1.2.3.4/install.exe</c>) is alerted unconditionally.
///      Legitimate services don't ship bare IPs in chat; the pattern
///      is overwhelmingly malware C2 or hastily-deployed scams.
///
/// ── Modes ──
///   "Off"        — Feature disabled. Refresh service also skips
///                  fetching to save the API call.
///   "AlertOnly"  — Default. Post to security-alerts and write an
///                  audit row. Message is NOT deleted, DM is NOT sent.
///
/// Enforce mode (auto-delete + DM the poster) is intentionally out of
/// scope for v1. False-positive cost on a legitimate domain that's
/// briefly on the Sinking Yachts list (rare, but happens — popular
/// brands sometimes get hit by community-reporting bursts) would be
/// "Officer's message disappears mid-conversation" with no clear
/// recourse. AlertOnly first builds trust in the signal.
///
/// ── Rank exemption ──
/// Members at <see cref="BotConfig.TokenGrabberScannerExemptMinRank"/>
/// or above (default BG) are exempt. The threat model here is
/// compromised/malicious account → posts phishing → members click.
/// BG+ accounts CAN be compromised, but the volume of legitimate
/// link-sharing by general-grade leadership is small enough that
/// false-positives on them are worse than missing the rare BG+ phish.
/// Server Administrators are always exempt.
///
/// Bots and webhooks are always exempt — Apollo, GitHub, etc. post
/// links constantly and have their own audit coverage (feature #5).
///
/// ── URL extraction + domain matching ──
/// Regex picks up <c>http://</c> and <c>https://</c> URLs and trims
/// trailing sentence punctuation. <see cref="Uri.TryCreate"/> then
/// validates and pulls <c>Host</c>. The host is lowercased and
/// matched against the blocklist + feed by:
///   • Exact host match, OR
///   • Any parent domain ("evil.bad.com" matches blocklist entry
///     "bad.com" but not vice-versa).
/// Multiple URLs per message are deduped by host before alerting; one
/// alert per message regardless of how many bad URLs it contains.
///
/// ── Output sanitization ──
/// The alert embed renders matched URLs with <c>https://</c> replaced
/// by <c>hxxps://</c> and wrapped in code blocks. This is the
/// security-industry convention to keep an officer reviewing the alert
/// from accidentally clicking through. The alert also includes a
/// Discord deep link to the original message so the officer can act
/// on it (delete, ban, etc.) without hunting.
///
/// ── Failure handling ──
/// Fire-and-forget from the gateway callback. The phishing-feed
/// service may not be hydrated on first boot — we treat its
/// <c>Contains</c> as returning false in that case and fall back to
/// the blocklist + IP pattern. Better fail-open behavior than blocking
/// message processing on a feed outage.
/// </summary>
public sealed class TokenGrabberScannerHandler
{
    public const string ModeOff       = "Off";
    public const string ModeAlertOnly = "AlertOnly";

    /// <summary>
    /// Matches http(s) URLs. Stops at whitespace and characters that
    /// commonly bound URLs in chat: <c>&lt; &gt; " ' ` [ ] ( )</c>.
    /// Trailing sentence punctuation (<c>. , ! ? : ;</c>) is stripped
    /// in post-processing rather than via lookbehind, because doing
    /// it in the regex either over-matches or under-matches depending
    /// on whether the URL legitimately ends in those characters.
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"\bhttps?://[^\s<>""'`\[\]()]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Detects URLs pointing to bare IPv4 addresses. Mostly catches
    /// hastily-deployed malware droppers and C2 endpoints — legitimate
    /// public services use DNS names, not IPs, in chat shares.
    /// </summary>
    private static readonly Regex IpUrlRegex = new(
        @"^https?://(?:\d{1,3}\.){3}\d{1,3}(?:[:/?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly char[] TrailingPunctuation =
        { '.', ',', '!', '?', ':', ';' };

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly PhishingDomainFeedService _feed;
    private readonly ILogger<TokenGrabberScannerHandler> _logger;
    private readonly BotConfig _config;

    public TokenGrabberScannerHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        PhishingDomainFeedService feed,
        ILogger<TokenGrabberScannerHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _feed     = feed;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.MessageUpdated  += OnMessageUpdated;
    }

    // ── Event entry points ─────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        _ = InspectAsync(message);
        return Task.CompletedTask;
    }

    private Task OnMessageUpdated(
        Cacheable<IMessage, ulong> _before,
        SocketMessage after,
        ISocketMessageChannel _channel)
    {
        _ = InspectAsync(after);
        return Task.CompletedTask;
    }

    // ── Inspection ─────────────────────────────────────────────────────

    private async Task InspectAsync(SocketMessage message)
    {
        try
        {
            var mode = (_config.TokenGrabberScannerMode ?? ModeOff).Trim();
            if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.Equals(mode, ModeAlertOnly, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unknown TokenGrabberScannerMode '{Mode}' — falling back to AlertOnly. " +
                    "Valid values: Off, AlertOnly.",
                    mode);
            }

            if (message is not SocketUserMessage userMsg) return;
            if (userMsg.Author.IsBot) return;
            if (userMsg.Author.IsWebhook) return;
            if (userMsg.Channel is not SocketGuildChannel guildChannel) return;

            var member = userMsg.Author as SocketGuildUser
                         ?? guildChannel.Guild.GetUser(userMsg.Author.Id);
            if (member is null) return;

            if (IsRankExempt(member)) return;

            var content = userMsg.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content)) return;

            var hits = FindHits(content);
            if (hits.Count == 0) return;

            _logger.LogInformation(
                "TokenGrabberScannerHandler: {Count} suspicious URL(s) from {Author} ({AuthorId}) in #{Channel}",
                hits.Count, member.Username, member.Id, guildChannel.Name);

            await WriteAuditAsync(userMsg, member, guildChannel, hits);
            await PostAlertAsync(userMsg, member, guildChannel, hits);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TokenGrabberScannerHandler crashed for message {MessageId}",
                message.Id);
        }
    }

    /// <summary>
    /// Walks the message content for URLs and classifies each. Returns
    /// one entry per distinct match (deduped by host + reason). An
    /// empty list means nothing tripped any detector.
    /// </summary>
    private List<Hit> FindHits(string content)
    {
        var hits = new List<Hit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var blocklist = _config.GetTokenGrabberScannerBlocklist();

        foreach (Match m in UrlRegex.Matches(content))
        {
            var raw = m.Value.TrimEnd(TrailingPunctuation);

            // IPv4 URL: high-confidence static pattern. Catches even
            // when the IP isn't in any feed.
            if (IpUrlRegex.IsMatch(raw))
            {
                var key = "ip:" + raw;
                if (seen.Add(key))
                    hits.Add(new Hit(raw, raw, HitSource.IpAddress));
                continue;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                continue;

            var host = uri.Host?.ToLowerInvariant();
            if (string.IsNullOrEmpty(host)) continue;

            // Blocklist match (officer-managed, highest signal).
            if (DomainOrParentMatches(host, blocklist))
            {
                var key = "block:" + host;
                if (seen.Add(key))
                    hits.Add(new Hit(raw, host, HitSource.OfficerBlocklist));
                continue;
            }

            // Phishing feed match. Iterate host then strip subdomains
            // ("evil.bad.com" → check "evil.bad.com", then "bad.com").
            var feedHit = FindFeedMatch(host);
            if (feedHit is not null)
            {
                var key = "feed:" + host;
                if (seen.Add(key))
                    hits.Add(new Hit(raw, feedHit, HitSource.PhishingFeed));
            }
        }

        return hits;
    }

    private string? FindFeedMatch(string host)
    {
        if (_feed.Count == 0) return null;  // feed not hydrated; fail open

        if (_feed.Contains(host)) return host;

        var parts = host.Split('.');
        // i starts at 1 because parts[0] is the leftmost subdomain;
        // parent candidates are parts[i..]. Stop one short of the TLD
        // so we never check just "com" / "io" against the feed.
        for (int i = 1; i < parts.Length - 1; i++)
        {
            var parent = string.Join('.', parts.Skip(i));
            if (_feed.Contains(parent)) return parent;
        }
        return null;
    }

    private static bool DomainOrParentMatches(string host, List<string> list)
    {
        if (list.Count == 0) return false;

        var lower = list
            .Select(d => d.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (lower.Contains(host)) return true;

        var parts = host.Split('.');
        for (int i = 1; i < parts.Length - 1; i++)
        {
            var parent = string.Join('.', parts.Skip(i));
            if (lower.Contains(parent)) return true;
        }
        return false;
    }

    /// <summary>
    /// Rank exemption: BG+ (configurable) or server Administrator
    /// bypasses the scanner. Same role-index pattern as
    /// InviteLinkFilterHandler.IsRankExempt.
    /// </summary>
    private bool IsRankExempt(SocketGuildUser member)
    {
        if (member.GuildPermissions.Administrator) return true;

        var minRank = _config.TokenGrabberScannerExemptMinRank;
        if (string.IsNullOrWhiteSpace(minRank)) return false;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return member.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    // ── Audit + alert ──────────────────────────────────────────────────

    private async Task WriteAuditAsync(
        SocketUserMessage message,
        SocketGuildUser member,
        SocketGuildChannel channel,
        List<Hit> hits)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var details = new StringBuilder();
            details.Append($"MessageId={message.Id}. ");
            details.Append($"Channel=#{channel.Name}. ");
            details.Append($"HitCount={hits.Count}. ");
            details.Append("Hits: ");
            details.Append(string.Join("; ", hits.Select(h =>
                $"{h.Source}={h.MatchedHostOrUrl}")));
            details.Append('.');

            db.SecurityAuditRecords.Add(new SecurityAuditRecord
            {
                GuildId      = channel.Guild.Id,
                Feature      = "TokenGrabberScanner",
                Action       = "Alerted",
                UserId       = member.Id,
                Username     = member.Username,
                DisplayName  = member.DisplayName,
                ChannelId    = channel.Id,
                Details      = details.ToString(),
                OccurredAt   = DateTime.UtcNow,
                ErrorMessage = null,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write SecurityAuditRecord for token-grabber scanner hit " +
                "(message {MessageId}, {Count} hits)",
                message.Id, hits.Count);
        }
    }

    private async Task PostAlertAsync(
        SocketUserMessage message,
        SocketGuildUser member,
        SocketGuildChannel channel,
        List<Hit> hits)
    {
        var alertsChannel = ResolveSecurityAlertsChannel(channel.Guild);
        if (alertsChannel is null)
        {
            _logger.LogWarning(
                "TokenGrabberScannerHandler has no resolvable alerts channel " +
                "(SecurityAlertsChannelId={Id}, HqChannelName='{Hq}'). Alert dropped.",
                _config.SecurityAlertsChannelId, _config.HqChannelName);
            return;
        }

        var hitLines = new StringBuilder();
        foreach (var hit in hits)
        {
            var sourceLabel = hit.Source switch
            {
                HitSource.OfficerBlocklist => "officer blocklist",
                HitSource.PhishingFeed     => "phishing-domain feed",
                HitSource.IpAddress        => "raw IP address",
                _ => hit.Source.ToString(),
            };
            // Sanitize URL: hxxps:// keeps it unclickable even if copied
            // out of the embed and pasted elsewhere. Code-block wraps to
            // disable Discord's own auto-linking behavior in embed text.
            var defanged = Defang(hit.RawUrl);
            hitLines.AppendLine($"• `{defanged}` — matched **{sourceLabel}** (`{hit.MatchedHostOrUrl}`)");
        }

        // Deep link to original message — lets officers jump straight
        // there for delete/ban/etc. without searching.
        var messageLink = $"https://discord.com/channels/{channel.Guild.Id}/{channel.Id}/{message.Id}";

        var embed = new EmbedBuilder()
            .WithColor(Color.Red)
            .WithTitle("🎣 Suspicious Link Detected")
            .AddField("Author",
                $"{member.Mention} (`{member.Username}` / `{member.Id}`)",
                inline: false)
            .AddField("Channel", channel is SocketTextChannel tc ? tc.Mention : $"#{channel.Name}", inline: true)
            .AddField("Hits", hits.Count.ToString(), inline: true)
            .AddField("Matched URLs", hitLines.ToString().TrimEnd(), inline: false)
            .AddField("Jump to Message", $"[Open]({messageLink})", inline: false)
            .WithFooter("ClanGuard • Token-Grabber Scanner")
            .WithCurrentTimestamp()
            .Build();

        try
        {
            await alertsChannel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TokenGrabberScannerHandler failed to post alert to channel {Channel} ({ChannelId})",
                alertsChannel.Name, alertsChannel.Id);
        }
    }

    /// <summary>
    /// Replace protocol with the security-industry "defanged" form so
    /// the URL doesn't render clickable anywhere it's copied to.
    /// </summary>
    private static string Defang(string url)
        => url
            .Replace("https://", "hxxps://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "hxxp://",  StringComparison.OrdinalIgnoreCase);

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

    // ── Internal types ─────────────────────────────────────────────────

    private enum HitSource
    {
        OfficerBlocklist,
        PhishingFeed,
        IpAddress,
    }

    private sealed record Hit(string RawUrl, string MatchedHostOrUrl, HitSource Source);
}
