"""
Yautja-themed ROG Ally boot animation generator.

Renders a 1920x1080 @ 60fps MP4 (~5 s) suitable for the AllyBootStudio Apply
flow. Programmatic / deterministic / re-runnable. No external assets required.

Usage:
    python generate_boot_animation.py
    python generate_boot_animation.py --text "AmsaOne" --status "STRIKE READY"

Output: <repo>/assets/boot_yautja.mp4
Requires: Pillow, numpy, ffmpeg on PATH (or full path via --ffmpeg).
"""

import argparse
import math
import os
import random
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont


# -----------------------------------------------------------------------------#
# Config                                                                       #
# -----------------------------------------------------------------------------#

W, H = 1920, 1080
FPS = 60
DURATION_S = 5.0
N_FRAMES = int(FPS * DURATION_S)

# Palette — Yautja thermal red / amber.
BG_DEEP   = (8,  4,  4)       # near-black with red bias
BG_MID    = (28, 10, 10)
HUD_RED   = (255, 50, 60)
HUD_DEEP  = (200, 30, 40)
HUD_AMBER = (255, 150, 60)
HUD_DIM   = (110, 25, 30)
TXT_HOT   = (255, 230, 200)
SCAR      = (255, 70, 60)
GRID      = (40, 12, 14)


# -----------------------------------------------------------------------------#
# Helpers                                                                      #
# -----------------------------------------------------------------------------#

