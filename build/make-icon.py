"""Compose the Windows .ico from the same artwork and recipe the Mac icon uses.

The recipe is ReportMate.icon/icon.json: the logo at 90% of the canvas, nudged
down, over a linear gradient of the amber, on a rounded square. The colours there
are Display-P3, which is a wider gamut than sRGB, so they are converted properly
rather than having their numbers reused -- taking P3 components as sRGB ones makes
the amber noticeably more saturated than the Mac's.
"""
from PIL import Image, ImageDraw
import math

P3 = [(1.00000, 0.72161, 0.04693), (0.84567, 0.60137, 0.00420)]

def to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def encode(c):
    c = max(0.0, min(1.0, c))
    return c * 12.92 if c <= 0.0031308 else 1.055 * (c ** (1 / 2.4)) - 0.055

# Display-P3 -> XYZ(D65) -> linear sRGB, concatenated.
M = [[ 1.2249401, -0.2249401,  0.0000000],
     [-0.0420569,  1.0420569,  0.0000000],
     [-0.0196376, -0.0786361,  1.0982737]]

def p3_to_srgb(rgb):
    lin = [to_linear(c) for c in rgb]
    out = [sum(M[i][j] * lin[j] for j in range(3)) for i in range(3)]
    return tuple(int(round(encode(c) * 255)) for c in out)

TOP, BOTTOM = (p3_to_srgb(P3[0]), p3_to_srgb(P3[1]))
print("gradient sRGB:", TOP, "->", BOTTOM)

logo = Image.open("reportmate-logo.png").convert("RGBA")

def compose(size):
    # Rendered at 4x and downsampled: the rounded corners and the logo edges both
    # alias badly at 16 and 24 pixels otherwise.
    s = size * 4
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    gradient = Image.new("RGBA", (s, s))
    px = gradient.load()
    for y in range(s):
        t = y / max(1, s - 1)
        px_row = tuple(int(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t)) for i in range(3)) + (255,)
        for x in range(s):
            px[x, y] = px_row

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.2), fill=255)
    canvas.paste(gradient, (0, 0), mask)

    side = int(s * 0.9)
    art = logo.resize((side, side), Image.LANCZOS)
    offset = int(round(32.140625 / 1024 * s))
    canvas.alpha_composite(art, ((s - side) // 2, (s - side) // 2 + offset))

    return canvas.resize((size, size), Image.LANCZOS)

sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [compose(n) for n in sizes]
frames[-1].save("ReportMate.ico", format="ICO",
                sizes=[(n, n) for n in sizes], append_images=frames[:-1])
frames[-1].save("preview-256.png")
compose(32).resize((128, 128), Image.NEAREST).save("preview-32-zoom.png")
print("wrote ReportMate.ico with", sizes)
