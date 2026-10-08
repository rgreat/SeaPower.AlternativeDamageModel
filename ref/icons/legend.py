import json, cairosvg
icons = json.load(open('icons.json'))
def g(name, x, y, scale=1):
    out = '<g transform="translate(%s,%s) scale(%s)">' % (x, y, scale)
    for d, col in icons[name]:
        rule = 'evenodd' if d.startswith('F0') else 'nonzero'
        out += '<path d="%s" fill="rgb(%d,%d,%d)" fill-rule="%s"/>' % (d.replace('F0 ', ''), *col, rule)
    return out + '</g>'
INK = "rgb(24,26,30)"
# ---- the picture, 200 x 200 (same path data goes into Noesis)
PIC = {
 'port':  ("M100,6 A94,94 0 0 0 100,194 Z", "rgb(200,62,54)"),
 'star':  ("M100,6 A94,94 0 0 1 100,194 Z", "rgb(46,160,86)"),
 'hull':  ("M100,14 C107,28 113,50 114,80 L114,168 C114,177 110,183 104,186 L96,186 C90,183 86,177 86,168 L86,80 C87,50 93,28 100,14 Z", "rgb(222,225,230)"),
 'deck':  ("M100,50 A6,6 0 1 1 99.99,50 Z M93,72 L107,72 L107,98 L93,98 Z M100,104 A4.5,7.5 0 1 1 99.99,104 Z M94,124 L106,124 L106,146 L94,146 Z M100,156 A6,6 0 1 1 99.99,156 Z", "rgb(96,104,116)"),
}
def picture(x, y):
    s = '<g transform="translate(%d,%d)">' % (x, y)
    s += '<path d="%s" fill="%s"/>' % PIC['port']
    s += '<path d="%s" fill="%s"/>' % PIC['star']
    s += '<circle cx="100" cy="100" r="94" fill="none" stroke="%s" stroke-width="2"/>' % INK
    s += '<line x1="100" y1="6" x2="100" y2="194" stroke="%s" stroke-width="1"/>' % INK
    s += '<path d="%s" fill="%s" stroke="%s" stroke-width="1.4"/>' % (PIC['hull'][0], PIC['hull'][1], INK)
    s += '<path d="%s" fill="%s"/>' % PIC['deck']
    # labels centred in their half (between the circle and the hull)
    s += '<text x="48" y="105" text-anchor="middle" font-size="15" font-weight="bold" fill="white">Port</text>'
    s += '<text x="153" y="105" text-anchor="middle" font-size="12.5" font-weight="bold" fill="white">Starboard</text>'
    s += '<text x="100" y="0" text-anchor="middle" font-size="0">.</text>'
    return s + '</g>'
W, H = 640, 300
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="DejaVu Sans">' % (W, H),
       '<rect width="%d" height="%d" fill="rgb(30,33,38)" stroke="rgb(70,76,86)" stroke-width="2"/>' % (W, H),
       picture(16, 40)]
x0, y = 240, 30
WH, GR = "rgb(214,218,222)", "rgb(170,176,184)"
for t, c, z in [("Each section has a port and a starboard compartment:", WH, 14.5),
                ("Aft   41% / 47%  - hull integrity, port / starboard", WH, 13.5),
                ("Flooding  23% / 5%  - water in the compartment", WH, 13.5),
                ("Fire  64%  - fire, % of its maximum (one per section)", WH, 13.5),
                ("Red arrows: getting worse, green: getting better; two: fast.", GR, 12.5),
                ("One icon = one damage control team:", WH, 14.5)]:
    svg.append('<text x="%d" y="%d" fill="%s" font-size="%s">%s</text>' % (x0, y, c, z, t)); y += 24
y -= 8
for k, t in [('leak','plugging the leak (Flooding Rate)'),('fire','fighting the fire (Fire)'),('pump','pumping water out (Flooding)'),
             ('hull','welding the hull (Structure)'),('systems','repairing systems (Systems)'),('idle','no task')]:
    svg.append(g(k, x0, y)); svg.append('<text x="%d" y="%d" fill="%s" font-size="12.5">%s</text>' % (x0 + 24, y + 12, GR, t)); y += 21
svg.append('</svg>')
cairosvg.svg2png(bytestring="".join(svg).encode(), write_to='legend.png', output_width=W * 2, output_height=H * 2)
json.dump(PIC, open('pic.json','w'), indent=1)
