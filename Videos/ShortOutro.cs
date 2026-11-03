namespace ClanGuardBot.Services;

/// <summary>
/// Where the 1080x1920 outro (<c>Assets/189th_outro.webm</c>, made by
/// <c>Tools/outro/make_outro.py</c>) is cropped to fit a Short's frame.
///
/// ── Keep band ──
/// The logo sits dead centre with the Discord-link pill under it. A frame that
/// isn't 9:16 (a square or 4:5 clip kept as is) only shows part of the outro's
/// height, and a centred crop of a square frame keeps rows 420–1500, which cuts
/// the link in half. So the crop is centred when the band from the top of the logo
/// to the bottom of the pill fits, and otherwise moved up just enough that it does:
/// the logo then sits a little above the middle. The band must match
/// <c>KEEP_BAND_TOP</c> / <c>KEEP_BAND_BOTTOM</c> in make_outro.py.
/// </summary>
internal static class ShortOutro
{
    /// <summary>Top of the logo, in outro pixels.</summary>
    public const int KeepBandTop = 600;

    /// <summary>Bottom of the pill plus a margin, in outro pixels.</summary>
    public const int KeepBandBottom = 1600;

    /// <summary>
    /// Top-left of a <paramref name="frameWidth"/>x<paramref name="frameHeight"/>
    /// crop of the outro scaled to 1080x1920: centred, or lower down when that's
    /// what brings the band's bottom into view. Every frame
    /// <see cref="ShortRenderer.BuildFilter"/> makes fits inside 1080x1920, so that
    /// is always the size that covers it, and the crop never leaves the outro.
    /// </summary>
    public static (int X, int Y) CropOrigin(int frameWidth, int frameHeight) =>
        ((ShortRenderer.OutputWidth - frameWidth) / 2,
         Math.Max((ShortRenderer.OutputHeight - frameHeight) / 2, KeepBandBottom - frameHeight));
}
