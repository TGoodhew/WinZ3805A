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
