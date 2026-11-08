#!/usr/bin/env python3
"""Generate the PWA icons (icon-192.png / icon-512.png) without Pillow.

Same art as web/favicon.svg: a rounded square with a diagonal gradient
(#4f6ef7 -> #7b5cf5 -> #f0653f) and three white rounded bars. 2x2
supersampling keeps the edges smooth enough at these sizes.
"""
import struct
import zlib
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WEB = os.path.join(HERE, "web")

# --- gradient stops (from favicon.svg) -------------------------------------
STOPS = [(0.0, (79, 110, 247)), (0.55, (123, 92, 245)), (1.0, (240, 101, 63))]


def gradient(t):
    for (t1, c1), (t2, c2) in zip(STOPS, STOPS[1:]):
        if t <= t2:
            f = 0 if t2 == t1 else (t - t1) / (t2 - t1)
            return tuple(round(a + (b - a) * f) for a, b in zip(c1, c2))
    return STOPS[-1][1]


def rounded_sq_dist(x, y, size, radius):
    """Signed distance to the rounded-square edge (<0 inside)."""
    cx = cy = size / 2
    qx = abs(x - cx) - (size / 2 - radius)
    qy = abs(y - cy) - (size / 2 - radius)
    dx = max(qx, 0.0)
    dy = max(qy, 0.0)
    return (dx * dx + dy * dy) ** 0.5 + min(max(qx, qy), 0.0) - radius


def capsule_dist(px, py, x1, y1, x2, y2, r):
    """Signed distance to a capsule (line segment with round caps)."""
    vx, vy = x2 - x1, y2 - y1
    wx, wy = px - x1, py - y1
    t = 0 if (vx * vx + vy * vy) == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / (vx * vx + vy * vy)))
    dx = wx - t * vx
    dy = wy - t * vy
    return (dx * dx + dy * dy) ** 0.5 - r


# bars: (cx, top_y, bottom_y, width) in favicon.svg coords (32x32 grid)
BARS = [(11.0, 13.0, 21.5, 2.6),
        (16.0, 17.0, 21.5, 2.6),
        (21.0, 10.5, 21.5, 2.6)]
HALF = 16.0  # svg centre


def render(size):
    k = size / 32.0
    radius = 8.0 * k
    bar_r = 1.3  # svg units — comparisons happen in svg space below
    ss = 2  # samples per axis
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG filter byte
        for px in range(size):
            cov_bar = 0
            for sy in range(ss):
                for sx in range(ss):
                    x = px + (sx + 0.5) / ss
                    y = py + (sy + 0.5) / ss
                    # background coverage
                    if rounded_sq_dist(x, y, size, radius) < 0:
                        # bar coverage (bars are in svg space, centred at HALF)
                        bx = (x / k - HALF) + 16.0
                        by = y / k
                        hit = False
                        for cx, top, bot, w in BARS:
                            if capsule_dist(bx, by, cx, top, cx, bot, bar_r) < 0:
                                hit = True
                                break
                        if hit:
                            cov_bar += 1
            a = cov_bar / (ss * ss)
            # background colour (opaque)
            t = (px + py) / (2.0 * (size - 1))
            r, g, b = gradient(t)
            if a > 0:
                r = round(r + (255 - r) * a)
                g = round(g + (255 - g) * a)
                b = round(b + (255 - b) * a)
            row += bytes((r, g, b, 255))
        rows.append(bytes(row))
    return b"".join(rows)


def write_png(path, size):
    raw = render(size)
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)
    print("wrote %s (%d bytes)" % (path, len(png)))


if __name__ == "__main__":
    for s in (192, 512):
        write_png(os.path.join(WEB, "icon-%d.png" % s), s)
