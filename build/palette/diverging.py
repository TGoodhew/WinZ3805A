"""Derive the section 9.4.4 diverging ramp, once per surface.

Run:  python build/palette/diverging.py       (requires numpy)

Section 9.4.4 gives the diverging ramp as ONE row of five values - dark teal, teal, pale grey,
peach, burnt orange - with no per-theme variant, exactly as it gave the sequential ramp one column.
The sequential ramp was fixed for that; this is the same defect in the ramp beside it, and it is
worse in one respect. TrendChart draws the 1 PPS chart's per-column min/max whisker with three of
these five, and A THIN LINE IS THE CASE THE 3:1 FLOOR IS FOR. On the LIGHT card the three it draws
with measure 2.29:1, 1.24:1 and 1.91:1: the neutral whisker is very nearly invisible on the theme
it was drawn for.

The ordering is inverted on Dark for the same reason the sequential ramp's was. A diverging ramp
puts its neutral NEAR the surface and its extremes AWAY from it, so magnitude reads as prominence;
the pale grey neutral does that on a light card and does the exact opposite on a dark one, where it
is the boldest mark on the chart (10.99:1) while the strong ends recede to 1.36:1 and 2.36:1.

WHY THIS IS NOT sequential.py WITH FIVE STEPS. A sequential ramp walks one hue and is allowed to
vanish at its low end - receding IS its encoding. A diverging ramp carries TWO hues that have to
stay told apart at every magnitude, and a neutral that has to read as "nothing to see" while
remaining a visible line. Nothing here may recede: all five are data.

SOLVED FOR, in this order:

  1. EVERY STEP CLEARS 3:1 on its own card. These are 1 px strokes carrying a reading, which is
     section 9.4.5's meaningful-non-text case and the defect #177 fixed for the categorical
     palette. There is no exemption for the neutral: a column that straddles zero is a real
     reading about a real bucket, not a background.
  2. MONOTONE PROMINENCE PER ARM. Contrast against the theme's own card rises from the neutral
     outward, so a larger excursion draws a bolder line. This is the property the one-row ramp
     breaks on Dark.
  3. A VISIBLE LADDER. Each step out from the neutral is at least 1.5x the contrast of the one
     before it, so "further from zero" is legible as a step rather than inferred. Prominence is
     even between the arms by construction, the two sides being symmetric in L* about the neutral,
     which is what makes a 3 ns early column and a 3 ns late one look equally loud.
  4. THE ARMS STAY APART - 20 dE00 at matching magnitude under normal vision, deuteranopia and
     protanopia. Teal against orange is the classic pair for this and measures 29-44 today; the
     assertion is here so a future "warmer" teal cannot quietly give it up. Early and late are the
     two things this chart exists to tell apart, and they are on opposite sides of zero: a reader
     who cannot separate them has lost the chart, not a nicety.
  5. THE NEUTRAL IS ACTUALLY NEUTRAL - chroma at or below 6. It keeps the family's slight teal cast
     (the specification's own neutral is C 2.5 at hue 211) but must not read as a weak member of
     either arm, or "on both sides of zero within this bucket" becomes "slightly early".
  6. CLEAR OF SECTION 9.4.3 by 5.0 dE00. Not hypothetical: the specification's own
     WzDivergingNegativeBrush IS WzInfoBrush in Dark, 0.0 dE00 apart - the same collision the
     sequential ramp had at its step 4, in the same theme, found the same way. FIVE HERE RATHER
     THAN sequential.py's THREE, and the difference is measured rather than a preference: at 3.0
     this constraint BINDS on Dark, so the search parks the teal arm exactly as close to the info
     colour as it is allowed to be, which is not what the constraint means. Raising it to 5.0 costs
     0.3 dE00 of distance from the specification's own values and buys 2.6 of margin. Where a floor
     binds, the floor is the design.

WHAT IS NOT SOLVED FOR: the two arms' hues themselves. They are the specification's - teal near
208 degrees, orange near 51 - and this file holds them inside the ramp's own measured bands rather
than re-choosing them. Changing the pair is a design decision, not a search.

WHAT THE LIGHT RAMP LOSES, and it is forced. Its neutral was #DDE4E5, a near-white, and no
near-white clears 3:1 on a near-white card. The derived Light ramp is therefore darker throughout
and its neutral reads as a grey line rather than as an absence - which is the honest rendering,
since it is not an absence. #87 made the same trade for the categorical palette and said so.

THIS FILE IS SHARED, and must stay identical in both copies. build/palette/ is carried between
WinZ3805A and smartclock-monitor byte-for-byte, and the two copies being identical is what lets
either repository trust the other's colours. Anything said here has to be true in both, which is
why the destinations at the bottom are named in pairs. It reuses sequential.py's gamut helpers
rather than restating them, so that file is imported and must sit beside this one.
"""
import numpy as np, vec
from sequential import arr, hexes, in_gamut

