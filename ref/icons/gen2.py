import math, json
from shapely.geometry import Polygon, Point, box, LineString
from shapely.ops import unary_union
from shapely import affinity
exec(open('gen.py').read().split("C = dict(")[0])   # path_of, rrect, circle

def drop(cx, top, r, bottom, n=48):
    """a drop: the apex at (cx, top), the sides tangent to the circle of radius r that touches 'bottom' - no kinks"""
    cy = bottom - r
    D = cy - top                               # apex to centre
    t = math.asin(min(1.0, r / D))             # half-angle of the tangents at the apex
    # tangent points: angle from the centre, measured from straight up (screen y down)
    phi = math.pi / 2 - t                      # angle between "up" and the radius to a tangent point
    pts = [(cx, top)]
    # from the right tangent point down round the bottom to the left one
    a0, a1 = -math.pi / 2 + phi, -math.pi / 2 - phi + 2 * math.pi
    for k in range(n + 1):
        a = a0 + (a1 - a0) * k / n
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return Polygon(pts)

C = dict(blue=(74,150,226), bluelt=(160,208,250), fire=(240,120,50), firein=(255,206,90),
         metal=(170,176,184), metaldk=(110,118,130), slate=(112,130,154), slatelt=(162,180,202),
         yellow=(255,200,60), kogrey=(84,90,100), kogreylt=(110,116,126), kotan=(128,124,116), kored=(120,74,70), cross=(222,60,48), red=(204,44,38), tan=(212,184,128), navy=(52,72,118), navylt=(86,110,160), white=(236,240,244), skin=(238,196,156), suit=(70,124,196), suitlt=(120,170,230), dark=(34,40,50), spark=(255,226,120), orange=(250,150,60))
I = {}

# leak (Flooding Rate): a drop, two-tone like the flame
d = drop(8, 0.6, 5.4, 15.5)
hl = affinity.rotate(Point(0, 0).buffer(1, resolution=12), 0)
hl = affinity.scale(hl, 1.3, 2.5); hl = affinity.rotate(hl, 20); hl = affinity.translate(hl, 5.6, 10.6)
I['leak'] = [(path_of(d), 'blue'), (path_of(hl.intersection(d)), 'bluelt')]

# fire: unchanged
I['fire'] = [("M8,0.5 C10.2,3.4 13.5,5.6 13.5,10 C13.5,13.2 11.2,15.5 8,15.5 C4.8,15.5 2.5,13.2 2.5,10 C2.5,7.2 4.2,5.6 5.2,2.8 C6.4,4.6 6.9,5.6 7.7,6.8 C8,4.6 8,2.6 8,0.5 Z", 'fire'),
             ("M8,8 C9.7,9.6 10.8,10.8 10.8,12.4 C10.8,14.4 9.6,15.3 8,15.3 C6.4,15.3 5.2,14.4 5.2,12.4 C5.2,11 6.3,10 8,8 Z", 'firein')]

# pump (Flooding): a hand pump with a drop from its spout - straight edges, small radii
body = box(6.8, 4.4, 10.4, 14.4)
base = box(5.2, 14.0, 12.0, 15.6)
cap = Polygon([(6.2, 4.6), (6.6, 2.8), (10.6, 2.8), (11.0, 4.6)])
spout = box(2.8, 6.6, 6.8, 8.2).union(box(2.8, 6.6, 4.3, 9.8))
lever = affinity.translate(affinity.rotate(box(0, -0.6, 6.2, 0.6), -30, origin=(0, 0)), 10.0, 3.4)
pivot = box(9.4, 2.4, 10.8, 3.8)
pump = unary_union([body, base, cap, spout, lever, pivot])
shine = box(7.4, 5.0, 8.1, 13.8)
d2 = drop(3.55, 10.6, 1.3, 15.0)
I['pump'] = [(path_of(pump), 'slate'), (path_of(shine), 'slatelt'), (path_of(d2), 'blue')]

