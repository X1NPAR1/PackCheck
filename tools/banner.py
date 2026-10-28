"""Builds docs/media/banner.png (README header) from the transparent wordmark."""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent.parent
word = Image.open(root / "src/PackCheck.App/Assets/logo-wordmark.png")
W, H = 1400, 420
banner = Image.new("RGBA", (W, H), (26, 26, 26, 255))
scaled = word.resize((int(word.width * 0.95), int(word.height * 0.95)), Image.LANCZOS)
banner.alpha_composite(scaled, ((W - scaled.width) // 2, (H - scaled.height) // 2))
banner.convert("RGB").save(root / "docs/media/banner.png", optimize=True)
print("banner.png written")
