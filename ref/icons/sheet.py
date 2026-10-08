import json, cairosvg
icons = json.load(open('icons.json'))
order = [('leak','Plugging the leak (Flooding Rate)'),('fire','Fighting the fire (Fire)'),('pump','Pumping water out (Flooding)'),
         ('hull','Repairing the hull - welding (Structure)'),('systems','Repairing systems (Systems)'),('idle','No task'),('priority','Menu: by priority (as the other teams)'),('transit','Team in transfer'),('injured','Team knocked out, recovering'),('dead','Team killed (knocked out twice)')]
def g(name, x, y, scale):
    out = '<g transform="translate(%s,%s) scale(%s)">' % (x, y, scale)
    for d, col in icons[name]:
        rule = 'evenodd' if d.startswith('F0') else 'nonzero'
        out += '<path d="%s" fill="rgb(%d,%d,%d)" fill-rule="%s"/>' % (d.replace('F0 ', ''), col[0], col[1], col[2], rule)
    return out + '</g>'
W, rowh = 760, 92
H = 40 + rowh * len(order) + 70
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="DejaVu Sans">' % (W, H),
       '<rect width="%d" height="%d" fill="rgb(30,33,38)"/>' % (W, H),
       '<text x="20" y="26" fill="rgb(214,218,222)" font-size="15">large (x5)          actual size (16 px)   x2</text>']
for i, (k, label) in enumerate(order):
    y = 40 + i * rowh
    svg.append('<rect x="20" y="%d" width="80" height="80" fill="rgb(40,44,50)"/>' % y)
    svg.append(g(k, 20, y, 5))
    svg.append(g(k, 150, y + 32, 1))
    svg.append(g(k, 190, y + 24, 2))
    svg.append('<text x="250" y="%d" fill="rgb(214,218,222)" font-size="16">%s</text>' % (y + 46, label))
# a row as it would look in the tab
y = 40 + rowh * len(order) + 14
svg.append('<rect x="20" y="%d" width="720" height="40" fill="rgb(26,28,32)" stroke="rgb(58,62,68)"/>' % y)
x = 40
for k in ['idle','idle','transit','injured','dead','leak','fire','pump','hull','systems','priority']:
    svg.append(g(k, x, y + 12, 1)); x += 22
svg.append('<text x="%d" y="%d" fill="rgb(150,156,162)" font-size="13">in the tab: 16 px, 6 px apart</text>' % (x + 20, y + 25))
svg.append('</svg>')
cairosvg.svg2png(bytestring="".join(svg).encode(), write_to='icons_sheet.png', output_width=W * 2, output_height=H * 2)
