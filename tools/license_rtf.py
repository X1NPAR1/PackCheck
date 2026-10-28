"""Converts LICENSE to the RTF file the MSI license dialog shows."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
text = (root / "LICENSE").read_text(encoding="utf-8")
paragraphs = [p.replace("\n", " ") for p in text.split("\n\n")]
escaped = [p.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}") for p in paragraphs]
rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\f0\\fs18 " + "\\par\\par ".join(escaped) + "}"
(root / "installer" / "license.rtf").write_text(rtf, encoding="ascii")
print("license.rtf written")
