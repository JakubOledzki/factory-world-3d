# Builds the crops that grow on fields (a farmstead's or ploughed by hand) and writes one .glb per crop and stage to
# RawAssets/processed/crops: crop_<id>_young (growing, green) and crop_<id>_ripe (ready to reap), each the plants of one
# 4 x 4 m tile in rows, and crop_soil, the tilled ridges under them. Everything is real geometry (Nanite foliage, no
# alpha cards); the colour is in the vertex colour.
# Sizes in metres; the origin is the middle of the tile at ground level. One material slot, bound by
# Tools/import_external.py:
#   FW_Crop (M_FWFoliage: plain white times the vertex colour, a little wind on the tall plants)
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_crops.py
import math
import os
import random

import bmesh
import bpy
from mathutils import Matrix, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "crops")
TILE = 4.0
UP = Vector((0, 0, 1))


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3)) + (1.0,)


def vary(color, rng, amount=0.12):
    k = 1.0 + rng.uniform(-amount, amount)
    return (min(color[0] * k, 1.0), min(color[1] * k, 1.0), min(color[2] * k, 1.0), 1.0)


class Builder:
    def __init__(self):
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.color = self.bm.loops.layers.color.new("Color")

    def paint(self, faces, color_of):
        for face in faces:
            face.material_index = 0
            face.smooth = True
            for loop in face.loops:
                loop[self.uv].uv = (0.0, loop.vert.co.z)
                loop[self.color] = color_of(loop.vert.co)

    def strip(self, points, widths, side, low, high):
        """A leaf or blade: a strip along points, widths across (side is the across direction), both faces."""
        n = len(points)
        for offset, flip in ((0.0, False), (0.0012, True)):
            normal = side.cross(points[-1] - points[0]).normalized() * offset
            left = [self.bm.verts.new(p + side * (w * 0.5) + normal) for p, w in zip(points, widths)]
            right = [self.bm.verts.new(p - side * (w * 0.5) + normal) for p, w in zip(points, widths)]
            faces = []
            for i in range(n - 1):
                quad = [left[i], right[i], right[i + 1], left[i + 1]]
                faces.append(self.bm.faces.new(quad[::-1] if flip else quad))
            base = points[0].z
            top = max(points[-1].z, base + 0.01)
            self.paint(faces, lambda co: mix(low, high, max(0.0, min(1.0, (co.z - base) / (top - base)))))

    def tube(self, points, radius, sides, low, high):
        rings = []
        for i, point in enumerate(points):
            tangent = (points[min(i + 1, len(points) - 1)] - points[max(i - 1, 0)]).normalized()
            a = tangent.orthogonal().normalized()
            b = tangent.cross(a)
            r = radius * (1.0 - 0.5 * i / max(len(points) - 1, 1))
            rings.append([self.bm.verts.new(point + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * r) for k in range(sides)])
        faces = []
        for i in range(len(rings) - 1):
            for k in range(sides):
                faces.append(self.bm.faces.new([rings[i][k], rings[i][(k + 1) % sides], rings[i + 1][(k + 1) % sides], rings[i + 1][k]]))
        base, top = points[0].z, max(points[-1].z, points[0].z + 0.01)
        self.paint(faces, lambda co: mix(low, high, max(0.0, min(1.0, (co.z - base) / (top - base)))))

    def blob(self, center, radii, color, segments=(8, 6)):
        made = bmesh.ops.create_uvsphere(self.bm, u_segments=segments[0], v_segments=segments[1], radius=1.0,
                                         matrix=Matrix.Translation(center) @ Matrix.Diagonal(Vector((radii[0], radii[1], radii[2], 1.0))))
        faces = {face for vert in made["verts"] for face in vert.link_faces}
        self.paint(faces, lambda co: color)


def leaf(b, root, direction, length, width, droop, low, high, segments=4):
    """A leaf from root rising along direction, bending down by droop at its tip, widest a third of the way."""
    direction = direction.normalized()
    side = UP.cross(direction).normalized() if abs(direction.z) < 0.99 else Vector((1, 0, 0))
    points, widths = [], []
    for i in range(segments + 1):
        t = i / segments
        points.append(root + direction * (length * t) - UP * (droop * t * t))
        widths.append(width * math.sin(math.pi * min(1.0, t * 1.3 + 0.08)) if t < 1.0 else 0.0)
    b.strip(points, widths, side, low, high)


