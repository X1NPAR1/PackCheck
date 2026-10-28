"""Turns a folder of PNG frames into an optimized GIF: python tools/make_gif.py frames out.gif [width] [ms]"""
import sys
from pathlib import Path
from PIL import Image

frames_dir, out = Path(sys.argv[1]), Path(sys.argv[2])
width = int(sys.argv[3]) if len(sys.argv) > 3 else 900
duration = int(sys.argv[4]) if len(sys.argv) > 4 else 70

frames = []
previous = None
for f in sorted(frames_dir.glob("*.png")):
    im = Image.open(f).convert("RGB")
    if im.getbbox() is None:  # fully black frame (window not painted yet)
        continue
    im = im.resize((width, int(im.height * width / im.width)), Image.LANCZOS)
    if previous is not None and im.tobytes()[::4093] == previous.tobytes()[::4093]:
        continue  # drop duplicates
    frames.append(im)
    previous = im

# One shared adaptive palette keeps colors stable between frames and the file small.
palette = frames[len(frames) // 2].quantize(colors=128, method=Image.Quantize.MEDIANCUT)
quantized = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
quantized[0].save(out, save_all=True, append_images=quantized[1:], duration=duration, loop=0, optimize=True, disposal=1)
print(f"{out}: {len(quantized)} frames, {out.stat().st_size // 1024} KB")
