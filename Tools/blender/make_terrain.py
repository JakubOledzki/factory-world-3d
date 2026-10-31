# Builds the land around a plot as one mesh: flat where the plot lies (so it can be built on), rising
# into rough mountains around it and high peaks on the horizon. Units are metres; the plot covers
# (0..PLOT) on X and Y, as in the game (1 km at TileSizeCm=400). Writes RawAssets/processed/terrain_*.glb.
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_terrain.py
import bpy
import bmesh
import math
import os
import random
import sys

from mathutils import noise, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed")
PLOT = 1000.0
CENTER = Vector((PLOT / 2, PLOT / 2, 0))

# name: (extent from centre in m, flat margin around the plot, near height, far height, roughness)
VARIANTS = {
    "terrain_mountains": (9000.0, 120.0, 140.0, 2000.0, 1.0),
    "terrain_hills": (9000.0, 120.0, 60.0, 420.0, 0.5),
}


def ridged(p, octaves):
    # Sharp crests, like eroded rock ridges
    total, amp, freq = 0.0, 1.0, 1.0
    for _ in range(octaves):
        n = 1.0 - abs(noise.noise(p * freq))
        total += n * n * amp
        amp *= 0.5
        freq *= 2.03
    return total


def smoothstep(a, b, x):
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def fbm(p, octaves):
    total, amp, freq = 0.0, 0.5, 1.0
    for _ in range(octaves):
        total += noise.noise(p * freq) * amp
        amp *= 0.5
        freq *= 2.0
    return total


def height(x, y, margin, near, far, rough):
    # Distance outside the plot square (0 inside it)
    dx = max(0.0, abs(x - CENTER.x) - PLOT / 2)
    dy = max(0.0, abs(y - CENTER.y) - PLOT / 2)
    t = math.hypot(dx, dy) - margin
    if t <= 0.0:
        return -0.05
    p = Vector((x, y, 0)) * 0.001
    # Rolling foothills over the first kilometre or so, gentle enough to look walkable
    foothills = near * (0.55 + fbm(p * 1.7, 5)) * smoothstep(0.0, 1400.0, t)
    # Then a ridged range that fills the horizon
    range_ = far * ridged(p * 0.45 + Vector((7.0, 3.0, 0.0)), 6) * 0.55 * smoothstep(1600.0, 5200.0, t)
    detail = fbm(Vector((x, y, 3.7)) * 0.03, 3) * 4.0 * rough * smoothstep(0.0, 300.0, t)
    return -0.05 + foothills + range_ + detail


def axis(extent):
    # Offsets from the centre: 6 m apart around the plot and the near slopes, coarser towards the horizon
    bands = [(1500.0, 6.0), (3500.0, 18.0), (extent, 30.0)]
    half = [0.0]
    for end, step in bands:
        while half[-1] + step <= end + 0.01:
            half.append(half[-1] + step)
    return [-v for v in reversed(half[1:])] + half


class Dem:
    # Real elevation cut out by Tools/dem_extract.mjs: SIZE x SIZE float32, STEP m apart, centred on the plot
    STEP = 30.0
    HALF = 9000.0

    def __init__(self, path):
        import array
        values = array.array("f")
        with open(path, "rb") as f:
            values.frombytes(f.read())
        self.size = int(round(math.sqrt(len(values))))
        self.values = values
        # The plot is levelled at the typical height of the land it covers
        inside = sorted(self.sample(CENTER.x + dx, CENTER.y + dy) for dx in range(-500, 501, 50) for dy in range(-500, 501, 50))
        self.base = inside[len(inside) // 2]

    def sample(self, x, y):
        # Catmull-Rom between the 30 m samples: bilinear would show every sample as a facet up close
        fx = min(max((x - CENTER.x + self.HALF) / self.STEP, 1.0), self.size - 2.001)
        fy = min(max((y - CENTER.y + self.HALF) / self.STEP, 1.0), self.size - 2.001)
        x0, y0 = int(fx), int(fy)
        ax, ay = fx - x0, fy - y0
        size, values = self.size, self.values

        def cubic(p0, p1, p2, p3, t):
            return p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)))

        rows = []
        for j in range(y0 - 1, y0 + 3):
            base = j * size
            rows.append(cubic(values[base + x0 - 1], values[base + x0], values[base + x0 + 1], values[base + x0 + 2], ax))
        return cubic(rows[0], rows[1], rows[2], rows[3], ay)


def dem_height(dem, x, y, margin):
    dx = max(0.0, abs(x - CENTER.x) - PLOT / 2)
    dy = max(0.0, abs(y - CENTER.y) - PLOT / 2)
    t = math.hypot(dx, dy) - margin
    if t <= 0.0:
        return -0.05
    # From the level plot into the real land over half a kilometre, with fine detail the 30 m data lacks
    w = smoothstep(0.0, 600.0, t)
    h = dem.sample(x, y)
    # Steep ground gets rocky ridges the 30 m data cannot hold; gentle ground small undulations
    steep = min(1.0, (abs(dem.sample(x + 15.0, y) - dem.sample(x - 15.0, y)) + abs(dem.sample(x, y + 15.0) - dem.sample(x, y - 15.0))) / 30.0)
    detail = fbm(Vector((x, y, 3.7)) * 0.03, 3) * 5.0 + fbm(Vector((x, y, 9.1)) * 0.12, 2) * 1.2
    detail += (ridged(Vector((x, y, 0.0)) * 0.012, 4) - 0.6) * 14.0 * steep
    return -0.05 + w * (h - dem.base + detail)


os.makedirs(OUT, exist_ok=True)
ONLY = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for file in sorted(os.listdir(OUT)):
    if file.startswith("dem_") and file.endswith(".bin"):
        VARIANTS["terrain_" + file[:-4]] = Dem(os.path.join(OUT, file))

for name, variant in VARIANTS.items():
    if ONLY and name not in ONLY:
        continue
    if isinstance(variant, Dem):
        extent, margin = Dem.HALF - 30.0, 120.0
        height_at = lambda x, y, d=variant: dem_height(d, x, y, margin)
    else:
        extent, margin, near, far, rough = variant
        height_at = lambda x, y: height(x, y, margin, near, far, rough)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    random.seed(1)
    xs = [CENTER.x + v for v in axis(extent)]
    ys = [CENTER.y + v for v in axis(extent)]
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    grid = []
    for y in ys:
        row = []
        for x in xs:
            row.append(bm.verts.new((x, y, height_at(x, y))))
        grid.append(row)
    for j in range(len(ys) - 1):
        for i in range(len(xs) - 1):
            face = bm.faces.new((grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]))
            for loop in face.loops:
                loop[uv_layer].uv = (loop.vert.co.x / 100.0, loop.vert.co.y / 100.0)
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    for poly in mesh.polygons:
        poly.use_smooth = True
    path = os.path.join(OUT, f"{name}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True)
    print(f"FWTERRAIN {name}: {len(mesh.polygons)} quads -> {path}")
