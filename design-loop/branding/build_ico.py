"""Build the Perch app icon (.ico) with Pillow only (no SVG rasterizer needed).

Draws the *cropped monocled face* (head + brow + monocle + eye + string) on a
rounded gradient tile by tracing the bird's actual head path, supersampled for
crisp edges, then writes a multi-resolution .ico. Including the head is what
makes it read as a monocle rather than a magnifying glass. Run: python build_ico.py
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(ROOT, "..", ".."))
APP_ICO = os.path.join(REPO, "src", "CmuxWin", "Assets", "cmux.ico")  # referenced by the app today
NAMED_ICO = os.path.join(ROOT, "perch.ico")
PREVIEW = os.path.join(ROOT, "perch-icon-256.png")

BODY   = (233, 241, 251, 255)   # light head  #E9F1FB
DARK   = (21, 35, 59, 255)      # eye + brow  #15233B
ACCENT = (118, 185, 237, 255)   # monocle + string  #76B9ED
TOP    = (34, 70, 111)          # tile gradient top  #22466F
BOT    = (16, 24, 42)           # tile gradient bottom #10182A
SS = 4

# --- bird head geometry in the bird's 24x24 coordinate space ---------------
BODY_START = (19.9, 10.0)
BODY_SEGS = [  # each: (c1x,c1y, c2x,c2y, ex,ey)
    (19.5, 7.9, 17.6, 6.3, 15.4, 6.5),
    (13.9, 6.5, 12.5, 6.8, 11.4, 7.4),
    (9.2, 7.6, 7.0, 5.9, 4.9, 6.3),
    (6.2, 7.8, 7.2, 9.7, 8.5, 11.1),
    (9.6, 12.7, 10.9, 14.4, 12.6, 14.4),
    (14.0, 14.4, 15.2, 13.9, 16.1, 13.0),
    (17.1, 12.4, 18.0, 11.9, 18.6, 11.2),
    (19.1, 10.9, 19.6, 10.6, 19.9, 10.0),
]
BROW = (15.9, 7.25, 16.6, 7.2, 17.4, 7.5, 18.05, 8.05)   # cubic
STRING = (17.6, 10.8, 17.8, 11.7, 17.5, 12.5, 16.9, 13.1)  # cubic
EYE_C, EYE_R = (17.0, 9.0), 0.95
MON_C, MON_R = (17.0, 9.0), 1.95

# crop window (bird coords) framing the head; mapped to the icon content square
BX0, BX1, BY0, BY1 = 8.6, 20.8, 3.9, 16.1


def cubic(p0, p1, p2, p3, n=28):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        x = mt**3*p0[0] + 3*mt**2*t*p1[0] + 3*mt*t**2*p2[0] + t**3*p3[0]
        y = mt**3*p0[1] + 3*mt**2*t*p1[1] + 3*mt*t**2*p2[1] + t**3*p3[1]
        pts.append((x, y))
    return pts


def draw_icon(size):
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for y in range(s):
        t = y / (s - 1)
        c = tuple(int(TOP[i] + (BOT[i]-TOP[i])*t) for i in range(3))
        d.line([(0, y), (s, y)], fill=c + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s-1, s-1], radius=int(s*0.225), fill=255)
    img.putalpha(mask)

    # map bird coords -> content square (centered, slight inset)
    cont = s * 0.86
    ox = (s - cont) / 2
    oy = (s - cont) / 2
    sx = cont / (BX1 - BX0)
    sy = cont / (BY1 - BY0)
    sc = (sx + sy) / 2

    def M(p):
        return (ox + (p[0]-BX0)*sx, oy + (p[1]-BY0)*sy)

    d = ImageDraw.Draw(img)
    # head polygon
    poly = [BODY_START]
    p = BODY_START
    for seg in BODY_SEGS:
        poly += cubic(p, (seg[0], seg[1]), (seg[2], seg[3]), (seg[4], seg[5]))
        p = (seg[4], seg[5])
    d.polygon([M(q) for q in poly], fill=BODY)
    # brow
    brow = cubic((BROW[0], BROW[1]), (BROW[2], BROW[3]), (BROW[4], BROW[5]), (BROW[6], BROW[7]))
    d.line([M(q) for q in brow], fill=DARK, width=max(SS, int(0.55*sc)), joint="curve")
    # eye
    ec = M(EYE_C); er = EYE_R*sc
    d.ellipse([ec[0]-er, ec[1]-er, ec[0]+er, ec[1]+er], fill=DARK)
    # monocle lens
    mc = M(MON_C); mr = MON_R*sc
    d.ellipse([mc[0]-mr, mc[1]-mr, mc[0]+mr, mc[1]+mr], outline=ACCENT, width=max(SS, int(0.62*sc)))
    # string
    st = cubic((STRING[0], STRING[1]), (STRING[2], STRING[3]), (STRING[4], STRING[5]), (STRING[6], STRING[7]))
    d.line([M(q) for q in st], fill=ACCENT, width=max(SS, int(0.5*sc)), joint="curve")

    return img.resize((size, size), Image.LANCZOS)


sizes = [256, 128, 64, 48, 32, 16]
imgs = [draw_icon(s) for s in sizes]
imgs[0].save(PREVIEW)
for path in (APP_ICO, NAMED_ICO):
    imgs[0].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[1:])
    print("wrote", path)
print("wrote", PREVIEW)
