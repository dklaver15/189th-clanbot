"""Renders the Shorts outro: the 189th logo easing in at the centre of a
transparent 1080x1920 frame, then a dark pill with "JOIN US ON DISCORD!" over the
Discord link popping in below it. Saved as a VP9 WebM with alpha, which the bot
(ShortRenderer) lays over the last seconds of every Short.

The logo's pixels always come straight from Assets/189th_logo.png: the motion is
only scaling and fading, and the optional glint brightens its colours without
touching its shape, so the lettering can never warp.

    python make_outro.py --all --background some-frame.png
    python make_outro.py --variant fade-rise-glint

Each variant gives out/NAME.webm and, with --background, out/NAME-preview.mp4
(the outro over that image, for players that show transparent WebM badly).
"""

from __future__ import annotations

import argparse
import math
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
LOGO_PATH = HERE.parent.parent / "Assets" / "189th_logo.png"
FONT_PATH = HERE / "fonts" / "Oswald[wght].ttf"

W, H = 1080, 1920
FPS = 30
DURATION = 3.0

LOGO_WIDTH = 720
LOGO_CENTER = (540, 960)
PILL_TOP = 1363

# Top of the logo to the bottom of the pill plus a margin. ShortOutro.KeepBandTop/
# KeepBandBottom in the bot must match: square Shorts crop the outro so this band
# stays in view.
KEEP_BAND_TOP = 600
KEEP_BAND_BOTTOM = 1600

CTA = "JOIN US ON DISCORD!"
LINK = "discord.gg/189th"

PILL_FILL = (10, 8, 4, 184)        # near-black at 72 %
PILL_RIM = (217, 169, 58, 255)     # #d9a93a
CTA_COLOR = (232, 191, 85, 255)    # #e8bf55
LINK_COLOR = (255, 255, 255, 255)


# ─── Easing ──────────────────────────────────────────────────────────────

def ease(name: str, t: float) -> float:
    """An easing curve on 0..1; t is clamped, so every curve starts at 0 and ends at 1."""
    t = min(max(t, 0.0), 1.0)
    if name == "out-cubic":
        return 1 - (1 - t) ** 3
    if name == "out-quint":
        return 1 - (1 - t) ** 5
    if name == "in-out-sine":
        return -(math.cos(math.pi * t) - 1) / 2
    if name == "out-back":                        # overshoots a little, then settles
        c1 = 1.2
        return 1 + (c1 + 1) * (t - 1) ** 3 + c1 * (t - 1) ** 2
    raise ValueError(f"unknown easing: {name}")


# ─── Variants ────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Glint:
    """A soft band of light that sweeps once across the gold."""
    angle: float = 30.0      # degrees; the direction the band travels
    width: float = 0.10      # band softness, as a fraction of the logo
    start: float = 0.9       # seconds
    length: float = 0.7      # seconds to cross the logo
    strength: float = 0.55   # how far the brightest metal is pushed toward white


@dataclass(frozen=True)
class Variant:
    description: str
    easing: str = "out-cubic"
    logo_in: float = 0.8           # seconds for the logo to fade and scale in
    scale_from: float = 0.92       # logo size at the start, relative to its final size
    glint: Glint | None = None
    pill_entrance: str = "pop"     # "pop" | "fade" | "slide"
    pill_delay: float = 0.5        # seconds after the logo settles
    pill_in: float = 0.25          # seconds for the pill's entrance


