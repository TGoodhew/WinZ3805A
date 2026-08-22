"""Derive a replacement for the section 9.4.4 categorical palette.

Run:  python build/palette/derive.py       (requires numpy)

Solved for, all at once, because fixing separability or contrast alone re-breaks the other:

  1. SEPARATION. All 28 pairs, under normal vision AND deuteranopia AND protanopia, in BOTH
     themes. Adjacency is the wrong test - up to eight traces sit on one chart at once.
  2. CONTRAST. At least 3:1 against the WORST section 9.4.1 surface in the theme, not merely
     against the card. An earlier candidate cleared 3.50 on the card and 3.26 on the Light
     overlay, which is the sort of thing nobody wants to re-ask about later.
  3. SHARED HUE. A hue belongs to a series index in both themes, so series 5 is the teal one
     in Light and in Dark. A satellite should not change identity with the desktop theme.
  4. NAMEABLE HUES. A minimum hue gap. Pure max-min dE00 happily puts two browns and two
     purples in the ramp separated by lightness; that scores well and fails a person asked
     which trace is which.
  5. CLEAR OF SECTION 9.4.3, except the neutral slot. Series 8 is grey and WzNeutralBrush is
     grey, and both mean "nothing is being asserted". Requiring those two apart makes the
     constraint unsatisfiable - the first run returned no solution at all for that reason.

Bounds are deliberately tighter than the gamut allows. Unconstrained, eight colours meeting the
contrast floor reach roughly 21 dE00 in Light and 17 in Dark, so separability was never the
binding constraint - taste and the floor are. The instrument-palette bounds below give up about
half of that ceiling and are worth it.

THE WALK IS STOCHASTIC, so this does not print one canonical answer. It reports a solution at
the ceiling its constraints allow, and there are several: the shipped ramp and a run of this
file may differ on an entry that measures identically. Series 7 in Light is the live example -
#085AA6 shipped, #43547A comes out of seed 0, and the two are the same to two decimals on worst
pair and on worst contrast. Where that happens the choice is taste, and taste belongs to whoever
is reading section 9. Use evaluate.py to check what actually shipped; the gate is what enforces it.

This is a provenance record, not a gate. build/Test-SeriesSeparation.ps1 is the gate, and it
re-checks in PowerShell what this found here. Run validate.py first if this is ever picked up
again: it holds the colour maths against the eight figures published on issue #87, and a palette
tool that has not been checked against a known answer is a random number generator with a
citation.
"""

import numpy as np

import vec

# The worst surface in each theme, composited over the opaque page background. For a dark line
# the lowest contrast is against the darkest Light surface (the overlay, 243); for a light line
# it is against the lightest Dark surface (the card, 43).
WORST_SURFACE = {'Light': np.array([243.0, 243.0, 243.0]),
                 'Dark':  np.array([43.0, 43.0, 43.0])}
FLOOR = 3.0
MARGIN = 0.35

BOUNDS = {'Light': (np.arange(34, 57, 2.0), np.arange(24, 63, 3.0)),
          'Dark':  (np.arange(64, 85, 2.0), np.arange(24, 55, 3.0))}
HUES = np.arange(0, 360, 5.0)

MIN_HUE_GAP = 32.0
SEM_FLOOR = 10.0

# The neutral slot, fixed rather than searched: graphite on light, silver on dark, which is the
# shape the previous ramp had and worth keeping.
NEUTRAL = {'Light': np.array([74.0, 74.0, 74.0]),
           'Dark':  np.array([196.0, 196.0, 196.0])}

# Section 9.4.3, chromatic entries only - see the note on the neutral slot above.
SEMANTIC = {'Light': ['#0F7B3C', '#8A5300', '#B22B2B', '#0B6C74'],
            'Dark':  ['#4CC38A', '#F2B155', '#FF6B6B', '#3FB8C4']}


def _hexes(hs):
    return np.array([[int(h[i:i + 2], 16) for i in (1, 3, 5)] for h in hs], float)


SEM_LAB = {t: vec.rgb2lab(_hexes(hs)) for t, hs in SEMANTIC.items()}


def variants(theme):
    """Every (L*, C*) rendering of each hue that is in gamut and clears the floor."""
    out = {}
    lightnesses, chromas = BOUNDS[theme]
    for hue in HUES:
        grid_l, grid_c = np.meshgrid(lightnesses, chromas, indexing='ij')
        rgb, inside = vec.lab2rgb(
            vec.lch2lab(grid_l.ravel(), grid_c.ravel(), np.full(grid_l.size, hue)))
        ok = inside & (vec.contrast(rgb, WORST_SURFACE[theme]) >= FLOOR + MARGIN)
        if ok.any():
            out[hue] = np.clip(np.round(rgb[ok]), 0, 255)
    return out


