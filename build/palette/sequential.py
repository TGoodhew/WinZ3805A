"""Derive the section 9.4.4 sequential ramp for a DARK surface.

Run:  python build/palette/sequential.py       (requires numpy)

Section 9.4.4 gives the sequential ramp as ONE column of seven values, pale to dark teal, with no
per-theme variant - unlike the categorical ramp, which has a Light column and a Dark one. A
sequential ramp is read by lightness, so which end recedes depends on the surface it is drawn on,
and used verbatim on the Dark card the encoding is exactly inverted: the strongest satellite draws
#08474D at 1.13:1 and the weakest draws the brightest mark on the plot.

REVERSING THE SAME SEVEN VALUES fixes the inversion and was what shipped first. It is not enough,
and the reason is what this file exists to show. The spec ramp is not perceptually uniform - its
adjacent steps run 5.1 to 17.1 dE00, a 3.33x spread - and reversing cannot change that ratio,
because max/min does not care about order. What reversing changes is WHERE the coarse steps land.
On Light they sit at the dark end, which is the strong-signal end. Reversed onto Dark they sit at
the weak-signal end, so the ramp spends its resolution where the data matters least and leaves the
strong end its finest steps. Telling 45 dB-Hz from 50 is the job, and that is the comparison the
reversal makes hardest.

SOLVED FOR, in this order:

  1. MONOTONE PROMINENCE. Contrast against the theme's own card rises with the value. This is the
     property section 9.4.4 actually asks for, and it is the one the verbatim ramp breaks.
  2. EVEN PERCEPTUAL STEPS. Equal differences in signal strength should look equal, so the walk
     minimises the ratio between the largest and smallest adjacent dE00 rather than maximising
     separation. A sequential ramp is not a categorical one: neighbouring steps are MEANT to be
     similar, and section 9.4.4 says so.
  3. A VISIBLE FLOOR. The low end recedes; it does not disappear. The verbatim ramp's pale end is
     1.13:1 on the Light card and the reversal's dark end is 1.36:1 on Dark - both below the point
     where a mark can be found at all. 1.8:1 is asserted here, which still reads as "receding"
     beside a 12:1 top end.
  4. ONE HUE FAMILY, shared with Light - AND the specification's own chroma trajectory. Hue
     alone is not enough. The spec ramp holds hue at 208.5 degrees and peaks chroma in the MIDDLE
     (C 33.2 at L* 69), falling to 6.3 at the light end and 18.3 at the dark one: it desaturates
     toward white, which is what makes it read as one family. A first run here maximised chroma at
     every L* instead, met every numeric constraint, and produced a top end of #64F9FF - a neon
     cyan that is the wrong colour to put beside the specification's teal. The chroma target is
     now interpolated from the spec's own (L*, C) curve and clamped to the gamut.
  5. CLEAR OF SECTION 9.4.3, by 3.0 dE00. A strength step must not be mistakable for a severity
     colour. The teal band is clear of success green, caution amber and critical red by a wide
     margin; INFO TEAL is the one to watch, and it is not a hypothetical - the specification's own
     step 4 is #3FB8C4, which IS the Dark info colour, 0.0 dE00 apart. A mid-strength satellite
     draws exactly the info indicator. 3.0 is asserted here because 1-2 dE00 is around the
     just-noticeable difference and "not mistakable" has to mean more than "not identical".

WHAT IS NOT SOLVED FOR: dichromat separability between adjacent steps. Section 9.4.4 is explicit
that a sequential ramp's neighbours measuring low under simulated protanopia is correct rather than
a defect - they encode a magnitude, they are read by lightness, and the simulated ramp stays
monotonic. That is checked here rather than maximised.

THIS FILE IS SHARED, and must stay identical in both copies. It was written in the Python port,
where the defect was found; build/palette/ is carried between WinZ3805A and smartclock-monitor
byte-for-byte, and the two copies being identical is what lets either repository trust the other's
colours. Anything said here therefore has to be true in both, which is why the destinations below
are named in pairs. WinZ3805A #367 and smartclock-monitor #9 are the same defect and the same fix.
"""
import numpy as np, vec

