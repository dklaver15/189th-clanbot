using System.Collections.Concurrent;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>One rendered line on the leaderboard graphic.</summary>
public sealed record XpBoardEntry(
    int Place,
    ulong UserId,
    string Name,
    int Level,
    int Xp,
    string? AvatarUrl);

/// <summary>
/// Renders the XP surfaces as PNGs: the leaderboard board, the "how to earn XP"
/// rates card, and the end-of-season results card.
///
/// ── Why draw instead of formatting an embed ──
/// A Discord embed can only ever look like a Discord embed. The leaderboard is
/// the one surface the whole clan is meant to look at and care about, so it gets
/// a designed graphic: a real podium, avatars, XP bars scaled against the leader,
/// level badges. The embed underneath still carries the season line and the
/// legend, so the message degrades to something readable if a render ever fails.
///
/// This follows MemberActivityChartRenderer exactly — same SkiaSharp-direct
/// approach (no plotting library), same Discord dark-theme palette so the image
/// sits in the embed rather than on top of it, same 2× render for retina, and the
/// same contract: EVERY entry point returns null on failure rather than throwing.
/// Callers MUST handle null and fall back to text. A leaderboard that vanishes
/// because a font is missing is much worse than an ugly one.
///
/// ── Avatars ──
/// Fetched over HTTP and cached by URL. Discord bakes a content hash into avatar
/// URLs, so a member changing their avatar produces a different URL and the cache
/// invalidates itself — no TTL needed. The cache is bounded and cleared wholesale
/// when it fills, which is crude but correct: worst case is one cold cycle of
/// re-fetching, and the alternative (LRU bookkeeping) is not worth the code for a
/// few hundred entries.
/// </summary>
public sealed class XpLeaderboardRenderer
{
    // ── Palette ─────────────────────────────────────────────────────────
    // Bare hex (no '#'), matching MemberActivityChartRenderer. Sampled from
    // Discord's dark theme plus the 189th gold.
    private const string BackgroundHex = "0d1017";   // deep console black
    private const string PanelHex      = "161b24";   // row / card fill
    private const string PanelAltHex   = "12161e";   // alternating row fill
    private const string TextHex       = "ffffff";
    private const string MutedHex      = "8b96a8";
    private const string GoldHex       = "ffd86b";   // 189th gold — accents, #1
    private const string SilverHex     = "cfd8e3";
    private const string BronzeHex     = "d08c52";
    private const string BlurpleHex    = "5865f2";   // XP bars
    private const string GreenHex      = "57f287";

    // ── Layout (pixels, 2× logical) ─────────────────────────────────────
    // 1200 wide renders at roughly 600 CSS px in Discord's message column,
    // so every edge lands on a whole device pixel on a retina display.
    private const int Width      = 1200;
    private const int Pad        = 44;
    private const int HeaderH    = 172;
    private const int PodiumH    = 320;
    private const int RowH       = 62;
    private const int FooterH    = 68;

    private const int MaxCachedAvatars = 400;

    /// <summary>Per-request avatar timeout.</summary>
    private static readonly TimeSpan AvatarTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Total budget for fetching every avatar on one render. See LoadAvatarsAsync.</summary>
    private static readonly TimeSpan AvatarBatchBudget = TimeSpan.FromSeconds(20);

    /// <summary>How long a FAILED avatar fetch stays negative-cached before we retry it.</summary>
    private static readonly TimeSpan AvatarFailureTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpFactory;
    private readonly BotConfig _config;
    private readonly ILogger<XpLeaderboardRenderer> _logger;

    // url → decoded PNG/WebP bytes. Null value = "we tried and it failed",
    // cached so a broken avatar URL isn't re-fetched on every single refresh.
    private readonly ConcurrentDictionary<string, byte[]> _avatarCache = new();

    // url → when the fetch last failed. Separate from the success cache so a failure
    // expires (AvatarFailureTtl) instead of blanking that member permanently — a
    // single CDN blip used to grey them out until they changed their avatar.
    private readonly ConcurrentDictionary<string, DateTime> _avatarFailures = new();