VARIANTS: dict[str, Variant] = {
    "steady": Variant(
        "Logo fades in at full size; the link fades in after it. The calmest.",
        scale_from=1.0, pill_entrance="fade"),
    "fade-rise": Variant(
        "Logo fades in while growing slightly; the link fades in.",
        pill_entrance="fade"),
    "fade-rise-glint": Variant(
        "Fade and slight grow, a soft glint sweeps the gold, the link pops in as the logo settles.",
        glint=Glint(), pill_delay=0.0),
    "soft-settle": Variant(
        "Slower settle from a little smaller, a faint glint, the link slides up.",
        easing="out-quint", scale_from=0.88, pill_entrance="slide",
        glint=Glint(angle=35, width=0.08, start=1.0, length=0.9, strength=0.35)),
    "slow-fade": Variant(
        "A gentle, even fade with almost no growth; the link fades in.",
        easing="in-out-sine", logo_in=1.0, scale_from=0.96, pill_entrance="fade", pill_delay=0.4),
    "quick-glint": Variant(
        "A quicker entrance, a brighter glint straight after, the link pops in.",
        easing="out-quint", logo_in=0.6, scale_from=0.94,
        glint=Glint(angle=20, width=0.12, start=0.7, length=0.5, strength=0.6)),
    "late-glint": Variant(
        "Fade and slight grow, the link slides up, then a glint once it's all in place.",
        pill_entrance="slide", glint=Glint(start=1.6, length=0.7, strength=0.5)),
}

# The variant committed as Assets/189th_outro.webm.
SHIPPED_VARIANT = "fade-rise-glint"


# ─── Layers ──────────────────────────────────────────────────────────────

def load_logo() -> Image.Image:
    """The logo PNG at its final on-screen size."""
    return Image.open(LOGO_PATH).convert("RGBA").resize((LOGO_WIDTH, LOGO_WIDTH), Image.LANCZOS)


def _font(size: int) -> ImageFont.FreeTypeFont:
    font = ImageFont.truetype(str(FONT_PATH), size)
    font.set_variation_by_axes([600])
    return font


def _tracked_width(font: ImageFont.FreeTypeFont, text: str, tracking: int) -> float:
    return sum(font.getlength(c) for c in text) + tracking * (len(text) - 1)


def make_pill() -> Image.Image:
    """The pill: the call to action in letter-spaced gold over the link in white."""
    cta_font, link_font = _font(43), _font(77)
    tracking, pad_x, pad_y, gap, rim = 12, 67, 19, 10, 7

    cta_w = _tracked_width(cta_font, CTA, tracking)
    cta_box = cta_font.getbbox(CTA)
    link_box = link_font.getbbox(LINK)
    link_w = link_box[2] - link_box[0]
    cta_h = cta_box[3] - cta_box[1]
    link_h = link_box[3] - link_box[1]

    width = math.ceil(max(cta_w, link_w)) + 2 * pad_x
    height = pad_y + cta_h + gap + link_h + pad_y + 2 * rim
    pill = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(pill).rounded_rectangle(
        (0, 0, width - 1, height - 1), radius=height // 2, fill=PILL_FILL, outline=PILL_RIM, width=rim)

    text = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(text)
    x = (width - cta_w) / 2
    y = rim + pad_y - cta_box[1]
    for c in CTA:
        draw.text((x, y), c, font=cta_font, fill=CTA_COLOR)
        x += cta_font.getlength(c) + tracking
    draw.text(((width - link_w) / 2 - link_box[0], rim + pad_y + cta_h + gap - link_box[1]),
              LINK, font=link_font, fill=LINK_COLOR)
    return Image.alpha_composite(pill, text)


def _with_glint(logo: Image.Image, glint: Glint, t: float) -> Image.Image:
    """The logo with the glint band at time t: colours brightened, alpha untouched."""
    local = (t - glint.start) / glint.length
    if local <= 0 or local >= 1:
        return logo
    centre = -0.25 + 1.5 * ease("in-out-sine", local)       # off one edge to off the other

    arr = np.asarray(logo).astype(np.float32)
    h, w = arr.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    a = math.radians(glint.angle)
    along = xx * math.cos(a) + yy * math.sin(a)
    along = (along - along.min()) / (along.max() - along.min())
    band = np.exp(-(((along - centre) / glint.width) ** 2)) * glint.strength

    rgb = arr[..., :3]
    luma = (rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)) / 255
    weight = band * luma ** 1.5                              # bright raised metal catches the light
    arr[..., :3] = np.clip(rgb + (255 - rgb) * weight[..., None], 0, 255)
    return Image.fromarray(arr.astype(np.uint8), "RGBA")


def _with_opacity(img: Image.Image, opacity: float) -> Image.Image:
    if opacity >= 1:
        return img
    out = img.copy()
    out.putalpha(img.getchannel("A").point(lambda v: round(v * opacity)))
    return out


