using System.Collections.Concurrent;
using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// A fully-built leaderboard page, ready to post as a new message or to send
/// back as an ephemeral reply.
/// </summary>
public sealed record XpPageRender(
    Embed Embed,
    byte[]? Png,
    string FileName,
    MessageComponent? Components,
    int Page,
    int TotalPages,
    string Signature,
    int SeasonId,
    XpStanding? Leader);

/// <summary>
/// Owns the XP channel: the pinned rules post, and the pinned leaderboard
/// directly beneath it.
///
/// ── Two messages, in a fixed order ──
/// Discord has no way to hold a message at the top of a channel; message order is
/// simply chronological. So "guide above board" is achieved by posting the guide
/// FIRST and the board SECOND, and never letting that invert. Both are pinned as
/// well, which puts them in the pin list but does nothing for channel position —
/// the ordering guarantee comes entirely from post order.
///
/// That's why the repair logic is asymmetric. If the board is missing, reposting
/// it lands it below the guide, which is correct. If the GUIDE is missing,
/// reposting it alone would land it below the board — so the board is deleted and
/// reposted too. The same applies if the two are found out of order (compared by
/// snowflake id, which is monotonic).
///
/// This all assumes the channel is locked so members can't post. If someone can,
/// their messages sit below the board until the next refresh reposts it.
///
/// ── The pin trap ──
/// Pinning emits a "… pinned a message" SYSTEM message into the channel, which
/// then sits BELOW whatever was just pinned. On a service that re-sticks its
/// board that becomes an endless post → pin → notice → re-stick loop; the events
/// board hit exactly this and abandoned pinning altogether. Here each message is
/// pinned once and the resulting notices are deleted, so the channel stays as two
/// clean messages.
///
/// ── Paging ──
/// The board itself is permanently page 1 (the top 25). Its buttons open a
/// PRIVATE paginated view for whoever clicked — see XpCommandHandler. Paging the
/// shared pinned message instead would mean one member's click changes what the
/// whole clan sees, two people clicking fight each other, and the refresh cycle
/// has to guess whether to snap back to page 1.
///
/// ── Re-upload avoidance ──
/// The board carries a rendered PNG, so a naive "edit every cycle" would
/// re-upload an image every few minutes forever. A signature of the rendered
/// content is held in memory per guild and the edit is skipped when nothing
/// changed. In-memory (not persisted) is deliberate: the cost of a cold start is
/// exactly one redundant render.
/// </summary>
public sealed class XpLeaderboardService : BackgroundService
{
    public const string BoardTitle = "🏆 189th Clanguard — XP Leaderboard";
    public const string BoardImageFileName = "xp-board.png";

    /// <summary>
    /// Bumped whenever the board's layout, components or embed shape changes.
    /// It's part of the signature, so a deploy that changes the rendering forces
    /// exactly one refresh instead of leaving the old board up until someone's XP
    /// happens to move.
    /// </summary>
    private const string BoardSchemaVersion = "v3";

    private const int BoardScanLimit = 50;

    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly XpService _xp;
    private readonly XpLeaderboardRenderer _renderer;
    private readonly BotConfig _config;
    private readonly ILogger<XpLeaderboardService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // guildId → signature of the last board we rendered.
    private readonly ConcurrentDictionary<ulong, string> _lastBoardSignature = new();

    // guildId → who we last saw in first place, and when we last announced a
    // change. Held in memory rather than persisted: the ONLY failure mode is
    // missing a takeover that happened while the bot was down, whereas a
    // persisted-but-wrong value would announce a "new #1" who has been leading
    // for a week. Same silent-baseline trick FinalsLeaderboardService uses for
    // rank-ups — a first sighting never announces.
    private readonly ConcurrentDictionary<ulong, LeaderState> _lastLeader = new();

    // guildId → signature of the economy values baked into the rendered rates card.
    private readonly ConcurrentDictionary<ulong, string> _lastRatesSignature = new();

    private readonly record struct LeaderState(int SeasonId, ulong UserId, DateTime AnnouncedAtUtc);