    public XpLeaderboardRenderer(
        IHttpClientFactory httpFactory,
        IOptions<BotConfig> config,
        ILogger<XpLeaderboardRenderer> logger)
    {
        _httpFactory = httpFactory;
        _config = config.Value;
        _logger = logger;
    }

    // ─── Leaderboard ────────────────────────────────────────────────────

    /// <summary>
    /// Renders one page of the leaderboard. Page 1 gets the podium treatment for
    /// the top three and lists the rest beneath; later pages are a plain ranked
    /// list, because a podium on page 3 would be celebrating 51st place.
    /// </summary>
    public async Task<byte[]?> TryRenderLeaderboardAsync(
        string seasonLabel,
        string subtitle,
        IReadOnlyList<XpBoardEntry> entries,
        int page,
        int totalPages,
        int totalRanked,
        CancellationToken ct)
    {
        try
        {
            var podium = page == 1 ? entries.Where(e => e.Place <= 3).ToList() : new List<XpBoardEntry>();
            var rows   = entries.Where(e => !podium.Contains(e)).ToList();

            // Avatars are fetched up front so the draw pass stays synchronous —
            // SkiaSharp state and awaits do not mix comfortably.
            var avatars = await LoadAvatarsAsync(entries, ct);
            using var avatarLease = new AvatarLease(avatars);

            var height = HeaderH
                         + (podium.Count > 0 ? PodiumH : 0)
                         + rows.Count * RowH
                         + FooterH
                         + (rows.Count > 0 ? Pad / 2 : 0);

            var info = new SKImageInfo(Width, Math.Max(height, HeaderH + FooterH));
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse(BackgroundHex));

            var boldFace = ResolveTypeface(SKFontStyle.Bold);
            var bodyFace = ResolveTypeface(SKFontStyle.Normal);

            using var titleFont    = new SKFont(boldFace, 46f);
            using var seasonFont   = new SKFont(boldFace, 24f);
            using var subtitleFont = new SKFont(bodyFace, 20f);
            using var nameFont     = new SKFont(boldFace, 25f);
            using var podiumName   = new SKFont(boldFace, 26f);
            using var valueFont    = new SKFont(boldFace, 24f);
            using var smallFont    = new SKFont(bodyFace, 19f);
            using var rankFont     = new SKFont(boldFace, 27f);

            // "TOP n" reflects how many are actually on this page — XpBoardPageSize
            // is configurable, so hardcoding "TOP 25" would eventually be a lie.
            var rightLabel = totalPages > 1 ? $"PAGE {page} / {totalPages}" : $"TOP {entries.Count}";
            var y = DrawHeader(canvas, titleFont, seasonFont, subtitleFont, smallFont,
                "XP LEADERBOARD", seasonLabel, subtitle, rightLabel, totalRanked);

            // Bars are scaled against the leader on THIS page, not against the
            // all-time top score — otherwise page 4 is 25 identical stubs and the
            // bar stops carrying any information.
            var barMax = entries.Count > 0 ? Math.Max(1, entries.Max(e => e.Xp)) : 1;

            if (podium.Count > 0)
            {
                DrawPodium(canvas, podium, avatars, y, podiumName, valueFont, smallFont, rankFont);
                y += PodiumH;
            }

            if (rows.Count > 0) y += Pad / 2;

            foreach (var entry in rows)
            {
                DrawRow(canvas, entry, avatars, y, barMax, nameFont, valueFont, smallFont, rankFont);
                y += RowH;
            }

            DrawFooter(canvas, smallFont, info.Height);

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XP leaderboard render failed (page {Page})", page);
            return null;
        }
    }

    private int DrawHeader(
        SKCanvas canvas, SKFont titleFont, SKFont seasonFont, SKFont subtitleFont, SKFont smallFont,
        string title, string seasonLabel, string subtitle, string rightLabel, int totalRanked)
    {
        using var gold  = new SKPaint { Color = SKColor.Parse(GoldHex),  IsAntialias = true };
        using var white = new SKPaint { Color = SKColor.Parse(TextHex),  IsAntialias = true };
        using var muted = new SKPaint { Color = SKColor.Parse(MutedHex), IsAntialias = true };

        // Gold-to-transparent wash behind the header — gives the top of the image
        // some weight without a hard band that would fight the embed's own edge.
        using (var wash = new SKPaint { IsAntialias = true })
        {
            using var washShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, HeaderH),
                new[] { SKColor.Parse(GoldHex).WithAlpha(26), SKColor.Parse(BackgroundHex).WithAlpha(0) },
                null,
                SKShaderTileMode.Clamp);
            wash.Shader = washShader;
            canvas.DrawRect(new SKRect(0, 0, Width, HeaderH), wash);
        }

        canvas.DrawText(title, Pad, 74f, titleFont, white);
        canvas.DrawText(seasonLabel.ToUpperInvariant(), Pad, 112f, seasonFont, gold);
        if (!string.IsNullOrWhiteSpace(subtitle))
            canvas.DrawText(subtitle, Pad, 144f, subtitleFont, muted);

        // Right-aligned status label + roster count.
        DrawRightAligned(canvas, rightLabel, Width - Pad, 78f, seasonFont, gold);
        DrawRightAligned(canvas, $"{totalRanked:N0} on the board", Width - Pad, 112f, smallFont, muted);

        using (var rule = new SKPaint { IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke })
        {
            using var ruleShader = SKShader.CreateLinearGradient(
                new SKPoint(Pad, 0),
                new SKPoint(Width - Pad, 0),
                new[] { SKColor.Parse(GoldHex), SKColor.Parse(GoldHex).WithAlpha(0) },
                null,
                SKShaderTileMode.Clamp);
            rule.Shader = ruleShader;
            canvas.DrawLine(Pad, HeaderH - 10f, Width - Pad, HeaderH - 10f, rule);
        }

        return HeaderH;
    }

    /// <summary>
    /// The classic 2–1–3 podium: first place centred, silver left, bronze right.
    /// Reading order puts the winner where the eye lands first.
    ///
    /// All three cards are the SAME height on purpose. Stair-stepping them the way
    /// a real podium does looks better in a mockup, but it makes every text
    /// baseline depend on the card height, and the shortest card then overflows
    /// its own bottom edge as soon as a name wraps or a level badge gets wider.
    /// Rank is carried by position, accent colour, card width and the "#1" label
    /// instead — all of which survive a long clan nickname.
    /// </summary>
    private void DrawPodium(
        SKCanvas canvas, IReadOnlyList<XpBoardEntry> podium, IReadOnlyDictionary<ulong, SKImage> avatars,
        int top, SKFont nameFont, SKFont valueFont, SKFont smallFont, SKFont rankFont)
    {
        var byPlace = podium.ToDictionary(p => p.Place);

        const float cardH  = 250f;
        const float cardTop = 50f;     // clear of the header rule above

        // (place, centre x, card width, avatar radius, accent)
        var slots = new (int Place, float Cx, float W, float R, string Accent)[]
        {
            (2, Width * 0.20f, 310f, 48f, SilverHex),
            (1, Width * 0.50f, 360f, 56f, GoldHex),
            (3, Width * 0.80f, 310f, 48f, BronzeHex),
        };

        foreach (var slot in slots)
        {
            if (!byPlace.TryGetValue(slot.Place, out var entry)) continue;

            var accent = SKColor.Parse(slot.Accent);
            var rectTop = top + cardTop;
            var rect = new SKRect(slot.Cx - slot.W / 2f, rectTop, slot.Cx + slot.W / 2f, rectTop + cardH);

            using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
            {
                using var fillShader = SKShader.CreateLinearGradient(
                    new SKPoint(0, rect.Top),
                    new SKPoint(0, rect.Bottom),
                    new[] { accent.WithAlpha(46), SKColor.Parse(PanelHex) },
                    null,
                    SKShaderTileMode.Clamp);
                fill.Shader = fillShader;
                canvas.DrawRoundRect(rect, 18f, 18f, fill);
            }

            using (var edge = new SKPaint
                   {
                       IsAntialias = true,
                       Style = SKPaintStyle.Stroke,
                       StrokeWidth = slot.Place == 1 ? 3f : 2f,
                       Color = accent.WithAlpha(slot.Place == 1 ? (byte)220 : (byte)140),
                   })
            {
                canvas.DrawRoundRect(rect, 18f, 18f, edge);
            }

            // Avatar sits fully INSIDE the card. Straddling the top edge looks
            // sharper but pushes the #1 avatar up into the header rule.
            var avatarCy = rect.Top + slot.R + 14f;
            DrawAvatar(canvas, avatars, entry.UserId, slot.Cx, avatarCy, slot.R, accent);

            using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };
            using var white = new SKPaint { Color = SKColor.Parse(TextHex), IsAntialias = true };
            using var muted = new SKPaint { Color = SKColor.Parse(MutedHex), IsAntialias = true };

            // Baselines are anchored to the CARD BOTTOM, not to the avatar. Anchoring
            // to the avatar meant #1 — which has a bigger avatar on a same-height
            // card — started its text lower and pushed "Level 29" out through the
            // bottom border. Anchoring here also lines all three cards' text up.
            var textTop = rect.Bottom - 104f;
            DrawCentered(canvas, $"#{entry.Place}", slot.Cx, textTop, rankFont, accentPaint);
            DrawCentered(canvas, Ellipsize(entry.Name, nameFont, slot.W - 30f), slot.Cx, textTop + 32f, nameFont, white);
            DrawCentered(canvas, $"{entry.Xp:N0} XP", slot.Cx, textTop + 62f, valueFont, accentPaint);
            DrawCentered(canvas, $"Level {entry.Level}", slot.Cx, textTop + 90f, smallFont, muted);
        }
    }

    private void DrawRow(
        SKCanvas canvas, XpBoardEntry entry, IReadOnlyDictionary<ulong, SKImage> avatars,
        int top, int barMax, SKFont nameFont, SKFont valueFont, SKFont smallFont, SKFont rankFont)
    {
        var rect = new SKRect(Pad, top + 4f, Width - Pad, top + RowH - 4f);

        using (var bg = new SKPaint
               {
                   IsAntialias = true,
                   Color = SKColor.Parse(entry.Place % 2 == 0 ? PanelAltHex : PanelHex),
               })
        {
            canvas.DrawRoundRect(rect, 10f, 10f, bg);
        }

        // XP bar fills the row behind the text. Kept low-alpha so it reads as a
        // gauge rather than a highlight — you can see relative standing at a
        // glance without the names becoming hard to read.
        var frac = Math.Clamp(entry.Xp / (float)barMax, 0.02f, 1f);
        var barRect = new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * frac, rect.Bottom);
        using (var bar = new SKPaint { IsAntialias = true })
        {
            using var barShader = SKShader.CreateLinearGradient(
                new SKPoint(barRect.Left, 0),
                new SKPoint(barRect.Right, 0),
                new[] { SKColor.Parse(BlurpleHex).WithAlpha(70), SKColor.Parse(BlurpleHex).WithAlpha(10) },
                null,
                SKShaderTileMode.Clamp);
            bar.Shader = barShader;
            canvas.Save();
            using var clipRr = new SKRoundRect(rect, 10f, 10f);
            canvas.ClipRoundRect(clipRr, SKClipOperation.Intersect, true);
            canvas.DrawRect(barRect, bar);
            canvas.Restore();
        }

        using var white = new SKPaint { Color = SKColor.Parse(TextHex), IsAntialias = true };
        using var muted = new SKPaint { Color = SKColor.Parse(MutedHex), IsAntialias = true };
        using var gold  = new SKPaint { Color = SKColor.Parse(GoldHex), IsAntialias = true };

        var midY = rect.MidY;
        var baseline = midY - (nameFont.Metrics.Ascent + nameFont.Metrics.Descent) / 2f;

        DrawRightAligned(canvas, entry.Place.ToString(), rect.Left + 68f, baseline, rankFont,
            entry.Place <= 10 ? gold : muted);

        DrawAvatar(canvas, avatars, entry.UserId, rect.Left + 108f, midY, 21f, SKColor.Parse(MutedHex).WithAlpha(90));

        var nameX = rect.Left + 142f;
        var levelText = $"Lv {entry.Level}";
        var xpText = $"{entry.Xp:N0} XP";
        var xpW = valueFont.MeasureText(xpText);
        var lvW = smallFont.MeasureText(levelText);
        var nameMax = rect.Right - nameX - xpW - lvW - 70f;

        canvas.DrawText(Ellipsize(entry.Name, nameFont, nameMax), nameX, baseline, nameFont, white);
        DrawRightAligned(canvas, levelText, rect.Right - xpW - 34f, baseline, smallFont, muted);
        DrawRightAligned(canvas, xpText, rect.Right - 20f, baseline, valueFont, white);
    }

    private void DrawFooter(SKCanvas canvas, SKFont font, int canvasHeight)
    {
        using var muted = new SKPaint { Color = SKColor.Parse(MutedHex), IsAntialias = true };
        canvas.DrawText(
            "XP is recognition only — it does not affect promotions.",
            Pad, canvasHeight - 26f, font, muted);
        DrawRightAligned(canvas, "189th CLANGUARD", Width - Pad, canvasHeight - 26f, font, muted);
    }

    // ─── Rates card (for the pinned guide) ──────────────────────────────

    /// <summary>
    /// The "how to earn XP" table, rendered rather than written as embed text —
    /// Discord markdown has no tables, and the bars are the actual message: an op
    /// bar that dwarfs the chat bar communicates the economy's priority faster
    /// than any sentence explaining it does.
    ///
    /// Values come from live config, so a retune can't leave the pinned guide
    /// quietly lying to the clan.
    /// </summary>
    public byte[]? TryRenderRatesCard()
    {
        try
        {
            var rows = new List<(string Label, string Value, int Weight, string Accent)>();

            if (_config.XpPerEvent > 0)
                rows.Add(("Attend an event", $"{_config.XpPerEvent:N0} XP", _config.XpPerEvent, GoldHex));
            if (_config.XpPerMeeting > 0)
                rows.Add(("Attend a clan meeting", $"{_config.XpPerMeeting:N0} XP", _config.XpPerMeeting, GoldHex));
            if (_config.XpStreak5Bonus > 0)
                rows.Add(("Every 5 events in a row", $"+{_config.XpStreak5Bonus:N0} XP", _config.XpStreak5Bonus, GreenHex));
            if (_config.XpStreak3Bonus > 0)
                rows.Add(("Every 3 events in a row", $"+{_config.XpStreak3Bonus:N0} XP", _config.XpStreak3Bonus, GreenHex));
            if (_config.XpRsvpHonoredBonus > 0)
                rows.Add(("RSVP \"Going\" — and show up", $"+{_config.XpRsvpHonoredBonus:N0} XP", _config.XpRsvpHonoredBonus, GreenHex));
            if (_config.XpVoicePer15Minutes > 0)
                rows.Add(($"Voice chat  (max {_config.XpVoiceDailyCap}/day)",
                    $"{_config.XpVoicePer15Minutes} XP / 15 min", _config.XpVoicePer15Minutes, BlurpleHex));
            if (_config.XpPerMessage > 0)
                rows.Add(($"Chatting  (max {_config.XpPerMessage * _config.XpMessageDailyCap}/day)",
                    $"{_config.XpPerMessage} XP / message", _config.XpPerMessage, BlurpleHex));

            const int rowH = 66;
            var height = 150 + rows.Count * rowH + 96;

            var info = new SKImageInfo(Width, height);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse(BackgroundHex));

            var boldFace = ResolveTypeface(SKFontStyle.Bold);
            var bodyFace = ResolveTypeface(SKFontStyle.Normal);

            using var titleFont = new SKFont(boldFace, 42f);
            using var subFont   = new SKFont(bodyFace, 21f);
            using var labelFont = new SKFont(bodyFace, 25f);
            using var valueFont = new SKFont(boldFace, 25f);
            using var noteFont  = new SKFont(bodyFace, 19f);

            using var white = new SKPaint { Color = SKColor.Parse(TextHex), IsAntialias = true };
            using var muted = new SKPaint { Color = SKColor.Parse(MutedHex), IsAntialias = true };
            using var gold  = new SKPaint { Color = SKColor.Parse(GoldHex), IsAntialias = true };

            using (var wash = new SKPaint { IsAntialias = true })
            {
                using var washShader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, 150),
                    new[] { SKColor.Parse(GoldHex).WithAlpha(26), SKColor.Parse(BackgroundHex).WithAlpha(0) },
                    null, SKShaderTileMode.Clamp);
                wash.Shader = washShader;
                canvas.DrawRect(new SKRect(0, 0, Width, 150), wash);
            }

            canvas.DrawText("HOW TO EARN XP", Pad, 70f, titleFont, white);
            canvas.DrawText("Events are worth more than everything else combined.", Pad, 108f, subFont, muted);

            // Bars are LINEAR against the biggest value on purpose. Log scaling
            // would make chat look competitive with ops; the near-invisible chat
            // bar is the honest picture and the whole point of the design.
            var max = Math.Max(1, rows.Count > 0 ? rows.Max(r => r.Weight) : 1);
            var y = 150f;

            foreach (var (label, value, weight, accentHex) in rows)
            {
                var accent = SKColor.Parse(accentHex);
                var rect = new SKRect(Pad, y + 6f, Width - Pad, y + rowH - 6f);

                using (var bg = new SKPaint { IsAntialias = true, Color = SKColor.Parse(PanelHex) })
                    canvas.DrawRoundRect(rect, 10f, 10f, bg);

                var frac = Math.Clamp(weight / (float)max, 0.012f, 1f);
                var barRect = new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * frac, rect.Bottom);
                using (var bar = new SKPaint { IsAntialias = true })
                {
                    using var barShader = SKShader.CreateLinearGradient(
                        new SKPoint(barRect.Left, 0), new SKPoint(barRect.Right, 0),
                        new[] { accent.WithAlpha(150), accent.WithAlpha(40) },
                        null, SKShaderTileMode.Clamp);
                    bar.Shader = barShader;
                    canvas.Save();
                    using var clipRr2 = new SKRoundRect(rect, 10f, 10f);
                    canvas.ClipRoundRect(clipRr2, SKClipOperation.Intersect, true);
                    canvas.DrawRect(barRect, bar);
                    canvas.Restore();
                }

                var baseline = rect.MidY - (labelFont.Metrics.Ascent + labelFont.Metrics.Descent) / 2f;
                var valueW = valueFont.MeasureText(value);
                canvas.DrawText(Ellipsize(label, labelFont, rect.Width - valueW - 60f),
                    rect.Left + 22f, baseline, labelFont, white);
                DrawRightAligned(canvas, value, rect.Right - 22f, baseline, valueFont, gold);

                y += rowH;
            }

            canvas.DrawText(
                "Event and meeting XP is automatic — it comes from being in the voice channel during the event.",
                Pad, y + 44f, noteFont, muted);
            canvas.DrawText(
                "AFK doesn't count. Short voice hops don't count. Chat is capped, on purpose.",
                Pad, y + 72f, noteFont, muted);

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XP rates card render failed");
            return null;
        }
    }

    // ─── Season results card ────────────────────────────────────────────

    /// <summary>
    /// End-of-season card: the podium, without the running-total furniture. Posted
    /// once when an officer closes a season, so it becomes the permanent artefact
    /// of that season in the channel even though the numbers behind it reset.
    /// </summary>
    public async Task<byte[]?> TryRenderSeasonResultsAsync(
        string seasonLabel, IReadOnlyList<XpBoardEntry> podium, int totalRanked, CancellationToken ct)
    {
        try
        {
            var avatars = await LoadAvatarsAsync(podium, ct);
            using var avatarLease = new AvatarLease(avatars);

            var info = new SKImageInfo(Width, HeaderH + PodiumH + FooterH);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse(BackgroundHex));

            var boldFace = ResolveTypeface(SKFontStyle.Bold);
            var bodyFace = ResolveTypeface(SKFontStyle.Normal);

            using var titleFont    = new SKFont(boldFace, 46f);
            using var seasonFont   = new SKFont(boldFace, 24f);
            using var subtitleFont = new SKFont(bodyFace, 20f);
            using var podiumName   = new SKFont(boldFace, 26f);
            using var valueFont    = new SKFont(boldFace, 24f);
            using var smallFont    = new SKFont(bodyFace, 19f);
            using var rankFont     = new SKFont(boldFace, 27f);

            var y = DrawHeader(canvas, titleFont, seasonFont, subtitleFont, smallFont,
                "SEASON RESULTS", seasonLabel, "Final standings", "FINAL", totalRanked);

            DrawPodium(canvas, podium, avatars, y, podiumName, valueFont, smallFont, rankFont);
            DrawFooter(canvas, smallFont, info.Height);

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "XP season results render failed");
            return null;
        }
    }

    // ─── Avatars ────────────────────────────────────────────────────────

    private async Task<Dictionary<ulong, SKImage>> LoadAvatarsAsync(
        IReadOnlyList<XpBoardEntry> entries, CancellationToken ct)
    {
        var result = new Dictionary<ulong, SKImage>();
        if (!_config.XpBoardShowAvatars) return result;

        // A whole-batch deadline, not just a per-request one. Sequentially, 25 cold
        // fetches at the per-request timeout is minutes of wall clock — and the board
        // refresh holds its lock the entire time, so an officer running /xp-season
        // would sit on "thinking" behind it. Past the budget we stop fetching and draw
        // plain discs for the rest; the next refresh picks them up from cache.
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(AvatarBatchBudget);

        foreach (var entry in entries)
        {
            if (batchCts.IsCancellationRequested)
            {
                _logger.LogDebug("XP board: avatar budget exhausted; remaining members draw without avatars");
                break;
            }

            if (string.IsNullOrWhiteSpace(entry.AvatarUrl)) continue;
            if (result.ContainsKey(entry.UserId)) continue;

            var bytes = await GetAvatarBytesAsync(entry.AvatarUrl!, batchCts.Token);
            if (bytes is null) continue;

            // A corrupt or unsupported payload decodes to null rather than
            // throwing; skipping it just means that member draws a plain disc.
            var image = SKImage.FromEncodedData(bytes);
            if (image is not null) result[entry.UserId] = image;
        }

        return result;
    }

    private async Task<byte[]?> GetAvatarBytesAsync(string url, CancellationToken ct)
    {
        if (_avatarCache.TryGetValue(url, out var cached)) return cached;

        if (_avatarFailures.TryGetValue(url, out var failedAt))
        {
            if (DateTime.UtcNow - failedAt < AvatarFailureTtl) return null;
            _avatarFailures.TryRemove(url, out _);
        }

        // Crude but correct: Discord bakes a content hash into avatar URLs, so
        // entries are naturally invalidated by the member changing their avatar.
        // The only unbounded growth is roster churn, and a full clear costs one
        // cold cycle.
        if (_avatarCache.Count > MaxCachedAvatars) { _avatarCache.Clear(); _avatarFailures.Clear(); }

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = AvatarTimeout;
            var bytes = await http.GetByteArrayAsync(url, ct);
            _avatarCache[url] = bytes;
            return bytes;
        }
        catch (OperationCanceledException)
        {
            // Ran out of the batch budget, or the caller's refresh was abandoned.
            // That says nothing about this avatar, so it must NOT be negative-cached:
            // doing so drew that member as a grey disc on every board for an hour
            // because one render happened to be slow.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "XP board: avatar fetch failed for {Url}", url);
            _avatarFailures[url] = DateTime.UtcNow;
            return null;
        }
    }

    private static void DrawAvatar(
        SKCanvas canvas, IReadOnlyDictionary<ulong, SKImage> avatars, ulong userId,
        float cx, float cy, float radius, SKColor ring)
    {
        var dest = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        // Backing disc: drawn first so a missing avatar still leaves a
        // deliberate-looking circle instead of a hole in the layout.
        using (var disc = new SKPaint { IsAntialias = true, Color = SKColor.Parse(MutedHex).WithAlpha(38) })
            canvas.DrawCircle(cx, cy, radius, disc);

        if (avatars.TryGetValue(userId, out var image))
        {
            canvas.Save();
            using (var clip = new SKPath())
            {
                clip.AddCircle(cx, cy, radius);
                canvas.ClipPath(clip, SKClipOperation.Intersect, true);
                canvas.DrawImage(image, dest);
            }
            canvas.Restore();
        }

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color = ring,
        };
        canvas.DrawCircle(cx, cy, radius, stroke);
    }

    /// <summary>
    /// Picks a sans-serif face deterministically.
    ///
    /// Asking for the family "null" (the system default) is what
    /// MemberActivityChartRenderer does, and in the Docker image — which installs
    /// only fonts-dejavu-core — that reliably lands on DejaVu Sans. On any host
    /// with a fuller font set it can resolve to a SERIF face instead, so the same
    /// code produced a visibly different board depending on where it ran. Naming
    /// the families explicitly, most-preferred first, keeps the rendering stable;
    /// the null lookup stays as the last resort before SKTypeface.Default.
    /// </summary>
    private static SKTypeface ResolveTypeface(SKFontStyle style)
    {
        foreach (var family in new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans", "Arial" })
        {
            var face = SKTypeface.FromFamilyName(family, style);
            // Skia falls back to *a* face rather than returning null for an unknown
            // family, so confirm we actually got the one we asked for.
            if (face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                return face;
        }

        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }

    /// <summary>
    /// Disposes the decoded avatar images once a render finishes. SKImage wraps
    /// native memory, and a board refresh decodes up to a page of them every few
    /// minutes — cheap to leak, but pointless to leave to finalizers.
    /// </summary>
    private sealed class AvatarLease : IDisposable
    {
        private readonly Dictionary<ulong, SKImage> _images;
        public AvatarLease(Dictionary<ulong, SKImage> images) => _images = images;

        public void Dispose()
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
    }

    // ─── Text helpers ───────────────────────────────────────────────────

    private static void DrawCentered(SKCanvas canvas, string text, float cx, float baseline, SKFont font, SKPaint paint)
    {
        var w = font.MeasureText(text);
        canvas.DrawText(text, cx - w / 2f, baseline, font, paint);
    }

    private static void DrawRightAligned(SKCanvas canvas, string text, float right, float baseline, SKFont font, SKPaint paint)
    {
        var w = font.MeasureText(text);
        canvas.DrawText(text, right - w, baseline, font, paint);
    }

    /// <summary>
    /// Truncates to fit, appending an ellipsis. Measures per character rather
    /// than estimating from an average width, because clan nicknames are full of
    /// rank prefixes, brackets and box-drawing characters whose widths vary
    /// wildly — an estimate overflows the column on exactly the names people care
    /// most about seeing.
    /// </summary>
    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxWidth <= 0) return string.Empty;
        if (font.MeasureText(text) <= maxWidth) return text;

        const string ellipsis = "…";
        var ellipsisW = font.MeasureText(ellipsis);
        if (ellipsisW >= maxWidth) return string.Empty;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var candidate = text[..len];
            if (font.MeasureText(candidate) + ellipsisW <= maxWidth)
                return candidate.TrimEnd() + ellipsis;
        }

        return ellipsis;
    }
}
