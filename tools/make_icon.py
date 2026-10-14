"""Draws the app icon (a small calendar page) and writes assets/icon.ico. Standard library only.

    python tools/make_icon.py
"""

import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "assets" / "icon.ico"
PREVIEW = OUT.parent.parent / "docs" / "icon.png"  # for the README
SIZES = (16, 24, 32, 48, 64, 256)
SUPERSAMPLE = 4

PAGE = (255, 255, 255)
HEADER = (15, 108, 189)
CELL = (15, 108, 189)
TODAY = (255, 185, 0)
EDGE = (200, 210, 222)


def rounded(x, y, x0, y0, x1, y1, r):
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def color_at(u, v):
    """Colour of the icon at (u, v) in 0..1 coordinates, or None for transparent."""
    page = (0.08, 0.12, 0.92, 0.92)
    if not rounded(u, v, *page, 0.12):
        return None
    if not rounded(u, v, page[0] + 0.025, page[1] + 0.025, page[2] - 0.025, page[3] - 0.025, 0.1):
        return EDGE
    if v < 0.36:
        return HEADER
    for row in range(2):
        for col in range(3):
            x0 = 0.2 + col * 0.21
            y0 = 0.47 + row * 0.2
            if rounded(u, v, x0, y0, x0 + 0.15, y0 + 0.13, 0.03):
                return TODAY if (row, col) == (0, 2) else CELL
    return PAGE


def render(size):
    n = size * SUPERSAMPLE
    rows = []
    for y in range(size):
        row = bytearray([0])  # PNG filter: none
        for x in range(size):
            r = g = b = a = 0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    c = color_at((x * SUPERSAMPLE + sx + 0.5) / n, (y * SUPERSAMPLE + sy + 0.5) / n)
                    if c:
                        r, g, b, a = r + c[0], g + c[1], b + c[2], a + 1
            samples = SUPERSAMPLE * SUPERSAMPLE
            row += bytes((r // a, g // a, b // a, 255 * a // samples)) if a else bytes(4)
        rows.append(bytes(row))
    return png(size, size, b"".join(rows))


def png(width, height, raw):
    """Encode 8-bit RGBA rows (each prefixed with a filter byte) as a PNG."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    images = [render(s) for s in SIZES]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, data in zip(SIZES, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(out + b"".join(images))
    PREVIEW.parent.mkdir(exist_ok=True)
    PREVIEW.write_bytes(images[-1])
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes) and {PREVIEW.name}")


if __name__ == "__main__":
    main()
