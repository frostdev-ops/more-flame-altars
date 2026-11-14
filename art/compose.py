#!/usr/bin/env python3
"""Rebuild all More Flame Altars finals using Pillow and generated art only.

Run: python art/compose.py (writes the finals next to it, from the artwork in art/source/).
The sources are read from source/; the finals are written to this script's directory. The game's
reference images are never opened, pasted, or otherwise used by this compositor.
Source art was made with Codex built-in image_gen; prompts are recorded alongside.
"""
from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps, PngImagePlugin

OUT = Path(__file__).resolve().parent
SRC = OUT / "source"
TITLE = "More Flame Altars"
MAIN_LINE = "16 Flame Altars from the first Flame level  ·  a Kiln mod"
SOCIAL_LINE = "for Enshrouded · Kiln"
DISCORD_LINE = "More bases, from day one"
WIDE = "hero_wide_v2.png"
ALTAR = "hero_altar_v2.png"
ICON = "icon_altar.png"
BOLD = "/usr/share/fonts/noto/NotoSerifDisplay-Bold.ttf"
REGULAR = "/usr/share/fonts/noto/NotoSerif-Regular.ttf"
TITLE_COLOR = (255, 242, 215, 255)
SECONDARY_COLOR = (236, 226, 208, 255)
SIZES = {
    "nexus_main_1280x720.png": (1280, 720),
    "thumb_1920x1080.png": (1920, 1080),
    "nexus_banner_1920x480.png": (1920, 480),
    "github_social_1280x640.png": (1280, 640),
    "discord_1200x630.png": (1200, 630),
    "thumb_512.png": (512, 512),
    "icon_256.png": (256, 256),
    "contact_sheet.png": (24 + 7 * 416, 368),
}
SOURCES = {
    "nexus_main_1280x720.png": WIDE,
    "thumb_1920x1080.png": WIDE,
    "nexus_banner_1920x480.png": WIDE,
    "github_social_1280x640.png": WIDE,
    "discord_1200x630.png": ALTAR,
    "thumb_512.png": ICON,
    "icon_256.png": ICON,
    "contact_sheet.png": "All seven final images",
}


def art(name: str) -> Image.Image:
    with Image.open((SRC if (SRC / name).exists() else OUT) / name) as im:
        return im.convert("RGBA")


def cover(name: str, size: tuple[int, int], center=(0.5, 0.5)) -> Image.Image:
    return ImageOps.fit(art(name), size, Image.Resampling.LANCZOS, centering=center)


def gradient_scrim(image: Image.Image, stops: list[tuple[float, int]],
                   vertical=False, color=(5, 20, 26)) -> Image.Image:
    """A smooth opacity ramp preserves the illustration under readable type."""
    length = image.height if vertical else image.width
    ramp = []
    for i in range(length):
        t = i / max(1, length - 1)
        for (a, va), (b, vb) in zip(stops, stops[1:]):
            if a <= t <= b:
                opacity = round(va + (vb - va) * (t - a) / (b - a))
                break
        else:
            opacity = stops[0][1] if t < stops[0][0] else stops[-1][1]
        ramp.append(opacity)
    strip = Image.new("L", (1, length) if vertical else (length, 1))
    strip.putdata(ramp)
    mask = strip.resize(image.size)
    tint = Image.new("RGBA", image.size, (*color, 0))
    tint.putalpha(mask)
    return Image.alpha_composite(image, tint)


def font_to_fit(text: str, font_path: str, maximum: int, width: int):
    for size in range(maximum, 11, -1):
        font = ImageFont.truetype(font_path, size)
        if font.getlength(text) <= width:
            return font
    raise ValueError(f"Text does not fit: {text!r}")


def lettering(image: Image.Image, text: str, xy: tuple[int, int],
              font: ImageFont.FreeTypeFont, color=TITLE_COLOR,
              line_spacing=0, shadow_offset=(0, 3)):
    """Draw exact copy at its visible top-left, with a soft dark shadow."""
    x, y = xy
    lines = text.split("\n")
    advance = font.size + line_spacing
    mask = Image.new("L", image.size)
    md = ImageDraw.Draw(mask)
    for index, line in enumerate(lines):
        box = font.getbbox(line)
        md.text((x - box[0], y + index * advance - box[1]), line,
                font=font, fill=255)
    shadow_mask = Image.new("L", image.size)
    shadow_mask.paste(mask, shadow_offset)
    shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(3.4))
    shadow = Image.new("RGBA", image.size, (0, 5, 10, 0))
    shadow.putalpha(shadow_mask.point(lambda value: round(value * 0.78)))
    image.alpha_composite(shadow)
    ink = Image.new("RGBA", image.size, color)
    ink.putalpha(mask)
    image.alpha_composite(ink)
    return mask.getbbox()


def save(image: Image.Image, name: str, text: list[str]):
    assert image.size == SIZES[name], (name, image.size)
    meta = PngImagePlugin.PngInfo()
    meta.add_text("Title", TITLE)
    meta.add_text("Artwork", SOURCES[name])
    meta.add_text("Visible copy", json.dumps(text, ensure_ascii=False))
    meta.add_text("Creation", "Built-in image_gen artwork; Pillow composition")
    meta.add_text("Reference use", "Look only; no reference images composited")
    image.convert("RGB").save(OUT / name, pnginfo=meta, optimize=True)


