"""
Generate official STORM CONNECT master icon (1024x1024 RGBA PNG),
multi-layer AppIcon.ico (16, 20, 24, 32, 40, 48, 64, 128, 256),
and Android adaptive/legacy mipmaps.
"""

import math
import os
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS_DIR = ROOT / "Assets"
ANDROID_RES = ROOT / "android" / "app" / "src" / "main" / "res"

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

ANDROID_MIPMAPS = {
    "mipmap-mdpi": 48,
    "mipmap-hdpi": 72,
    "mipmap-xhdpi": 96,
    "mipmap-xxhdpi": 144,
    "mipmap-xxxhdpi": 192,
}


def lerp_color(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(len(c1)))


def draw_squircle_mask(size: int, radius_ratio: float = 0.225) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    pad = int(size * 0.04)
    r = int(size * radius_ratio)
    draw.rounded_rectangle([pad, pad, size - pad, size - pad], radius=r, fill=255)
    return mask


def render_master_icon(size: int = 2048, for_adaptive_fg: bool = False) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    if not for_adaptive_fg:
        # Rich midnight-violet to deep-indigo vertical-diagonal gradient background
        bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        px = bg.load()
        c_top_left = (22, 16, 48, 255)
        c_mid = (32, 22, 74, 255)
        c_bot_right = (12, 18, 42, 255)
        for y in range(size):
            ty = y / (size - 1)
            for x in range(size):
                tx = x / (size - 1)
                t = 0.55 * ty + 0.45 * tx
                base = lerp_color(c_top_left, c_mid, min(1.0, t * 1.6)) if t < 0.6 else lerp_color(c_mid, c_bot_right, (t - 0.6) / 0.4)
                # Subtle radial aurora glow at center
                dx = tx - 0.5
                dy = ty - 0.46
                dist = math.sqrt(dx * dx + dy * dy)
                glow = max(0.0, 1.0 - dist * 1.9)
                r = min(255, int(base[0] + glow * 45))
                g = min(255, int(base[1] + glow * 28))
                b = min(255, int(base[2] + glow * 85))
                px[x, y] = (r, g, b, 255)

        mask = draw_squircle_mask(size)
        img.paste(bg, (0, 0), mask)

        # Specular rim highlight inside squircle
        rim = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        rdraw = ImageDraw.Draw(rim)
        pad = int(size * 0.045)
        r = int(size * 0.22)
        rdraw.rounded_rectangle([pad, pad, size - pad, size - pad], radius=r, outline=(139, 92, 246, 210), width=max(6, size // 128))
        rdraw.rounded_rectangle([pad + size // 140, pad + size // 140, size - pad - size // 140, size - pad - size // 140], radius=r, outline=(6, 182, 212, 120), width=max(3, size // 260))
        img = Image.alpha_composite(img, rim)

    # Ambient neon glow layer behind emblem
    glow_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(glow_layer)
    cx, cy = size // 2, size // 2
    gdraw.ellipse([cx - size * 0.30, cy - size * 0.26, cx + size * 0.30, cy + size * 0.28], fill=(139, 92, 246, 95))
    gdraw.ellipse([cx - size * 0.22, cy - size * 0.16, cx + size * 0.22, cy + size * 0.22], fill=(6, 182, 212, 70))
    glow_layer = glow_layer.filter(ImageFilter.GaussianBlur(radius=size * 0.06))
    img = Image.alpha_composite(img, glow_layer)

    # Emblem: Dual overlapping remote monitors + high-voltage STORM lightning link
    emblem = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    edraw = ImageDraw.Draw(emblem)

    scale = 0.72 if for_adaptive_fg else 1.0
    def sx(v):
        return cx + (v - 0.5) * size * scale
    def sy(v):
        return cy + (v - 0.5) * size * scale

    stroke_main = max(8, int(size * 0.032 * scale))
    stroke_thin = max(4, int(size * 0.018 * scale))

    # Back monitor (top-right, cyan border)
    bm_rect = [sx(0.32), sy(0.20), sx(0.80), sy(0.56)]
    edraw.rounded_rectangle(bm_rect, radius=int(size * 0.045 * scale), fill=(18, 28, 58, 235), outline=(6, 182, 212, 255), width=stroke_main)
    # Back monitor top bar dots
    for idx, col in enumerate([(6, 182, 212, 255), (139, 92, 246, 255), (248, 250, 252, 210)]):
        dx = sx(0.37 + idx * 0.035)
        dy = sy(0.25)
        rad = size * 0.010 * scale
        edraw.ellipse([dx - rad, dy - rad, dx + rad, dy + rad], fill=col)

    # Front monitor (bottom-left, bright violet-white border)
    fm_rect = [sx(0.20), sy(0.38), sx(0.70), sy(0.75)]
    # Drop shadow under front monitor
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sdraw = ImageDraw.Draw(shadow)
    sdraw.rounded_rectangle([fm_rect[0], fm_rect[1] + size * 0.02, fm_rect[2], fm_rect[3] + size * 0.02], radius=int(size * 0.045 * scale), fill=(0, 0, 0, 160))
    shadow = shadow.filter(ImageFilter.GaussianBlur(radius=size * 0.02))
    emblem = Image.alpha_composite(emblem, shadow)
    edraw = ImageDraw.Draw(emblem)

    edraw.rounded_rectangle(fm_rect, radius=int(size * 0.045 * scale), fill=(26, 19, 56, 248), outline=(167, 139, 250, 255), width=stroke_main)
    # Stand base under front monitor
    edraw.line([(sx(0.45), sy(0.75)), (sx(0.45), sy(0.82))], fill=(196, 181, 253, 255), width=stroke_main)
    edraw.line([(sx(0.34), sy(0.82)), (sx(0.56), sy(0.82))], fill=(6, 182, 212, 255), width=stroke_main)

    # Glowing STORM Lightning Bolt bridging the two displays
    bolt = [
        (sx(0.52), sy(0.27)),
        (sx(0.39), sy(0.52)),
        (sx(0.49), sy(0.52)),
        (sx(0.42), sy(0.69)),
        (sx(0.61), sy(0.46)),
        (sx(0.50), sy(0.46)),
    ]
    bolt_glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    bgdraw = ImageDraw.Draw(bolt_glow)
    bgdraw.polygon(bolt, fill=(56, 189, 248, 220))
    bolt_glow = bolt_glow.filter(ImageFilter.GaussianBlur(radius=size * 0.024))
    emblem = Image.alpha_composite(emblem, bolt_glow)
    edraw = ImageDraw.Draw(emblem)
    edraw.polygon(bolt, fill=(255, 255, 255, 255), outline=(6, 182, 212, 255))

    # Connection signal nodes on left & right corners
    for nx, ny, col in [(0.24, 0.26, (167, 139, 250, 255)), (0.77, 0.69, (6, 182, 212, 255))]:
        r_outer = size * 0.032 * scale
        r_inner = size * 0.015 * scale
        cxn, cyn = sx(nx), sy(ny)
        edraw.ellipse([cxn - r_outer, cyn - r_outer, cxn + r_outer, cyn + r_outer], outline=col, width=stroke_thin)
        edraw.ellipse([cxn - r_inner, cyn - r_inner, cxn + r_inner, cyn + r_inner], fill=(255, 255, 255, 255))

    img = Image.alpha_composite(img, emblem)
    return img


def main():
    ASSETS_DIR.mkdir(parents=True, exist_ok=True)
    master_2048 = render_master_icon(2048, for_adaptive_fg=False)
    master_1024 = master_2048.resize((1024, 1024), Image.Resampling.LANCZOS)
    png_path = ASSETS_DIR / "app_icon.png"
    master_1024.save(png_path, format="PNG")
    print(f"Saved master PNG: {png_path} ({master_1024.size})")

    # Build multi-layer ICO (16..256) with unsharp mask on small sizes
    ico_layers = []
    for sz in ICON_SIZES:
        layer = master_2048.resize((sz, sz), Image.Resampling.LANCZOS)
        if sz <= 48:
            layer = layer.filter(ImageFilter.UnsharpMask(radius=0.8, percent=120, threshold=2))
        ico_layers.append(layer)

    ico_path = ROOT / "AppIcon.ico"
    # Save largest layer with append_images containing all sizes for proper multi-layer ICO
    ico_layers[-1].save(
        ico_path,
        format="ICO",
        sizes=[(sz, sz) for sz in ICON_SIZES],
        append_images=ico_layers[:-1],
    )
    print(f"Saved multi-layer ICO: {ico_path} with sizes {ICON_SIZES}")

    # Android legacy + adaptive icons
    for folder, sz in ANDROID_MIPMAPS.items():
        mdir = ANDROID_RES / folder
        mdir.mkdir(parents=True, exist_ok=True)
        ic = master_2048.resize((sz, sz), Image.Resampling.LANCZOS)
        if sz <= 72:
            ic = ic.filter(ImageFilter.UnsharpMask(radius=0.7, percent=110, threshold=2))
        ic.save(mdir / "ic_launcher.png", format="PNG")
        ic.save(mdir / "ic_launcher_round.png", format="PNG")

    fg_2048 = render_master_icon(2048, for_adaptive_fg=True)
    fg_432 = fg_2048.resize((432, 432), Image.Resampling.LANCZOS)
    drawable_dir = ANDROID_RES / "drawable"
    drawable_dir.mkdir(parents=True, exist_ok=True)
    fg_432.save(drawable_dir / "ic_launcher_foreground.png", format="PNG")

    anydpi_dir = ANDROID_RES / "mipmap-anydpi-v26"
    anydpi_dir.mkdir(parents=True, exist_ok=True)
    adaptive_xml = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
</adaptive-icon>
"""
    (anydpi_dir / "ic_launcher.xml").write_text(adaptive_xml, encoding="utf-8")
    (anydpi_dir / "ic_launcher_round.xml").write_text(adaptive_xml, encoding="utf-8")
    print("Saved Android mipmaps and adaptive icon XML.")


if __name__ == "__main__":
    main()
