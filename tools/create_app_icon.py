"""Render our own small vector-like earbud mark; no third-party artwork."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
destination = root / "src" / "HeyMelody.App" / "Assets"
destination.mkdir(parents=True, exist_ok=True)
image = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((24, 24, 1000, 1000), radius=232, fill="#126C75")
draw.rounded_rectangle((185, 210, 481, 493), radius=140, fill="#FFFFFF")
draw.rounded_rectangle((356, 363, 475, 781), radius=60, fill="#FFFFFF")
draw.rounded_rectangle((543, 210, 839, 493), radius=140, fill="#FFFFFF")
draw.rounded_rectangle((550, 363, 669, 781), radius=60, fill="#FFFFFF")
draw.rounded_rectangle((245, 292, 357, 346), radius=26, fill="#7CB2B6")
draw.rounded_rectangle((667, 292, 779, 346), radius=26, fill="#7CB2B6")
draw.ellipse((392, 692, 438, 738), fill="#C3E2E5")
draw.ellipse((586, 692, 632, 738), fill="#C3E2E5")
native = image.resize((256, 256), Image.Resampling.LANCZOS)
native.save(destination / "HeyMelody.png")
native.save(destination / "HeyMelody.ico", sizes=[(size, size) for size in [16, 20, 24, 28, 32, 40, 48, 64, 96, 128, 256]])
print("Created own earbud icon: 11 frames, 16 through 256 px")
