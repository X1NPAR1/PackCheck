"""Derive transparent brand assets (wordmark, cube icon, .ico) from logo.png."""
import sys
from pathlib import Path
from PIL import Image
import numpy as np

src = Path(sys.argv[1]); out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
img = np.asarray(Image.open(src).convert("RGB")).astype(np.float32)
bg = np.median(img[:40, :40].reshape(-1, 3), axis=0)

# alpha = how far a pixel is from the background colour, then un-blend the colour
dist = np.abs(img - bg).max(axis=2)
alpha = np.clip((dist - 6) / 40.0, 0, 1)
a3 = np.maximum(alpha[..., None], 1e-3)
rgb = np.clip((img - bg * (1 - a3)) / a3, 0, 255)
# keep dark details enclosed by the cube (check mark, edge lines) opaque:
# background is only what is connected to the image border
from PIL import ImageDraw, ImageFilter
m = np.pad(np.where(dist < 46, 255, 0).astype(np.uint8), 1, constant_values=255)
# close the thin gaps between cube faces before flooding, then grow back
m = Image.fromarray(m, "L").filter(ImageFilter.MinFilter(15)).copy()
ImageDraw.floodfill(m, (0, 0), 128)
m = Image.fromarray(np.where(np.asarray(m) == 128, 255, 0).astype(np.uint8), "L").filter(ImageFilter.MaxFilter(15))
outside = (np.asarray(m) == 255)[1:-1, 1:-1]
cube_cols = np.zeros_like(outside); cube_cols[:, : int(img.shape[1] * 0.345)] = True
enclosed = (~outside) & cube_cols
alpha[enclosed] = 1.0
a3 = np.maximum(alpha[..., None], 1e-3)
rgb = np.where(enclosed[..., None], img, np.clip((img - bg * (1 - a3)) / a3, 0, 255))
rgba = np.dstack([rgb, alpha * 255]).astype(np.uint8)
full = Image.fromarray(rgba, "RGBA")
full = full.crop(full.getbbox())
full.save(out / "logo-wordmark.png", optimize=True)

# dark text variant for light theme: recolour near-white pixels to graphite
arr = np.asarray(full).copy()
light = (arr[..., :3].min(axis=2) > 170) & (arr[..., 3] > 0)
arr[light, :3] = (32, 35, 33)
Image.fromarray(arr, "RGBA").save(out / "logo-wordmark-dark.png", optimize=True)

# cube only = left part of the wordmark (everything left of the first wide gap)
cols = np.asarray(full)[..., 3].max(axis=0) > 20
gap_start = None
for x in range(len(cols)):
    if not cols[x]:
        if gap_start is None: gap_start = x
        if x - gap_start > 15: break
    else:
        gap_start = None
cube = full.crop((0, 0, gap_start, full.height))
cube = cube.crop(cube.getbbox())
s = max(cube.size); sq = Image.new("RGBA", (s, s)); sq.paste(cube, ((s - cube.width)//2, (s - cube.height)//2))
sq.resize((512, 512), Image.LANCZOS).save(out / "cube-512.png", optimize=True)
sq.resize((256, 256), Image.LANCZOS).save(out / "cube-256.png", optimize=True)
sq.save(out / "app.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print("bg", bg, "wordmark", full.size, "cube", cube.size)
