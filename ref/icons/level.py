import cairosvg
def bar(ox, oy, frac):
    w, h, inner = 16, 56, 54; red = 1 + inner*0.2
    s = '<g transform="translate(%d,%d)">' % (ox, oy)
    s += '<text x="8" y="-6" text-anchor="middle" font-size="11" fill="rgb(150,156,162)">LEVEL</text>'
    s += '<rect x="0" y="2" width="16" height="56" fill="rgb(36,40,46)"/>'
    fh = inner*frac
    s += '<rect x="1" y="%.2f" width="14" height="%.2f" fill="rgb(74,150,226)"/>' % (2+1+inner-fh, fh)
    d = ""
    for k in range(1, 8):
        y = 1 + inner*k/8
        if y <= red: continue
        d += "M1,%.2f L3.5,%.2f M12.5,%.2f L15,%.2f " % (y+2, y+2, y+2, y+2)
    s += '<path d="%s" stroke="rgb(214,218,222)" stroke-width="1"/>' % d
    s += '<path d="M1,3 L4,3 L4,%.2f L1,%.2f Z M12,3 L15,3 L15,%.2f L12,%.2f Z" fill="rgb(200,62,54)"/>' % (red+2, red+2, red+2, red+2)
    s += '<rect x="0.5" y="2.5" width="15" height="55" fill="none" stroke="rgb(150,156,162)" stroke-width="1"/>'
    s += '<text x="8" y="70" text-anchor="middle" font-size="10" fill="rgb(150,156,162)">%d%%</text></g>' % round(frac*28)
    return s
svg = '<svg xmlns="http://www.w3.org/2000/svg" width="160" height="90" font-family="DejaVu Sans"><rect width="160" height="90" fill="rgb(30,33,38)"/>' + bar(20, 14, 0.0) + bar(70, 14, 0.4) + bar(120, 14, 0.9) + '</svg>'
cairosvg.svg2png(bytestring=svg.encode(), write_to='level.png', output_width=640, output_height=360)