# The Dark card, from section 9.4.1. The ramp is drawn on this and nothing else.
CARD_DARK = np.array([43.0, 43.0, 43.0])
CARD_LIGHT = np.array([251.0, 251.0, 251.0])

# Section 9.4.4's ramp, verbatim, low to high on a light surface.
SPEC = ['#DFF1F3', '#A8DDE3', '#6FC5CE', '#3FB8C4', '#189AA6', '#0B6C74', '#08474D']

# Section 9.4.3's semantic colours in Dark, which a strength step must not be mistaken for.
SEMANTIC_DARK = {'success': '#4CC38A', 'caution': '#F2B155', 'critical': '#FF6B6B',
                 'info': '#3FB8C4', 'neutral': '#9A9A9A'}

FLOOR = 1.8          # constraint 3: the weakest step must still be findable
CLEARANCE = 3.0      # constraint 5: dE00 from the nearest section 9.4.3 colour
STEPS = 7


def arr(hs):
    return np.array([[int(h[i:i + 2], 16) for i in (1, 3, 5)] for h in hs], float)


def hexes(cols):
    return ['#%02X%02X%02X' % tuple(int(round(max(0, min(255, v)))) for v in c) for c in cols]


def adjacent_de00(cols):
    L = vec.rgb2lab(cols)
    return np.array([vec.ciede2000(L[i], L[i + 1]) for i in range(len(cols) - 1)])


def in_gamut(lab):
    """True when this Lab colour has an sRGB representation.

    NOTE THE FLAG, not the returned bytes. vec.unlin CLIPS, so lab2rgb's first return value is
    always inside 0..255 and asking whether it is answers yes for every colour in the plane. An
    earlier version of this file did exactly that and therefore "clamped to the gamut" while doing
    nothing whatever. It changed nothing here - the specification's chroma curve never asks for an
    impossible colour, and the ramp above re-derives byte for byte either way - and it changed the
    answer immediately in diverging.py, which does ask. Corrected there, then here.
    """
    return bool(vec.lab2rgb(lab)[1])


def hue_band():
    """The specification ramp's own hue range, in LCh degrees. The derived ramp stays inside it."""
    L = vec.rgb2lab(arr(SPEC))
    h = np.degrees(np.arctan2(L[:, 2], L[:, 1])) % 360.0
    return float(h.min()), float(h.max())


def spec_chroma_curve():
    """The specification ramp's own chroma as a function of L*, ascending in L* for interpolation.

    This is what keeps the derived ramp in the same family rather than merely the same hue: the
    spec desaturates toward white, and a ramp that saturates toward white instead is a different
    design wearing the same hue angle.
    """
    L = vec.rgb2lab(arr(SPEC))
    lightness = L[:, 0]
    chroma = np.hypot(L[:, 1], L[:, 2])
    order = np.argsort(lightness)
    return lightness[order], chroma[order]


def gamut_chroma(lightness, hue):
    """The largest in-gamut chroma at this L* and hue. Bisection, 1e-2 tolerance."""
    lo, hi = 0.0, 140.0
    for _ in range(40):
        mid = (lo + hi) / 2.0
        if in_gamut(vec.lch2lab(lightness, mid, hue)):
            lo = mid
        else:
            hi = mid
    return lo


def build(low, high, hue, boost):
    """Seven steps, L* evenly spaced, chroma following the spec's curve scaled by ``boost``.

    ``boost`` exists because the Dark ramp occupies a different L* range from the Light one, and
    the spec's curve read literally there is a little flat. It scales the whole curve rather than
    reshaping it, so the family character survives.
    """
    curve_l, curve_c = spec_chroma_curve()
    out = []
    for i in range(STEPS):
        lightness = low + (high - low) * i / (STEPS - 1)
        target = float(np.interp(lightness, curve_l, curve_c)) * boost
        chroma = min(target, gamut_chroma(lightness, hue))
        out.append(vec.lab2rgb(vec.lch2lab(lightness, chroma, hue))[0])
    return np.array(out)


