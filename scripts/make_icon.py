#!/usr/bin/env python3
"""Generate assets/constantproxy.ico (PNG-compressed ICO) without any imaging library.

The icon is a blue rounded square with a white ring and a centre dot: a steady, always-on connection.
Re-run this script to regenerate the file; the output is deterministic.
"""

import math
import struct
import zlib
from pathlib import Path

MASTER = 256
SUPERSAMPLE = 3
SIZES = [16, 24, 32, 48, 64, 128, 256]

BLUE = (37, 99, 235)
DARK = (30, 64, 175)
WHITE = (255, 255, 255)


def inside_rounded_square(x: float, y: float, size: float, radius: float) -> bool:
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def shade(x: float, y: float, size: float):
    """Return an RGBA tuple for a point in master coordinates, or None for transparent."""
    if not inside_rounded_square(x, y, size, size * 0.22):
        return None
    t = y / size
    base = tuple(round(BLUE[i] * (1 - t) + DARK[i] * t) for i in range(3))
    d = math.hypot(x - size / 2, y - size / 2)
    ring_outer, ring_inner = size * 0.36, size * 0.27
    dot = size * 0.13
    if ring_inner <= d <= ring_outer or d <= dot:
        return (*WHITE, 255)
    return (*base, 255)


def render_master() -> list:
    n = MASTER * SUPERSAMPLE
    pixels = []
    for py in range(MASTER):
        row = []
        for px in range(MASTER):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    x = (px * SUPERSAMPLE + sx + 0.5) / SUPERSAMPLE
                    y = (py * SUPERSAMPLE + sy + 0.5) / SUPERSAMPLE
                    c = shade(x, y, MASTER)
                    if c is not None:
                        acc[0] += c[0]
                        acc[1] += c[1]
                        acc[2] += c[2]
                        acc[3] += 255
            count = SUPERSAMPLE * SUPERSAMPLE
            alpha = acc[3] / count
            if alpha == 0:
                row.append((0, 0, 0, 0))
            else:
                covered = acc[3] / 255
                row.append((round(acc[0] / covered), round(acc[1] / covered), round(acc[2] / covered), round(alpha)))
        pixels.append(row)
    return pixels


def downscale(master: list, size: int) -> list:
    factor = MASTER / size
    out = []
    for y in range(size):
        row = []
        for x in range(size):
            x0, x1 = int(x * factor), int((x + 1) * factor)
            y0, y1 = int(y * factor), int((y + 1) * factor)
            r = g = b = a = 0.0
            n = 0
            for yy in range(y0, y1):
                for xx in range(x0, x1):
                    pr, pg, pb, pa = master[yy][xx]
                    r += pr * pa
                    g += pg * pa
                    b += pb * pa
                    a += pa
                    n += 1
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append((round(r / a), round(g / a), round(b / a), round(a / n)))
        out.append(row)
    return out


def png(pixels: list) -> bytes:
    size = len(pixels)
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag: bytes, data: bytes) -> bytes:
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def main() -> None:
    master = render_master()
    images = [(size, png(downscale(master, size) if size != MASTER else master)) for size in SIZES]
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    target = Path(__file__).resolve().parent.parent / "assets" / "constantproxy.ico"
    target.write_bytes(bytes(out))
    print(f"wrote {target} ({len(out)} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
