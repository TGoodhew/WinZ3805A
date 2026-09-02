from cvd import *

LIGHT = ['#0072B2','#D55E00','#009E73','#CC79A7','#56B4E9','#8C6D1F','#6E4B9E','#4A4A4A']
DARK  = ['#56B4E9','#E69F00','#3FD9A8','#E0A3C8','#0072B2','#D9C36B','#B79CE0','#C4C4C4']

def pairs(pal, kind):
    sim = [simulate(hex2rgb(h), kind) for h in pal]
    out = {}
    for i in range(len(pal)):
        for j in range(i+1, len(pal)):
            out[(i+1, j+1)] = de00(sim[i], sim[j])
    return out

print("#87 reported these four collapsed pairs. Reproducing them is the check on this tool.\n")
claims = [("Light", "deutan", (1,7), 4.6), ("Dark","deutan",(1,7),3.1),
          ("Light","protan",(2,6),3.0),   ("Dark","protan",(1,7),4.0)]
for name, kind, pair, claimed in claims:
    pal = LIGHT if name == "Light" else DARK
    got = pairs(pal, kind)[pair]
    mark = "OK " if abs(got - claimed) < 0.6 else "!! "
    print(f"  {mark}{name:5} {kind:6} series {pair[0]} vs {pair[1]}: issue says {claimed}, tool says {got:.1f}")

print("\nAdjacent-only minima (#87's table of what the spec's own weaker test reports):")
for name, pal in (("Light", LIGHT), ("Dark", DARK)):
    for kind, claimed in (("deutan", 16.6 if name=="Light" else 16.5), ("protan", 14.4 if name=="Light" else 19.7)):
        p = pairs(pal, kind)
        adj = min(p[(i, i+1)] for i in range(1, 8))
        mark = "OK " if abs(adj - claimed) < 0.8 else "!! "
        print(f"  {mark}{name:5} {kind:6} min adjacent: issue says {claimed}, tool says {adj:.1f}")


# ---------- section 9.4.4's sequential ramp on the Dark card ----------
#
# The categorical checks above reproduce #87's published figures. These reproduce the ones
# published on the sequential ramp's own issue and written into section 9.4.4 (docs/requirements.md
# here, docs/palette-figures.md in the port), for the ramp derived by sequential.py. Same purpose:
# the tool is only worth trusting if it still says what it said when the colours were chosen.
#
# NOTE de00() takes RGB and converts internally. Passing it Lab double-converts and under-reports
# by roughly threefold, silently. That is how the first draft of this block "measured" the spec
# ramp's spread as 2.23x when it is 3.33x, and the wrong number reached two docstrings before
# these checks disagreed with sequential.py and caught it.

DARK_CARD = hex2rgb('#2B2B2B')
SEQ_SPEC = ['#DFF1F3','#A8DDE3','#6FC5CE','#3FB8C4','#189AA6','#0B6C74','#08474D']
SEQ_DARK = ['#216D74','#2A828A','#3398A1','#48ADB7','#70C1C9','#9BD3D9','#C5E6E9']
INFO_DARK = '#3FB8C4'

def steps(pal):
    rgb = [hex2rgb(h) for h in pal]
    return [de00(rgb[i], rgb[i+1]) for i in range(len(rgb)-1)]

def evenness(pal):
    s = steps(pal)
    return max(s) / min(s)

def check(label, got, claimed, tol):
    mark = "OK " if abs(got - claimed) < tol else "!! "
    print(f"  {mark}{label}: doc says {claimed}, tool says {got:.2f}")

print("\nSequential ramp, Dark card. Section 9.4.4 is the source of these numbers.\n")

# Reversal cannot change this ratio - max/min is order-independent - which is the point. What it
# changes is WHERE the coarse steps land: the strong-signal end on Light, the weak end on Dark.
check("spec ramp step evenness (dE00 max/min)", evenness(SEQ_SPEC), 3.33, 0.05)
check("reversed ramp step evenness, necessarily the same", evenness(list(reversed(SEQ_SPEC))), 3.33, 0.05)
check("derived ramp step evenness", evenness(SEQ_DARK), 1.20, 0.05)

sp = steps(SEQ_SPEC)
print(f"  ..  spec adjacent steps, light to dark: {' '.join('%.1f' % v for v in sp)}")
print(f"  ..  coarsest step is number {sp.index(max(sp))+1} of 6, at the dark end")

check("spec step 4 vs Dark info, dE00", de00(hex2rgb(SEQ_SPEC[3]), hex2rgb(INFO_DARK)), 0.0, 0.05)
nearest = min(de00(hex2rgb(h), hex2rgb(INFO_DARK)) for h in SEQ_DARK)
check("derived ramp nearest approach to info, dE00", nearest, 3.2, 0.15)

check("reversed weakest step on the Dark card", contrast(hex2rgb(list(reversed(SEQ_SPEC))[0]), DARK_CARD), 1.36, 0.02)
check("derived weakest step on the Dark card", contrast(hex2rgb(SEQ_DARK[0]), DARK_CARD), 2.36, 0.02)
check("derived strongest step on the Dark card", contrast(hex2rgb(SEQ_DARK[-1]), DARK_CARD), 10.68, 0.03)

cr = [contrast(hex2rgb(h), DARK_CARD) for h in SEQ_DARK]
rising = all(b > a for a, b in zip(cr, cr[1:]))
print(f"  {'OK ' if rising else '!! '}derived ramp prominence rises with the value: {rising}")

