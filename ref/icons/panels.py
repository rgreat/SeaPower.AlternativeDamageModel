import math, cairosvg
DialW, DialH, cx, cy, R = 104, 56, 52, 52, 46
def P(r, a):
    a = math.radians(a); return "%.2f,%.2f" % (cx + r*math.sin(a), cy - r*math.cos(a))
def band(r0, r1, a0, a1):
    return "M%s A%s,%s 0 0 1 %s L%s A%s,%s 0 0 0 %s Z" % (P(r1,a0), r1, r1, P(r1,a1), P(r0,a1), r0, r0, P(r0,a0))
def dial(x, y, title, left, right, rng, danger, value, valtext, valcol):
    dz = danger / rng * 90; ang = -max(-1, min(1, value / rng)) * 90
    s = '<g transform="translate(%d,%d)">' % (x, y)
    s += '<text x="52" y="11" text-anchor="middle" font-size="11" fill="rgb(150,156,162)">%s</text>' % title
    s += '<g transform="translate(0,15)">'
    s += '<path d="M%s A%d,%d 0 0 1 %s Z" fill="rgb(36,40,46)"/>' % (P(R,-90), R, R, P(R,90))
    s += '<path d="%s" fill="rgb(200,62,54)"/>' % band(R-9, R-2, -90, -dz)
    s += '<path d="%s" fill="rgb(160,166,174)"/>' % band(R-9, R-2, -dz, dz)
    s += '<path d="%s" fill="rgb(200,62,54)"/>' % band(R-9, R-2, dz, 90)
    for k in range(-2, 3):
        s += '<path d="M%s L%s" stroke="rgb(214,218,222)" stroke-width="1.2"/>' % (P(R-14, k*45), P(R-9, k*45))
    s += '<path d="M52,52 L%s" stroke="white" stroke-width="2" transform="rotate(%.1f 52 52)"/>' % (P(R-4, 0), ang)
    s += '<circle cx="52" cy="52" r="3.5" fill="white"/></g>'
    s += '<text x="0" y="83" font-size="10" fill="rgb(150,156,162)">%s</text>' % left
    s += '<text x="104" y="83" text-anchor="end" font-size="10" fill="rgb(150,156,162)">%s</text>' % right
    s += '<rect x="34" y="72" width="36" height="15" fill="rgb(36,40,46)"/>'
    s += '<text x="52" y="84" text-anchor="middle" font-size="12" fill="%s">%s</text>' % (valcol, valtext)
    return s + '</g>'
W, H = 760, 150
bg, card, border = "rgb(26,28,32)", "rgb(30,33,38)", "rgb(58,62,68)"
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="DejaVu Sans">' % (W, H),
       '<rect width="%d" height="%d" fill="%s"/>' % (W, H, bg)]
# list/trim card
svg.append('<rect x="10" y="10" width="282" height="104" fill="%s" stroke="%s"/>' % (card, border))
svg.append(dial(18, 16, "LIST", "PORT", "STBD", 31.5, 31.5*0.75, 7.4, "7.4°", "rgb(214,218,222)"))
svg.append(dial(132, 16, "TRIM", "BOW", "STERN", 7, 7*0.75, -5.6, "5.6°", "rgb(200,62,54)"))
svg.append('<text x="262" y="27" text-anchor="middle" font-size="11" fill="rgb(150,156,162)">LEVEL</text>')
svg.append('<rect x="254" y="31" width="16" height="58" fill="rgb(36,40,46)" stroke="rgb(150,156,162)"/>')
svg.append('<rect x="255" y="32" width="14" height="14" fill="rgb(200,62,54)"/>')
svg.append('<rect x="255" y="%.1f" width="14" height="%.1f" fill="rgb(74,150,226)"/>' % (32 + 56*(1-14/28), 56*14/28))
svg.append('<text x="262" y="102" text-anchor="middle" font-size="10" fill="rgb(150,156,162)">14%</text>')
# hull card (3 rows)
x0 = 560
svg.append('<rect x="%d" y="10" width="190" height="86" fill="%s" stroke="%s"/>' % (x0, card, border))
for k, (lab, val, col) in enumerate([("Hull Damage:", "Moderate", "rgb(236,160,82)"), ("Flooding:", "Moderate", "rgb(236,160,82)"), ("Fires:", "Severe (2)", "rgb(222,82,62)")]):
    y = 34 + k*26
    svg.append('<text x="%d" y="%d" font-size="13" fill="rgb(214,218,222)">%s</text>' % (x0+8, y, lab))
    svg.append('<rect x="%d" y="%d" width="%d" height="19" fill="rgb(22,24,28)"/>' % (x0+182-len(val)*8.2-10, y-14, len(val)*8.2+10))
    svg.append('<text x="%d" y="%d" text-anchor="end" font-size="13" fill="%s">%s</text>' % (x0+182-5, y, col, val))
svg.append('<text x="300" y="140" font-size="11" fill="rgb(120,126,132)">left: under "Damage Control Teams" / "Teams Knocked Out"   right: the "Hull Damage" card</text>')
svg.append('</svg>')
cairosvg.svg2png(bytestring="".join(svg).encode(), write_to='panels.png', output_width=W*2, output_height=H*2)