def find_font(size, bold=False):
    """Pick a system font. Falls back to PIL default."""
    candidates = [
        r"C:\Windows\Fonts\Consolab.ttf" if bold else r"C:\Windows\Fonts\consola.ttf",
        r"C:\Windows\Fonts\Lucida Console Bold.ttf" if bold else r"C:\Windows\Fonts\lucon.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for p in candidates:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


def smoothstep(t, a=0.0, b=1.0):
    if b <= a:
        return 1.0 if t >= b else 0.0
    x = max(0.0, min(1.0, (t - a) / (b - a)))
    return x * x * (3.0 - 2.0 * x)


def add_glow(img: Image.Image, radius: int, alpha: float = 1.0) -> Image.Image:
    """Additive glow by blurring the image and screen-blending it back."""
    blur = img.filter(ImageFilter.GaussianBlur(radius=radius))
    if alpha != 1.0:
        # scale RGB only, keep alpha
        b = np.asarray(blur).astype(np.float32)
        b[..., :3] = np.clip(b[..., :3] * alpha, 0, 255)
        blur = Image.fromarray(b.astype(np.uint8), "RGBA")
    out = ImageChops_screen(img, blur)
    return out


def ImageChops_screen(a: Image.Image, b: Image.Image) -> Image.Image:
    aa = np.asarray(a).astype(np.float32) / 255.0
    bb = np.asarray(b).astype(np.float32) / 255.0
    out = 1.0 - (1.0 - aa) * (1.0 - bb)
    return Image.fromarray((out * 255).astype(np.uint8), "RGBA")


# -----------------------------------------------------------------------------#
# Glyph generation — Yautja-style strokes (proprietary pictograms in the films, #
# but the visual language is short vertical/diagonal slashes with anchor marks).#
# We synthesize ours so we don't lift any specific design.                     #
# -----------------------------------------------------------------------------#

def random_yautja_glyph(size: int) -> Image.Image:
    """Generate a small monochrome glyph: 3-5 strokes inside a square."""
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    n_strokes = random.randint(3, 5)
    pad = max(2, size // 8)
    for _ in range(n_strokes):
        x = random.randint(pad, size - pad)
        # Mostly vertical, sometimes a diagonal slash, sometimes a tick.
        kind = random.random()
        if kind < 0.6:
            y0 = random.randint(pad, size // 2)
            y1 = random.randint(size // 2, size - pad)
            d.line([(x, y0), (x, y1)], fill=HUD_RED, width=max(1, size // 18))
        elif kind < 0.85:
            x2 = x + random.choice([-1, 1]) * random.randint(size // 4, size // 2)
            y = random.randint(pad, size - pad)
            d.line([(x, y), (x2, y + random.randint(-size // 6, size // 6))],
                   fill=HUD_AMBER, width=max(1, size // 22))
        else:
            y = random.randint(pad, size - pad)
            d.ellipse([x - 2, y - 2, x + 2, y + 2], fill=HUD_RED)
    return g


# -----------------------------------------------------------------------------#
# Layer renderers                                                              #
# -----------------------------------------------------------------------------#

def render_background(t: float) -> Image.Image:
    """Dark vignette + faint hex grid + scanline shimmer."""
    img = Image.new("RGBA", (W, H), BG_DEEP + (255,))
    arr = np.asarray(img).copy()

    # Vertical gradient: slightly brighter near the centre.
    yy = np.linspace(0, 1, H).reshape(-1, 1, 1)
    fade = (1.0 - np.abs(yy - 0.5) * 1.6).clip(0.0, 1.0)
    arr = arr.astype(np.float32)
    arr[..., 0] += fade.squeeze(-1) * 22
    arr[..., 1] += fade.squeeze(-1) * 6
    arr[..., 2] += fade.squeeze(-1) * 6

    # Scanlines (every 3 rows dim by 25%).
    arr[1::3, :, :3] *= 0.78

    # Subtle moving noise (scope grain).
    rng = np.random.default_rng(int(t * 60))
    grain = rng.integers(-8, 9, (H, W, 1)).astype(np.float32)
    arr[..., :3] = np.clip(arr[..., :3] + grain, 0, 255)

    img = Image.fromarray(arr.astype(np.uint8), "RGBA")

    # Hex grid overlay (drawn once, faint).
    hex_layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hex_layer)
    r = 60
    dx = r * math.sqrt(3)
    dy = r * 1.5
    for row in range(-1, int(H / dy) + 2):
        for col in range(-1, int(W / dx) + 2):
            cx = col * dx + (dx / 2 if row % 2 else 0)
            cy = row * dy
            pts = [
                (cx + r * math.cos(math.radians(a)),
                 cy + r * math.sin(math.radians(a)))
                for a in (30, 90, 150, 210, 270, 330)
            ]
            hd.polygon(pts, outline=GRID, width=1)
    img = Image.alpha_composite(img, hex_layer)
    return img


def render_glyph_rain(t: float, columns_state: list) -> Image.Image:
    """Cascading Yautja glyphs. `columns_state` mutates across frames."""
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    fade = smoothstep(t, 0.4, 1.6) * (1.0 - smoothstep(t, 2.6, 3.4))
    if fade <= 0.01:
        return img

    glyph_size = 40
    col_w = glyph_size + 6
    n_cols = W // col_w

    while len(columns_state) < n_cols:
        columns_state.append({
            "y": random.uniform(-H, 0),
            "speed": random.uniform(120, 360),
            "trail": [],
            "next_glyph_at": 0.0,
        })

    for i, col in enumerate(columns_state):
        col["y"] += col["speed"] / FPS
        if t * 1000 >= col["next_glyph_at"]:
            col["trail"].append({
                "y": int(col["y"]),
                "glyph": random_yautja_glyph(glyph_size),
                "born": t,
            })
            col["next_glyph_at"] = (t + random.uniform(0.04, 0.10)) * 1000
        # prune off-screen glyphs
        col["trail"] = [g for g in col["trail"] if g["y"] < H + glyph_size]

        x = i * col_w
        for j, g in enumerate(col["trail"]):
            age = max(0.0, t - g["born"])
            head = j == len(col["trail"]) - 1
            alpha = max(0.0, 1.0 - age * 0.9)
            if head:
                tinted = g["glyph"]
            else:
                # dim non-head glyphs
                arr = np.asarray(g["glyph"]).astype(np.float32)
                arr[..., :3] *= 0.55
                arr[..., 3] *= alpha * 0.8
                tinted = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGBA")
            tinted = tinted.copy()
            a = np.asarray(tinted).astype(np.float32)
            a[..., 3] *= fade
            tinted = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), "RGBA")
            img.alpha_composite(tinted, (x, g["y"]))

    img = add_glow(img, 6, alpha=0.6)
    return img


def render_reticle(t: float) -> Image.Image:
    """Tri-laser reticle: three dots converging, then locking on a hex frame."""
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = W // 2, H // 2

    # Phase A: scan-in (0–1.0s). Three dots travel from off-screen edges to centre.
    scan = smoothstep(t, 0.0, 1.0)
    # Triangle vertex offsets (centred around (0,0)).
    triangle = [
        (-220, -130),
        (220, -130),
        (0, 250),
    ]
    for vx, vy in triangle:
        # Start far off-screen along the same direction.
        sx = vx * 6 - 200 * (1 if vx >= 0 else -1)
        sy = vy * 6 - 200 * (1 if vy >= 0 else -1)
        ex = cx + vx
        ey = cy + vy
        x = sx * (1 - scan) + ex * scan
        y = sy * (1 - scan) + ey * scan
        # laser tail
        tx = sx * (1 - max(0.0, scan - 0.05)) + ex * max(0.0, scan - 0.05)
        ty = sy * (1 - max(0.0, scan - 0.05)) + ey * max(0.0, scan - 0.05)
        d.line([(tx, ty), (x, y)], fill=HUD_RED + (180,), width=2)
        # dot
        r = 6 if scan < 0.99 else 5
        d.ellipse([x - r, y - r, x + r, y + r], fill=HUD_RED + (255,))

    # Phase B: hex frame draws around the centre once dots locked.
    if scan >= 0.95:
        lock_t = smoothstep(t, 1.0, 1.8)
        hex_r = 320
        pts = [
            (cx + hex_r * math.cos(math.radians(a)),
             cy + hex_r * math.sin(math.radians(a)))
            for a in (30, 90, 150, 210, 270, 330)
        ]
        # animated stroke: only draw `lock_t` fraction of the perimeter
        n_segs = 6
        n_to_draw = int(round(n_segs * lock_t))
        for i in range(n_to_draw):
            d.line([pts[i], pts[(i + 1) % n_segs]], fill=HUD_RED + (220,), width=3)
        # corner brackets at every vertex (sharp HUD feel)
        if lock_t > 0.4:
            bracket_a = smoothstep(t, 1.4, 2.0) * 255
            for px, py in pts:
                size = 18
                d.line([(px - size, py), (px, py)], fill=HUD_AMBER + (int(bracket_a),), width=2)
                d.line([(px, py - size), (px, py)], fill=HUD_AMBER + (int(bracket_a),), width=2)

        # crosshair through centre
        cross_a = smoothstep(t, 1.6, 2.2)
        if cross_a > 0:
            cross_len = 240
            ca = int(220 * cross_a)
            d.line([(cx - cross_len, cy), (cx + cross_len, cy)], fill=HUD_DEEP + (ca,), width=1)
            d.line([(cx, cy - cross_len), (cx, cy + cross_len)], fill=HUD_DEEP + (ca,), width=1)

    img = add_glow(img, 8, alpha=0.7)
    return img


def render_text(t: float, primary: str, status: str, subtitle: str) -> Image.Image:
    """Centre title + status line + subtitle, all materialising in sequence."""
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = W // 2, H // 2

    # Primary title — character-by-character reveal.
    title_font = find_font(120, bold=True)
    title_t = smoothstep(t, 1.8, 3.2)
    n_chars = max(0, int(round(len(primary) * title_t)))
    shown = primary[:n_chars]
    if shown:
        # Pulse alpha for active char.
        active_alpha = 255
        bbox = d.textbbox((0, 0), shown, font=title_font)
        tw = bbox[2] - bbox[0]
        th = bbox[3] - bbox[1]
        x = cx - tw // 2 - bbox[0]
        y = cy - th // 2 - bbox[1] - 18
        # glow background
        for off, blur_a in [(8, 80), (4, 130)]:
            d.text((x, y), shown, font=title_font, fill=HUD_RED + (blur_a,))
        d.text((x, y), shown, font=title_font, fill=TXT_HOT + (active_alpha,))
        # caret on the right edge if not done
        if title_t < 1.0 and (int(t * 8) % 2 == 0):
            cax = x + tw + 6
            d.rectangle([cax, y + 8, cax + 14, y + th - 4], fill=HUD_RED + (220,))

    # Subtitle (small, above title).
    sub_t = smoothstep(t, 2.2, 3.0)
    if sub_t > 0:
        sub_font = find_font(28, bold=False)
        sub_text = subtitle
        bbox = d.textbbox((0, 0), sub_text, font=sub_font)
        sw = bbox[2] - bbox[0]
        sx = cx - sw // 2 - bbox[0]
        sy = cy - 200
        a = int(220 * sub_t)
        d.text((sx, sy), sub_text, font=sub_font, fill=HUD_AMBER + (a,))

    # Status line — pulses after title locks in.
    stat_t = smoothstep(t, 3.0, 3.8)
    if stat_t > 0:
        # blink: on-off-on-off
        phase = (t - 3.0) * 2.0  # cycles per second
        blink = 0.45 + 0.55 * (math.sin(phase * math.pi) * 0.5 + 0.5)
        a = int(255 * stat_t * blink)
        stat_font = find_font(46, bold=True)
        text = f"●  {status}  ●"
        bbox = d.textbbox((0, 0), text, font=stat_font)
        sw = bbox[2] - bbox[0]
        sh = bbox[3] - bbox[1]
        sx = cx - sw // 2 - bbox[0]
        sy = cy + 80
        d.text((sx, sy), text, font=stat_font, fill=SCAR + (a,))
        # underline
        d.line([(sx, sy + sh + 14), (sx + sw, sy + sh + 14)],
               fill=HUD_DEEP + (a,), width=2)

    img = add_glow(img, 10, alpha=0.75)
    return img


def render_overlay(t: float) -> Image.Image:
    """Persistent HUD chrome: corner brackets, side ticks, signal bars."""
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    fade = smoothstep(t, 0.0, 0.8)
    a = int(180 * fade)
    if a <= 0:
        return img
    margin = 60
    blen = 90
    color = HUD_DIM + (a,)
    # Corners
    for cx, cy, dx, dy in [
        (margin, margin, 1, 1),
        (W - margin, margin, -1, 1),
        (margin, H - margin, 1, -1),
        (W - margin, H - margin, -1, -1),
    ]:
        d.line([(cx, cy), (cx + dx * blen, cy)], fill=color, width=3)
        d.line([(cx, cy), (cx, cy + dy * blen)], fill=color, width=3)
    # Side ticks (left + right)
    for y in range(margin + 30, H - margin, 36):
        long = (y // 36) % 5 == 0
        end = 18 if long else 8
        d.line([(margin, y), (margin + end, y)], fill=color, width=1)
        d.line([(W - margin - end, y), (W - margin, y)], fill=color, width=1)
    # Top status strip
    d.rectangle([margin + 30, margin + 4, margin + 230, margin + 18], outline=color, width=1)
    n = int(8 * (0.5 + 0.5 * math.sin(t * 4)))
    for i in range(8):
        c = HUD_RED + (a,) if i < n else HUD_DIM + (a,)
        d.rectangle([margin + 33 + i * 24, margin + 6,
                     margin + 33 + i * 24 + 20, margin + 16], fill=c)
    bars_font = find_font(18, bold=True)
    d.text((margin + 270, margin + 1), "TARGETING", font=bars_font, fill=HUD_AMBER + (a,))
    return img


def render_fade(t: float) -> Image.Image:
    """Black overlay that fades in at the very end."""
    f = smoothstep(t, 4.5, 5.0)
    if f <= 0:
        return Image.new("RGBA", (W, H), (0, 0, 0, 0))
    return Image.new("RGBA", (W, H), (0, 0, 0, int(f * 255)))


# -----------------------------------------------------------------------------#
# Frame composer + ffmpeg pipe                                                 #
# -----------------------------------------------------------------------------#

def compose_frame(i: int, columns_state: list, args) -> Image.Image:
    t = i / FPS
    bg = render_background(t)
    rain = render_glyph_rain(t, columns_state)
    bg.alpha_composite(rain)
    overlay = render_overlay(t)
    bg.alpha_composite(overlay)
    reticle = render_reticle(t)
    bg.alpha_composite(reticle)
    text = render_text(t, args.text, args.status, args.subtitle)
    bg.alpha_composite(text)
    fade = render_fade(t)
    bg.alpha_composite(fade)
    return bg.convert("RGB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", default="AmsaOne",
                        help="Centre title text")
    parser.add_argument("--status", default="STRIKE READY",
                        help="Bottom status banner")
    parser.add_argument("--subtitle", default="[ ALLY · YAUTJA PROTOCOL ]",
                        help="Small subtitle above the title")
    parser.add_argument("--out", default=None, help="Output MP4 path")
    parser.add_argument("--ffmpeg", default=None, help="Full path to ffmpeg.exe")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    out = Path(args.out) if args.out else repo_root / "assets" / "boot_yautja.mp4"
    out.parent.mkdir(parents=True, exist_ok=True)

    ffmpeg = args.ffmpeg or shutil.which("ffmpeg")
    if not ffmpeg:
        # winget Gyan.FFmpeg default install
        candidate = Path(os.environ.get("LOCALAPPDATA", "")) / \
            "Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe"
        for p in candidate.rglob("ffmpeg.exe"):
            ffmpeg = str(p)
            break
    if not ffmpeg or not Path(ffmpeg).exists():
        print("ERROR: ffmpeg not found. Install with: winget install Gyan.FFmpeg",
              file=sys.stderr)
        sys.exit(2)

    print(f"Rendering {N_FRAMES} frames @ {W}x{H} {FPS}fps -> {out}")
    print(f"  text='{args.text}'  status='{args.status}'  sub='{args.subtitle}'")

    cmd = [
        ffmpeg, "-y", "-loglevel", "error",
        "-f", "image2pipe", "-vcodec", "png",
        "-r", str(FPS), "-i", "-",
        "-c:v", "libx264", "-preset", "medium", "-crf", "18",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
        str(out),
    ]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)

    columns_state: list = []
    random.seed(42)  # deterministic glyph stream
    for i in range(N_FRAMES):
        frame = compose_frame(i, columns_state, args)
        frame.save(proc.stdin, format="PNG")
        if (i + 1) % 30 == 0:
            print(f"  frame {i + 1}/{N_FRAMES}")
    proc.stdin.close()
    rc = proc.wait()
    if rc != 0:
        print(f"ffmpeg exited {rc}", file=sys.stderr)
        sys.exit(rc)
    print(f"Done: {out}  ({out.stat().st_size / 1_048_576:.1f} MB)")


if __name__ == "__main__":
    main()