verbatim = [contrast(hex2rgb(h), DARK_CARD) for h in SEQ_SPEC]
inverted = all(b < a for a, b in zip(verbatim, verbatim[1:]))
print(f"  {'OK ' if inverted else '!! '}spec ramp used verbatim on Dark is inverted: {inverted}")


# ---------- section 9.4.4's diverging ramp, on both cards ----------
#
# The ramp above is read from one end; this one is read outward from its middle, along two arms,
# and it is drawn as the 1 PPS chart's per-column whisker. Same purpose as every check in this
# file: reproduce the figures the colours were chosen against, so the tool has to keep agreeing
# with the documents rather than the documents trusting the tool.

LIGHT_CARD = hex2rgb('#FBFBFB')
DIV_SPEC = ['#08474D', '#3FB8C4', '#DDE4E5', '#F0A882', '#B23A00']
DIV_LIGHT = ['#1D5D64', '#2A7D85', '#8B9293', '#C24D19', '#93370F']
DIV_DARK = ['#90DEE7', '#75B6BD', '#818788', '#EB976A', '#F3C9B4']
INFO = {'Light': '#0B6C74', 'Dark': '#3FB8C4'}
SEMANTIC = {'Light': ['#0F7B3C', '#8A5300', '#B22B2B', '#0B6C74', '#616161'],
            'Dark': ['#4CC38A', '#F2B155', '#FF6B6B', '#3FB8C4', '#9A9A9A']}
CARDS = {'Light': LIGHT_CARD, 'Dark': DARK_CARD}


def div_contrasts(pal, card):
    return [contrast(hex2rgb(h), card) for h in pal]


def outward_rising(cr):
    """Prominence rises from the neutral outward along both arms: [2] < [1] < [0], [2] < [3] < [4]."""
    return cr[2] < cr[1] < cr[0] and cr[2] < cr[3] < cr[4]


def arms_apart(pal):
    """dE00 between the arms at matching magnitude, worst of three vision models."""
    worst = float('inf')
    for kind in ('normal', 'deutan', 'protan'):
        seen = [hex2rgb(h) if kind == 'normal' else simulate(hex2rgb(h), kind) for h in pal]
        worst = min(worst, de00(seen[1], seen[3]), de00(seen[0], seen[4]))
    return worst


def nearest_semantic(pal, theme):
    return min(de00(hex2rgb(a), hex2rgb(b)) for a in pal for b in SEMANTIC[theme])


print("\nDiverging ramp, both cards. Section 9.4.4 is the source of these numbers.\n")

# The defect: one row of five values used on two surfaces. It is not that the values are bad, it
# is that a ramp is read against the surface it is drawn on, and this one was drawn against white.
for theme, claimed in (("Light", [10.05, 2.29, 1.24, 1.91, 5.80]),
                       ("Dark", [1.36, 5.98, 10.99, 7.17, 2.36])):
    got = div_contrasts(DIV_SPEC, CARDS[theme])
    ok = all(abs(g - c) < 0.05 for g, c in zip(got, claimed))
    print(f"  {'OK ' if ok else '!! '}spec ramp on the {theme:5} card: doc says {claimed},"
          f" tool says {[round(v, 2) for v in got]}")

print(f"  {'OK ' if not outward_rising(div_contrasts(DIV_SPEC, CARDS['Dark'])) else '!! '}"
      "spec ramp on Dark runs backwards (neutral boldest, extremes faintest): "
      f"{not outward_rising(div_contrasts(DIV_SPEC, CARDS['Dark']))}")
check("spec WzDivergingNegativeBrush vs Dark info, dE00", de00(hex2rgb(DIV_SPEC[1]), hex2rgb(INFO['Dark'])), 0.0, 0.05)

for theme, pal, claimed in (("Light", DIV_LIGHT, [7.24, 4.66, 3.06, 4.66, 7.24]),
                            ("Dark", DIV_DARK, [9.31, 6.17, 3.90, 6.17, 9.31])):
    got = div_contrasts(pal, CARDS[theme])
    ok = all(abs(g - c) < 0.05 for g, c in zip(got, claimed))
    print(f"  {'OK ' if ok else '!! '}derived {theme:5} ramp contrast: doc says {claimed},"
          f" tool says {[round(v, 2) for v in got]}")
    rising = outward_rising(got)
    print(f"  {'OK ' if rising else '!! '}derived {theme:5} prominence rises outward on both arms: {rising}")
    ladder = min(got[1] / got[2], got[0] / got[1], got[3] / got[2], got[4] / got[3])
    print(f"  {'OK ' if ladder >= 1.5 else '!! '}derived {theme:5} ladder: every step at least 1.5x"
          f" the last, smallest {ladder:.2f}x")

check("derived Light arms apart, worst of three vision models", arms_apart(DIV_LIGHT), 28.5, 0.15)
check("derived Dark  arms apart, worst of three vision models", arms_apart(DIV_DARK), 20.2, 0.15)
check("derived Light nearest section 9.4.3 colour, dE00", nearest_semantic(DIV_LIGHT, 'Light'), 5.3, 0.15)
check("derived Dark  nearest section 9.4.3 colour, dE00", nearest_semantic(DIV_DARK, 'Dark'), 5.6, 0.15)
