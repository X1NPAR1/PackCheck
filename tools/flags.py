"""Draws round flag icons (PNG) for the language picker. Original drawings, no external assets."""
import math, sys
from pathlib import Path
from PIL import Image, ImageDraw

S = 256  # drawn large, downsampled for smooth edges
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)

def stripes(colors, vertical=False):
    im = Image.new("RGB", (S, S)); d = ImageDraw.Draw(im); n = len(colors)
    for i, c in enumerate(colors):
        box = (i * S / n, 0, (i + 1) * S / n, S) if vertical else (0, i * S / n, S, (i + 1) * S / n)
        d.rectangle(box, fill=c)
    return im

def gb():
    im = Image.new("RGB", (S, S), "#012169"); d = ImageDraw.Draw(im)
    d.line([(0, 0), (S, S)], fill="white", width=52); d.line([(0, S), (S, 0)], fill="white", width=52)
    d.line([(0, 0), (S, S)], fill="#C8102E", width=18); d.line([(0, S), (S, 0)], fill="#C8102E", width=18)
    d.rectangle((S/2 - 40, 0, S/2 + 40, S), fill="white"); d.rectangle((0, S/2 - 40, S, S/2 + 40), fill="white")
    d.rectangle((S/2 - 24, 0, S/2 + 24, S), fill="#C8102E"); d.rectangle((0, S/2 - 24, S, S/2 + 24), fill="#C8102E")
    return im

def tr():
    im = Image.new("RGB", (S, S), "#E30A17"); d = ImageDraw.Draw(im)
    cx, cy, r = S * 0.42, S / 2, S * 0.25
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill="white")
    r2 = r * 0.8; cx2 = cx + r * 0.25
    d.ellipse((cx2 - r2, cy - r2, cx2 + r2, cy + r2), fill="#E30A17")
    sx, sr = S * 0.70, S * 0.105
    pts = []
    for k in range(10):
        a = math.pi / 2 + k * math.pi / 5 + math.pi  # point left toward the crescent
        rad = sr if k % 2 == 0 else sr * 0.4
        pts.append((sx + rad * math.cos(a + math.pi / 2), cy + rad * math.sin(a + math.pi / 2)))
    d.polygon(pts, fill="white")
    return im

flags = {
    "en": gb(),
    "tr": tr(),
    "de": stripes(["#000000", "#DD0000", "#FFCE00"]),
    "fr": stripes(["#0055A4", "#FFFFFF", "#EF4135"], vertical=True),
    "ru": stripes(["#FFFFFF", "#0039A6", "#D52B1E"]),
}
mask = Image.new("L", (S, S), 0); ImageDraw.Draw(mask).ellipse((2, 2, S - 3, S - 3), fill=255)
for code, im in flags.items():
    rgba = im.convert("RGBA"); rgba.putalpha(mask)
    ring = Image.new("RGBA", (S, S)); ImageDraw.Draw(ring).ellipse((2, 2, S - 3, S - 3), outline=(0, 0, 0, 60), width=6)
    rgba.alpha_composite(ring)
    rgba.resize((48, 48), Image.LANCZOS).save(out / f"flag-{code}.png", optimize=True)
print("ok")