def stem(b, root, height, radius, lean, low, high, segments=3, sides=4):
    points = [root + Vector((lean.x * t * t, lean.y * t * t, height * t)) for t in (i / segments for i in range(segments + 1))]
    b.tube(points, radius, sides, low, high)
    return points[-1]


def rows(rng, spacing_rows, spacing_plants, jitter=0.04, margin=0.18):
    """Plant spots in rows along X, filling the tile."""
    count_rows = max(1, int((TILE - 2 * margin) / spacing_rows) + 1)
    first = -(count_rows - 1) * spacing_rows / 2
    for r in range(count_rows):
        y = first + r * spacing_rows
        x = -TILE / 2 + margin + rng.uniform(0, spacing_plants)
        while x < TILE / 2 - margin:
            yield Vector((x + rng.uniform(-jitter, jitter), y + rng.uniform(-jitter, jitter), 0.0))
            x += spacing_plants * rng.uniform(0.85, 1.15)


def around(rng, k, n):
    angle = 2 * math.pi * k / n + rng.uniform(-0.3, 0.3)
    return Vector((math.cos(angle), math.sin(angle), 0.0))


GREEN = (0.09, 0.26, 0.05)
LIGHT = (0.22, 0.45, 0.10)
DARK = (0.05, 0.16, 0.04)
STRAW = (0.52, 0.40, 0.15)
GOLD = (0.78, 0.62, 0.27)


def wheat(b, rng, ripe):
    for spot in rows(rng, 0.2, 0.13):
        if ripe:
            for _ in range(rng.randint(2, 3)):
                lean = Vector((rng.uniform(-0.06, 0.06), rng.uniform(-0.06, 0.06), 0))
                top = stem(b, spot + Vector((rng.uniform(-0.03, 0.03), rng.uniform(-0.03, 0.03), 0)), rng.uniform(0.82, 1.0), 0.003, lean,
                           vary(STRAW, rng), vary(GOLD, rng))
                # The ear, drooping a little with its weight
                b.blob(top + Vector((lean.x * 0.3, lean.y * 0.3, 0.03)), (0.011, 0.011, 0.045), vary(GOLD, rng, 0.08), (6, 5))
            leaf(b, spot + UP * 0.2, around(rng, 0, 1) + UP * 1.2, 0.25, 0.012, 0.1, vary(STRAW, rng), vary(GOLD, rng))
        else:
            for k in range(4):
                leaf(b, spot, around(rng, k, 4) * 0.35 + UP, rng.uniform(0.28, 0.42), 0.012, 0.08, vary(GREEN, rng), vary(LIGHT, rng))


def corn(b, rng, ripe):
    for spot in rows(rng, 0.75, 0.32):
        height = rng.uniform(2.0, 2.4) if ripe else rng.uniform(0.6, 0.8)
        low, high = (vary((0.30, 0.34, 0.12), rng), vary((0.55, 0.50, 0.22), rng)) if ripe else (vary(GREEN, rng), vary(LIGHT, rng))
        top = stem(b, spot, height, 0.016, Vector((rng.uniform(-0.05, 0.05), rng.uniform(-0.05, 0.05), 0)), low, high, 4, 5)
        leaves = 10 if ripe else 6
        for k in range(leaves):
            at = spot + UP * (height * (0.12 + 0.8 * k / leaves))
            leaf(b, at, around(rng, k, 2.3) + UP * 0.6, rng.uniform(0.55, 0.85) if ripe else rng.uniform(0.35, 0.5), 0.075, 0.35,
                 low, high if not ripe else vary((0.62, 0.55, 0.25), rng))
        if ripe:
            # Cobs in their husks, and the tassel
            for k in range(rng.randint(1, 2)):
                side = around(rng, k, 2)
                b.blob(spot + UP * (height * 0.48 + 0.1 * k) + side * 0.06, (0.04, 0.04, 0.11), vary((0.85, 0.68, 0.20), rng, 0.06), (8, 6))
                leaf(b, spot + UP * (height * 0.42 + 0.1 * k) + side * 0.05, side * 0.2 + UP, 0.22, 0.05, 0.02, vary((0.55, 0.52, 0.25), rng), vary((0.72, 0.64, 0.33), rng))
            for k in range(5):
                leaf(b, top, around(rng, k, 5) * 0.3 + UP, 0.3, 0.008, 0.12, vary(GOLD, rng), vary((0.85, 0.72, 0.4), rng))


