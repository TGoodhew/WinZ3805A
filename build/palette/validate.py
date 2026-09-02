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
