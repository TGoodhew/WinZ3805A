"""Render current vs proposed, in both themes, as normal vision and as a deuteranope
and protanope see them - drawn as the thin strokes TrendChart actually uses."""
import numpy as np, vec
from PIL import Image, ImageDraw, ImageFont

CURRENT = {'Light': ['#0072B2','#D55E00','#009E73','#CC79A7','#56B4E9','#8C6D1F','#6E4B9E','#4A4A4A'],
           'Dark' : ['#56B4E9','#E69F00','#3FD9A8','#E0A3C8','#0072B2','#D9C36B','#B79CE0','#C4C4C4']}
PROPOSED = {'Light': ['#BD5572','#B4684E','#766110','#455530','#109180','#45849F','#085AA6','#4A4A4A'],
            'Dark' : ['#DD7F97','#E97E59','#EAC96A','#B5C79C','#07AE9A','#7CB9D6','#719BEA','#C4C4C4']}
CARD = {'Light': (251,251,251), 'Dark': (43,43,43)}
INK  = {'Light': (30,30,30),    'Dark': (230,230,230)}

def sim(hexes, kind):
    a = np.array([[int(h[i:i+2],16) for i in (1,3,5)] for h in hexes], float)
    if kind != 'normal':
        a = vec.simulate(a, kind)
    return [tuple(int(round(min(255,max(0,v)))) for v in c) for c in a]

SW, SH, GAP, PAD = 96, 40, 8, 14
LBL = 116
try:
    F = ImageFont.truetype("segoeui.ttf", 15)
    FB = ImageFont.truetype("segoeuib.ttf", 17)
except OSError:
    F = FB = ImageFont.load_default()

def block(pal, theme, title):
    kinds = ('normal','deutan','protan')
    W = LBL + 8*(SW+GAP) + PAD*2
    H = PAD*2 + 30 + len(kinds)*(SH+18+GAP)
    im = Image.new('RGB', (W,H), CARD[theme]); d = ImageDraw.Draw(im)
    d.text((PAD, PAD), title, font=FB, fill=INK[theme])
    y = PAD + 30
    for kind in kinds:
        d.text((PAD, y+SH//2-9), {'normal':'normal','deutan':'deuteranopia','protan':'protanopia'}[kind],
               font=F, fill=INK[theme])
        for i, c in enumerate(sim(pal, kind)):
            x = LBL + i*(SW+GAP)
            d.rectangle([x, y, x+SW, y+SH//2-3], fill=c)                  # swatch
            d.line([x, y+SH-6, x+SW, y+SH-6], fill=c, width=2)            # thin stroke
            if kind == 'normal':
                d.text((x, y+SH+2), str(i+1), font=F, fill=INK[theme])
        y += SH+18+GAP
    return im

rows = []
for theme in ('Light','Dark'):
    rows.append(block(CURRENT[theme], theme, f'{theme} - CURRENT §9.4.4'))
    rows.append(block(PROPOSED[theme], theme, f'{theme} - PROPOSED'))
W = max(r.width for r in rows); H = sum(r.height for r in rows) + 3*6
out = Image.new('RGB',(W,H),(128,128,128)); y=0
for r in rows:
    out.paste(r,(0,y)); y += r.height + 6
out.save('palette-proposal.png')
print('palette-proposal.png', out.size)