LIGHT_VARIANTS = variants('Light')
DARK_VARIANTS = variants('Dark')
SHARED_HUES = sorted(set(LIGHT_VARIANTS) & set(DARK_VARIANTS))


def worst_pair(cols):
    """The smallest dE00 between any two of these, across the three vision models."""
    c = np.array(cols, dtype=float)
    worst = np.inf
    for kind in (None, 'deutan', 'protan'):
        view = c if kind is None else vec.simulate(c, kind)
        lab = vec.rgb2lab(view)
        d = vec.ciede2000(lab[:, None, :], lab[None, :, :])
        np.fill_diagonal(d, np.inf)
        worst = min(worst, d.min())
    return worst


def clear_of_semantics(cols, theme):
    lab = vec.rgb2lab(np.array(cols, dtype=float))
    d = vec.ciede2000(lab[:, None, :], SEM_LAB[theme][None, :, :])
    return d.min() >= SEM_FLOOR


def hues_nameable(state):
    hs = sorted(h for h, _, _ in state)
    for i, a in enumerate(hs):
        b = hs[(i + 1) % len(hs)]
        gap = (b - a) % 360
        if min(gap, 360 - gap) < MIN_HUE_GAP:
            return False
    return True


def build(state):
    """state is a list of (hue, light_variant_index, dark_variant_index)."""
    light = [LIGHT_VARIANTS[h][i % len(LIGHT_VARIANTS[h])] for h, i, _ in state]
    dark = [DARK_VARIANTS[h][j % len(DARK_VARIANTS[h])] for h, _, j in state]
    return light + [NEUTRAL['Light']], dark + [NEUTRAL['Dark']]


def score(state):
    if not hues_nameable(state):
        return -1.0
    light, dark = build(state)
    # the chromatic seven only; the neutral slot is exempt, see SEMANTIC above
    if not (clear_of_semantics(light[:-1], 'Light') and clear_of_semantics(dark[:-1], 'Dark')):
        return -1.0
    return min(worst_pair(light), worst_pair(dark))


def anneal(n=7, iters=12000, seed=0):
    """Hill-climb on max-min separation. Accepting ties matters: the plateaus are wide and a
    strictly-improving walk stalls on the first one it reaches."""
    rng = np.random.default_rng(seed)
    start = sorted(SHARED_HUES[k] for k in range(0, len(SHARED_HUES), max(1, len(SHARED_HUES) // n)))[:n]
    state = [(h, int(rng.integers(len(LIGHT_VARIANTS[h]))), int(rng.integers(len(DARK_VARIANTS[h]))))
             for h in start]
    best = score(state)

    for _ in range(iters):
        k = int(rng.integers(n))
        old = state[k]
        move = rng.integers(3)
        if move == 0:
            hue = SHARED_HUES[int(rng.integers(len(SHARED_HUES)))]
            if any(s[0] == hue for i, s in enumerate(state) if i != k):
                continue
            state[k] = (hue,
                        int(rng.integers(len(LIGHT_VARIANTS[hue]))),
                        int(rng.integers(len(DARK_VARIANTS[hue]))))
        elif move == 1:
            state[k] = (old[0], int(rng.integers(len(LIGHT_VARIANTS[old[0]]))), old[2])
        else:
            state[k] = (old[0], old[1], int(rng.integers(len(DARK_VARIANTS[old[0]]))))

        s = score(state)
        if s >= best:
            best = s
        else:
            state[k] = old

    return best, state


if __name__ == '__main__':
    def as_hex(cols):
        return [('#%02X%02X%02X' % tuple(int(v) for v in c)) for c in cols]

    print(f'{len(SHARED_HUES)} hues have a usable rendering in both themes.')

    top = (0.0, None)
    for seed in range(12):
        s, state = anneal(seed=seed)
        print(f'  seed {seed:2}: worst pairwise dE00 across both themes = {s:5.1f}')
        if s > top[0]:
            top = (s, state)

    best, state = top
    state = sorted(state, key=lambda t: t[0])
    light, dark = build(state)
    print(f'\nBEST {best:.1f}')
    print('  Light:', ' '.join(as_hex(light)))
    print('  Dark :', ' '.join(as_hex(dark)))
    print('  hues :', ' '.join(f'{h:.0f}' for h, _, _ in state), '+ neutral')
