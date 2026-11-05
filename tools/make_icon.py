#!/usr/bin/env python3
"""Draws AVALightWave's icon (src/AVALightWave/Assets/AVALightWave.png): a spotlight's beam falling on a wave above a
perspective floor grid, on a dark rounded square. Standard library only; the same picture every time."""
import math, struct, zlib, os

N = 256
SS = 3  # supersamples per side

def clamp(v): return max(0.0, min(1.0, v))

def mix(a, b, t): return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))

def inside_rounded(x, y, r=46, m=8):
    lo, hi = m, N - m
    if x < lo or x > hi or y < lo or y > hi: return False
    cx = min(max(x, lo + r), hi - r); cy = min(max(y, lo + r), hi - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

def dist_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)

HORIZON = 150
def colour(x, y):
    if not inside_rounded(x, y): return None
    t = y / N
    c = mix((0.10, 0.12, 0.22), (0.03, 0.03, 0.07), t)
    # The beam: a soft cone from the lamp at the top right down to the wave's crest.
    lx, ly = 196, 34
    ang = math.atan2(y - ly, x - lx)
    centre = math.atan2(118 - ly, 120 - lx)
    spread = 0.30
    d = abs((ang - centre + math.pi) % (2 * math.pi) - math.pi)
    if d < spread and y > ly:
        k = (1 - d / spread) ** 1.6 * clamp(1 - math.hypot(x - lx, y - ly) / 230)
        c = mix(c, (1.0, 0.86, 0.55), 0.55 * k)
    # The floor grid below the horizon, in perspective toward a vanishing point.
    if y > HORIZON:
        depth = (y - HORIZON) / (N - HORIZON)
        vx = 128
        lines = []
        for i in range(-6, 7):
            # lines fanning from the vanishing point
            bx = vx + i * 46
            u = (x - vx) / max(1e-6, (y - HORIZON)) * (N - HORIZON)
            lines.append(abs(u - (bx - vx)) * depth)
        row = math.log(max(1e-6, depth)) / math.log(0.62)
        rowd = abs(row - round(row)) * (y - HORIZON) * 0.9
        g = min(min(lines), rowd)
        if g < 1.2:
            c = mix(c, (0.20, 0.75, 0.80), 0.55 * (1 - g / 1.2) * clamp(depth * 1.5))
    # The wave: a bright sine ribbon across the middle.
    wy = 120 + 26 * math.sin((x - 40) / 176 * 2 * math.pi) * (0.6 + 0.4 * x / N)
    dw = abs(y - wy)
    if 26 < x < 230 and dw < 9:
        k = 1 - dw / 9
        c = mix(c, (1.0, 0.70, 0.18), k ** 0.7)
        if dw < 2.2: c = mix(c, (1.0, 0.97, 0.85), 1 - dw / 2.2)
    # The lamp: a small housing at the beam's source.
    if dist_to_segment(x, y, 186, 24, 206, 44) < 9:
        c = (0.88, 0.89, 0.93)
    if math.hypot(x - lx, y - ly) < 5:
        c = (1.0, 0.95, 0.75)
    return c

rows = []
for y in range(N):
    row = bytearray([0])
    for x in range(N):
        acc = [0.0, 0.0, 0.0, 0.0]
        for sy in range(SS):
            for sx in range(SS):
                c = colour(x + (sx + 0.5) / SS, y + (sy + 0.5) / SS)
                if c is not None:
                    acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 1
        a = acc[3] / (SS * SS)
        if acc[3] > 0:
            row += bytes([int(255 * clamp(acc[0] / acc[3])), int(255 * clamp(acc[1] / acc[3])), int(255 * clamp(acc[2] / acc[3])), int(255 * a)])
        else:
            row += bytes([0, 0, 0, 0])
    rows.append(bytes(row))

def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", N, N, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "AVALightWave", "Assets", "AVALightWave.png")
with open(out, "wb") as f: f.write(png)
print("wrote", os.path.normpath(out))
