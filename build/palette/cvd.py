"""Colour tools: sRGB, CIEDE2000, WCAG contrast, and Vienot dichromat simulation."""
import math

# ---------- sRGB ----------
def hex2rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))

def rgb2hex(c):
    return '#%02X%02X%02X' % tuple(max(0, min(255, int(round(v)))) for v in c)

def lin(u):
    u /= 255.0
    return u / 12.92 if u <= 0.04045 else ((u + 0.055) / 1.055) ** 2.4

def unlin(v):
    v = max(0.0, min(1.0, v))
    s = 12.92 * v if v <= 0.0031308 else 1.055 * (v ** (1 / 2.4)) - 0.055
    return s * 255.0

def relative_luminance(c):
    r, g, b = (lin(v) for v in c)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b

def contrast(a, b):
    la, lb = relative_luminance(a), relative_luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)

def composite(over_hex_argb, under):
    """#AARRGGBB over an opaque colour."""
    h = over_hex_argb.lstrip('#')
    a = int(h[0:2], 16) / 255.0
    r, g, b = (int(h[i:i+2], 16) for i in (2, 4, 6))
    return tuple(round(o * a + u * (1 - a)) for o, u in ((r, under[0]), (g, under[1]), (b, under[2])))

# ---------- CIE Lab ----------
M_RGB2XYZ = ((0.4124564, 0.3575761, 0.1804375),
             (0.2126729, 0.7151522, 0.0721750),
             (0.0193339, 0.1191920, 0.9503041))
WHITE = (0.95047, 1.00000, 1.08883)

def rgb2xyz(c):
    r, g, b = (lin(v) for v in c)
    return tuple(m[0] * r + m[1] * g + m[2] * b for m in M_RGB2XYZ)

def xyz2lab(xyz):
    def f(t):
        return t ** (1 / 3) if t > 216 / 24389 else (841 / 108) * t + 4 / 29
    fx, fy, fz = (f(v / w) for v, w in zip(xyz, WHITE))
    return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))

def rgb2lab(c):
    return xyz2lab(rgb2xyz(c))

# ---------- CIEDE2000 ----------
def ciede2000(lab1, lab2):
    L1, a1, b1 = lab1
    L2, a2, b2 = lab2
    C1 = math.hypot(a1, b1)
    C2 = math.hypot(a2, b2)
    Cb = (C1 + C2) / 2
    G = 0.5 * (1 - math.sqrt(Cb ** 7 / (Cb ** 7 + 25 ** 7))) if Cb > 0 else 0.5 * (1 - 1)
    a1p, a2p = (1 + G) * a1, (1 + G) * a2
    C1p, C2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1p = math.degrees(math.atan2(b1, a1p)) % 360 if (b1 or a1p) else 0.0
    h2p = math.degrees(math.atan2(b2, a2p)) % 360 if (b2 or a2p) else 0.0

    dLp = L2 - L1
    dCp = C2p - C1p
    if C1p * C2p == 0:
        dhp = 0.0
    else:
        d = h2p - h1p
        dhp = d - 360 if d > 180 else (d + 360 if d < -180 else d)
    dHp = 2 * math.sqrt(C1p * C2p) * math.sin(math.radians(dhp) / 2)

    Lbp = (L1 + L2) / 2
    Cbp = (C1p + C2p) / 2
    if C1p * C2p == 0:
        hbp = h1p + h2p
    else:
        d = abs(h1p - h2p)
        s = h1p + h2p
        hbp = (s / 2) if d <= 180 else ((s + 360) / 2 if s < 360 else (s - 360) / 2)

    T = (1 - 0.17 * math.cos(math.radians(hbp - 30))
           + 0.24 * math.cos(math.radians(2 * hbp))
           + 0.32 * math.cos(math.radians(3 * hbp + 6))
           - 0.20 * math.cos(math.radians(4 * hbp - 63)))
    dTheta = 30 * math.exp(-(((hbp - 275) / 25) ** 2))
    Rc = 2 * math.sqrt(Cbp ** 7 / (Cbp ** 7 + 25 ** 7)) if Cbp > 0 else 0.0
    Sl = 1 + (0.015 * (Lbp - 50) ** 2) / math.sqrt(20 + (Lbp - 50) ** 2)
    Sc = 1 + 0.045 * Cbp
    Sh = 1 + 0.015 * Cbp * T
    Rt = -math.sin(math.radians(2 * dTheta)) * Rc

    return math.sqrt((dLp / Sl) ** 2 + (dCp / Sc) ** 2 + (dHp / Sh) ** 2
                     + Rt * (dCp / Sc) * (dHp / Sh))

def de00(c1, c2):
    return ciede2000(rgb2lab(c1), rgb2lab(c2))

# ---------- Dichromat simulation (Vienot, Brettel & Mollon 1999) ----------
# Linear-RGB -> LMS (Hunt-Pointer-Estevez normalised to D65), as used by Vienot et al.
RGB2LMS = ((17.8824,   43.5161,   4.11935),
           ( 3.45565,  27.1554,   3.86714),
           ( 0.0299566, 0.184309, 1.46709))
LMS2RGB = (( 0.0809444479, -0.130504409,  0.116721066),
           (-0.0102485335,  0.0540193266, -0.113614708),
           (-0.000365296938, -0.00412161469, 0.693511405))

def _mat(m, v):
    return tuple(sum(mi * vi for mi, vi in zip(row, v)) for row in m)

# Vienot's single-plane projections in LMS.
PROTAN = ((0.0, 2.02344, -2.52581), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
DEUTAN = ((1.0, 0.0, 0.0), (0.494207, 0.0, 1.24827), (0.0, 0.0, 1.0))

def simulate(c, kind):
    v = tuple(lin(x) for x in c)
    lms = _mat(RGB2LMS, v)
    lms = _mat(PROTAN if kind == 'protan' else DEUTAN, lms)
    rgb = _mat(LMS2RGB, lms)
    return tuple(unlin(x) for x in rgb)
