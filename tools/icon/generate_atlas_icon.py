#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Atlas - application icon / brand mark generator
================================================

Reproducible generator for all Atlas icon deliverables:

  * src/Atlas.App/Assets/atlas.ico             (10 frames: 16..256)
  * src/Atlas.App/Assets/atlas-mark.png        (64x64 transparent mark)
  * src/Atlas.App/Assets/atlas-mark-128.png    (128x128 transparent mark)
  * docs/branding/atlas-icon-preview.png       (contact sheet, dark + light)

Design: deep-sea navy rounded tile, a geometric "A" monogram wrapped by a
fan / orbit ring with a cyan sensor-progress arc.  Every icon frame is drawn
independently at its own resolution (not downscaled from 1024): small sizes
drop the orbit ring and the glow, enlarge and thicken the "A", so 16/24 px
stay recognisable.

Requires Pillow + numpy:
  C:\\Users\\Administrator\\.dsh\\dsh-runtimes\\dsh-primary-runtime\\dependencies\\python\\python.exe

Usage:
  python generate_atlas_icon.py [--root D:\\Github\\Atlas]
"""

import argparse
import io
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

# --------------------------------------------------------------------------- #
# palette / constants
# --------------------------------------------------------------------------- #
DESIGN = 1024.0                      # all geometry lives in a 1024x1024 space

NAVY_TOP = "#11203A"                 # tile gradient (spec)
NAVY_BOT = "#0B1220"
TILE_HIGHLIGHT = "#1E3A63"
BLUE_HI = "#63B3FF"                  # mark gradient (spec)
BLUE_LO = "#3D7BF7"
CYAN = "#46D6E6"                     # accent (spec)
RING_HI = "#5E86B8"
RING_LO = "#1B2B41"
RIM_HI = "#A8CAF2"
RIM_LO = "#3D597E"

TILE_INSET = 46.0
TILE_RADIUS = 210.0
A_APEX = (512.0, 274.0)
A_FOOT_L = (330.0, 754.0)
A_FOOT_R = (694.0, 754.0)
A_STROKE = 122.0
A_CROSS_Y = 642.0
A_CROSS_W = 92.0
A_CROSS_X0 = 396.0
A_CROSS_X1 = 628.0
RING_R = 366.0

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

DARK_BG = (10, 14, 20)               # #0A0E14
LIGHT_BG = (245, 247, 250)           # #F5F7FA


def hexc(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float32) / 255.0


# --------------------------------------------------------------------------- #
# canvas + raster helpers
# --------------------------------------------------------------------------- #
class Canvas:
    def __init__(self, size, ss):
        self.size = size
        self.S = size * ss
        self.k = self.S / DESIGN
        self.rgba = np.zeros((self.S, self.S, 4), np.float32)

    def p(self, v):
        return v * self.k


def blank_mask(c):
    return Image.new("L", (c.S, c.S), 0)


def mask_arr(m):
    return np.asarray(m, dtype=np.float32) / 255.0


def linear_grad(c, c0, c1, p0=(0, 0), p1=(DESIGN, DESIGN), dither=0.8, seed=7):
    """Two-colour linear gradient in design space, banding-dithered."""
    S = c.S
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    x = xx / S * DESIGN
    y = yy / S * DESIGN
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    d2 = dx * dx + dy * dy
    t = ((x - p0[0]) * dx + (y - p0[1]) * dy) / d2
    t = np.clip(t, 0.0, 1.0)[..., None]
    c0 = np.asarray(c0, np.float32)
    c1 = np.asarray(c1, np.float32)
    rgb = c0[None, None, :] * (1 - t) + c1[None, None, :] * t
    rng = np.random.default_rng(seed)
    rgb += rng.uniform(-dither, dither, (S, S, 1)).astype(np.float32) / 255.0
    return np.clip(rgb, 0, 1)


def radial_grad(c, c_in, c_out, center, radius, power=1.0):
    S = c.S
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    x = xx / S * DESIGN
    y = yy / S * DESIGN
    r = np.sqrt((x - center[0]) ** 2 + (y - center[1]) ** 2) / radius
    t = np.clip(r, 0, 1) ** power
    c_in = np.asarray(c_in, np.float32)
    c_out = np.asarray(c_out, np.float32)
    return c_in[None, None, :] * (1 - t[..., None]) + c_out[None, None, :] * t[..., None]


def composite(dst, src_rgb, src_a):
    """Straight-alpha source-over into dst float RGBA."""
    a = np.asarray(src_a, np.float32)
    if a.ndim == 2:
        a = a[..., None]
    a = np.clip(a, 0, 1)
    rgb = np.asarray(src_rgb, np.float32)
    if rgb.ndim == 1:
        rgb = rgb[None, None, :]
    if rgb.ndim == 2:
        rgb = rgb[..., None]
    if rgb.shape[2] == 1:
        rgb = np.broadcast_to(rgb, (*a.shape[:2], 3))
    da = dst[..., 3:4]
    oa = a + da * (1 - a)
    safe = np.maximum(oa, 1e-6)
    dst[..., :3] = np.clip((rgb * a + dst[..., :3] * da * (1 - a)) / safe, 0, 1)
    dst[..., 3:4] = np.clip(oa, 0, 1)
    return dst


def add_glow(c, mask, color, intensity, blur_design):
    """Soft additive glow, clipped to the already-painted area."""
    blurred = mask.filter(ImageFilter.GaussianBlur(max(c.p(blur_design), 0.6)))
    g = mask_arr(blurred)[..., None] * intensity * c.rgba[..., 3:4]
    c.rgba[..., :3] = np.clip(
        c.rgba[..., :3] + np.asarray(color, np.float32)[None, None, :] * g, 0, 1)


def drop_shadow(c, mask, color, intensity, offset_y, blur_design):
    """Dark blurred silhouette offset downwards (adds depth / separates the A)."""
    im = mask.filter(ImageFilter.GaussianBlur(max(c.p(blur_design), 0.6)))
    arr = np.asarray(im, np.float32) / 255.0
    shift = int(round(c.p(offset_y)))
    if shift:
        arr = np.roll(arr, shift, axis=0)
        arr[:shift, :] = 0.0
    a = arr[..., None] * intensity
    c.rgba[..., :3] = np.clip(c.rgba[..., :3] * (1 - a), 0, 1)
    return


def downsample(c):
    """Premultiplied-alpha LANCZOS downsample + straight-alpha restore."""
    prem = np.concatenate([c.rgba[..., :3] * c.rgba[..., 3:4], c.rgba[..., 3:4]], axis=2)
    chans = []
    for ch in range(4):
        im = Image.fromarray(prem[..., ch].astype(np.float32), mode="F")
        chans.append(np.asarray(im.resize((c.size, c.size), Image.LANCZOS), dtype=np.float32))
    rgb = np.stack(chans[:3], axis=2)
    a = np.clip(chans[3], 0, 1)
    rgb = np.where(a[..., None] > 1e-6, rgb / np.maximum(a[..., None], 1e-6), 0.0)
    return np.clip(np.concatenate([rgb, a[..., None]], axis=2), 0, 1)


def to_pil(rgba):
    return Image.fromarray((np.clip(rgba, 0, 1) * 255.0 + 0.5).astype(np.uint8), "RGBA")


# --------------------------------------------------------------------------- #
# shape masks
# --------------------------------------------------------------------------- #
def rounded_rect_mask(c, inset, radius):
    m = blank_mask(c)
    ImageDraw.Draw(m).rounded_rectangle(
        [c.p(inset), c.p(inset), c.p(DESIGN - inset), c.p(DESIGN - inset)],
        radius=c.p(radius), fill=255)
    return m


def rim_mask(c, inset, radius, width):
    outer = rounded_rect_mask(c, inset, radius)
    inner = rounded_rect_mask(c, inset + width, radius - width)
    diff = np.clip(np.asarray(outer, np.int16) - np.asarray(inner, np.int16), 0, 255)
    return Image.fromarray(diff.astype(np.uint8), "L")


def circle_mask(c, center, radius):
    m = blank_mask(c)
    r = c.p(radius)
    ImageDraw.Draw(m).ellipse(
        [c.p(center[0]) - r, c.p(center[1]) - r, c.p(center[0]) + r, c.p(center[1]) + r],
        fill=255)
    return m


def ring_mask(c, radius, width, center=(512.0, 512.0)):
    m = blank_mask(c)
    r = c.p(radius)
    ImageDraw.Draw(m).ellipse(
        [c.p(center[0]) - r, c.p(center[1]) - r, c.p(center[0]) + r, c.p(center[1]) + r],
        outline=255, width=max(1, int(round(c.p(width)))))
    return m


def arc_mask(c, radius, width, a0, a1, center=(512.0, 512.0)):
    m = blank_mask(c)
    r = c.p(radius)
    ImageDraw.Draw(m).arc(
        [c.p(center[0]) - r, c.p(center[1]) - r, c.p(center[0]) + r, c.p(center[1]) + r],
        a0, a1, fill=255, width=max(1, int(round(c.p(width)))))
    return m


def angular_fade(c, a0, a1, lo=0.03):
    """Alpha ramp along the arc, 0 at a0 -> 1 at a1 (angles in degrees)."""
    S = c.S
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    x = xx / S * DESIGN - 512.0
    y = yy / S * DESIGN - 512.0
    ang = np.degrees(np.arctan2(y, x))  # same convention as Pillow arcs (y down)
    t = np.clip((ang - a0) / (a1 - a0), 0.0, 1.0)
    return np.clip(lo + (1.0 - lo) * t, 0, 1)


def a_mask(c, k=0.82, center=(512.0, 512.0), stroke=A_STROKE, stroke_boost=1.0):
    """Geometric 'A': two round-capped legs meeting at the apex + crossbar."""
    m = blank_mask(c)
    d = ImageDraw.Draw(m)
    w = stroke * k * stroke_boost

    def tr(p):
        return (center[0] + (p[0] - 512.0) * k, center[1] + (p[1] - 512.0) * k)

    apex, fl, fr = tr(A_APEX), tr(A_FOOT_L), tr(A_FOOT_R)
    d.line([(c.p(fl[0]), c.p(fl[1])), (c.p(apex[0]), c.p(apex[1])),
            (c.p(fr[0]), c.p(fr[1]))], fill=255, width=int(round(c.p(w))), joint="curve")
    cap = c.p(w) / 2.0
    for p in (fl, fr):
        d.ellipse([c.p(p[0]) - cap, c.p(p[1]) - cap, c.p(p[0]) + cap, c.p(p[1]) + cap], fill=255)

    cy = center[1] + (A_CROSS_Y - center[1]) * k
    ch = A_CROSS_W * k * stroke_boost
    x0 = center[0] + (A_CROSS_X0 - center[0]) * k
    x1 = center[0] + (A_CROSS_X1 - center[0]) * k
    d.rounded_rectangle([c.p(x0), c.p(cy - ch / 2), c.p(x1), c.p(cy + ch / 2)],
                        radius=c.p(ch * 0.18), fill=255)
    return m


def tapered_a_mask(c, k=0.82, center=(512.0, 512.0), stroke_top=102.0, stroke_bot=136.0,
                   stroke_boost=1.0):
    """Alternative 'fan blade' A with tapered legs (kept for reference/tuning)."""
    apex = np.array(A_APEX, np.float32)
    fl = np.array(A_FOOT_L, np.float32)
    fr = np.array(A_FOOT_R, np.float32)
    st = stroke_top
    sb = stroke_bot * stroke_boost

    def leg(a, f):
        d = f - a
        n = np.array([-d[1], d[0]]) / float(np.hypot(*d))
        pts = []
        N = 8
        for i in range(N + 1):
            t = i / N
            p = a + d * t
            h = (st + (sb - st) * t) / 2.0
            pts.append(p + n * h)
        for i in range(N, -1, -1):
            t = i / N
            p = a + d * t
            h = (st + (sb - st) * t) / 2.0
            pts.append(p - n * h)
        return pts

    def tr(p):
        return (center[0] + (p[0] - 512.0) * k, center[1] + (p[1] - 512.0) * k)

    m = blank_mask(c)
    d = ImageDraw.Draw(m)
    for poly in (leg(apex, fl), leg(apex, fr)):
        d.polygon([(c.p(x), c.p(y)) for x, y in (tr(p) for p in poly)], fill=255)
    for p, r in ((apex, st / 2), (fl, sb / 2), (fr, sb / 2)):
        cx, cy = tr(p)
        rr = c.p(r * k)
        d.ellipse([c.p(cx) - rr, c.p(cy) - rr, c.p(cx) + rr, c.p(cy) + rr], fill=255)
    cy = center[1] + (A_CROSS_Y - center[1]) * k
    ch = A_CROSS_W * k
    x0 = center[0] + (A_CROSS_X0 - center[0]) * k
    x1 = center[0] + (A_CROSS_X1 - center[0]) * k
    d.rounded_rectangle([c.p(x0), c.p(cy - ch / 2), c.p(x1), c.p(cy + ch / 2)],
                        radius=c.p(ch * 0.18), fill=255)
    return m


# --------------------------------------------------------------------------- #
# render: app icon
# --------------------------------------------------------------------------- #
A_STYLE = "taper"                    # "round" | "taper"


def icon_params(size):
    """Size-specific simplification: bigger, bolder A + fewer details when small."""
    if size >= 96:
        return dict(k=0.82, boost=1.0, glow="full", ring=True, ring_w=24.0, shadow=True)
    if size >= 48:
        return dict(k=0.82, boost=1.0, glow="soft", ring=True, ring_w=24.0, shadow=True)
    if size >= 40:
        return dict(k=0.86, boost=1.01, glow="soft", ring=True, ring_w=40.0, shadow=False)
    if size >= 32:
        return dict(k=0.92, boost=1.03, glow="tight", ring=True, ring_w=46.0, shadow=False)
    if size >= 24:
        return dict(k=1.02, boost=1.06, glow="none", ring=False, ring_w=0.0, shadow=False)
    if size >= 20:
        return dict(k=1.10, boost=1.08, glow="none", ring=False, ring_w=0.0, shadow=False)
    return dict(k=1.16, boost=1.10, glow="none", ring=False, ring_w=0.0, shadow=False)


def render_icon(size):
    ss = max(8, min(64, int(round(2048 / size))))
    c = Canvas(size, ss)
    p = icon_params(size)

    # ---- tile ---------------------------------------------------------- #
    tile = rounded_rect_mask(c, TILE_INSET, TILE_RADIUS)
    tile_a = mask_arr(tile)[..., None]

    base = linear_grad(c, hexc(NAVY_TOP), hexc(NAVY_BOT), (0, 0), (0, DESIGN), seed=11)
    hl = radial_grad(c, hexc(TILE_HIGHLIGHT), hexc(NAVY_BOT), (455, 240), 780, power=1.4)
    base = np.clip(base * 0.74 + hl * 0.26, 0, 1)
    yy, xx = np.mgrid[0:c.S, 0:c.S].astype(np.float32)
    nx = xx / c.S * 2 - 1
    ny = yy / c.S * 2 - 1
    v = np.clip(np.sqrt(nx * nx + ny * ny) / 1.42, 0, 1)
    base = np.clip(base * (1.0 - 0.30 * v ** 2.2)[..., None], 0, 1)
    composite(c.rgba, base, tile_a)

    # top rim light (keeps the tile readable on near-black taskbars)
    rim_px = 1.0 if size >= 48 else 0.8
    rim_w = max(3.0, rim_px * DESIGN / size)
    rim = rim_mask(c, TILE_INSET, TILE_RADIUS, rim_w)
    rim_rgb = linear_grad(c, hexc(RIM_HI), hexc(RIM_LO), (0, 0), (0, DESIGN), seed=13)
    composite(c.rgba, rim_rgb, mask_arr(rim)[..., None] * 0.60)

    # ---- A silhouette -------------------------------------------------- #
    if A_STYLE == "taper":
        am = tapered_a_mask(c, k=p["k"], stroke_boost=p["boost"])
    else:
        am = a_mask(c, k=p["k"], stroke_boost=p["boost"])

    # ---- glow ---------------------------------------------------------- #
    if p["glow"] == "full":
        add_glow(c, am, hexc(BLUE_LO), 0.13, 44)
        add_glow(c, am, hexc(BLUE_HI), 0.30, 11)
    elif p["glow"] == "soft":
        add_glow(c, am, hexc(BLUE_LO), 0.10, 34)
        add_glow(c, am, hexc(BLUE_HI), 0.24, 9)
    elif p["glow"] == "tight":
        add_glow(c, am, hexc(BLUE_HI), 0.22, 8)

    # ---- orbit ring + cyan sensor arc --------------------------------- #
    if p["ring"]:
        om = ring_mask(c, RING_R, p["ring_w"])
        ring_rgb = linear_grad(c, hexc(RING_HI), hexc(RING_LO),
                               (512 - RING_R, 512 - RING_R),
                               (512 + RING_R, 512 + RING_R), seed=17)
        composite(c.rgba, ring_rgb, mask_arr(om)[..., None] * 0.92)

        a0, a1 = -150.0, -32.0
        arcm = arc_mask(c, RING_R, p["ring_w"], a0, a1)
        fade = angular_fade(c, a0, a1, lo=0.02)
        composite(c.rgba, hexc(CYAN), mask_arr(arcm)[..., None] * fade[..., None])
        # rounded leading end + sensor dot
        cw = p["ring_w"]
        for ang, rad in ((a1, cw / 2.0), (a1, cw * 0.85)):
            cc = (512 + RING_R * math.cos(math.radians(ang)),
                  512 + RING_R * math.sin(math.radians(ang)))
            dm = circle_mask(c, cc, rad)
            composite(c.rgba, hexc(CYAN), mask_arr(dm)[..., None])
            if rad > cw / 2.0 and p["glow"] == "full":
                add_glow(c, dm, hexc(CYAN), 0.45, 26)

    # ---- depth: dark halo just around the A (separates it from the ring) #
    if p["shadow"]:
        drop_shadow(c, am, hexc("#03060C"), 0.55, 8.0, 16.0)

    # ---- A body -------------------------------------------------------- #
    a_rgb = linear_grad(c, hexc(BLUE_HI), hexc(BLUE_LO), (340, 250), (740, 880), seed=19)
    composite(c.rgba, a_rgb, mask_arr(am))
    # soft top light on the A
    gloss = Image.new("L", (c.S, c.S), 0)
    ImageDraw.Draw(gloss).rectangle([0, 0, c.S, c.p(470)], fill=255)
    gloss = gloss.filter(ImageFilter.GaussianBlur(c.p(46)))
    composite(c.rgba, np.array([0.92, 0.97, 1.0], np.float32),
              mask_arr(gloss)[..., None] * mask_arr(am)[..., None] * 0.14)

    return to_pil(downsample(c))


# --------------------------------------------------------------------------- #
# render: standalone brand mark (no tile, transparent)
# --------------------------------------------------------------------------- #
def mark_params(size):
    if size >= 128:
        return dict(k=0.98, boost=1.0, ring_w=30.0, ring_r=424.0, glow=True)
    return dict(k=0.98, boost=1.0, ring_w=38.0, ring_r=424.0, glow=True)


def render_mark(size):
    ss = max(8, min(64, int(round(2048 / size))))
    c = Canvas(size, ss)
    p = mark_params(size)

    if A_STYLE == "taper":
        am = tapered_a_mask(c, k=p["k"], stroke_boost=p["boost"])
    else:
        am = a_mask(c, k=p["k"], stroke_boost=p["boost"])

    if p["glow"]:
        add_glow(c, am, hexc(BLUE_LO), 0.20, 18)
        add_glow(c, am, hexc(BLUE_HI), 0.18, 7)

    # orbit ring
    om = ring_mask(c, p["ring_r"], p["ring_w"])
    ring_rgb = linear_grad(c, hexc(RING_HI), hexc("#27425F"),
                           (512 - p["ring_r"], 512 - p["ring_r"]),
                           (512 + p["ring_r"], 512 + p["ring_r"]), seed=17)
    composite(c.rgba, ring_rgb, mask_arr(om)[..., None] * 0.95)

    a0, a1 = -146.0, -28.0
    arcm = arc_mask(c, p["ring_r"], p["ring_w"], a0, a1)
    fade = angular_fade(c, a0, a1, lo=0.02)
    composite(c.rgba, hexc(CYAN), mask_arr(arcm)[..., None] * fade[..., None])
    cw = p["ring_w"]
    for rad in (cw / 2.0, cw * 0.85):
        cc = (512 + p["ring_r"] * math.cos(math.radians(a1)),
              512 + p["ring_r"] * math.sin(math.radians(a1)))
        dm = circle_mask(c, cc, rad)
        composite(c.rgba, hexc(CYAN), mask_arr(dm)[..., None])
        if rad > cw / 2.0:
            add_glow(c, dm, hexc(CYAN), 0.40, 18)

    a_rgb = linear_grad(c, hexc(BLUE_HI), hexc(BLUE_LO), (340, 250), (740, 880), seed=19)
    composite(c.rgba, a_rgb, mask_arr(am))
    gloss = Image.new("L", (c.S, c.S), 0)
    ImageDraw.Draw(gloss).rectangle([0, 0, c.S, c.p(470)], fill=255)
    gloss = gloss.filter(ImageFilter.GaussianBlur(c.p(46)))
    composite(c.rgba, np.array([0.92, 0.97, 1.0], np.float32),
              mask_arr(gloss)[..., None] * mask_arr(am)[..., None] * 0.14)

    return to_pil(downsample(c))


# --------------------------------------------------------------------------- #
# ICO writer (PNG-compressed frames, manual ICONDIR/ICONDIRENTRY)
# --------------------------------------------------------------------------- #
def write_ico(path, images):
    frames = []
    for size, im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG", optimize=True)
        frames.append((size, buf.getvalue()))
    count = len(frames)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = b""
    blob = b""
    for size, png in frames:
        w = 0 if size == 256 else size
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(png), offset + len(blob))
        blob += png
    with open(path, "wb") as f:
        f.write(header + entries + blob)


# --------------------------------------------------------------------------- #
# preview contact sheet
# --------------------------------------------------------------------------- #
def font(size, bold=False):
    names = ["seguisb.ttf", "segoeuib.ttf"] if bold else ["segoeui.ttf"]
    for n in names:
        for base in (os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts"), ""):
            path = os.path.join(base, n) if base else n
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                pass
    return ImageFont.load_default()


def save_preview(path, renders, marks):
    W, H = 1460, 1270
    img = Image.new("RGB", (W, H), (17, 22, 30))
    d = ImageDraw.Draw(img)

    title = font(30, bold=True)
    sub = font(15)
    small = font(12)
    d.text((48, 26), "Atlas  -  app icon & brand mark", font=title, fill=(233, 238, 247))
    d.text((48, 66),
           "deep-sea navy tile  |  geometric A monogram  |  fan / orbit ring  |  cyan sensor arc",
           font=sub, fill=(130, 150, 178))

    def panel(y0, dark):
        bg = DARK_BG if dark else LIGHT_BG
        fg = (233, 238, 247) if dark else (30, 40, 55)
        lab = (138, 160, 188) if dark else (108, 124, 146)
        d.rounded_rectangle([40, y0, W - 40, y0 + 540], radius=18, fill=bg)
        d.text((72, y0 + 18), "Dark UI  /  #0A0E14" if dark else "Light UI  /  #F5F7FA",
               font=font(15, bold=True), fill=fg)

        # ---- 256 hero ---------------------------------------------------- #
        d.text((72, y0 + 50), "APP ICON", font=small, fill=lab)
        hero = renders[256]
        img.paste(hero, (72, y0 + 70), hero)
        d.text((72, y0 + 332), "256 px", font=small, fill=lab)

        # ---- actual taskbar sizes ---------------------------------------- #
        d.text((72, y0 + 372), "Taskbar / title-bar sizes  /  actual pixels", font=small, fill=lab)
        x = 72
        y = y0 + 392
        for s in (48, 32, 24, 16):
            im = renders[s]
            img.paste(im, (x, y + (48 - s)), im)
            d.text((x + max(0, s // 2 - 7), y + 52), str(s), font=small, fill=lab)
            x += 62

        # ---- zooms -------------------------------------------------------- #
        zx = 400
        d.text((zx, y0 + 372), "16 px  x8", font=small, fill=lab)
        z16 = renders[16].resize((128, 128), Image.NEAREST)
        img.paste(z16, (zx, y0 + 392), z16)
        d.text((zx + 152, y0 + 372), "32 px  x4", font=small, fill=lab)
        z32 = renders[32].resize((128, 128), Image.NEAREST)
        img.paste(z32, (zx + 152, y0 + 392), z32)

        # ---- brand mark column -------------------------------------------- #
        mx = 776
        d.text((mx, y0 + 50), "BRAND MARK", font=small, fill=lab)
        big = marks[128]
        img.paste(big, (mx, y0 + 70), big)
        d.text((mx, y0 + 70 + 128 + 6), "128 px  /  atlas-mark-128.png", font=small, fill=lab)

        x = mx + 150
        d.text((x, y0 + 56), "64 / 40 / 28 / 20 px  (atlas-mark.png)", font=small, fill=lab)
        for s in (64, 40, 28, 20):
            im = marks[s]
            img.paste(im, (x, y0 + 78 + (64 - s) // 2), im)
            d.text((x + max(0, s // 2 - 7), y0 + 148), str(s), font=small, fill=lab)
            x += s + 34

        # ---- in-app title bar mock ---------------------------------------- #
        ty = y0 + 232
        d.text((mx, ty), "In-app title bar  /  actual pixels", font=small, fill=lab)
        bar = Image.new("RGB", (420, 44), (12, 17, 24) if dark else (250, 251, 252))
        bd = ImageDraw.Draw(bar)
        edge = (36, 48, 68) if dark else (212, 220, 230)
        bd.rounded_rectangle([0, 0, 419, 43], radius=8, outline=edge, width=1)
        m20 = marks[20]
        bar.paste(m20, (14, 12), m20)
        bd.text((44, 13), "Atlas", font=font(15, bold=True), fill=fg)
        for cx in (372, 392, 412):
            bd.rectangle([cx - 4, 19, cx + 4, 27], outline=lab, width=1)
        img.paste(bar, (mx, ty + 20))

        # second mock: brand lockup with 28 px mark
        bar2 = Image.new("RGB", (420, 62), (12, 17, 24) if dark else (250, 251, 252))
        bd2 = ImageDraw.Draw(bar2)
        bd2.rounded_rectangle([0, 0, 419, 61], radius=10, outline=edge, width=1)
        m28 = marks[28]
        bar2.paste(m28, (16, 17), m28)
        bd2.text((58, 16), "Atlas", font=font(20, bold=True), fill=fg)
        bd2.text((58, 40), "fan control  /  sensors", font=font(10), fill=lab)
        img.paste(bar2, (mx, ty + 80))

        # ---- notes --------------------------------------------------------- #
        d.text((mx, y0 + 440),
               "16-24 px: ring and glow removed, A enlarged and thickened.",
               font=small, fill=lab)
        d.text((mx, y0 + 459),
               "32 px and up: full detail. Tile keeps a transparent margin.",
               font=small, fill=lab)

    panel(108, True)
    panel(668, False)
    d.text((48, H - 30),
           "Generated by tools/icon/generate_atlas_icon.py   |   atlas.ico (16-256)   |   atlas-mark.png / atlas-mark-128.png",
           font=small, fill=(110, 126, 148))
    img.save(path)


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main():
    ap = argparse.ArgumentParser(description="Generate Atlas icon assets")
    ap.add_argument("--root", default=None, help="repository root (default: ../../ from this script)")
    args = ap.parse_args()

    here = os.path.dirname(os.path.abspath(__file__))
    root = args.root or os.path.abspath(os.path.join(here, "..", ".."))
    assets = os.path.join(root, "src", "Atlas.App", "Assets")
    branding = os.path.join(root, "docs", "branding")
    os.makedirs(assets, exist_ok=True)
    os.makedirs(branding, exist_ok=True)

    print("rendering icon frames ...")
    renders = {s: render_icon(s) for s in ICO_SIZES}
    print("rendering brand marks ...")
    m64 = render_mark(64)
    m128 = render_mark(128)
    marks = dict(renders)
    marks.update({64: m64, 128: m128, 40: render_mark(40), 28: render_mark(28), 20: render_mark(20)})

    ico = os.path.join(assets, "atlas.ico")
    write_ico(ico, [(s, renders[s]) for s in ICO_SIZES])
    print("wrote", ico)

    m64.save(os.path.join(assets, "atlas-mark.png"))
    m128.save(os.path.join(assets, "atlas-mark-128.png"))
    print("wrote", os.path.join(assets, "atlas-mark.png"))
    print("wrote", os.path.join(assets, "atlas-mark-128.png"))

    prev = os.path.join(branding, "atlas-icon-preview.png")
    save_preview(prev, renders, marks)
    print("wrote", prev)


if __name__ == "__main__":
    main()

