"""Tests for make_outro.py: python -m unittest discover -s Tools/outro -v"""

import json
import shutil
import subprocess
import tempfile
import unittest

import make_outro as m


class OutroTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.logo = m.load_logo()
        cls.pill = m.make_pill()

    def pill_middle(self):
        return (540, m.PILL_TOP + self.pill.height // 2)

    def test_ease_ends_at_0_and_1(self):
        for name in ("out-cubic", "out-quint", "in-out-sine"):
            self.assertEqual(m.ease(name, 0), 0)
            self.assertEqual(m.ease(name, 1), 1)
            self.assertEqual(m.ease(name, -1), 0)
            self.assertEqual(m.ease(name, 2), 1)

    def test_pill_fits_inside_the_keep_band(self):
        self.assertLessEqual(m.PILL_TOP + self.pill.height, m.KEEP_BAND_BOTTOM - 24)
        self.assertLess(self.pill.width, m.W)

    def test_logo_top_is_inside_the_keep_band(self):
        self.assertGreaterEqual(m.LOGO_CENTER[1] - m.LOGO_WIDTH // 2, m.KEEP_BAND_TOP)

    def test_every_variant_starts_empty_and_ends_on_logo_and_pill(self):
        for name, v in m.VARIANTS.items():
            first = m.render_frame(v, 0.0, self.logo, self.pill)
            last = m.render_frame(v, m.DURATION, self.logo, self.pill)
            self.assertLessEqual(first.getchannel("A").getextrema()[1], 8, name)
            self.assertEqual(last.getpixel((40, 40))[3], 0, name)                   # corner transparent
            # The logo ends exactly as opaque as the PNG (whose inside is 252, not 255).
            self.assertEqual(last.getpixel(m.LOGO_CENTER)[3],
                             self.logo.getpixel((m.LOGO_WIDTH // 2, m.LOGO_WIDTH // 2))[3], name)
            self.assertGreater(last.getpixel(self.pill_middle())[3], 150, name)     # pill shown

    def test_pill_is_hidden_until_the_logo_settles(self):
        for name, v in m.VARIANTS.items():
            frame = m.render_frame(v, v.logo_in, self.logo, self.pill)
            self.assertEqual(frame.getpixel(self.pill_middle())[3], 0, name)

    def test_the_shipped_outro_shows_the_link_for_its_last_1_4_seconds(self):
        frame = m.render_frame(m.VARIANTS[m.SHIPPED_VARIANT], m.DURATION - 1.4, self.logo, self.pill)
        self.assertGreater(frame.getpixel(self.pill_middle())[3], 150)

    def test_the_bot_crops_with_the_same_keep_band(self):
        # ShortOutro in the bot keeps this band in view on square Shorts.
        source = (m.HERE.parent.parent / "Videos" / "ShortOutro.cs").read_text(encoding="utf-8")
        self.assertIn(f"KeepBandTop = {m.KEEP_BAND_TOP};", source)
        self.assertIn(f"KeepBandBottom = {m.KEEP_BAND_BOTTOM};", source)

    def test_there_are_six_to_eight_variants(self):
        self.assertTrue(6 <= len(m.VARIANTS) <= 8)

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg isn't on PATH")
    def test_a_variant_renders_to_a_webm_with_alpha(self):
        name = next(iter(m.VARIANTS))
        with tempfile.TemporaryDirectory() as out:
            self.assertEqual(m.main(["--variant", name, "--out", out]), 0)
            probe = subprocess.run(
                ["ffprobe", "-v", "error", "-show_entries", "stream=width,height:stream_tags=alpha_mode:format=duration",
                 "-of", "json", f"{out}/{name}.webm"],
                capture_output=True, text=True, check=True)
            info = json.loads(probe.stdout)
            stream = info["streams"][0]
            self.assertEqual((stream["width"], stream["height"]), (m.W, m.H))
            self.assertEqual(stream["tags"]["alpha_mode"], "1")
            self.assertTrue(2.9 <= float(info["format"]["duration"]) <= 3.1)


if __name__ == "__main__":
    unittest.main()