# The two cards, from section 9.4.1: the stock card fill composited over the opaque page
# background, which is the same resolution build/Test-ContrastFloor.ps1 performs.
CARD = {'Light': np.array([251.0, 251.0, 251.0]), 'Dark': np.array([43.0, 43.0, 43.0])}

# Section 9.4.4's ramp, verbatim, in the order the tokens are declared:
# NegativeStrong, Negative, Zero, Positive, PositiveStrong.
SPEC = ['#08474D', '#3FB8C4', '#DDE4E5', '#F0A882', '#B23A00']

# Section 9.4.3's semantic colours, per theme, which a magnitude step must not be mistaken for.
SEMANTIC = {
    'Light': {'success': '#0F7B3C', 'caution': '#8A5300', 'critical': '#B22B2B',
              'info': '#0B6C74', 'neutral': '#616161'},
    'Dark': {'success': '#4CC38A', 'caution': '#F2B155', 'critical': '#FF6B6B',
             'info': '#3FB8C4', 'neutral': '#9A9A9A'},
}

FLOOR = 3.0            # constraint 1: section 9.4.5's floor for meaningful non-text
SEPARATION = 20.0      # constraint 4: dE00 between the arms at matching magnitude
CLEARANCE = 5.0        # constraint 6: dE00 from the nearest section 9.4.3 colour
NEUTRAL_CHROMA = 6.0   # constraint 5
ARM_CHROMA = 12.0      # constraint 7: an arm step still reads as its hue, not as near-black
LADDER = 1.5           # constraint 3: contrast multiplier between one magnitude and the next

_gamut_cache = {}


def gamut_chroma(lightness, hue):
    """The largest in-gamut chroma at this L* and hue. Bisection, memoised over the search grid."""
    key = (round(lightness, 3), round(hue, 3))
    if key not in _gamut_cache:
        lo, hi = 0.0, 140.0
        for _ in range(40):
            mid = (lo + hi) / 2.0
            if in_gamut(vec.lch2lab(lightness, mid, hue)):
                lo = mid
            else:
                hi = mid
        _gamut_cache[key] = lo
    return _gamut_cache[key]


def lch(colours):
    """(L*, C, h) of each colour, hue in degrees."""
    lab = vec.rgb2lab(colours)
    return (lab[:, 0],
            np.hypot(lab[:, 1], lab[:, 2]),
            np.degrees(np.arctan2(lab[:, 2], lab[:, 1])) % 360.0)


def spec_arms():
    """The specification ramp's two hue bands and its neutral, measured from the values themselves."""
    lightness, chroma, hue = lch(arr(SPEC))
    return ([hue[0], hue[1]], [hue[3], hue[4]], (lightness[2], chroma[2], hue[2]))


NEGATIVE_HUES, POSITIVE_HUES, NEUTRAL = spec_arms()