# hull (Structure): a torch over the seam of two plates - the arc, a glowing bead along the seam and flying sparks
plates = box(0.4, 11.4, 7.5, 15.6).union(box(8.5, 11.4, 15.6, 15.6))
bead = box(7.5, 11.4, 8.5, 15.6)                              # welded part of the seam, glowing
bead_hot = box(7.5, 11.4, 8.5, 12.8)
handle = affinity.translate(affinity.rotate(box(-0.9, -3.0, 0.9, 3.0), 42, origin=(0, 0)), 12.4, 3.6)
neck = affinity.translate(affinity.rotate(box(-0.45, -2.2, 0.45, 2.2), 42, origin=(0, 0)), 9.6, 6.9)
nozzle = affinity.translate(affinity.rotate(Polygon([(-0.75, -1.0), (0.75, -1.0), (0.45, 1.0), (-0.45, 1.0)]), 42, origin=(0, 0)), 8.6, 8.5)
# arc flash: a sharp 8-ray star, and spark streaks flying off
star = []
for k in range(16):
    a = k * math.pi / 8 + math.pi / 16; r = 4.0 if k % 2 == 0 else 1.1
    star.append((8.0 + r * math.cos(a), 10.4 + r * math.sin(a)))
streaks = []
for ang, r0, r1 in [(205, 4.6, 7.4), (235, 4.8, 7.0), (160, 4.6, 7.2), (320, 4.8, 6.8), (182, 5.4, 7.8)]:
    a = math.radians(ang)
    p0 = (8.0 + r0 * math.cos(a), 10.4 + r0 * math.sin(a)); p1 = (8.0 + r1 * math.cos(a), 10.4 + r1 * math.sin(a))
    streaks.append(LineString([p0, p1]).buffer(0.42, cap_style=2))
I['hull'] = [(path_of(plates), 'metal'), (path_of(bead), 'orange'), (path_of(bead_hot), 'spark'),
             (path_of(handle), 'slatelt'), (path_of(neck.union(nozzle)), 'metal'),
             (path_of(Polygon(star)), 'spark'), (path_of(unary_union(streaks)), 'orange')]

# systems: crossed open-end wrench and screwdriver - angular
head = Polygon([(-3.2, -2.6), (-3.2, -6.2), (-1.2, -8.2), (-1.2, -5.0), (1.2, -5.0), (1.2, -8.2), (3.2, -6.2), (3.2, -2.6), (1.1, -1.2), (-1.1, -1.2)])
wr = head.union(box(-1.1, -1.6, 1.1, 7.6))
wr = affinity.translate(affinity.rotate(wr, 45, origin=(0, 0)), 8, 8)
shaft = box(-0.55, -6.6, 0.55, 0.6).union(Polygon([(-0.55, -6.6), (0.55, -6.6), (0.3, -7.8), (-0.3, -7.8)]))
handle = Polygon([(-1.2, 0.4), (1.2, 0.4), (1.6, 1.4), (1.6, 7.0), (1.0, 7.6), (-1.0, 7.6), (-1.6, 7.0), (-1.6, 1.4)])
grip = box(-0.3, 1.8, 0.3, 6.6)
def place(g): return affinity.translate(affinity.rotate(g, -45, origin=(0, 0)), 8, 8)
I['systems'] = [(path_of(wr), 'metal'), (path_of(place(shaft)), 'metaldk'), (path_of(place(handle)), 'blue'), (path_of(place(grip)), 'bluelt')]