    public XpLeaderboardService(
        IServiceProvider services,
        DiscordSocketClient client,
        XpService xp,
        XpLeaderboardRenderer renderer,
        IOptions<BotConfig> config,
        ILogger<XpLeaderboardService> logger)
    {
        _services = services;
        _client = client;
        _xp = xp;
        _renderer = renderer;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.XpEnabled || !_config.XpBoardEnabled)
        {
            _logger.LogInformation("XpLeaderboardService: disabled by config — service idle.");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(2, _config.XpBoardRefreshIntervalMinutes));
        _logger.LogInformation("XpLeaderboardService started — refreshing every {Min}min", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "XP board refresh failed; will retry next interval"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Rebuilds the guide and the board now. Public so the season and adjust
    /// commands can push an immediate update rather than leaving an officer
    /// looking at a stale board for ten minutes after they changed something.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!_config.XpEnabled || !_config.XpBoardEnabled) return;

        var channelId = _config.XpBoardChannelId;
        if (channelId == 0)
        {
            _logger.LogDebug("XP board enabled but XpBoardChannelId is unset — skipping.");
            return;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            foreach (var guild in _client.Guilds.ToList())
            {
                if (guild.GetChannel(channelId) is not SocketTextChannel channel) continue;
                try { await MaintainChannelAsync(guild, channel, ct); }
                catch (Exception ex) { _logger.LogError(ex, "XP channel maintenance failed for guild {Guild}", guild.Id); }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Forces the next refresh to re-render, e.g. after a season change.</summary>
    public void InvalidateBoard(ulong guildId) => _lastBoardSignature.TryRemove(guildId, out _);

    // ─── Channel maintenance ────────────────────────────────────────────

    private async Task MaintainChannelAsync(SocketGuild guild, SocketTextChannel channel, CancellationToken ct)
    {
        if (_client.CurrentUser is null) return;

        // After a full reconnect Discord.NET re-chunks the member cache asynchronously,
        // so guild.GetUser() briefly returns null for most of the roster. With
        // XpBoardCurrentMembersOnly on, a refresh in that gap filters the board down to
        // whoever happens to be cached, re-numbers them, and — far worse — hands
        // CheckLeaderChangeAsync a bogus leader, pinging a mid-table member as the new
        // #1. The startup grace only covers process start, not later reconnects.
        if (_config.XpBoardCurrentMembersOnly && guild.MemberCount > 0
            && guild.DownloadedMemberCount < guild.MemberCount)
        {
            _logger.LogDebug("XP: member cache still syncing ({Have}/{Want}) — skipping this refresh",
                guild.DownloadedMemberCount, guild.MemberCount);
            return;
        }

        var recent = (await channel.GetMessagesAsync(BoardScanLimit).FlattenAsync()).ToList();
        var mine = recent.OfType<IUserMessage>().Where(m => m.Author.Id == _client.CurrentUser.Id).ToList();

        var guides = mine.Where(m => HasEmbedTitled(m, XpGuide.GuideTitle)).ToList();
        var boards = mine.Where(m => HasEmbedTitled(m, BoardTitle)).ToList();

        var guide = guides.FirstOrDefault();
        var board = boards.FirstOrDefault();

        // Drop duplicates before doing anything else, so the ordering logic below
        // is reasoning about one of each.
        await DeleteAllAsync(guides.Skip(1).Concat(boards.Skip(1)), ct);

        var page = await RenderPageAsync(guild, page: 1, forBoard: true, ct);
        if (page is null) return;

        // Deliberately here and not inside RenderPageAsync: that method also runs
        // for /xp-leaderboard and for every pagination click, and firing an
        // announcement from those would post whenever anyone browsed the board.
        await CheckLeaderChangeAsync(guild, page, ct);

        var wantGuide = _config.XpGuideEnabled;

        // Snowflake ids are monotonic, so a guide with a HIGHER id than the board
        // means the two are inverted in the channel and the board has to move.
        var outOfOrder = wantGuide && guide is not null && board is not null && guide.Id > board.Id;

        // Guide turned off after one was posted: take it down rather than leaving an
        // orphan that is still pinned, never updated, and can't be re-ordered.
        if (!wantGuide && guide is not null)
        {
            await DeleteAllAsync(new[] { guide }, ct);
            guide = null;
        }

        if (wantGuide && guide is null)
        {
            // Reposting the guide alone would put it BELOW the board, so the board has
            // to go too. POST FIRST, THEN DELETE. Deleting first and then failing to
            // post (rate limit, transient 5xx, Attach Files denied so SendFileAsync
            // fails where the board's SendMessageAsync would not) leaves guide null
            // forever, and every subsequent cycle deletes and reposts the board again:
            // new id, new upload, new pin, new pin notice, indefinitely.
            var replacement = await PostGuideAsync(guild, channel, ct);
            if (replacement is not null)
            {
                await DeleteAllAsync(board is null ? Array.Empty<IUserMessage>() : new[] { board }, ct);
                board = null;
                guide = replacement;
            }
            else
            {
                // Leave the board exactly where it is and try again next cycle. A
                // board with no guide above it beats no board at all.
                _logger.LogWarning("XP: could not repost the guide; leaving the existing board in place");
            }
        }
        else if (wantGuide && guide is not null)
        {
            await UpdateGuideAsync(guild, guide, ct);
        }

        if (outOfOrder && board is not null)
        {
            await DeleteAllAsync(new[] { board }, ct);
            board = null;
        }

        if (board is null)
        {
            board = await PostBoardAsync(channel, page, ct);
            if (board is not null) _lastBoardSignature[guild.Id] = page.Signature;
        }
        else
        {
            await UpdateBoardAsync(guild, board, page, ct);
        }

        await EnsurePinnedAsync(guide, ct);
        await EnsurePinnedAsync(board, ct);
        await CleanPinNoticesAsync(channel, ct);
    }

    private async Task<IUserMessage?> PostGuideAsync(SocketGuild guild, SocketTextChannel channel, CancellationToken ct)
    {
        var (season, scheduled) = await GetSeasonStateAsync(guild.Id, ct);
        var png = _renderer.TryRenderRatesCard();
        var embed = XpGuide.Build(_config, season, scheduled, png is not null);

        // Bank the signature ONLY when a card went up. Leaving it unbanked after a
        // failed render means UpdateGuideAsync sees ratesChanged forever, and because
        // a message with no attachment has hasImage false it can never re-render
        // either: an identical PATCH every cycle, and a card unrecoverable without
        // deleting the guide by hand.
        if (png is not null) _lastRatesSignature[guild.Id] = BuildRatesSignature();

        try
        {
            if (png is null)
                return await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);

            using var fa = new FileAttachment(new MemoryStream(png), XpGuide.RatesImageFileName);
            return await channel.SendFileAsync(fa, embed: embed, flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to post the guide message");
            return null;
        }
    }

    /// <summary>
    /// The guide is static copy over config values, so it's only rewritten when
    /// the rendered text actually differs — otherwise every refresh would
    /// re-upload the rates card for nothing.
    /// </summary>
    private async Task UpdateGuideAsync(SocketGuild guild, IUserMessage guide, CancellationToken ct)
    {
        var (season, scheduled) = await GetSeasonStateAsync(guild.Id, ct);
        var hasImage = guide.Attachments.Any(a => a.Filename == XpGuide.RatesImageFileName);
        var embed = XpGuide.Build(_config, season, scheduled, hasImage);

        // Most of the economy appears ONLY inside the rendered card, so a text-only
        // comparison would let a retuned rate (meeting XP, streak bonuses, the voice
        // cap) stay wrong on the pinned guide forever. Track a signature of the values
        // the card draws and re-upload it when they move.
        var ratesSignature = BuildRatesSignature();
        var ratesChanged = !_lastRatesSignature.TryGetValue(guild.Id, out var lastRates)
                           || lastRates != ratesSignature;

        var current = guide.Embeds.FirstOrDefault();
        var textUnchanged = current is not null
            && current.Description == embed.Description
            && current.Footer?.Text == embed.Footer?.Text
            && current.Fields.Length == embed.Fields.Length
            && !current.Fields.Where((f, i) => f.Value != embed.Fields[i].Value).Any();

        if (textUnchanged && !ratesChanged) return;

        try
        {
            var png = ratesChanged && hasImage ? _renderer.TryRenderRatesCard() : null;

            if (png is not null)
            {
                using var fa = new FileAttachment(new MemoryStream(png), XpGuide.RatesImageFileName);
                await guide.ModifyAsync(m =>
                {
                    m.Embed = embed;
                    m.Attachments = new List<FileAttachment> { fa };
                });
            }
            else
            {
                await guide.ModifyAsync(m => m.Embed = embed);
            }

            // Only bank the signature if a card actually went up. Recording it after a
            // failed render (png null) would mean the retuned rates are never drawn
            // again: the text half would be right and the graphic permanently wrong.
            if (png is not null || !ratesChanged)
                _lastRatesSignature[guild.Id] = ratesSignature;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "XP: failed to update the guide message"); }
    }

    private async Task<IUserMessage?> PostBoardAsync(SocketTextChannel channel, XpPageRender page, CancellationToken ct)
    {
        try
        {
            if (page.Png is null)
                return await channel.SendMessageAsync(embed: page.Embed, components: page.Components,
                    flags: MessageFlags.SuppressNotification);

            using var fa = new FileAttachment(new MemoryStream(page.Png), page.FileName);
            var posted = await channel.SendFileAsync(fa, embed: page.Embed, components: page.Components,
                flags: MessageFlags.SuppressNotification);

            _logger.LogInformation("XP board posted in channel {Channel} as {Msg}", channel.Id, posted.Id);
            return posted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to post the leaderboard");
            return null;
        }
    }

    private async Task UpdateBoardAsync(SocketGuild guild, IUserMessage board, XpPageRender page, CancellationToken ct)
    {
        var signature = page.Signature;
        if (_lastBoardSignature.TryGetValue(guild.Id, out var last) && last == signature)
            return;

        try
        {
            if (page.Png is null)
            {
                await board.ModifyAsync(m =>
                {
                    m.Embed = page.Embed;
                    m.Components = page.Components;
                    // MUST clear explicitly. Discord's PATCH keeps existing attachments
                    // when the field is omitted, so ending a season (or a failed render)
                    // would leave the previous leaderboard PNG hanging under a "no season
                    // is running" embed — and the matching signature meant it never
                    // corrected itself.
                    m.Attachments = new List<FileAttachment>();
                });
            }
            else
            {
                using var fa = new FileAttachment(new MemoryStream(page.Png), page.FileName);
                await board.ModifyAsync(m =>
                {
                    m.Embed = page.Embed;
                    m.Components = page.Components;
                    // Replacing the attachment list swaps the image; the embed's
                    // attachment:// reference re-binds to the new upload.
                    m.Attachments = new List<FileAttachment> { fa };
                });
            }

            _lastBoardSignature[guild.Id] = signature;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to update the leaderboard message");
        }
    }

    // ─── Page building (shared with the ephemeral pager) ────────────────

    /// <summary>
    /// Builds one page of standings: the rendered graphic, the embed that carries
    /// it, and the buttons. Shared by the pinned board (page 1, buttons that open
    /// a private view) and by the private pager itself (any page, prev/next
    /// buttons).
    ///
    /// Returns null only if the guild has no XP channel worth rendering into;
    /// a failed IMAGE render is not a failure — the embed carries a text fallback
    /// so the board degrades instead of disappearing.
    /// </summary>
    public async Task<XpPageRender?> RenderPageAsync(SocketGuild guild, int page, bool forBoard, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var season = await _xp.GetActiveSeasonAsync(db, guild.Id, ct);
        var pageSize = Math.Max(1, _config.XpBoardPageSize);

        if (season is null)
        {
            // A season lined up to open is worth showing. Without this the channel
            // reads "no season is running" right up to the moment one starts, which
            // is the opposite of the reassurance anyone checking the board wants.
            var scheduled = await _xp.GetScheduledSeasonAsync(db, guild.Id, ct);

            // The pending start is part of the signature, so moving or cancelling it
            // redraws the board instead of leaving the old date up.
            var idleSignature = scheduled is null
                ? $"{BoardSchemaVersion}|no-season"
                : $"{BoardSchemaVersion}|pending|{scheduled.Id}|{scheduled.StartUtc:O}|{scheduled.AutoEndUtc:O}";

            return new XpPageRender(BuildNoSeasonEmbed(scheduled), null, BoardImageFileName, null, 1, 1,
                idleSignature, 0, null);
        }

        var all = await _xp.GetSeasonStandingsAsync(db, guild.Id, season.Id, int.MaxValue, ct);

        if (_config.XpBoardCurrentMembersOnly)
        {
            // A board topped by people who left reads as a memorial, not a
            // contest. Re-number after filtering so places stay contiguous.
            all = all
                .Where(s => guild.GetUser(s.UserId) is not null)
                .Select((s, i) => s with { Place = i + 1 })
                .ToList();
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var slice = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var entries = slice
            .Select(s => new XpBoardEntry(
                s.Place,
                s.UserId,
                s.Username,
                s.Level,
                s.Xp,
                guild.GetUser(s.UserId)?.GetDisplayAvatarUrl(ImageFormat.Png, 128)))
            .ToList();

        var subtitle = BuildSubtitle(season, all.Count);

        var png = entries.Count > 0
            ? await _renderer.TryRenderLeaderboardAsync(
                XpService.SeasonLabel(season), subtitle, entries, page, totalPages, all.Count, ct)
            : null;

        var embed = BuildEmbed(season, subtitle, entries, png is not null, page, totalPages);
        var components = BuildComponents(page, totalPages, forBoard, pageSize);

        return new XpPageRender(embed, png, BoardImageFileName, components, page, totalPages,
            BuildSignature(season, subtitle, entries, page, totalPages, png is not null),
            season.Id,
            // The leader comes from the FILTERED, ordered list, so "new #1" can
            // never disagree with what the board actually shows.
            all.FirstOrDefault(s => s.Xp > 0));
    }

    /// <summary>
    /// What the board currently SAYS, reduced to a string.
    ///
    /// This deliberately hashes the standings rather than the embed, because when
    /// the image renders the embed description is just the season line — it
    /// wouldn't change when someone overtakes someone else, and the board would
    /// freeze on whatever it first drew. The schema version is included so a
    /// deploy that changes the layout forces exactly one refresh.
    /// </summary>
    private static string BuildSignature(
        XpSeason season, string subtitle, IReadOnlyList<XpBoardEntry> entries,
        int page, int totalPages, bool hasImage)
    {
        var sb = new StringBuilder();
        sb.Append(BoardSchemaVersion).Append('|')
          .Append(season.Id).Append('|')
          .Append(subtitle).Append('|')
          .Append(page).Append('/').Append(totalPages).Append('|')
          .Append(hasImage ? 'i' : 't').Append('|');

        foreach (var e in entries)
            sb.Append(e.Place).Append(':').Append(e.UserId).Append(':')
              .Append(e.Xp).Append(':').Append(e.Level).Append(':').Append(e.Name).Append(';');

        return sb.ToString();
    }

    // ─── "New #1" announcements ─────────────────────────────────────────

    /// <summary>
    /// Announces a change at the top of the leaderboard.
    ///
    /// ── Why it can go quiet ──
    /// A first sighting (fresh process) and a season change both seed the tracked
    /// leader SILENTLY. Announcing on those would mean every restart congratulating
    /// whoever happens to be leading, and every new season crowning someone at
    /// zero XP. The cost is that a takeover which happens while the bot is down is
    /// missed, which is the right way round: a missed celebration is invisible, a
    /// false one has to be explained.
    ///
    /// XpLeaderChangeCooldownMinutes bounds the worst case where two members sit
    /// within a few XP of each other and trade the lead back and forth. The tracked
    /// leader still updates during the cooldown — only the post is suppressed — so
    /// the next genuine takeover is measured against reality, not against a stale
    /// name.
    /// </summary>
    private async Task CheckLeaderChangeAsync(SocketGuild guild, XpPageRender page, CancellationToken ct)
    {
        if (!_config.XpLeaderChangeAnnounceEnabled) return;
        if (page.SeasonId == 0 || page.Leader is not { } leader || leader.Xp <= 0) return;

        var channelId = _config.XpLevelUpAnnounceChannelId;
        if (channelId == 0) return;
        if (channelId == _config.XpBoardChannelId) return;   // would bury the board

        var now = DateTime.UtcNow;

        if (!_lastLeader.TryGetValue(guild.Id, out var previous) || previous.SeasonId != page.SeasonId)
        {
            // First sighting this process, or a brand-new season. Baseline only.
            _lastLeader[guild.Id] = new LeaderState(page.SeasonId, leader.UserId, DateTime.MinValue);
            return;
        }

        if (previous.UserId == leader.UserId) return;

        var cooldown = TimeSpan.FromMinutes(Math.Max(0, _config.XpLeaderChangeCooldownMinutes));
        if (previous.AnnouncedAtUtc != DateTime.MinValue && now - previous.AnnouncedAtUtc < cooldown)
        {
            _lastLeader[guild.Id] = previous with { UserId = leader.UserId };
            _logger.LogDebug("XP: leader changed to {User} but within the announce cooldown — staying quiet", leader.UserId);
            return;
        }

        // The dethroned member is named, not pinged. Being notified that you just
        // lost first place is not a notification anyone asked for.
        var previousName = guild.GetUser(previous.UserId)?.DisplayName;

        var embed = new EmbedBuilder()
            .WithTitle("👑 New #1")
            .WithColor(new Color(0xF1C40F))
            .WithDescription(
                $"<@{leader.UserId}> has taken the top spot" +
                (string.IsNullOrWhiteSpace(previousName) ? "" : $" from **{Sanitize(previousName)}**") +
                $" — **{leader.Xp:N0} XP**, Level {leader.Level}.")
            .WithFooter("XP is recognition only — it does not affect promotions")
            .WithCurrentTimestamp()
            .Build();

        try
        {
            if (guild.GetChannel(channelId) is SocketTextChannel channel)
            {
                // The one send left without a retry guard, and it runs while holding
                // _refreshLock. Discord.NET's default RetryMode silently AWAITS a
                // rate limit, which would park the board loop, make every officer
                // command's bounded refresh time out, and delay a season transition
                // blocked on RefreshAsync for as long as the bucket lasts.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(AnnounceTimeout);

                await channel.SendMessageAsync(
                    text: $"<@{leader.UserId}>",
                    embed: embed,
                    allowedMentions: new AllowedMentions { UserIds = new List<ulong> { leader.UserId } },
                    options: new RequestOptions { RetryMode = RetryMode.AlwaysFail, CancelToken = cts.Token });

                _logger.LogInformation("XP: announced new leader {User} (was {Previous})", leader.UserId, previous.UserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to announce the new leader");
        }

        // Recorded even if the post failed, so a broken channel can't queue up a
        // burst of announcements the moment permissions are fixed.
        _lastLeader[guild.Id] = new LeaderState(page.SeasonId, leader.UserId, now);
    }

    /// <summary>
    /// Which page a member is on, or 0 if they haven't earned any XP this season.
    /// Powers the "Find me" button, which is the whole reason paging is tolerable
    /// on a clan of any size — without it, member #63 has to click Next twice
    /// every time they want to see their own line.
    ///
    /// Deliberately mirrors RenderPageAsync's filtering and ordering rather than
    /// sharing a cached list; the two have to agree or the button lands on the
    /// wrong page, and re-deriving is cheap next to getting that subtly wrong.
    /// </summary>
    public async Task<int> FindPageForMemberAsync(SocketGuild guild, ulong userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var season = await _xp.GetActiveSeasonAsync(db, guild.Id, ct);
        if (season is null) return 0;

        var all = await _xp.GetSeasonStandingsAsync(db, guild.Id, season.Id, int.MaxValue, ct);

        if (_config.XpBoardCurrentMembersOnly)
            all = all.Where(s => guild.GetUser(s.UserId) is not null).ToList();

        var index = all.FindIndex(s => s.UserId == userId);
        if (index < 0) return 0;

        return index / Math.Max(1, _config.XpBoardPageSize) + 1;
    }

    private Embed BuildEmbed(
        XpSeason season, string subtitle, IReadOnlyList<XpBoardEntry> entries,
        bool hasImage, int page, int totalPages)
    {
        var embed = new EmbedBuilder()
            .WithTitle(BoardTitle)
            .WithColor(new Color(0xF1C40F))
            .WithCurrentTimestamp();

        if (hasImage)
        {
            embed.WithImageUrl($"attachment://{BoardImageFileName}");
            embed.WithDescription($"**{XpService.SeasonLabel(season)}** · {subtitle}");
        }
        else
        {
            // Render failed (missing font, missing native lib, OOM). Falling all
            // the way back to text keeps the board useful rather than blank.
            embed.WithDescription(BuildTextFallback(season, subtitle, entries));
        }

        embed.WithFooter(totalPages > 1
            ? $"Page {page} of {totalPages} • buttons below open a private view • /xp for your card"
            : "XP is recognition only — it does not affect promotions • /xp for your card");

        return embed.Build();
    }

    private static string BuildTextFallback(XpSeason season, string subtitle, IReadOnlyList<XpBoardEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(XpService.SeasonLabel(season)).Append("** · ").Append(subtitle).Append("\n\n");

        if (entries.Count == 0)
        {
            sb.Append("_Nobody's earned XP yet this season. Turn up to an event and you'll be top of an empty board._");
            return sb.ToString();
        }

        foreach (var e in entries.Take(25))
        {
            var place = e.Place switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"`#{e.Place,2}`" };
            var line = $"{place} **{Sanitize(e.Name)}** — Lv {e.Level} · {e.Xp:N0} XP\n";
            if (sb.Length + line.Length > 3900) break;
            sb.Append(line);
        }

        return sb.ToString().TrimEnd();
    }

    private Embed BuildNoSeasonEmbed(XpSeason? scheduled)
    {
        var embed = new EmbedBuilder()
            .WithColor(new Color(0x8B96A8))
            .WithFooter("XP is recognition only. It does not affect promotions.")
            .WithCurrentTimestamp();

        if (scheduled is null)
        {
            return embed
                .WithTitle(BoardTitle)
                .WithDescription(
                    "**No season is running right now.**\n\n" +
                    "XP isn't being counted until an officer opens a season with `/xp-season start`. " +
                    "All-time totals and past season results are untouched, check yours with `/xp`.")
                .Build();
        }

        var body = new StringBuilder();
        body.Append($"**{XpService.SeasonLabel(scheduled)} opens <t:{Unix(scheduled.StartUtc)}:F>**\n");
        body.Append($"That is <t:{Unix(scheduled.StartUtc)}:R>.\n\n");
        body.Append("Nothing counts until then, and everyone starts at zero. Attend events between now and ");
        body.Append("the start if you like, they just won't be worth XP yet.");

        if (scheduled.AutoEndUtc is { } close)
            body.Append($"\n\nIt runs until <t:{Unix(close)}:F>.");
        else if (scheduled.EndUtc is { } target)
            body.Append($"\n\nPlanned to run until <t:{Unix(target)}:D>.");

        return embed
            .WithTitle(BoardTitle)
            .WithColor(new Color(0xF1C40F))
            .WithDescription(body.ToString())
            .Build();
    }

    private static string BuildSubtitle(XpSeason season, int ranked)
    {
        var parts = new List<string>();

        if (season.EndUtc is { } end)
        {
            var days = (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays);

            if (season.AutoEndUtc is not null)
            {
                // A season that closes itself can state a deadline honestly. The
                // "past" case should be seconds long: the scheduler closes it on the
                // next tick.
                parts.Add(days > 1 ? $"closes in {days} days"
                    : days == 1 ? "closes today"
                    : "closing now");
            }
            else
            {
                // No automatic close, so a past date is a note for the officers, not
                // something the board should claim already happened.
                parts.Add(days > 1 ? $"{days} days to go"
                    : days == 1 ? "last day"
                    : "past its planned end");
            }
        }

        parts.Add(ranked == 1 ? "1 member ranked" : $"{ranked:N0} members ranked");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Buttons. On the pinned board these open a private view; inside that
    /// private view they page it in place. Custom ids carry the target page and
    /// nothing else, so they keep working across restarts with no state to
    /// rehydrate.
    /// </summary>
    private static MessageComponent? BuildComponents(int page, int totalPages, bool forBoard, int pageSize)
    {
        if (totalPages <= 1 && forBoard) return null;

        var builder = new ComponentBuilder();

        if (forBoard)
        {
            builder.WithButton($"Next {pageSize} \u25B6", "xp:page:2", ButtonStyle.Secondary);
            builder.WithButton("🔎 Find me", "xp:find", ButtonStyle.Primary);
            return builder.Build();
        }

        // One page means no pager. Four buttons where three of them are dead is
        // noise, and it sidesteps the duplicate-id trap below entirely.
        if (totalPages <= 1)
        {
            builder.WithButton("🔎 Find me", "xp:find", ButtonStyle.Primary);
            return builder.Build();
        }

        // Every custom id in a single message must be UNIQUE. Discord rejects the
        // whole payload with 50035 COMPONENT_CUSTOM_ID_DUPLICATED otherwise, which
        // fails the send, not just the button.
        //
        // Clamping the target page produced exactly that: on the first page Prev
        // clamped to xp:page:1 and on the last page Next clamped to the same value
        // as the page it was already on, so a one-page view emitted xp:page:1 twice
        // and /xp-leaderboard threw for every member. Disabled buttons still carry
        // an id and are still validated, so the ends get their own inert ids rather
        // than a clamped real one.
        var prevId = page > 1 ? $"xp:page:{page - 1}" : "xp:noop:first";
        var nextId = page < totalPages ? $"xp:page:{page + 1}" : "xp:noop:last";

        builder.WithButton("◀ Prev", prevId, ButtonStyle.Secondary, disabled: page <= 1);
        builder.WithButton($"Page {page} / {totalPages}", "xp:noop", ButtonStyle.Secondary, disabled: true);
        builder.WithButton("Next ▶", nextId, ButtonStyle.Secondary, disabled: page >= totalPages);
        builder.WithButton("🔎 Find me", "xp:find", ButtonStyle.Primary);

        return builder.Build();
    }

    // ─── Season announcements ───────────────────────────────────────────

    /// <summary>
    /// Resolves the channel XP announcements go to. Falls back to the board channel
    /// so a season opening or closing is never completely silent just because the
    /// announce channel is unset. Returns null when neither is configured or
    /// reachable, and callers report that rather than pretending it posted.
    /// </summary>
    /// <summary>
    /// Ceiling on a single season announcement. Discord.NET's default retry mode
    /// silently AWAITS a rate limit's retry-after, which on the scheduler's loop
    /// would mean one throttled post stops every later season transition from being
    /// checked at all. Same guard /kick-awols needed for the same reason.
    /// </summary>
    private static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(20);

    private SocketTextChannel? ResolveAnnounceChannel(SocketGuild guild)
    {
        var channelId = _config.XpLevelUpAnnounceChannelId;

        // 0 means "no announcements", the same as it does for level-ups and leader
        // changes. Falling back to the board channel would drop a post into the
        // read-only channel and leave the board no longer the last message there.
        if (channelId == 0) return null;
        if (channelId == _config.XpBoardChannelId) return null;

        return guild.GetChannel(channelId) as SocketTextChannel;
    }

    /// <summary>
    /// Announces that a season has opened.
    ///
    /// This only exists because seasons can now open on a schedule. A season an
    /// officer starts by hand announces itself: they are stood at the keyboard and
    /// can say so. One that opens at midnight has nobody to say it, and the first
    /// anyone would know is the board quietly changing, which is a poor way to
    /// discover that the thing you were told about on Tuesday is now live.
    ///
    /// Deliberately not pinged to @everyone. It is a scoreboard, not an alert.
    /// </summary>
    public async Task<bool> PostSeasonStartAsync(SocketGuild guild, XpSeason season, CancellationToken ct)
    {
        var channel = ResolveAnnounceChannel(guild);
        if (channel is null) return false;

        var body = new StringBuilder();
        body.Append("XP is now being counted. Every event, meeting, voice session and message from ");
        body.Append($"<t:{Unix(season.StartUtc)}:t> onwards adds to your total.\n\n");
        body.Append("Everyone starts at zero. Lifetime totals from past seasons are untouched.");

        if (season.AutoEndUtc is { } close)
            body.Append($"\n\nThe season closes <t:{Unix(close)}:F>, <t:{Unix(close)}:R>.");
        else if (season.EndUtc is { } target)
            body.Append($"\n\nIt is planned to run until <t:{Unix(target)}:D>.");

        var embed = new EmbedBuilder()
            .WithTitle($"🚩 {XpService.SeasonLabel(season)} is live")
            .WithColor(new Color(0xF1C40F))
            .WithDescription(body.ToString())
            .AddField("Where to look",
                _config.XpBoardChannelId != 0
                    ? $"The leaderboard is in <#{_config.XpBoardChannelId}>, updated automatically."
                    : "Run `/xp-leaderboard` for the standings.")
            .AddField("Your card", "`/xp` shows your level and exactly where every point came from.")
            .WithFooter("XP is recognition only. It does not affect promotions.")
            .WithCurrentTimestamp()
            .Build();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnnounceTimeout);

            await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None,
                options: new RequestOptions { RetryMode = RetryMode.AlwaysFail, CancelToken = cts.Token });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to announce the start of Season {Number}", season.Number);
            return false;
        }
    }

    /// <summary>
    /// Posts the end-of-season results card.
    ///
    /// This is the permanent artefact of a season. The numbers behind it reset the
    /// moment the next one opens, so if this never gets posted the season may as
    /// well not have happened. Returns true only if it actually reached a channel,
    /// so a manual /xp-season end can tell the officer the truth rather than sending
    /// them hunting for a card that was never sent.
    ///
    /// Lives here rather than in XpCommandHandler because a season can now close on
    /// a schedule with no command and no officer attached to it.
    /// </summary>
    public async Task<bool> PostSeasonResultsAsync(
        SocketGuild guild, XpSeason finished, IReadOnlyList<XpStanding> standings, CancellationToken ct)
    {
        var channel = ResolveAnnounceChannel(guild);
        if (channel is null) return false;

        var podium = standings.Take(3)
            .Select(s => new XpBoardEntry(
                s.Place, s.UserId, s.Username, s.Level, s.Xp,
                guild.GetUser(s.UserId)?.GetDisplayAvatarUrl(ImageFormat.Png, 128)))
            .ToList();

        var medals = new[] { "🥇", "🥈", "🥉" };
        var lines = podium.Count == 0
            ? "_Nobody placed. The season closed with no XP earned._"
            : string.Join('\n', podium.Select((s, i) =>
                $"{medals[Math.Min(i, medals.Length - 1)]} <@{s.UserId}> is on **{s.Xp:N0} XP** (Level {s.Level})"));

        var png = podium.Count > 0
            ? await _renderer.TryRenderSeasonResultsAsync(
                XpService.SeasonLabel(finished), podium, standings.Count, ct)
            : null;

        var embed = new EmbedBuilder()
            .WithTitle($"🏁 {XpService.SeasonLabel(finished)}: final standings")
            .WithColor(new Color(0xF1C40F))
            .WithDescription(
                $"{lines}\n\n{standings.Count:N0} member(s) placed. Season XP resets to zero for the next " +
                "season. Lifetime totals and every past result are kept, check yours with `/xp`.")
            .WithCurrentTimestamp();

        if (png is not null) embed.WithImageUrl("attachment://xp-season-results.png");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnnounceTimeout);
            var options = new RequestOptions { RetryMode = RetryMode.AlwaysFail, CancelToken = cts.Token };

            if (png is null)
            {
                await channel.SendMessageAsync(embed: embed.Build(),
                    allowedMentions: AllowedMentions.None, options: options);
            }
            else
            {
                using var fa = new FileAttachment(new MemoryStream(png), "xp-season-results.png");
                await channel.SendFileAsync(fa, embed: embed.Build(),
                    allowedMentions: AllowedMentions.None, options: options);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to post the results for Season {Number}", finished.Number);
            return false;
        }
    }

    internal static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>
    /// A plain-English gap, for the places Discord's own relative stamp cannot go
    /// (embed footers, and sentences where "in 3 days" reads better mid-line).
    /// </summary>
    internal static string FormatRelative(DateTime utc)
    {
        var delta = utc - DateTime.UtcNow;
        if (delta <= TimeSpan.Zero) return "any moment now";
        if (delta.TotalHours < 1) return $"in {Math.Max(1, (int)delta.TotalMinutes)} min";
        if (delta.TotalHours < 36) return $"in {(int)Math.Round(delta.TotalHours)} hours";
        return $"in {(int)Math.Round(delta.TotalDays)} days";
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Both season states in one scope: the one accruing right now, and the one
    /// lined up to open later. They are mutually exclusive in practice (a season
    /// cannot be scheduled while another is running) but the guide and the board
    /// both need to know which of the two, if either, they are describing.
    /// </summary>
    private async Task<(XpSeason? Active, XpSeason? Scheduled)> GetSeasonStateAsync(ulong guildId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var active = await _xp.GetActiveSeasonAsync(db, guildId, ct);
        if (active is not null) return (active, null);

        return (null, await _xp.GetScheduledSeasonAsync(db, guildId, ct));
    }

    /// <summary>
    /// Every config value that appears inside the rendered rates card. If any moves,
    /// the card is stale and must be re-uploaded — the guide's text alone wouldn't
    /// show it.
    /// </summary>
    private string BuildRatesSignature() => string.Join('|',
        _config.XpPerEvent, _config.XpPerMeeting, _config.XpRsvpHonoredBonus,
        _config.XpStreak3Bonus, _config.XpStreak5Bonus,
        _config.XpVoicePer15Minutes, _config.XpVoiceDailyCap,
        _config.XpPerMessage, _config.XpMessageDailyCap);

    private static bool HasEmbedTitled(IUserMessage m, string title) =>
        m.Embeds.Any(e => string.Equals(e.Title, title, StringComparison.Ordinal));

    private async Task DeleteAllAsync(IEnumerable<IUserMessage> messages, CancellationToken ct)
    {
        foreach (var m in messages)
        {
            try { await m.DeleteAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "XP channel: couldn't delete message {Msg}", m.Id); }
        }
    }

    private async Task EnsurePinnedAsync(IUserMessage? message, CancellationToken ct)
    {
        if (message is null || message.IsPinned) return;

        try
        {
            await message.PinAsync();
            _logger.LogInformation("XP: pinned message {Msg}", message.Id);
        }
        catch (Exception ex)
        {
            // Missing Manage Messages, or the channel's 50-pin limit is full.
            // Not fatal — the messages still render, they're just not in the pins.
            _logger.LogWarning(ex, "XP: could not pin message {Msg}", message.Id);
        }
    }

    /// <summary>
    /// Deletes the "… pinned a message to this channel" system notices that
    /// pinning generates. Without this the locked channel accumulates a notice
    /// per pin and the board stops being the last thing in the channel.
    /// </summary>
    private async Task CleanPinNoticesAsync(SocketTextChannel channel, CancellationToken ct)
    {
        try
        {
            var recent = (await channel.GetMessagesAsync(BoardScanLimit).FlattenAsync()).ToList();
            foreach (var m in recent)
            {
                if (m.Type != MessageType.ChannelPinnedMessage) continue;
                if (m.Author.Id != _client.CurrentUser!.Id) continue;   // only clean up after ourselves
                try { await m.DeleteAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "XP: couldn't delete pin notice {Msg}", m.Id); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "XP: pin-notice cleanup failed");
        }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        return s.Replace("`", "'").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "(").Replace("]", ")").Trim();
    }
}

