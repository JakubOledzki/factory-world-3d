# Builds dwarf mountain pine shrubs (kosodrzewina, Pinus mugo) and writes one .glb per variant to
# RawAssets/processed/mugo. The Tatra belt above the spruce forest: many curved stems from one base creep
# along the slope and turn up at their tips (1-2.5 m high, a few metres across), side shoots end in dense
# brushes of paired needles. Everything is real geometry (Nanite foliage, no alpha cards).
# Sizes in metres; the origin is the base of the shrub at ground level. Material slots, bound by
# Tools/import_external.py:
#   FW_MugoBark (Poly Haven pine_tree_01 bark, CC0; UV U around the stem, V along it)
#   FW_MugoNeedles (plain colour; the vertex colour carries the shade of each brush)
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_mugo_pine.py
import math
import os
import random

import bmesh
import bpy

from mathutils import Matrix, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "mugo")
VARIANTS = [
    # name, seed, stems, spread (m), height (m)
    ("mugo_pine_01", 11, 7, 2.6, 1.6),
    ("mugo_pine_02", 23, 9, 3.2, 2.1),
    ("mugo_pine_03", 37, 6, 2.0, 1.2),
    ("mugo_pine_04", 41, 8, 2.9, 2.4),
    ("mugo_pine_05", 59, 5, 1.6, 1.0),
]
BARK_UV_METRES = 0.6  # one bark texture repeat along a stem


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


class Builder:
    def __init__(self):
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.color = self.bm.loops.layers.color.new("Color")

    def face(self, verts, uvs, color, slot):
        face = self.bm.faces.new(verts)
        face.material_index = slot
        face.smooth = True
        for loop, uv in zip(face.loops, uvs):
            loop[self.uv].uv = uv
            loop[self.color] = color
        return face

    def tube(self, points, radii, sides):
        """A stem along points (parallel-transported rings), bark UVs wrap around it."""
        rings = []
        normal = (points[1] - points[0]).orthogonal().normalized()
        length = 0.0
        for i, point in enumerate(points):
            tangent = (points[min(i + 1, len(points) - 1)] - points[max(i - 1, 0)]).normalized()
            normal = (normal - tangent * normal.dot(tangent)).normalized()
            binormal = tangent.cross(normal)
            if i > 0:
                length += (point - points[i - 1]).length
            ring = []
            for s in range(sides + 1):
                angle = 2 * math.pi * s / sides
                offset = (normal * math.cos(angle) + binormal * math.sin(angle)) * radii[i]
                ring.append((self.bm.verts.new(point + offset), (s / sides, length / BARK_UV_METRES)))
            rings.append(ring)
        white = (1.0, 1.0, 1.0, 1.0)
        for a, b in zip(rings, rings[1:]):
            for s in range(sides):
                quad = [a[s], a[s + 1], b[s + 1], b[s]]
                self.face([v for v, _ in quad], [uv for _, uv in quad], white, 0)
        # Closed tip
        tip = self.bm.verts.new(points[-1] + (points[-1] - points[-2]).normalized() * radii[-1])
        last = rings[-1]
        for s in range(sides):
            self.face([last[s][0], last[s + 1][0], tip], [last[s][1], last[s + 1][1], last[s][1]], white, 0)

    def needle(self, root, direction, length, width, color):
        """One needle: a slim three-sided spike."""
        side = direction.orthogonal().normalized()
        other = direction.cross(side).normalized()
        base = [root + (side * math.cos(a) + other * math.sin(a)) * width for a in (0.0, 2.1, 4.2)]
        verts = [self.bm.verts.new(p) for p in base]
        tip = self.bm.verts.new(root + direction * length)
        dark = tuple(c * 0.55 for c in color[:3]) + (1.0,)
        for i in range(3):
            face = self.face([verts[i], verts[(i + 1) % 3], tip], [(0, 0), (0, 0), (0, 0)], color, 1)
            # Darker at the base, where the brush shades itself
            face.loops[0][self.color] = dark
            face.loops[1][self.color] = dark

    def sleeve(self, points, start, rng):
        """Needles all round the outer part of a shoot (from fraction start to its tip), sweeping forward
        like a bottle brush: the dense, dark foliage that makes a dwarf pine thicket."""
        shade = rng.uniform(0.8, 1.15)
        lengths = [(b - a).length for a, b in zip(points, points[1:])]
        total = sum(lengths)
        d = total * start
        while d < total:
            # Point and direction at distance d along the shoot
            walked, i = 0.0, 0
            while i < len(lengths) - 1 and walked + lengths[i] < d:
                walked += lengths[i]
                i += 1
            t = (d - walked) / lengths[i]
            at = points[i].lerp(points[i + 1], t)
            axis = (points[i + 1] - points[i]).normalized()
            # Younger needles towards the tip are lighter
            age = (d / total - start) / (1.0 - start)
            tone = shade * (0.75 + 0.35 * age)
            color = (0.75 * tone, 0.95 * tone, 0.7 * tone, 1.0)
            spin0 = rng.uniform(0, 2 * math.pi)
            for k in range(5):
                out = Matrix.Rotation(spin0 + k * 2 * math.pi / 5, 3, axis) @ axis.orthogonal().normalized()
                lean = rng.uniform(0.5, 0.95)
                direction = (axis * math.cos(lean) + out * math.sin(lean)).normalized()
                self.needle(at, direction, rng.uniform(0.06, 0.09), 0.0025, color)
            d += 0.018


