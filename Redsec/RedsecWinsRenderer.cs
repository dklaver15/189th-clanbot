using System.Globalization;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ClanGuardBot.Services;

public sealed record RedsecSquadMember(ulong UserId, string Name, string? AvatarUrl);

/// <summary>A member's win count. Tied members share a <paramref name="Place"/>.</summary>
public sealed record RedsecOperatorLine(int Place, RedsecSquadMember Member, int Wins);

public sealed record RedsecWinLine(int Id, DateOnly WonOn, IReadOnlyList<RedsecSquadMember> Players, string? Notes);

public sealed record RedsecSeasonCard(
    int Season,
    int TotalWins,
    int FullClanSquads,
    int TotalOperators,
    DateOnly? LastWin,
    IReadOnlyList<RedsecOperatorLine> Operators,
    IReadOnlyList<RedsecWinLine> RecentWins);

/// <summary>
/// Draws the /redsec-wins season card. Reuses XpLeaderboardRenderer's typeface,
/// text and avatar helpers. Returns null on failure so the caller can fall back
/// to a text-only embed.
/// </summary>
public sealed class RedsecWinsRenderer
{
    public const int SquadSize = 4;

    private const string BackgroundHex = "0d1017";
    private const string PanelHex      = "161b24";
    private const string PanelAltHex   = "12161e";
    private const string TextHex       = "ffffff";
    private const string MutedHex      = "8b96a8";
    private const string RedHex        = "ff4148";
    private const string GoldHex       = "ffd86b";
    private const string SilverHex     = "cfd8e3";
    private const string BronzeHex     = "d08c52";

    private const int Width     = 950;
    private const int Pad       = 44;
    private const int HeaderH   = 176;
    private const int TilesH    = 128;
    private const int SectionH  = 58;
    private const int PodiumH   = 250;
    private const int RowH      = 58;
    private const int WinRowH   = 84;
    private const int FooterH   = 64;
    private const int ColumnGap = 22;

    private readonly XpLeaderboardRenderer _shared;
    private readonly ILogger<RedsecWinsRenderer> _logger;

    public RedsecWinsRenderer(XpLeaderboardRenderer shared, ILogger<RedsecWinsRenderer> logger)
    {
        _shared = shared;
        _logger = logger;
    }