def cabbage(b, rng, ripe):
    for spot in rows(rng, 0.6, 0.5):
        outer = 7 if ripe else 5
        for k in range(outer):
            leaf(b, spot + UP * 0.02, around(rng, k, outer) + UP * (0.5 if ripe else 0.9), 0.3 if ripe else 0.18, 0.2 if ripe else 0.12, 0.05,
                 vary((0.12, 0.30, 0.16), rng), vary((0.30, 0.50, 0.32), rng))
        if ripe:
            b.blob(spot + UP * 0.13, (0.13, 0.13, 0.12), vary((0.50, 0.66, 0.36), rng, 0.08), (10, 8))


def carrot(b, rng, ripe):
    for spot in rows(rng, 0.3, 0.09):
        for k in range(6):
            direction = around(rng, k, 6) * 0.35 + UP
            leaf(b, spot, direction, rng.uniform(0.25, 0.38) if ripe else rng.uniform(0.15, 0.22), 0.02, 0.1, vary(DARK, rng), vary(GREEN, rng))
        if ripe:
            b.blob(spot + UP * 0.01, (0.022, 0.022, 0.025), vary((0.85, 0.36, 0.06), rng, 0.08), (6, 4))


def potato(b, rng, ripe):
    low, high = ((0.30, 0.32, 0.10), (0.55, 0.50, 0.18)) if ripe else (GREEN, LIGHT)
    for spot in rows(rng, 0.7, 0.35):
        for k in range(5):
            direction = around(rng, k, 5) * 0.5 + UP
            top = stem(b, spot, rng.uniform(0.35, 0.5), 0.006, direction * 0.12, vary(low, rng), vary(high, rng), 2, 4)
            for j in range(3):
                leaf(b, top - UP * (0.1 * j), around(rng, j, 3) + UP * 0.3, 0.12, 0.06, 0.03, vary(low, rng), vary(high, rng), 3)


def sunflower(b, rng, ripe):
    for spot in rows(rng, 0.75, 0.45):
        height = rng.uniform(1.7, 2.0) if ripe else rng.uniform(0.6, 0.8)
        top = stem(b, spot, height, 0.018, Vector((0.05, 0, 0)), vary(GREEN, rng), vary(LIGHT, rng), 4, 5)
        for k in range(8 if ripe else 5):
            at = spot + UP * (height * (0.15 + 0.75 * k / 8))
            leaf(b, at, around(rng, k, 2.2) + UP * 0.2, 0.25, 0.16, 0.08, vary(DARK, rng), vary(GREEN, rng))
        if ripe:
            # The head, turned east, its seeds brown and the petals yellow round it
            facing = Vector((1.0, 0.0, -0.25)).normalized()
            middle = top + facing * 0.05
            b.blob(middle, (0.035, 0.14, 0.14), vary((0.22, 0.13, 0.05), rng, 0.08), (10, 8))
            side = Vector((0, 1, 0))
            for k in range(18):
                angle = 2 * math.pi * k / 18
                out = (side * math.cos(angle) + facing.cross(side) * math.sin(angle)).normalized()
                leaf(b, middle + out * 0.12, out, 0.09, 0.035, 0.0, vary((0.95, 0.70, 0.08), rng, 0.05), vary((1.0, 0.82, 0.15), rng, 0.05), 2)


def sugar_beet(b, rng, ripe):
    for spot in rows(rng, 0.5, 0.25):
        for k in range(9):
            leaf(b, spot + UP * 0.02, around(rng, k, 9) + UP * 0.9, rng.uniform(0.28, 0.4) if ripe else rng.uniform(0.16, 0.24), 0.11, 0.08,
                 vary((0.38, 0.14, 0.12), rng), vary((0.18, 0.40, 0.12), rng))
        if ripe:
            b.blob(spot + UP * 0.03, (0.06, 0.06, 0.05), vary((0.88, 0.82, 0.72), rng, 0.05), (8, 6))