def _place(canvas: Image.Image, layer: Image.Image, centre: tuple[float, float]) -> None:
    canvas.alpha_composite(layer, (round(centre[0] - layer.width / 2), round(centre[1] - layer.height / 2)))


def render_frame(variant: Variant, t: float, logo: Image.Image, pill: Image.Image) -> Image.Image:
    """One transparent W x H frame of the outro at t seconds."""
    frame = Image.new("RGBA", (W, H), (0, 0, 0, 0))

    p = ease(variant.easing, t / variant.logo_in)
    if p > 0:
        size = max(1, round(LOGO_WIDTH * (variant.scale_from + (1 - variant.scale_from) * p)))
        layer = logo if size == LOGO_WIDTH else logo.resize((size, size), Image.LANCZOS)
        if variant.glint:
            layer = _with_glint(layer, variant.glint, t)
        _place(frame, _with_opacity(layer, p), LOGO_CENTER)

    q = (t - variant.logo_in - variant.pill_delay) / variant.pill_in
    if q > 0:
        centre_x, centre_y = W / 2, PILL_TOP + pill.height / 2
        layer = pill
        if variant.pill_entrance == "pop":
            s = 0.85 + 0.15 * ease("out-back", q)
            if abs(s - 1) > 1e-3:
                layer = pill.resize((max(1, round(pill.width * s)), max(1, round(pill.height * s))), Image.LANCZOS)
        elif variant.pill_entrance == "slide":
            centre_y += 40 * (1 - ease("out-cubic", q))
        _place(frame, _with_opacity(layer, ease("out-cubic", q)), (centre_x, centre_y))
    return frame


# ─── Output ──────────────────────────────────────────────────────────────

def _ffmpeg(*args: str) -> None:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)


def render_variant(name: str, out: Path, logo: Image.Image, pill: Image.Image, background: Path | None) -> list[Path]:
    variant = VARIANTS[name]
    webm = out / f"{name}.webm"
    with tempfile.TemporaryDirectory() as tmp:
        for i in range(round(DURATION * FPS)):
            render_frame(variant, i / FPS, logo, pill).save(Path(tmp) / f"{i:04d}.png", compress_level=1)
        # crf 20: the lettering must stay crisp; the file is still small (mostly transparent).
        _ffmpeg("-framerate", str(FPS), "-i", str(Path(tmp) / "%04d.png"),
                "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0", "-crf", "20",
                "-auto-alt-ref", "0", "-row-mt", "1", str(webm))
    written = [webm]

    if background:
        # Half a second of background first, then the outro, then its last frame held for a second.
        preview = out / f"{name}-preview.mp4"
        _ffmpeg("-loop", "1", "-i", str(background), "-c:v", "libvpx-vp9", "-i", str(webm),
                "-filter_complex",
                f"[0:v]scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1[bg];"
                "[1:v]setpts=PTS+0.5/TB[o];[bg][o]overlay=eof_action=repeat,format=yuv420p",
                "-t", str(DURATION + 1.5), "-r", str(FPS), "-c:v", "libx264", "-crf", "18", str(preview))
        written.append(preview)
    return written


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Render the 189th Shorts outro.")
    pick = parser.add_mutually_exclusive_group(required=True)
    pick.add_argument("--variant", action="append", choices=sorted(VARIANTS), help="a variant to render (repeatable)")
    pick.add_argument("--all", action="store_true", help="render every variant")
    pick.add_argument("--list", action="store_true", help="list the variants and stop")
    parser.add_argument("--out", type=Path, default=HERE / "out", help="output folder (default: Tools/outro/out)")
    parser.add_argument("--background", type=Path, help="an image to make preview MP4s over")
    args = parser.parse_args(argv)

    if args.list:
        for name, v in VARIANTS.items():
            print(f"{name:16} {v.description}")
        return 0

    args.out.mkdir(parents=True, exist_ok=True)
    logo, pill = load_logo(), make_pill()
    for name in (list(VARIANTS) if args.all else args.variant):
        for path in render_variant(name, args.out, logo, pill, args.background):
            print(path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