def build(theme, neutral_l, gap, k_negative, k_positive, hue_negative, hue_positive):
    """Five colours: L* evenly spaced outward from the neutral, chroma a fraction of the gamut.

    ``gap`` is one L* step and each arm is two steps long, so the ramp is symmetric in lightness
    about its neutral - the two sides of zero sit the same distance from "nothing to report", which
    is what lets an excursion's size be read without consulting the axis.

    CHROMA IS A FRACTION OF WHAT THE HUE CAN DO AT THAT LIGHTNESS, per arm, rather than a curve
    copied from the specification's four arm values. Two reasons. The gamut boundary already has
    the shape a colour family wants - it peaks in the mid lightnesses and falls away toward both
    black and white - so a constant fraction of it desaturates at both ends on its own, which is
    what makes the sequential ramp read as one family. And the specification's own arms sit at
    0.74 to 0.98 of that boundary, so the fraction is measured from them rather than invented;
    interpolating their four points instead extrapolates badly at the lightnesses this ramp needs,
    which produced a neon cyan at one end and a near-black maroon at the other.
    """
    away = -1.0 if vec.luminance(CARD[theme]) > 0.2 else 1.0
    out = []
    for step, hue, k in ((2, hue_negative, k_negative), (1, hue_negative, k_negative),
                         (0, NEUTRAL[2], None),
                         (1, hue_positive, k_positive), (2, hue_positive, k_positive)):
        lightness = neutral_l + away * gap * step
        chroma = NEUTRAL[1] if k is None else k * gamut_chroma(lightness, hue)
        out.append(vec.lab2rgb(vec.lch2lab(lightness, chroma, hue))[0])
    return np.array(out)


def steps_de00(colours):
    """The four adjacent steps, neutral outward: -1 to -2, 0 to -1, 0 to +1, +1 to +2."""
    lab = vec.rgb2lab(colours)
    return np.array([vec.ciede2000(lab[1], lab[0]), vec.ciede2000(lab[2], lab[1]),
                     vec.ciede2000(lab[2], lab[3]), vec.ciede2000(lab[3], lab[4])])


def arm_separation(colours):
    """dE00 between the arms at matching magnitude, worst of three vision models."""
    worst = float('inf')
    for kind in ('normal', 'deutan', 'protan'):
        seen = colours if kind == 'normal' else vec.simulate(colours, kind)
        lab = vec.rgb2lab(seen)
        worst = min(worst, float(vec.ciede2000(lab[1], lab[3])), float(vec.ciede2000(lab[0], lab[4])))
    return worst


def semantic_clearance(theme, colours):
    """dE00 from the nearest section 9.4.3 colour of this theme, over every step."""
    semantic = vec.rgb2lab(arr(list(SEMANTIC[theme].values())))
    ours = vec.rgb2lab(colours)
    return float(vec.ciede2000(ours[:, None, :], semantic[None, :, :]).min())


def prominence(theme, colours):
    """Contrast against this theme's card, in declaration order."""
    return vec.contrast(colours, CARD[theme])


def distance_from_spec(colours):
    """Mean dE00 between this ramp and the specification's, position by position."""
    lab = vec.rgb2lab(colours)
    spec = vec.rgb2lab(arr(SPEC))
    return float(np.mean([vec.ciede2000(lab[i], spec[i]) for i in range(len(SPEC))]))


def score(theme, colours):
    """Lower is better: how far this ramp sits from the specification's. inf if a constraint fails.

    THE OBJECTIVE IS "CHANGE AS LITTLE AS POSSIBLE", not "optimise some property". The five values
    in section 9.4.4 are a design somebody chose; what is wrong with them is that they were chosen
    for one surface and used on two. So the constraints below are non-negotiable and everything
    else stays as near the original as it can - which is why the teal arm remains recognisably the
    teal arm even on Dark, where it has to be lighter than the neutral rather than darker.

    An earlier version minimised the evenness of the four dE00 steps instead, as sequential.py
    does. It is the wrong objective here and it showed: the warm arm's hue has a far larger gamut
    than the teal one, so equalising colour differences drove both arms to the extremes of
    lightness - a 13.5:1 near-black maroon on Light - chasing a number no reader can see. Prominence
    is already even by construction, the arms being symmetric in L* about the neutral.
    """
    cr = prominence(theme, colours)
    if cr.min() < FLOOR:                                      # constraint 1
        return float('inf')
    if not (cr[1] >= LADDER * cr[2] and cr[0] >= LADDER * cr[1]):   # constraint 2
        return float('inf')
    if not (cr[3] >= LADDER * cr[2] and cr[4] >= LADDER * cr[3]):   # constraint 2
        return float('inf')
    chroma = lch(colours)[1]
    if chroma[2] > NEUTRAL_CHROMA:                            # constraint 5
        return float('inf')
    if min(chroma[0], chroma[1], chroma[3], chroma[4]) < ARM_CHROMA:  # constraint 7
        return float('inf')
    if semantic_clearance(theme, colours) < CLEARANCE:        # constraint 6
        return float('inf')
    if arm_separation(colours) < SEPARATION:                  # constraint 4
        return float('inf')
    if steps_de00(colours).min() <= 0:
        return float('inf')
    return distance_from_spec(colours)


