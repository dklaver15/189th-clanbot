# Shorts outro

`make_outro.py` renders `Assets/189th_outro.webm`, the 2.5 s outro the bot lays over
the end of every Short (`ShortRenderer`): the 189th logo easing in at the centre,
then a dark pill with "SQUAD UP WITH US" over `discord.gg/189th`. It is a 1080x1920
VP9 WebM with real transparency, made from `Assets/189th_logo.png` itself, so the
lettering is always exact.

## Setup (once)

Needs Python 3 and ffmpeg (with libvpx) on PATH.

```sh
python -m venv Tools/outro/.venv
Tools/outro/.venv/Scripts/python -m pip install -r Tools/outro/requirements.txt
```

## Use

```sh
Tools/outro/.venv/Scripts/python Tools/outro/make_outro.py --list
Tools/outro/.venv/Scripts/python Tools/outro/make_outro.py --all --background some-frame.png
```

Files land in `Tools/outro/out/` (git-ignored): `NAME.webm` per variant and, with
`--background`, `NAME-preview.mp4` showing it over that image. To ship one, copy
it to `Assets/189th_outro.webm` and commit.

The layout (logo size and position, pill position) is fixed: `KEEP_BAND_TOP` /
`KEEP_BAND_BOTTOM` must match `ShortOutro` in the bot, which uses them to keep the
pill in view on square Shorts. Variants only change the animation.

Tests: `Tools/outro/.venv/Scripts/python -m unittest discover -s Tools/outro -v`

The font is Oswald (SIL Open Font License, `fonts/OFL.txt`).
