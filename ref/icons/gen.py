import math, json
from shapely.geometry import Polygon, Point, box, LineString
from shapely.ops import unary_union
from shapely import affinity

def path_of(geom, nd=2):
    """shapely (multi)polygon -> path data (outer rings + holes; draw with EvenOdd 'F0')"""
    polys = [geom] if geom.geom_type == 'Polygon' else list(geom.geoms)
    out = []
    def ring(coords):
        pts = list(coords)[:-1]
        s = "M%s,%s" % (round(pts[0][0], nd), round(pts[0][1], nd))
        for x, y in pts[1:]: s += " L%s,%s" % (round(x, nd), round(y, nd))
        return s + " Z"
    for p in polys:
        out.append(ring(p.exterior.coords))
        for h in p.interiors: out.append(ring(h.coords))
    return "F0 " + " ".join(out)

def rrect(x0, y0, x1, y1, r):
    return box(x0 + r, y0 + r, x1 - r, y1 - r).buffer(r, resolution=8)

def circle(cx, cy, r): return Point(cx, cy).buffer(r, resolution=16)

C = dict(
    plate=(150,158,168), dark=(36,40,46), patch=(226,200,150), blue=(86,160,232), white=(236,240,244),
    fire=(240,120,50), firein=(255,206,90), spark=(255,226,120), orange=(250,150,60), grey=(170,176,184), chip=(96,104,116), pin=(190,196,204))
icons = {}

# --- leak: water spurting in through a hole in a hull plate, a patch being put over it
plate = box(0.5, 10.5, 15.5, 14.5)
hole = Polygon([(6.0,10.5),(6.8,11.6),(6.2,12.6),(7.4,13.4),(9.0,13.0),(9.8,13.6),(10.4,12.2),(9.6,11.2),(10.2,10.5)])
plate = plate.difference(hole)
jet = Polygon([(7.0,11.4),(6.2,8.0),(6.6,5.0),(7.6,2.6),(8.4,2.4),(9.2,4.8),(9.6,8.0),(9.2,11.4)])
spray = circle(4.6, 3.6, 0.9).union(circle(11.2, 3.0, 0.8)).union(circle(3.6, 6.6, 0.7)).union(circle(12.4, 6.2, 0.7))
patch = affinity.rotate(rrect(0.8, 8.6, 7.6, 12.6, 0.7), -10, origin=(4.2, 10.6))
icons['leak'] = [(path_of(plate), 'plate'), (path_of(hole), 'dark'), (path_of(jet.union(spray)), 'blue'), (path_of(patch), 'patch')]

# --- pump: a compartment with water in it and an arrow taking the water out
walls = box(1, 4, 3, 15).union(box(13, 4, 15, 15)).union(box(1, 13.5, 15, 15))
wave = [(3, 15 - 1.5)]
for k in range(0, 21):
    x = 3 + 10 * k / 20; wave.append((x, 10.2 + 0.8 * math.sin(k / 20 * 2 * math.pi * 1.5)))
wave.append((13, 13.5))
water = Polygon([(3, 13.5)] + wave[1:])
arrow = Polygon([(8,0.2),(11.6,4.2),(9.3,4.2),(9.3,9.4),(6.7,9.4),(6.7,4.2),(4.4,4.2)])
icons['pump'] = [(path_of(walls), 'plate'), (path_of(water), 'blue'), (path_of(arrow), 'white')]

# --- fire
flame = ("M8,0.5 C10.2,3.4 13.5,5.6 13.5,10 C13.5,13.2 11.2,15.5 8,15.5 C4.8,15.5 2.5,13.2 2.5,10 C2.5,7.2 4.2,5.6 5.2,2.8 "
         "C6.4,4.6 6.9,5.6 7.7,6.8 C8,4.6 8,2.6 8,0.5 Z")
inner = "M8,8 C9.7,9.6 10.8,10.8 10.8,12.4 C10.8,14.4 9.6,15.3 8,15.3 C6.4,15.3 5.2,14.4 5.2,12.4 C5.2,11 6.3,10 8,8 Z"
icons['fire'] = [(flame, 'fire'), (inner, 'firein')]

# --- hull repair: welding torch over a seam between two plates, with sparks
plates = box(0.5, 11, 7.6, 15).union(box(8.4, 11, 15.5, 15))
seam = box(7.6, 11, 8.4, 15)
torch_handle = affinity.rotate(rrect(-3.2, -1.1, 3.2, 1.1, 1.0), -45, origin=(0, 0))
torch_handle = affinity.translate(torch_handle, 11.6, 4.4)
nozzle = affinity.translate(affinity.rotate(box(-0.6, -2.4, 0.6, 2.4), -45, origin=(0, 0)), 8.2, 7.8)
tip = affinity.translate(affinity.rotate(box(-0.4, -0.8, 0.4, 0.8), -45, origin=(0, 0)), 6.5, 9.5)
star = []
for k in range(16):
    a = k * math.pi / 8; r = 3.0 if k % 2 == 0 else 1.0
    star.append((6.0 + r * math.cos(a), 10.6 + r * math.sin(a)))
spark = Polygon(star)
dots = circle(2.6, 8.4, 0.6).union(circle(10.0, 9.6, 0.5)).union(circle(3.6, 6.6, 0.45))
icons['hull'] = [(path_of(plates), 'plate'), (path_of(seam), 'orange'), (path_of(torch_handle), 'grey'),
                 (path_of(nozzle.union(tip)), 'pin'), (path_of(spark), 'spark'), (path_of(dots), 'orange')]

# --- systems, variant A: open-end wrench
head = circle(0, -4.2, 3.6)
jaw = box(-1.25, -9, 1.25, -4.4).union(circle(0, -4.4, 1.25))
handle = rrect(-1.25, -1.5, 1.25, 7.6, 1.2)
w = head.difference(jaw).union(handle)
w = affinity.rotate(w, 45, origin=(0, 0))
w = affinity.translate(w, 8, 8)
icons['wrench'] = [(path_of(w), 'grey')]

# --- systems, variant B: microchip
body = rrect(3.5, 3.5, 12.5, 12.5, 0.8)
core = rrect(5.8, 5.8, 10.2, 10.2, 0.4)
pins = []
for k in range(3):
    t = 5.0 + k * 2.5
    pins += [box(t - 0.55, 1, t + 0.55, 3.5), box(t - 0.55, 12.5, t + 0.55, 15),
             box(1, t - 0.55, 3.5, t + 0.55), box(12.5, t - 0.55, 15, t + 0.55)]
icons['chip'] = [(path_of(unary_union(pins)), 'pin'), (path_of(body), 'chip'), (path_of(core), 'pin')]

# --- idle: a person
icons['idle'] = [(path_of(circle(8, 3.8, 3.2)), 'grey'),
                 ("M2,15.5 C2,10.6 4.8,8.2 8,8.2 C11.2,8.2 14,10.6 14,15.5 Z", 'grey')]

json.dump({k: [(p, list(C[c])) for p, c in v] for k, v in icons.items()}, open('icons.json', 'w'), indent=1)
print({k: len(v) for k, v in icons.items()})