def make_main(name="nexus_main_1280x720.png"):
    # The 16:9 layout, laid out on 1280x720 and scaled by k for larger sizes.
    size = SIZES[name]
    k = size[0] / 1280
    px = lambda v: round(v * k)
    image = cover(WIDE, size, center=(0.15, 0.5))
    image = gradient_scrim(image, [(0, 125), (.35, 100), (.63, 10), (1, 0)])
    image = gradient_scrim(image, [(0, 0), (.70, 0), (1, 195)], vertical=True)
    title_font = font_to_fit("More Flame", BOLD, px(104), px(620))
    lettering(image, "More Flame\nAltars", (px(64), px(214)), title_font,
              line_spacing=px(7), shadow_offset=(0, px(3)))
    secondary = font_to_fit(MAIN_LINE, REGULAR, px(27), px(1148))
    lettering(image, MAIN_LINE, (px(66), px(640)), secondary,
              SECONDARY_COLOR, shadow_offset=(0, px(2)))
    save(image, name, [TITLE, MAIN_LINE])


def make_banner():
    image = cover(WIDE, (1920, 480), center=(0.5, 0.64))
    image = gradient_scrim(image, [(0, 128), (.22, 96), (.38, 15), (.6, 0), (1, 0)])
    font = ImageFont.truetype(BOLD, 102)
    lettering(image, "More\nFlame\nAltars", (72, 67), font, line_spacing=4)
    save(image, "nexus_banner_1920x480.png", [TITLE])


def make_social():
    image = cover(WIDE, (1280, 640), center=(0.12, 0.5))
    image = gradient_scrim(image, [(0, 130), (.33, 100), (.6, 0), (1, 0)])
    image = gradient_scrim(image, [(0, 0), (.8, 0), (1, 70)], vertical=True)
    title_font = font_to_fit("More Flame", BOLD, 100, 610)
    boxes = [
        lettering(image, "More Flame\nAltars", (112, 185), title_font, line_spacing=7),
        lettering(image, SOCIAL_LINE, (115, 444), ImageFont.truetype(REGULAR, 30),
                  SECONDARY_COLOR, shadow_offset=(0, 2)),
    ]
    # GitHub crops social cards: keep every letter 40pt (80 px here) inside the edges.
    for left, top, right, bottom in boxes:
        assert left >= 80 and top >= 80 and right <= 1200 and bottom <= 560, (left, top, right, bottom)
    save(image, "github_social_1280x640.png", [TITLE, SOCIAL_LINE])
    # GitHub's social preview takes at most 1 MB; the PNG is larger.
    jpg = OUT / "github_social_1280x640.jpg"
    image.convert("RGB").save(jpg, quality=92, optimize=True, progressive=True, subsampling=0)
    assert jpg.stat().st_size < 1_000_000, (jpg.name, jpg.stat().st_size)


def make_discord():
    # Preserve the entire square painting at right. Its own softly blurred,
    # darkened crop provides a seamless background for copy at left.
    size = (1200, 630)
    image = cover(ALTAR, size, center=(0, .35)).filter(ImageFilter.GaussianBlur(22))
    image = gradient_scrim(image, [(0, 215), (.4, 195), (.66, 30), (1, 0)])
    subject = art(ALTAR).resize((630, 630), Image.Resampling.LANCZOS)
    feather = Image.new("L", (630, 1))
    feather.putdata([round(255 * min(1, x / 110)) for x in range(630)])
    image.paste(subject, (570, 0), feather.resize(subject.size))
    title_font = font_to_fit("More Flame", BOLD, 82, 530)
    lettering(image, "More Flame\nAltars", (58, 190), title_font, line_spacing=7)
    secondary = font_to_fit(DISCORD_LINE, REGULAR, 29, 520)
    lettering(image, DISCORD_LINE, (61, 411), secondary,
              SECONDARY_COLOR, shadow_offset=(0, 2))
    save(image, "discord_1200x630.png", [TITLE, DISCORD_LINE])


def make_icons():
    for name in ("thumb_512.png", "icon_256.png"):
        image = art(ICON).resize(SIZES[name], Image.Resampling.LANCZOS)
        save(image, name, [])


def make_contact_sheet():
    image = Image.new("RGBA", SIZES["contact_sheet.png"], (10, 25, 33, 255))
    draw = ImageDraw.Draw(image)
    draw.text((24, 14), TITLE, font=ImageFont.truetype(BOLD, 29),
              fill=TITLE_COLOR)
    label = ImageFont.truetype(REGULAR, 17)
    for index, name in enumerate(n for n in SIZES if n != "contact_sheet.png"):
        x = 24 + index * 416
        tile = art(name)
        tile.thumbnail((392, 222), Image.Resampling.LANCZOS)
        px = x + (392 - tile.width) // 2
        py = 78 + (222 - tile.height) // 2
        image.alpha_composite(tile, (px, py))
        draw.text((x, 323), name, font=label, fill=SECONDARY_COLOR)
    save(image, "contact_sheet.png", [TITLE, "Previews of all seven finals"])


def verify():
    expected_copy = {
        "nexus_main_1280x720.png": [TITLE, MAIN_LINE],
        "thumb_1920x1080.png": [TITLE, MAIN_LINE],
        "nexus_banner_1920x480.png": [TITLE],
        "github_social_1280x640.png": [TITLE, SOCIAL_LINE],
        "discord_1200x630.png": [TITLE, DISCORD_LINE],
        "thumb_512.png": [],
        "icon_256.png": [],
    }
    for name, size in SIZES.items():
        with Image.open(OUT / name) as image:
            image.load()
            assert image.size == size, (name, image.size, size)
            assert image.mode == "RGB"
            assert image.info["Artwork"] == SOURCES[name]
            if name in expected_copy:
                assert json.loads(image.info["Visible copy"]) == expected_copy[name]
        print(f"{name}: {size[0]}x{size[1]} | {SOURCES[name]}")


def main():
    make_main()
    make_main("thumb_1920x1080.png")
    make_banner()
    make_social()
    make_discord()
    make_icons()
    make_contact_sheet()
    verify()


if __name__ == "__main__":
    main()
