import cairosvg, re
exec(open('panels.py').read().split("W, H = 760, 150")[0])   # dial()
W, H = 1290, 330
bg, card, border, lab, dim = "rgb(26,28,32)", "rgb(30,33,38)", "rgb(58,62,68)", "rgb(214,218,222)", "rgb(150,156,162)"
s = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="DejaVu Sans">' % (W, H), '<rect width="%d" height="%d" fill="%s"/>' % (W, H, bg)]
def box(x, y, w, h): s.append('<rect x="%d" y="%d" width="%d" height="%d" fill="%s" stroke="%s"/>' % (x, y, w, h, card, border))
def t(x, y, text, size=13, col=lab, anchor="middle"): s.append('<text x="%d" y="%d" font-size="%s" fill="%s" text-anchor="%s">%s</text>' % (x, y, size, col, anchor, text))
X0, Y0, CH = 10, 10, 86
# summary row
box(X0, Y0, 144, CH); t(X0+72, Y0+22, "Damage Control Teams", 12, dim); t(X0+72, Y0+52, "0 / 2", 20); t(X0+72, Y0+76, "Available / Total", 11, dim)
box(X0+150, Y0, 139, CH); t(X0+219, Y0+22, "Teams Knocked Out", 12, dim); t(X0+219, Y0+52, "2", 20, "rgb(222,82,62)"); t(X0+219, Y0+76, "Available In: 73 s", 11, dim)
hx = W - X0 - 220
box(hx, Y0, 220, CH)
for k, (a, v, c) in enumerate([("Hull Damage:", "Moderate", "rgb(236,160,82)"), ("Flooding:", "Slight", lab), ("Fires:", "Severe (2)", "rgb(222,82,62)")]):
    y = Y0 + 24 + k*26; t(hx+10, y, a, 13, lab, "start"); t(hx+210, y, v, 13, c, "end")
# picture row: equal side columns of 300, picture in the middle
RY = Y0 + CH + 6; RH = 200
box(X0, RY, 300, RH)
t(X0+150, RY+50, "Auto Damage Control", 12, dim)
s.append('<rect x="%d" y="%d" width="100" height="24" fill="rgb(236,160,82)"/>' % (X0+100, RY+60)); t(X0+150, RY+77, "Enabled", 13, "rgb(26,28,32)")
t(X0+150, RY+118, "DC Priority", 12, dim)
for k, n in enumerate(["Auto", "Fire", "Flooding"]):
    bx = X0+150-90 + k*62
    s.append('<rect x="%d" y="%d" width="56" height="24" fill="%s" stroke="%s"/>' % (bx, RY+128, "rgb(236,160,82)" if k == 0 else "rgb(44,48,54)", border))
    t(bx+28, RY+145, n, 12, "rgb(26,28,32)" if k == 0 else lab)
s.append('<rect x="%d" y="%d" width="330" height="120" fill="rgb(40,44,50)"/>' % (W/2-165, RY-20)); t(W/2, RY+45, "ship profile picture (centred)", 13, dim)
lx = W - X0 - 300
box(lx, RY, 300, RH)
inner = dial(0, 0, "LIST", "PORT", "STBD", 31.5, 31.5*0.75, 7.4, "7.4°", lab) + dial(114, 0, "TRIM", "BOW", "STERN", 7, 5.25, 2.1, "2.1°", lab)
s.append('<g transform="translate(%d,%d)">%s' % (lx+9, RY+48, inner))
s.append('<text x="262" y="11" text-anchor="middle" font-size="11" fill="%s">LEVEL</text><rect x="254" y="15" width="16" height="58" fill="rgb(36,40,46)" stroke="%s"/><rect x="255" y="16" width="14" height="14" fill="rgb(200,62,54)"/><rect x="255" y="62" width="14" height="10" fill="rgb(74,150,226)"/><text x="262" y="86" text-anchor="middle" font-size="10" fill="%s">5%%</text></g>' % (dim, dim, dim))
s.append('<rect x="%d" y="%d" width="%d" height="20" fill="%s" stroke="%s"/>' % (X0, RY+RH+6, W-2*X0, card, border)); t(W/2, RY+RH+20, "section columns (Aft ... Forward)", 11, dim)
s.append('</svg>')
cairosvg.svg2png(bytestring="".join(s).encode(), write_to='layout.png', output_width=W, output_height=H)