def search(theme):
    """A coarse deterministic grid, and coarse on purpose.

    The space is five numbers wide and every one of them is a design quantity rather than a free
    parameter, so a finer grid buys a third decimal place on the evenness ratio and costs the
    ability to re-run this in a second while thinking. Hues are the specification arms' own two
    values each, held rather than re-chosen.
    """
    best, best_score = None, float('inf')
    for neutral_l in np.arange(36.0, 77.0, 2.0):
        for gap in np.arange(8.0, 27.0, 2.0):
            for k_negative in (0.55, 0.7, 0.85):
                for k_positive in (0.55, 0.7, 0.85):
                    for hue_negative in NEGATIVE_HUES:
                        for hue_positive in POSITIVE_HUES:
                            colours = build(theme, neutral_l, gap,
                                            k_negative, k_positive, hue_negative, hue_positive)
                            s = score(theme, colours)
                            if s < best_score:
                                best, best_score = colours, s
    return best, best_score


NAMES = ['-strong', '-', '0', '+', '+strong']


def report(title, theme, colours):
    cr = prominence(theme, colours)
    d = steps_de00(colours)
    lightness, chroma, _ = lch(colours)
    print(f"--- {title} ---")
    print("  hex           " + " ".join(f"{h:>9}" for h in hexes(colours)))
    print("  step          " + " ".join(f"{n:>9}" for n in NAMES))
    print("  contrast      " + " ".join(f"{v:9.2f}" for v in cr))
    print("  L*            " + " ".join(f"{v:9.1f}" for v in lightness))
    print("  chroma        " + " ".join(f"{v:9.1f}" for v in chroma))
    rising = cr[2] < cr[1] < cr[0] and cr[2] < cr[3] < cr[4]
    print(f"  prominence rises with magnitude on both arms: {'yes' if rising else 'NO'}"
          f"   neutral {cr[2]:.2f}:1   ends {cr[0]:.2f}:1 / {cr[4]:.2f}:1")
    print(f"  weakest step on the card: {cr.min():.2f}:1  (floor {FLOOR})")
    print(f"  step evenness: dE00 {d.min():.1f} to {d.max():.1f}, ratio {d.max()/d.min():.2f}x"
          "   (1.00x is perfectly even)")
    print(f"  arms apart at matching magnitude, worst of three vision models:"
          f" {arm_separation(colours):.1f} dE00")

    semantic = arr(list(SEMANTIC[theme].values()))
    names = list(SEMANTIC[theme])
    ds = vec.ciede2000(vec.rgb2lab(colours)[:, None, :], vec.rgb2lab(semantic)[None, :, :])
    k = np.unravel_index(np.argmin(ds), ds.shape)
    print(f"  closest approach to a section 9.4.3 colour: step {NAMES[k[0]]} vs {names[k[1]]}"
          f" = {ds.min():.1f} dE00")
    print(f"  neutral chroma: {chroma[2]:.1f}  (at or below {NEUTRAL_CHROMA} is 'actually neutral')")
    print()


if __name__ == '__main__':
    print("Section 9.4.4's diverging ramp, on each card it is drawn on.\n")

    derived = {}
    for theme in ('Light', 'Dark'):
        report(f"SPEC, verbatim, on the {theme} card (what ships)", theme, arr(SPEC))
        colours, s = search(theme)
        if colours is None:
            raise SystemExit(f"No ramp satisfies the constraints on {theme}. Loosen one deliberately"
                             " and say which, rather than widening the grid until something passes.")
        derived[theme] = colours
        report(f"DERIVED for {theme}", theme, colours)

    print("Put these in Themes/Colors.xaml as WzDivergingNegativeStrongBrush, WzDivergingNegativeBrush,")
    print("WzDivergingZeroBrush, WzDivergingPositiveBrush, WzDivergingPositiveStrongBrush,")
    print("and in themes/tokens.py as _DIVERGING:\n")
    for theme in ('Light', 'Dark'):
        print(f"    # {theme}")
        for name, value in zip(NAMES, hexes(derived[theme])):
            print(f'    "{value}",   # {name}')