def flax(b, rng, ripe):
    for spot in rows(rng, 0.18, 0.08):
        top = stem(b, spot, rng.uniform(0.7, 0.85) if ripe else rng.uniform(0.45, 0.6), 0.0025, Vector((rng.uniform(-0.05, 0.05), 0, 0)),
                   vary(STRAW if ripe else GREEN, rng), vary(GOLD if ripe else LIGHT, rng))
        if ripe:
            for k in range(3):
                b.blob(top + around(rng, k, 3) * 0.02 + UP * 0.01, (0.009, 0.009, 0.01), vary((0.55, 0.40, 0.18), rng), (5, 4))
        else:
            # In flower: small sky-blue blossoms
            b.blob(top + UP * 0.01, (0.014, 0.014, 0.006), vary((0.35, 0.50, 0.95), rng, 0.06), (6, 3))


def hemp(b, rng, ripe):
    for spot in rows(rng, 0.45, 0.3):
        height = rng.uniform(1.8, 2.2) if ripe else rng.uniform(0.7, 0.9)
        low, high = (vary((0.07, 0.20, 0.05), rng), vary((0.16, 0.34, 0.08), rng))
        top = stem(b, spot, height, 0.012, Vector((rng.uniform(-0.06, 0.06), rng.uniform(-0.06, 0.06), 0)), low, high, 4, 5)
        for level in range(5 if ripe else 3):
            at = spot + UP * (height * (0.3 + 0.6 * level / 5))
            base = around(rng, level, 2.5)
            # A hand of seven narrow leaflets
            for k in range(7):
                angle = (k - 3) * 0.35
                direction = (Matrix.Rotation(angle, 3, UP) @ base + UP * 0.25).normalized()
                leaf(b, at, direction, 0.18 * (1.0 - abs(k - 3) * 0.18), 0.025, 0.04, low, high, 3)
        if ripe:
            for k in range(6):
                b.blob(top - UP * (0.04 * k) + around(rng, k, 6) * 0.03, (0.025, 0.025, 0.04), vary((0.30, 0.38, 0.12), rng), (6, 4))


def soil(b, rng):
    """Tilled earth: the flat bed and ridges along the rows."""
    flat = [b.bm.verts.new(Vector((x, y, 0.004))) for x, y in ((-2, -2), (2, -2), (2, 2), (-2, 2))]
    b.paint([b.bm.faces.new(flat)], lambda co: (0.15, 0.10, 0.06, 1.0))
    for r in range(8):
        y = -1.75 + r * 0.5
        points = [Vector((x, y + rng.uniform(-0.02, 0.02), 0.02)) for x in (-1.98 + i * 0.33 for i in range(13))]
        b.tube(points, 0.11, 4, vary((0.20, 0.13, 0.08), rng), vary((0.26, 0.17, 0.10), rng))


CROPS = {
    "wheat": wheat, "corn": corn, "cabbage": cabbage, "carrot": carrot, "potato": potato, "sunflower": sunflower,
    "sugar_beet": sugar_beet, "flax": flax, "hemp": hemp,
}


def build(name, fill, seed):
    for existing in list(bpy.data.objects):
        bpy.data.objects.remove(existing, do_unlink=True)
    b = Builder()
    fill(b, random.Random(seed))
    mesh = bpy.data.meshes.new(name)
    b.bm.to_mesh(mesh)
    b.bm.free()
    # The colour attribute must be the active one, or the glTF exporter leaves it out
    colors = mesh.color_attributes.get("Color")
    if colors:
        mesh.color_attributes.active_color = colors
        mesh.color_attributes.render_color_index = mesh.color_attributes.active_color_index
    mesh.materials.append(material("FW_Crop"))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    path = os.path.join(OUT, f"{name}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True,
                              export_vertex_color="ACTIVE", export_all_vertex_colors=False)
    print(f"FWCROPS {name}: {len(obj.data.polygons)} faces -> {path}")


os.makedirs(OUT, exist_ok=True)
build("crop_soil", soil, 5)
for index, (crop, fill) in enumerate(CROPS.items()):
    build(f"crop_{crop}_young", lambda b, rng, f=fill: f(b, rng, False), 100 + index)
    build(f"crop_{crop}_ripe", lambda b, rng, f=fill: f(b, rng, True), 200 + index)