def score(cols):
    """Lower is better: how uneven the perceptual steps are. inf where a constraint fails."""
    cr = vec.contrast(cols, CARD_DARK)
    if not np.all(np.diff(cr) > 0):            # constraint 1
        return float('inf')
    if cr[0] < FLOOR:                          # constraint 3
        return float('inf')
    if semantic_clearance(cols) < CLEARANCE:   # constraint 5
        return float('inf')
    d = adjacent_de00(cols)
    if d.min() <= 0:
        return float('inf')
    return float(d.max() / d.min())            # constraint 2


def semantic_clearance(cols):
    """dE00 from the nearest section 9.4.3 colour, over every step."""
    sem = vec.rgb2lab(arr(list(SEMANTIC_DARK.values())))
    ours = vec.rgb2lab(cols)
    return float(vec.ciede2000(ours[:, None, :], sem[None, :, :]).min())


def search():
    """A small deterministic grid. The space is three numbers wide and does not need a walk."""
    lo_hue, hi_hue = hue_band()
    best, best_score = None, float('inf')
    for hue in np.arange(lo_hue, hi_hue + 0.001, 0.5):
        for low in np.arange(28.0, 46.0, 1.0):
            for high in np.arange(84.0, 96.0, 1.0):
                for boost in (0.9, 1.0, 1.1, 1.25, 1.4):
                    cols = build(low, high, hue, boost)
                    s = score(cols)
                    if s < best_score:
                        best, best_score = cols, s
    return best, best_score


def report(name, cols, card):
    cr = vec.contrast(cols, card)
    d = adjacent_de00(cols)
    L = vec.rgb2lab(cols)
    print(f"--- {name} ---")
    print("  hex           " + " ".join(hexes(cols)))
    print("  contrast      " + "  ".join(f"{v:5.2f}" for v in cr))
    print("  L*            " + "  ".join(f"{v:5.1f}" for v in L[:, 0]))
    print("  dE00 steps    " + "  ".join(f"{v:5.1f}" for v in d))
    print(f"  monotone prominence: {'yes' if np.all(np.diff(cr) > 0) else 'NO'}"
          f"   low end {cr[0]:.2f}:1   high end {cr[-1]:.2f}:1")
    print(f"  step evenness: dE00 {d.min():.1f} to {d.max():.1f}, ratio {d.max()/d.min():.2f}x"
          "   (1.00x is perfectly even)")

    sem = arr(list(SEMANTIC_DARK.values()))
    names = list(SEMANTIC_DARK)
    ls, lm = vec.rgb2lab(cols), vec.rgb2lab(sem)
    ds = vec.ciede2000(ls[:, None, :], lm[None, :, :])
    k = np.unravel_index(np.argmin(ds), ds.shape)
    print(f"  closest approach to a section 9.4.3 colour: step {k[0]+1} vs {names[k[1]]}"
          f" = {ds.min():.1f} dE00")

    for kind in ('deutan', 'protan'):
        sim_cr = vec.contrast(vec.simulate(cols, kind), card)
        print(f"  {kind:7} still monotonic: {'yes' if np.all(np.diff(sim_cr) > 0) else 'NO'}")
    print()


if __name__ == '__main__':
    print("Section 9.4.4's sequential ramp on the Dark card.\n")
    report("SPEC, verbatim (the inversion)", arr(SPEC), CARD_DARK)
    report("SPEC, reversed (what shipped first)", arr(list(reversed(SPEC))), CARD_DARK)

    derived, s = search()
    report("DERIVED for Dark", derived, CARD_DARK)

    print("For reference, the specification ramp on the surface it was drawn for:")
    report("SPEC, verbatim, on the LIGHT card", arr(SPEC), CARD_LIGHT)

    print("Put this in the Dark dictionary of Themes/Colors.xaml as WzSequential1..7Brush,")
    print("and in themes/tokens.py as _SEQUENTIAL_DARK:\n")
    for h in hexes(derived):
        print(f'    "{h}",')