# no task: a damage control crewman standing by, as on the deck photo - red helmet with a yellow reflective band,
# tan flash hood round the face and over the shoulders, navy coveralls with a white name tag
dome = Polygon([(4.4, 4.4), (4.8, 2.2), (6.4, 0.8), (9.6, 0.8), (11.2, 2.2), (11.6, 4.4)])
helmet = dome.union(box(3.2, 4.0, 12.8, 5.0))
band = box(0, 2.9, 16, 3.7).intersection(dome)
hood = Polygon([(4.9, 5.0), (11.1, 5.0), (11.3, 8.0), (10.5, 9.6), (5.5, 9.6), (4.7, 8.0)])
face = Polygon([(6.3, 5.0), (9.7, 5.0), (9.5, 7.2), (8.7, 8.1), (7.3, 8.1), (6.5, 7.2)])
body = Polygon([(1.6, 15.6), (1.6, 12.4), (2.6, 10.6), (5.4, 9.4), (10.6, 9.4), (13.4, 10.6), (14.4, 12.4), (14.4, 15.6)])
drape = Polygon([(4.4, 9.2), (11.6, 9.2), (10.6, 10.8), (8.0, 11.8), (5.4, 10.8)])
pocket = box(3.4, 12.4, 6.2, 13.2)
tag = box(9.6, 12.4, 12.0, 13.2)
I['idle'] = [(path_of(body), 'navy'), (path_of(pocket), 'navylt'), (path_of(tag), 'white'),
             (path_of(hood.union(drape)), 'tan'), (path_of(face), 'skin'), (path_of(helmet), 'red'), (path_of(band), 'yellow')]

# by priority (menu): a gear in a ring of two arrows going round - the teams take work in the priority order
gear = circle(8, 8, 3.4)
for k in range(8):
    t = affinity.rotate(box(7.25, 2.9, 8.75, 5.0), k * 45, origin=(8, 8))
    gear = gear.union(t)
gear = gear.difference(circle(8, 8, 1.35))
def arc(a0, a1, r0=6.1, r1=7.5, n=40):
    pts = []
    for k in range(n + 1):
        a = math.radians(a0 + (a1 - a0) * k / n); pts.append((8 + r1 * math.cos(a), 8 + r1 * math.sin(a)))
    for k in range(n, -1, -1):
        a = math.radians(a0 + (a1 - a0) * k / n); pts.append((8 + r0 * math.cos(a), 8 + r0 * math.sin(a)))
    return Polygon(pts)
def head(a_deg, r=6.8, size=2.2):
    """arrowhead at angle a (degrees, screen), pointing clockwise along the ring"""
    a = math.radians(a_deg)
    cx, cy = 8 + r * math.cos(a), 8 + r * math.sin(a)
    tx, ty = -math.sin(a), math.cos(a)          # clockwise tangent (y down)
    nx, ny = math.cos(a), math.sin(a)
    tip = (cx + tx * size, cy + ty * size)
    return Polygon([tip, (cx + nx * size, cy + ny * size), (cx - nx * size, cy - ny * size)])
arrows = unary_union([arc(195, 300), head(300), arc(15, 120), head(120)])
I['priority'] = [(path_of(arrows), 'blue'), (path_of(gear), 'metal')]

# knocked out (summary card): the crewman greyed out, a red cross on him
ko = []
grey = {'navy': 'kogrey', 'navylt': 'kogreylt', 'white': 'kogreylt', 'tan': 'kotan', 'skin': 'kotan', 'red': 'kored', 'yellow': 'kotan'}
for p, c in I['idle']:
    ko.append((p, grey.get(c, c)))
cross = affinity.rotate(box(7.1, 6.2, 8.9, 15.8).union(box(3.2, 10.1, 12.8, 11.9)), 45, origin=(8, 11))
ko.append((path_of(cross.intersection(box(0, 0, 16, 16))), 'cross'))
I['knocked'] = ko

# in transfer: the crewman greyed out (no marks)
I['transit'] = [(p, grey.get(c, c)) for p, c in I['idle']]
# injured (recovering): the crewman greyed out with a medical badge (white square, red cross)
badge = box(9.6, 9.6, 15.6, 15.6)
plus = box(11.85, 10.4, 13.35, 14.8).union(box(10.4, 11.85, 14.8, 13.35))
I['injured'] = [(p, grey.get(c, c)) for p, c in I['idle']] + [(path_of(badge), 'white'), (path_of(plus), 'cross')]
# dead: the old "knocked out" look - greyed out with a big red cross over him
I['dead'] = I['knocked']

json.dump({k: [(p, list(C[c])) for p, c in v] for k, v in I.items()}, open('icons.json', 'w'), indent=1)
print('ok', list(I))
