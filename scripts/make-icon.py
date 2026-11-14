"""Draws the Media Cleanup icon and writes assets/app.ico (16 to 256 px) and assets/app.png.

Run with:  python scripts/make-icon.py   (needs Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024  # Draw large, then scale down for smooth edges.
ROOT = Path(__file__).resolve().parent.parent / "assets"


def gradient(top, bottom):
    image = Image.new("RGB", (S, S))
    pixels = image.load()
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2 * S - 2)
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    return image


def sparkle(draw, cx, cy, r, color):
    # A four-point star: long points up/down/left/right, pinched in between.
    k = r * 0.22
    draw.polygon([(cx, cy - r), (cx + k, cy - k), (cx + r, cy), (cx + k, cy + k),
                  (cx, cy + r), (cx - k, cy + k), (cx - r, cy), (cx - k, cy - k)], fill=color)


def draw_icon():
    # Rounded square background with a teal-to-indigo gradient.
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=210, fill=255)
    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(gradient((20, 184, 166), (67, 56, 202)), (0, 0), mask)
    draw = ImageDraw.Draw(icon)

    # A white film frame with sprocket holes down both sides.
    white = (255, 255, 255, 255)
    draw.rounded_rectangle([190, 300, 834, 790], radius=70, fill=white)
    hole = (45, 110, 190, 255)
    for i in range(4):
        y = 345 + i * 108
        draw.rounded_rectangle([225, y, 290, y + 66], radius=16, fill=hole)
        draw.rounded_rectangle([734, y, 799, y + 66], radius=16, fill=hole)

    # A play triangle in the middle of the frame.
    draw.polygon([(440, 425), (440, 665), (640, 545)], fill=(67, 56, 202, 255))

    # Sparkles: the "cleaned up" part.
    gold = (253, 224, 71, 255)
    sparkle(draw, 770, 215, 150, gold)
    sparkle(draw, 905, 370, 62, white)
    return icon


def main():
    ROOT.mkdir(exist_ok=True)
    icon = draw_icon()
    icon.resize((256, 256), Image.LANCZOS).save(ROOT / "app.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    icon.resize((256, 256), Image.LANCZOS).save(ROOT / "app.ico", sizes=[(s, s) for s in sizes])
    print("Wrote", ROOT / "app.ico")


if __name__ == "__main__":
    main()
