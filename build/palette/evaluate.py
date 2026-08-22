"""Report the shipped §9.4.4 ramp against every constraint that bears on it, beside the one it
replaced. Run: python build/palette/evaluate.py

PROPOSED below is what is in Themes/Colors.xaml. Change it here when the palette changes, or
this stops describing anything."""
import numpy as np, vec

CARD = {'Light': np.array([251.0, 251.0, 251.0]), 'Dark': np.array([43.0, 43.0, 43.0])}
PAGE = {'Light': np.array([243.0, 243.0, 243.0]), 'Dark': np.array([32.0, 32.0, 32.0])}

CURRENT = {'Light': ['#0072B2','#D55E00','#009E73','#CC79A7','#56B4E9','#8C6D1F','#6E4B9E','#4A4A4A'],
           'Dark' : ['#56B4E9','#E69F00','#3FD9A8','#E0A3C8','#0072B2','#D9C36B','#B79CE0','#C4C4C4']}
PROPOSED = {'Light': ['#BD5572','#B4684E','#766110','#455530','#109180','#45849F','#085AA6','#4A4A4A'],
            'Dark' : ['#DD7F97','#E97E59','#EAC96A','#B5C79C','#07AE9A','#7CB9D6','#719BEA','#C4C4C4']}

SEMANTIC = {'Light': {'success':'#0F7B3C','caution':'#8A5300','critical':'#B22B2B','info':'#0B6C74'},
            'Dark' : {'success':'#4CC38A','caution':'#F2B155','critical':'#FF6B6B','info':'#3FB8C4'}}

def arr(hs): return np.array([[int(h[i:i+2],16) for i in (1,3,5)] for h in hs], float)

def pair_table(cols):
    out = {}
    for kind in (None, 'deutan', 'protan'):
        v = cols if kind is None else vec.simulate(cols, kind)
        L = vec.rgb2lab(v)
        d = vec.ciede2000(L[:, None, :], L[None, :, :])
        np.fill_diagonal(d, np.inf)
        out[kind or 'normal'] = d
    return out

def report(name, pal):
    print(f"########## {name} ##########")
    for theme in ('Light', 'Dark'):
        cols = arr(pal[theme])
        card = CARD[theme]
        print(f"\n--- {theme} (card {'#%02X%02X%02X' % tuple(int(v) for v in card)}) ---")
        cr = vec.contrast(cols, card)
        fails = [(i+1, pal[theme][i], cr[i]) for i in range(8) if cr[i] < 3.0]
        print("  contrast vs card:  " + "  ".join(f"{i+1}:{cr[i]:.2f}" for i in range(8)))
        print(f"  floor 3.0 -> {'ALL PASS, min %.2f' % cr.min() if not fails else 'FAIL: ' + ', '.join('series %d %s %.2f' % f for f in fails)}")

        d = pair_table(cols)
        for kind in ('normal', 'deutan', 'protan'):
            m = d[kind]
            i, j = np.unravel_index(np.argmin(m), m.shape)
            print(f"  {kind:7} min pairwise DE00 {m.min():5.1f}  (series {min(i,j)+1} vs {max(i,j)+1})")
        allmin = min(d[k].min() for k in d)
        print(f"  WORST across all three: {allmin:.1f}")

        sem = arr(list(SEMANTIC[theme].values()))
        names = list(SEMANTIC[theme])
        ls, lm = vec.rgb2lab(cols), vec.rgb2lab(sem)
        ds = vec.ciede2000(ls[:, None, :], lm[None, :, :])
        k = np.unravel_index(np.argmin(ds), ds.shape)
        print(f"  closest approach to a §9.4.3 semantic colour: series {k[0]+1} vs {names[k[1]]} = {ds.min():.1f}")

for name, pal in (('CURRENT §9.4.4', CURRENT), ('PROPOSED', PROPOSED)):
    report(name, pal)
    print()
