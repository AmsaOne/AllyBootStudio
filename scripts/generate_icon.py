"""
App icon generator — original logo inspired by ROG (asymmetric eye / triangular
predator) and ACSE (hex/gear chrome), but composed entirely from primitives so
it does not infringe either trademark. Yautja-thermal palette to match the boot
animation.

Renders:
  assets/icon_1024.png   — high-res master
  assets/icon.ico        — multi-resolution Windows icon (256/128/64/48/32/16)

Run:
    python scripts/generate_icon.py
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


SIZE = 1024  # render at 1024 then downsample for ICO

DEEP_BG    = (12, 8, 10)
RIM        = (180, 50, 60)
RIM_HI     = (255, 110, 100)
GRID       = (40, 12, 14)
EYE_DEEP   = (140, 20, 30)
EYE_HOT    = (255, 70, 70)
EYE_CORE   = (255, 230, 200)
SCAR       = (255, 90, 90)
GEAR_DIM   = (90, 25, 30)


def hex_grid_layer(size: int, cell: int, color) -> Image.Image:
    """Faint hexagonal mesh covering the whole image."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = cell
    dx = r * math.sqrt(3)
    dy = r * 1.5
    for row in range(-1, int(size / dy) + 2):
        for col in range(-1, int(size / dx) + 2):
            cx = col * dx + (dx / 2 if row % 2 else 0)
            cy = row * dy
            pts = [
                (cx + r * math.cos(math.radians(a)),
                 cy + r * math.sin(math.radians(a)))
                for a in (30, 90, 150, 210, 270, 330)
            ]
            d.polygon(pts, outline=color, width=2)
    return img


def gear_ring(size: int, outer_r: int, inner_r: int, teeth: int, color) -> Image.Image:
    """Sparse gear teeth around a circular bezel — ACSE flavour without copying it."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = size // 2, size // 2
    # Outer ring
    d.ellipse([cx - outer_r, cy - outer_r, cx + outer_r, cy + outer_r],
              outline=color, width=8)
    d.ellipse([cx - inner_r, cy - inner_r, cx + inner_r, cy + inner_r],
              outline=color, width=4)
    # Teeth: short outward rectangles at evenly spaced angles
    tooth_inner = outer_r - 2
    tooth_outer = outer_r + 28
    tooth_w = 18
    for k in range(teeth):
        ang = (k / teeth) * 2 * math.pi
        c, s = math.cos(ang), math.sin(ang)
        # Build a rectangle perpendicular to the radial direction
        pts = []
        for r_off, t_off in [(tooth_inner, -tooth_w), (tooth_outer, -tooth_w),
                             (tooth_outer, tooth_w), (tooth_inner, tooth_w)]:
            x = cx + c * r_off + (-s) * t_off
            y = cy + s * r_off + (c) * t_off
            pts.append((x, y))
        d.polygon(pts, fill=color)
    return img


def predator_eye(size: int) -> Image.Image:
    """Asymmetric, angular 'eye' — diagonal lozenge with a hot core. ROG-flavoured
    but the geometry is our own (trapezoidal, not the ROG eye)."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = size // 2, size // 2

    # Outer angular bezel — six-sided lozenge.
    bezel = [
        (cx - 320, cy + 40),
        (cx - 200, cy - 140),
        (cx + 200, cy - 160),
        (cx + 320, cy + 20),
        (cx + 200, cy + 200),
        (cx - 200, cy + 220),
    ]
    d.polygon(bezel, outline=RIM, width=10)

    # Inner narrow lozenge — the "iris".
    iris = [
        (cx - 230, cy + 30),
        (cx - 130, cy - 90),
        (cx + 130, cy - 100),
        (cx + 230, cy + 20),
        (cx + 130, cy + 140),
        (cx - 130, cy + 150),
    ]
    d.polygon(iris, fill=EYE_DEEP)
    d.polygon(iris, outline=RIM_HI, width=4)

    # Hot core
    d.ellipse([cx - 70, cy - 40, cx + 70, cy + 50], fill=EYE_HOT)
    d.ellipse([cx - 25, cy - 18, cx + 25, cy + 18], fill=EYE_CORE)

    return img


def scar_marks(size: int) -> Image.Image:
    """Three vertical predator scar marks crossing the eye, slightly diagonal."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = size // 2, size // 2
    # Three roughly parallel slashes, slight skew.
    for offset_x, length, width, jitter in [
        (-110, 540, 14, 0),
        (0,    600, 18, 4),
        (110,  540, 14, -2),
    ]:
        x0 = cx + offset_x + jitter
        y0 = cy - length // 2 - 30
        x1 = cx + offset_x - jitter
        y1 = cy + length // 2 + 30
        # Soft edges via a darker outer + brighter inner stroke
        d.line([(x0, y0), (x1, y1)], fill=(60, 8, 10, 220), width=width + 6)
        d.line([(x0, y0), (x1, y1)], fill=SCAR + (255,), width=width)
    return img


def vignette(size: int) -> Image.Image:
    """Radial darkening overlay."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    cx, cy = size / 2, size / 2
    max_r = math.hypot(cx, cy)
    pixels = []
    for y in range(size):
        for x in range(size):
            r = math.hypot(x - cx, y - cy) / max_r
            a = int(min(255, max(0, (r - 0.4) * 320)))
            pixels.append((0, 0, 0, a))
    img.putdata(pixels)
    return img


def add_glow(layer: Image.Image, radius: int, intensity: float = 1.0) -> Image.Image:
    blur = layer.filter(ImageFilter.GaussianBlur(radius=radius))
    if intensity != 1.0:
        from numpy import asarray, clip
        a = asarray(blur).astype("float32")
        a[..., :3] = clip(a[..., :3] * intensity, 0, 255)
        blur = Image.fromarray(a.astype("uint8"), "RGBA")
    return Image.alpha_composite(blur, layer)


def main():
    repo_root = Path(__file__).resolve().parent.parent
    assets = repo_root / "assets"
    assets.mkdir(parents=True, exist_ok=True)

    img = Image.new("RGBA", (SIZE, SIZE), DEEP_BG + (255,))

    # Faint hex grid background.
    grid = hex_grid_layer(SIZE, cell=42, color=GRID)
    img = Image.alpha_composite(img, grid)

    # Gear ring (chunky, sparse — chrome bezel).
    ring = gear_ring(SIZE, outer_r=440, inner_r=400, teeth=12, color=GEAR_DIM)
    img = Image.alpha_composite(img, ring)

    # Predator eye lozenge.
    eye = predator_eye(SIZE)
    eye = add_glow(eye, radius=18, intensity=1.0)
    img = Image.alpha_composite(img, eye)

    # Three scar marks across the eye.
    scars = scar_marks(SIZE)
    scars = add_glow(scars, radius=8, intensity=0.9)
    img = Image.alpha_composite(img, scars)

    # Subtle vignette.
    img = Image.alpha_composite(img, vignette(SIZE))

    png_path = assets / "icon_1024.png"
    img.save(png_path, "PNG")
    print(f"Wrote {png_path}")

    # Multi-resolution ICO. Pillow .save with "ICO" accepts a `sizes` kwarg.
    ico_path = assets / "icon.ico"
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    img.save(ico_path, format="ICO", sizes=sizes)
    print(f"Wrote {ico_path}  (sizes: {[s[0] for s in sizes]})")


if __name__ == "__main__":
    main()