def curve(start, direction, length, rise, rng, steps):
    """A stem from the shared base: up and out, then arching over towards the ground."""
    points = []
    heading = math.atan2(direction.y, direction.x)
    wobble = rng.uniform(0, 6.0)
    for i in range(steps + 1):
        t = i / steps
        bend = math.sin(t * 2.3 + wobble) * 0.25
        horizontal = length * t
        x = math.cos(heading + bend) * horizontal
        y = math.sin(heading + bend) * horizontal
        # Rising out of the base, then levelling off and arching over: together the stems make a low dome
        u = min(1.0, t / 0.8)
        z = 0.05 + rise * (1.0 - (1.0 - u) ** 2) - 0.25 * rise * max(0.0, t - 0.8) / 0.2 + math.sin(t * 7.0 + wobble) * 0.05 * t
        points.append(start + Vector((x, y, z)))
    return points


def twig(base, direction, length, rng, steps=4):
    """A shoot: a gently curving line that lifts towards the light."""
    points = [base]
    for k in range(1, steps + 1):
        direction = (direction + Vector((rng.uniform(-0.15, 0.15), rng.uniform(-0.15, 0.15), 0.12))).normalized()
        points.append(points[-1] + direction * length / steps)
    return points


def shrub(name, seed, stems, spread, height):
    rng = random.Random(seed)
    b = Builder()
    for s in range(stems):
        heading = 2 * math.pi * (s + rng.uniform(-0.3, 0.3)) / stems
        length = spread * rng.uniform(0.3, 1.0)
        rise = height * rng.uniform(0.5, 0.9)
        start = Vector((rng.uniform(-0.1, 0.1), rng.uniform(-0.1, 0.1), 0.0))
        points = curve(start, Vector((math.cos(heading), math.sin(heading), 0)), length, rise, rng, 14)
        base_radius = rng.uniform(0.045, 0.07) * (spread / 2.6) ** 0.5
        radii = [base_radius * (1.0 - 0.8 * i / (len(points) - 1)) for i in range(len(points))]
        b.tube(points, radii, 7)
        # The leader keeps its needles on its outer third
        b.sleeve(points[9:], 0.0, rng)
        # Shoots all along the stem; nearer the tip they are longer and stand up more, which rounds the
        # thicket into a dome
        for i in range(2, len(points) - 1):
            for _ in range(rng.randint(3, 4)):
                along = points[i].lerp(points[i + 1], rng.random())
                tangent = (points[i + 1] - points[i]).normalized()
                side = Matrix.Rotation(rng.uniform(0, 2 * math.pi), 3, tangent) @ tangent.orthogonal().normalized()
                height_bias = 0.25 + 0.5 * (1.0 - i / len(points))  # low on the stem: shoots climb more
                side.z = side.z * 0.6 + height_bias * 0.6  # some hang to the sides, filling the dome down to the ground
                direction = (tangent * 0.9 + side.normalized()).normalized()
                shoot_length = rng.uniform(0.35, 0.8) * (0.7 + 0.5 * (1.0 - abs(i / len(points) - 0.6)))
                shoot = twig(along, direction, shoot_length, rng)
                r = radii[i] * 0.4
                b.tube(shoot, [r * (1.0 - 0.7 * k / 4) for k in range(5)], 5)
                b.sleeve(shoot, 0.2, rng)
                # Each shoot forks into twigs, all needled
                for _ in range(2):
                    k = rng.randint(1, 3)
                    fork = Matrix.Rotation(rng.uniform(-0.9, 0.9), 3, Vector((0, 0, 1))) @ direction
                    small = twig(shoot[k], (fork + Vector((0, 0, 0.3))).normalized(), shoot_length * rng.uniform(0.4, 0.7), rng, 3)
                    b.tube(small, [r * 0.5 * (1.0 - 0.6 * k / 3) for k in range(4)], 4)
                    b.sleeve(small, 0.2, rng)

    mesh = bpy.data.meshes.new(name)
    b.bm.to_mesh(mesh)
    b.bm.free()
    # The colour attribute must be the active one, or the glTF exporter leaves it out (and the needles lose their shading)
    colors = mesh.color_attributes.get("Color")
    if colors:
        mesh.color_attributes.active_color = colors
        mesh.color_attributes.render_color_index = mesh.color_attributes.active_color_index
    mesh.materials.append(material("FW_MugoBark"))
    mesh.materials.append(material("FW_MugoNeedles"))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


os.makedirs(OUT, exist_ok=True)
for name, seed, stems, spread, height in VARIANTS:
    for existing in list(bpy.data.objects):
        bpy.data.objects.remove(existing, do_unlink=True)
    obj = shrub(name, seed, stems, spread, height)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    path = os.path.join(OUT, f"{name}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True,
                              export_vertex_color="ACTIVE", export_all_vertex_colors=False)
    print(f"FWMUGO {name}: {len(obj.data.polygons)} faces -> {path}")