    public async Task<byte[]?> TryRenderSeasonAsync(RedsecSeasonCard card, CancellationToken ct)
    {
        try
        {
            var podium = card.Operators.Take(3).ToList();
            var rows   = card.Operators.Skip(3).ToList();

            var avatarSources = card.Operators.Select(o => o.Member)
                .Concat(card.RecentWins.SelectMany(w => w.Players))
                .DistinctBy(m => m.UserId)
                .Select(m => (m.UserId, m.AvatarUrl))
                .ToList();
            var avatars = await _shared.LoadAvatarImagesAsync(avatarSources, ct);
            using var avatarLease = new XpLeaderboardRenderer.AvatarLease(avatars);

            var twoCol   = rows.Count >= 4;
            var rowBands = twoCol ? (rows.Count + 1) / 2 : rows.Count;

            var height = HeaderH + TilesH
                         + SectionH + PodiumH + rowBands * RowH
                         + (card.RecentWins.Count > 0 ? SectionH + card.RecentWins.Count * WinRowH : 0)
                         + FooterH;

            var info = new SKImageInfo(Width, height);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse(BackgroundHex));

            var bold = XpLeaderboardRenderer.ResolveTypeface(SKFontStyle.Bold);
            var body = XpLeaderboardRenderer.ResolveTypeface(SKFontStyle.Normal);
            using var f = new Fonts(bold, body);

            float y = DrawHeader(canvas, f, card);
            y = DrawTiles(canvas, f, card, y);

            y = DrawSectionLabel(canvas, f, "TOP OPERATORS", $"{card.TotalOperators} with a win", y);
            DrawPodium(canvas, f, podium, avatars, y);
            y += PodiumH;

            var maxWins = Math.Max(1, card.Operators.Count > 0 ? card.Operators.Max(o => o.Wins) : 1);
            if (twoCol)
            {
                var colW = (Width - Pad * 2 - ColumnGap) / 2f;
                for (var i = 0; i < rows.Count; i++)
                {
                    var inLeft = i < rowBands;
                    var band   = inLeft ? i : i - rowBands;
                    var x0     = inLeft ? Pad : Pad + colW + ColumnGap;
                    DrawOperatorRow(canvas, f, rows[i], avatars, y + band * RowH, maxWins, x0, x0 + colW, band % 2 == 1);
                }
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                    DrawOperatorRow(canvas, f, rows[i], avatars, y + i * RowH, maxWins, Pad, Width - Pad, i % 2 == 1);
            }
            y += rowBands * RowH;

            if (card.RecentWins.Count > 0)
            {
                y = DrawSectionLabel(canvas, f, "RECENT WINS", $"latest {card.RecentWins.Count}", y);
                foreach (var win in card.RecentWins)
                {
                    DrawWinRow(canvas, f, win, avatars, y);
                    y += WinRowH;
                }
            }

            DrawFooter(canvas, f, info.Height);

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "REDSEC season card render failed (season {Season})", card.Season);
            return null;
        }
    }

    private static float DrawHeader(SKCanvas canvas, Fonts f, RedsecSeasonCard card)
    {
        using (var wash = new SKPaint { IsAntialias = true })
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(Width * 0.6f, HeaderH),
                new[] { SKColor.Parse(RedHex).WithAlpha(70), SKColor.Parse(BackgroundHex).WithAlpha(0) },
                null, SKShaderTileMode.Clamp);
            wash.Shader = shader;
            canvas.DrawRect(new SKRect(0, 0, Width, HeaderH), wash);
        }

        DrawChevrons(canvas, Width - 300f, 30f, 120f);

        using var white = Paint(TextHex);
        using var red   = Paint(RedHex);
        using var muted = Paint(MutedHex);

        canvas.DrawText("RANKED REDSEC", Pad, 78f, f.Title, white);
        canvas.DrawText($"SEASON {card.Season}  ·  QUADS", Pad, 116f, f.Subtitle, red);
        canvas.DrawText("189th squad wins", Pad, 148f, f.Small, muted);

        XpLeaderboardRenderer.DrawRightAligned(canvas, card.TotalWins.ToString("N0"), Width - Pad, 112f, f.Huge, white);
        XpLeaderboardRenderer.DrawRightAligned(canvas, card.TotalWins == 1 ? "WIN" : "WINS", Width - Pad, 146f, f.Subtitle, red);

        using (var rule = new SKPaint { IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke })
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(Pad, 0), new SKPoint(Width - Pad, 0),
                new[] { SKColor.Parse(RedHex), SKColor.Parse(RedHex).WithAlpha(0) },
                null, SKShaderTileMode.Clamp);
            rule.Shader = shader;
            canvas.DrawLine(Pad, HeaderH - 8f, Width - Pad, HeaderH - 8f, rule);
        }

        return HeaderH;
    }

    /// <summary>Faint slanted stripes behind the win counter.</summary>
    private static void DrawChevrons(SKCanvas canvas, float x, float top, float h)
    {
        using var stripe = new SKPaint { IsAntialias = true, Color = SKColor.Parse(RedHex).WithAlpha(22) };
        for (var i = 0; i < 6; i++)
        {
            var x0 = x + i * 34f;
            using var path = new SKPath();
            path.MoveTo(x0, top + h);
            path.LineTo(x0 + 18f, top + h);
            path.LineTo(x0 + 58f, top);
            path.LineTo(x0 + 40f, top);
            path.Close();
            canvas.DrawPath(path, stripe);
        }
    }

    private static float DrawTiles(SKCanvas canvas, Fonts f, RedsecSeasonCard card, float top)
    {
        var tiles = new (string Label, string Value)[]
        {
            ("OPERATORS", card.TotalOperators.ToString("N0")),
            ("FULL 189th SQUADS", card.FullClanSquads.ToString("N0")),
            ("LAST WIN", card.LastWin?.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant() ?? "—"),
        };

        const float gap = 18f;
        var tileW = (Width - Pad * 2 - gap * (tiles.Length - 1)) / tiles.Length;

        using var white = Paint(TextHex);
        using var muted = Paint(MutedHex);
        using var accent = new SKPaint { IsAntialias = true, Color = SKColor.Parse(RedHex) };
        using var fill = Paint(PanelHex);

        for (var i = 0; i < tiles.Length; i++)
        {
            var left = Pad + i * (tileW + gap);
            var rect = new SKRect(left, top + 22f, left + tileW, top + TilesH - 10f);
            canvas.DrawRoundRect(rect, 12f, 12f, fill);
            canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Left + 6f, rect.Bottom), 3f, 3f, accent);

            canvas.DrawText(tiles[i].Label, rect.Left + 24f, rect.Top + 34f, f.Small, muted);
            canvas.DrawText(tiles[i].Value, rect.Left + 24f, rect.Top + 78f, f.TileValue, white);
        }

        return top + TilesH;
    }

    private static float DrawSectionLabel(SKCanvas canvas, Fonts f, string label, string right, float top)
    {
        using var white = Paint(TextHex);
        using var muted = Paint(MutedHex);
        using var red   = Paint(RedHex);

        var baseline = top + 40f;
        canvas.DrawRect(new SKRect(Pad, baseline - 20f, Pad + 5f, baseline + 2f), red);
        canvas.DrawText(label, Pad + 16f, baseline, f.Section, white);
        XpLeaderboardRenderer.DrawRightAligned(canvas, right, Width - Pad, baseline, f.Small, muted);
        return top + SectionH;
    }

    /// <summary>2–1–3 podium. Accent colour follows the shared place, so tied members match.</summary>
    private static void DrawPodium(
        SKCanvas canvas, Fonts f, IReadOnlyList<RedsecOperatorLine> podium,
        IReadOnlyDictionary<ulong, SKImage> avatars, float top)
    {
        if (podium.Count == 0)
        {
            using var muted = Paint(MutedHex);
            XpLeaderboardRenderer.DrawCentered(canvas, "No wins recorded yet", Width / 2f, top + PodiumH / 2f, f.Name, muted);
            return;
        }

        // Index into podium for the left, centre and right slots.
        var slots = new (int Index, float Cx, float W, float R)[]
        {
            (1, Width * 0.20f, Width * 0.27f, 44f),
            (0, Width * 0.50f, Width * 0.30f, 52f),
            (2, Width * 0.80f, Width * 0.27f, 44f),
        };

        foreach (var slot in slots)
        {
            if (slot.Index >= podium.Count) continue;
            var line = podium[slot.Index];
            var accent = SKColor.Parse(PlaceHex(line.Place));
            var rect = new SKRect(slot.Cx - slot.W / 2f, top + 10f, slot.Cx + slot.W / 2f, top + PodiumH - 16f);

            using (var fill = new SKPaint { IsAntialias = true })
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
                    new[] { accent.WithAlpha(50), SKColor.Parse(PanelHex) },
                    null, SKShaderTileMode.Clamp);
                fill.Shader = shader;
                canvas.DrawRoundRect(rect, 18f, 18f, fill);
            }
            using (var edge = new SKPaint
                   {
                       IsAntialias = true, Style = SKPaintStyle.Stroke,
                       StrokeWidth = slot.Index == 0 ? 3f : 2f,
                       Color = accent.WithAlpha(slot.Index == 0 ? (byte)220 : (byte)140),
                   })
            {
                canvas.DrawRoundRect(rect, 18f, 18f, edge);
            }

            XpLeaderboardRenderer.DrawAvatar(canvas, avatars, line.Member.UserId, slot.Cx, rect.Top + slot.R + 14f, slot.R, accent);

            using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };
            using var white = Paint(TextHex);

            var textTop = rect.Bottom - 78f;
            DrawPlaceAndName(canvas, f, line, slot.Cx, textTop, slot.W - 30f, accentPaint, white);
            XpLeaderboardRenderer.DrawCentered(canvas, WinsLabel(line.Wins), slot.Cx, textTop + 60f, f.Value, accentPaint);
        }
    }

    private static void DrawPlaceAndName(
        SKCanvas canvas, Fonts f, RedsecOperatorLine line, float cx, float baseline, float maxW,
        SKPaint accent, SKPaint white)
    {
        var place = $"#{line.Place} ";
        var placeW = f.Name.MeasureText(place);
        var name = XpLeaderboardRenderer.Ellipsize(line.Member.Name, f.Name, maxW - placeW);
        var total = placeW + f.Name.MeasureText(name);
        var x = cx - total / 2f;
        canvas.DrawText(place, x, baseline + 28f, f.Name, accent);
        canvas.DrawText(name, x + placeW, baseline + 28f, f.Name, white);
    }

    private static void DrawOperatorRow(
        SKCanvas canvas, Fonts f, RedsecOperatorLine line, IReadOnlyDictionary<ulong, SKImage> avatars,
        float top, int maxWins, float left, float right, bool alt)
    {
        var rect = new SKRect(left, top + 4f, right, top + RowH - 4f);
        using (var bg = Paint(alt ? PanelAltHex : PanelHex))
            canvas.DrawRoundRect(rect, 10f, 10f, bg);

        var frac = Math.Clamp(line.Wins / (float)maxWins, 0.03f, 1f);
        var barRect = new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * frac, rect.Bottom);
        using (var bar = new SKPaint { IsAntialias = true })
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(barRect.Left, 0), new SKPoint(barRect.Right, 0),
                new[] { SKColor.Parse(RedHex).WithAlpha(80), SKColor.Parse(RedHex).WithAlpha(12) },
                null, SKShaderTileMode.Clamp);
            bar.Shader = shader;
            canvas.Save();
            using var clip = new SKRoundRect(rect, 10f, 10f);
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
            canvas.DrawRect(barRect, bar);
            canvas.Restore();
        }

        using var white = Paint(TextHex);
        using var muted = Paint(MutedHex);

        var midY = rect.MidY;
        var baseline = midY - (f.Row.Metrics.Ascent + f.Row.Metrics.Descent) / 2f;

        XpLeaderboardRenderer.DrawRightAligned(canvas, line.Place.ToString(), rect.Left + 40f, baseline, f.Row, muted);
        XpLeaderboardRenderer.DrawAvatar(canvas, avatars, line.Member.UserId, rect.Left + 66f, midY, 17f,
            SKColor.Parse(MutedHex).WithAlpha(90));

        var winsText = WinsLabel(line.Wins);
        var winsW = f.Row.MeasureText(winsText);
        var nameX = rect.Left + 92f;
        canvas.DrawText(XpLeaderboardRenderer.Ellipsize(line.Member.Name, f.Row, rect.Right - nameX - winsW - 30f),
            nameX, baseline, f.Row, white);
        XpLeaderboardRenderer.DrawRightAligned(canvas, winsText, rect.Right - 16f, baseline, f.Row, white);
    }

    private static void DrawWinRow(
        SKCanvas canvas, Fonts f, RedsecWinLine win, IReadOnlyDictionary<ulong, SKImage> avatars, float top)
    {
        var rect = new SKRect(Pad, top + 5f, Width - Pad, top + WinRowH - 5f);
        using (var bg = Paint(PanelHex))
            canvas.DrawRoundRect(rect, 12f, 12f, bg);

        using var white = Paint(TextHex);
        using var muted = Paint(MutedHex);
        using var red   = Paint(RedHex);

        // Date block
        var dateRect = new SKRect(rect.Left + 10f, rect.Top + 9f, rect.Left + 78f, rect.Bottom - 9f);
        using (var dateBg = new SKPaint { IsAntialias = true, Color = SKColor.Parse(RedHex).WithAlpha(34) })
            canvas.DrawRoundRect(dateRect, 9f, 9f, dateBg);
        XpLeaderboardRenderer.DrawCentered(canvas,
            win.WonOn.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
            dateRect.MidX, dateRect.Top + 22f, f.Tiny, red);
        XpLeaderboardRenderer.DrawCentered(canvas, win.WonOn.Day.ToString(), dateRect.MidX, dateRect.Bottom - 10f, f.DateDay, white);

        // Squad: clan members first, then a "?" for each non-clan player.
        const float r = 19f, step = 32f;
        var cx = dateRect.Right + 34f;
        var cy = rect.MidY;
        foreach (var p in win.Players)
        {
            XpLeaderboardRenderer.DrawAvatar(canvas, avatars, p.UserId, cx, cy, r, SKColor.Parse(PanelHex));
            cx += step;
        }
        for (var i = win.Players.Count; i < SquadSize; i++)
        {
            DrawUnknownSlot(canvas, f, cx, cy, r);
            cx += step;
        }

        var textX = dateRect.Right + 34f + step * (SquadSize - 1) + r + 18f;
        var maxW = rect.Right - textX - 18f;
        var names = string.Join("  ·  ", win.Players.Select(p => p.Name));
        canvas.DrawText(XpLeaderboardRenderer.Ellipsize(names, f.Row, maxW), textX, rect.Top + 32f, f.Row, white);

        var randoms = SquadSize - win.Players.Count;
        var detail = $"#{win.Id}";
        if (randoms > 0) detail += $"  ·  +{randoms} non-clan";
        if (!string.IsNullOrWhiteSpace(win.Notes)) detail += $"  ·  {win.Notes}";
        canvas.DrawText(XpLeaderboardRenderer.Ellipsize(detail, f.Small, maxW), textX, rect.Bottom - 16f, f.Small, muted);
    }

    private static void DrawUnknownSlot(SKCanvas canvas, Fonts f, float cx, float cy, float r)
    {
        using var disc = new SKPaint { IsAntialias = true, Color = SKColor.Parse(PanelAltHex) };
        canvas.DrawCircle(cx, cy, r, disc);
        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f,
            Color = SKColor.Parse(MutedHex).WithAlpha(110),
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0),
        };
        canvas.DrawCircle(cx, cy, r - 1f, ring);
        using var muted = Paint(MutedHex);
        var baseline = cy - (f.Small.Metrics.Ascent + f.Small.Metrics.Descent) / 2f;
        XpLeaderboardRenderer.DrawCentered(canvas, "?", cx, baseline, f.Small, muted);
    }

    private static void DrawFooter(SKCanvas canvas, Fonts f, int canvasHeight)
    {
        using var muted = Paint(MutedHex);
        canvas.DrawText("Ranked Battle Royale · Quads", Pad, canvasHeight - 26f, f.Small, muted);
        XpLeaderboardRenderer.DrawRightAligned(canvas, "189th CLANGUARD", Width - Pad, canvasHeight - 26f, f.Small, muted);
    }

    private static string WinsLabel(int wins) => wins == 1 ? "1 WIN" : $"{wins:N0} WINS";

    private static string PlaceHex(int place) => place switch
    {
        1 => GoldHex,
        2 => SilverHex,
        3 => BronzeHex,
        _ => MutedHex,
    };

    private static SKPaint Paint(string hex) => new() { Color = SKColor.Parse(hex), IsAntialias = true };

    private sealed class Fonts : IDisposable
    {
        public SKFont Huge { get; }
        public SKFont Title { get; }
        public SKFont Subtitle { get; }
        public SKFont Section { get; }
        public SKFont TileValue { get; }
        public SKFont Name { get; }
        public SKFont Value { get; }
        public SKFont Row { get; }
        public SKFont DateDay { get; }
        public SKFont Small { get; }
        public SKFont Tiny { get; }

        public Fonts(SKTypeface bold, SKTypeface body)
        {
            Huge      = new SKFont(bold, 80f);
            Title     = new SKFont(bold, 50f);
            Subtitle  = new SKFont(bold, 24f);
            Section   = new SKFont(bold, 24f);
            TileValue = new SKFont(bold, 38f);
            Name      = new SKFont(bold, 23f);
            Value     = new SKFont(bold, 26f);
            Row       = new SKFont(bold, 22f);
            DateDay   = new SKFont(bold, 28f);
            Small     = new SKFont(body, 19f);
            Tiny      = new SKFont(bold, 16f);
        }

        public void Dispose()
        {
            foreach (var font in new[] { Huge, Title, Subtitle, Section, TileValue, Name, Value, Row, DateDay, Small, Tiny })
                font.Dispose();
        }
    }
}
