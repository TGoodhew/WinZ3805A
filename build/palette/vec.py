"""Vectorised sRGB / Lab / CIEDE2000 / dichromat simulation, validated against cvd.py."""
import numpy as np

def lin(u):
    u = u / 255.0
    return np.where(u <= 0.04045, u / 12.92, ((u + 0.055) / 1.055) ** 2.4)

def unlin(v):
    v = np.clip(v, 0.0, 1.0)
    return np.where(v <= 0.0031308, 12.92 * v, 1.055 * v ** (1 / 2.4) - 0.055) * 255.0

def luminance(c):
    l = lin(c)
    return l[..., 0] * 0.2126 + l[..., 1] * 0.7152 + l[..., 2] * 0.0722

def contrast(c, other):
    a, b = luminance(c), luminance(other)
    hi, lo = np.maximum(a, b), np.minimum(a, b)
    return (hi + 0.05) / (lo + 0.05)

M = np.array([[0.4124564, 0.3575761, 0.1804375],
              [0.2126729, 0.7151522, 0.0721750],
              [0.0193339, 0.1191920, 0.9503041]])
WHITE = np.array([0.95047, 1.0, 1.08883])

def rgb2lab(c):
    xyz = lin(c) @ M.T
    t = xyz / WHITE
    f = np.where(t > 216 / 24389, np.cbrt(t), (841 / 108) * t + 4 / 29)
    return np.stack([116 * f[..., 1] - 16,
                     500 * (f[..., 0] - f[..., 1]),
                     200 * (f[..., 1] - f[..., 2])], axis=-1)

RGB2LMS = np.array([[17.8824, 43.5161, 4.11935],
                    [3.45565, 27.1554, 3.86714],
                    [0.0299566, 0.184309, 1.46709]])
LMS2RGB = np.array([[0.0809444479, -0.130504409, 0.116721066],
                    [-0.0102485335, 0.0540193266, -0.113614708],
                    [-0.000365296938, -0.00412161469, 0.693511405]])
PROTAN = np.array([[0.0, 2.02344, -2.52581], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]])
DEUTAN = np.array([[1.0, 0.0, 0.0], [0.494207, 0.0, 1.24827], [0.0, 0.0, 1.0]])

def simulate(c, kind):
    lms = lin(c) @ RGB2LMS.T
    lms = lms @ (PROTAN if kind == 'protan' else DEUTAN).T
    return unlin(lms @ LMS2RGB.T)

def ciede2000(lab1, lab2):
    L1, a1, b1 = lab1[..., 0], lab1[..., 1], lab1[..., 2]
    L2, a2, b2 = lab2[..., 0], lab2[..., 1], lab2[..., 2]
    C1, C2 = np.hypot(a1, b1), np.hypot(a2, b2)
    Cb = (C1 + C2) / 2
    G = 0.5 * (1 - np.sqrt(Cb ** 7 / (Cb ** 7 + 25.0 ** 7 + 1e-300)))
    a1p, a2p = (1 + G) * a1, (1 + G) * a2
    C1p, C2p = np.hypot(a1p, b1), np.hypot(a2p, b2)
    h1p = np.degrees(np.arctan2(b1, a1p)) % 360
    h2p = np.degrees(np.arctan2(b2, a2p)) % 360

    dLp = L2 - L1
    dCp = C2p - C1p
    d = h2p - h1p
    dhp = np.where(d > 180, d - 360, np.where(d < -180, d + 360, d))
    dhp = np.where(C1p * C2p == 0, 0.0, dhp)
    dHp = 2 * np.sqrt(C1p * C2p) * np.sin(np.radians(dhp) / 2)

    Lbp = (L1 + L2) / 2
    Cbp = (C1p + C2p) / 2
    s = h1p + h2p
    dd = np.abs(h1p - h2p)
    hbp = np.where(C1p * C2p == 0, s,
          np.where(dd <= 180, s / 2, np.where(s < 360, (s + 360) / 2, (s - 360) / 2)))

    T = (1 - 0.17 * np.cos(np.radians(hbp - 30)) + 0.24 * np.cos(np.radians(2 * hbp))
           + 0.32 * np.cos(np.radians(3 * hbp + 6)) - 0.20 * np.cos(np.radians(4 * hbp - 63)))
    dTheta = 30 * np.exp(-(((hbp - 275) / 25) ** 2))
    Rc = 2 * np.sqrt(Cbp ** 7 / (Cbp ** 7 + 25.0 ** 7 + 1e-300))
    Sl = 1 + (0.015 * (Lbp - 50) ** 2) / np.sqrt(20 + (Lbp - 50) ** 2)
    Sc = 1 + 0.045 * Cbp
    Sh = 1 + 0.015 * Cbp * T
    Rt = -np.sin(np.radians(2 * dTheta)) * Rc
    return np.sqrt((dLp / Sl) ** 2 + (dCp / Sc) ** 2 + (dHp / Sh) ** 2
                   + Rt * (dCp / Sc) * (dHp / Sh))

M_INV = np.linalg.inv(M)

def lab2rgb(lab):
    """Lab -> sRGB 0..255, with an in-gamut flag."""
    L, a, b = lab[..., 0], lab[..., 1], lab[..., 2]
    fy = (L + 16) / 116
    fx = fy + a / 500
    fz = fy - b / 200
    def g(f):
        return np.where(f ** 3 > 216 / 24389, f ** 3, (f - 4 / 29) * 108 / 841)
    xyz = np.stack([g(fx), g(fy), g(fz)], axis=-1) * WHITE
    rgbl = xyz @ M_INV.T
    inside = np.all((rgbl >= -0.001) & (rgbl <= 1.001), axis=-1)
    return unlin(rgbl), inside

def lch2lab(L, C, h):
    return np.stack([L, C * np.cos(np.radians(h)), C * np.sin(np.radians(h))], axis=-1)
