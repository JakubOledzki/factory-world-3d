# Renders the world map's close-up textures: each biome seen from straight above, lit by a low sun from the north-west
# like the shaded relief, seamless (what leaves one edge comes back at the other), into one atlas
# RawAssets/processed/worldmap/world_tiles.png (4 x 4 tiles of 1024 px, biome id left to right, top to bottom). The map's
# material (M_FWWorldMap) repeats a tile every few plots and blends the biomes into each other along wavy borders.
# Real geometry: conifers and broadleaf trees, fields in rows with hedges, dunes, rock ridges with snow, glacier ice with
# crevasses, tundra with ponds and snow patches, town blocks, palms. Everything is built here (no outside assets). Run:
#   blender --background --factory-startup --python Tools/blender/make_world_tiles.py
import math
import os
import random

import bpy
import numpy as np

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "worldmap")
TILE = 1024
SIZE = 60.0  # metres a tile shows
GRID = 4
OCEAN, LAKE, FOREST, FARMLAND, GRASSLAND, DESERT, MOUNTAINS, TUNDRA, ICE, CITY, COAST, LAKESHORE, OASIS = range(13)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = TILE
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("sky")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.65, 0.8, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.55
    scene.world = world
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.2
    sun.data.angle = math.radians(3)
    # Light from the north-west (top-left of the map), 40° above the horizon
    sun.rotation_euler = (math.radians(50), 0, math.radians(135))
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = SIZE
    cam.data.clip_end = 500
    cam.location = (0, 0, 100)
    scene.collection.objects.link(cam)
    scene.camera = cam


def srgb(c):
    return tuple(((x + 0.055) / 1.055) ** 2.4 if x > 0.04045 else x / 12.92 for x in c)


def material(name, color, rough=0.8):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*srgb(color), 1)
    bsdf.inputs["Roughness"].default_value = rough
    return mat


def vertex_material(rough=0.85):
    mat = bpy.data.materials.new("vcol")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    attr = nodes.new("ShaderNodeVertexColor")
    attr.layer_name = "Col"
    mat.node_tree.links.new(attr.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
    nodes["Principled BSDF"].inputs["Roughness"].default_value = rough
    return mat


def periodic(n, seed, octaves=5, base=4, ridged=False):
    """Fractal noise on an n x n torus: cells wrap, so the field tiles seamlessly."""
    rng = np.random.default_rng(seed)
    out = np.zeros((n, n), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        cells = base * 2 ** o
        grid = rng.random((cells, cells)).astype(np.float32)
        xs = np.arange(n) / n * cells
        i0 = np.floor(xs).astype(int)
        f = xs - i0
        f = f * f * (3 - 2 * f)
        i1 = (i0 + 1) % cells
        top = grid[i0][:, i0] * (1 - f[None, :]) + grid[i0][:, i1] * f[None, :]
        bottom = grid[i1][:, i0] * (1 - f[None, :]) + grid[i1][:, i1] * f[None, :]
        layer = top * (1 - f[:, None]) + bottom * f[:, None]
        if ridged:
            layer = 1 - np.abs(layer * 2 - 1)
        out += layer * amp
        total += amp
        amp *= 0.5
    return out / total


def mix(t, a, b):
    t = t[..., None]
    return np.array(a, np.float32) * (1 - t) + np.array(b, np.float32) * t


def terrain(heights, colors, rough=0.85):
    """A grid mesh exactly one tile across from a periodic height field (n x n) and colours (n x n x 3, sRGB)."""
    h = np.pad(heights, ((0, 1), (0, 1)), mode="wrap")
    c = np.pad(colors, ((0, 1), (0, 1), (0, 0)), mode="wrap")
    n = h.shape[0]
    xs = np.linspace(-SIZE / 2, SIZE / 2, n)
    verts = [(xs[j], -xs[i], float(h[i, j])) for i in range(n) for j in range(n)]
    faces = [(i * n + j, i * n + j + 1, (i + 1) * n + j + 1, (i + 1) * n + j) for i in range(n - 1) for j in range(n - 1)]
    mesh = bpy.data.meshes.new("ground")
    mesh.from_pydata(verts, [], faces)
    attr = mesh.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
    lin = np.array([srgb(v) for v in c.reshape(-1, 3)], dtype=np.float32)
    attr.data.foreach_set("color", np.concatenate([lin, np.ones((len(lin), 1), np.float32)], axis=1).ravel())
    for poly in mesh.polygons:
        poly.use_smooth = True
    obj = bpy.data.objects.new("ground", mesh)
    obj.data.materials.append(vertex_material(rough))
    bpy.context.scene.collection.objects.link(obj)
    return obj


def height_at(h, x, y):
    n = h.shape[0]
    j = int((x / SIZE + 0.5) * n) % n
    i = int((-y / SIZE + 0.5) * n) % n
    return float(h[i, j])


def scatter(rng, count, spacing):
    """Points on the tile's torus, at least `spacing` apart across the edges too."""
    points = []
    for _ in range(count * 40):
        if len(points) >= count:
            break
        p = (rng.uniform(-SIZE / 2, SIZE / 2), rng.uniform(-SIZE / 2, SIZE / 2))
        ok = True
        for q in points:
            dx = abs(p[0] - q[0]); dx = min(dx, SIZE - dx)
            dy = abs(p[1] - q[1]); dy = min(dy, SIZE - dy)
            if dx * dx + dy * dy < spacing * spacing:
                ok = False
                break
        if ok:
            points.append(p)
    return points


def wrapped(x, y, margin=6.0):
    """The point and its copies across the edges it is near, so things standing on an edge show on both sides."""
    for dx in (-SIZE, 0.0, SIZE):
        for dy in (-SIZE, 0.0, SIZE):
            if abs(x + dx) <= SIZE / 2 + margin and abs(y + dy) <= SIZE / 2 + margin:
                yield x + dx, y + dy


def conifer(x, y, z, height, rng):
    tone = (0.07, 0.17 + rng.uniform(-0.03, 0.04), 0.07)
    mat = material(f"needles{int(tone[1] * 100)}", tone, 0.9)
    for X, Y in wrapped(x, y):
        for k, (r, h) in enumerate(((0.42, 0.55), (0.32, 0.45), (0.2, 0.35))):
            bpy.ops.mesh.primitive_cone_add(vertices=9, radius1=height * r, radius2=0, depth=height * h, location=(X, Y, z + height * (0.25 + k * 0.22)))
            bpy.context.active_object.data.materials.append(mat)


def broadleaf(x, y, z, size, rng, tone=(0.13, 0.25, 0.08)):
    mat = material(f"leaves{int(tone[0] * 100)}{int(tone[1] * 100)}", tone, 0.9)
    blobs = [(rng.uniform(-0.3, 0.3) * size, rng.uniform(-0.3, 0.3) * size, size * rng.uniform(0.35, 0.55)) for _ in range(3)]
    for X, Y in wrapped(x, y):
        for ox, oy, r in blobs:
            bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=r, location=(X + ox, Y + oy, z + size * 0.8))
            bpy.context.active_object.scale.z = 0.75
            bpy.context.active_object.data.materials.append(mat)


def box(sx, sy, sz, x, y, z, color, wrap=True):
    mat = material(f"c{color}", color, 0.7)
    for X, Y in (wrapped(x, y, max(sx, sy)) if wrap else [(x, y)]):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(X, Y, z))
        obj = bpy.context.active_object
        obj.scale = (sx, sy, sz)
        obj.data.materials.append(mat)


def build(biome):
    rng = random.Random(biome * 17 + 3)
    seed = biome * 101
    n = 160
    if biome in (OCEAN, LAKE):
        deep, light = ((0.03, 0.09, 0.17), (0.05, 0.14, 0.24)) if biome == OCEAN else ((0.05, 0.16, 0.24), (0.08, 0.24, 0.30))
        terrain((periodic(n, seed, 4, 8) - 0.5) * 0.25, mix(periodic(n, seed + 1, 3, 3), deep, light), rough=0.12)
    elif biome == FOREST:
        terrain((periodic(n, seed) - 0.5) * 1.5, mix(periodic(n, seed, 4, 6), (0.14, 0.20, 0.08), (0.20, 0.25, 0.10)))
        for x, y in scatter(rng, 520, 2.0):
            if rng.random() < 0.65:
                conifer(x, y, 0, rng.uniform(4.5, 7.0), rng)
            else:
                broadleaf(x, y, 0, rng.uniform(1.6, 2.4), rng, (0.13 + rng.uniform(-0.02, 0.04), 0.25 + rng.uniform(-0.03, 0.05), 0.07))
    elif biome == FARMLAND:
        terrain(np.zeros((8, 8), np.float32), np.full((8, 8, 3), (0.30, 0.26, 0.16), np.float32))
        crops = [(0.62, 0.55, 0.26), (0.32, 0.45, 0.17), (0.40, 0.50, 0.20), (0.55, 0.42, 0.25), (0.72, 0.64, 0.33), (0.27, 0.38, 0.15)]
        # Field edges, the tile's own edge one of them so the patchwork continues across it
        xs = [-SIZE / 2, -12.0, 6.0, SIZE / 2]
        ys = [-SIZE / 2, -4.0, 14.0, SIZE / 2]
        for i in range(3):
            for j in range(3):
                color = rng.choice(crops)
                x0, x1, y0, y1 = xs[i] + 0.4, xs[i + 1] - 0.4, ys[j] + 0.4, ys[j + 1] - 0.4
                box(x1 - x0, y1 - y0, 0.1, (x0 + x1) / 2, (y0 + y1) / 2, 0.05, color, wrap=False)
                along_x = rng.random() < 0.5
                span = (y1 - y0) if along_x else (x1 - x0)
                row = tuple(v * 0.82 for v in color)
                for k in range(int(span / 0.7)):
                    if along_x:
                        box(x1 - x0, 0.25, 0.08, (x0 + x1) / 2, y0 + 0.35 + k * 0.7, 0.12, row, wrap=False)
                    else:
                        box(0.25, y1 - y0, 0.08, x0 + 0.35 + k * 0.7, (y0 + y1) / 2, 0.12, row, wrap=False)
        for x in xs[:-1]:
            for y in np.arange(-SIZE / 2, SIZE / 2, 2.4):
                if rng.random() < 0.45:
                    broadleaf(x, y, 0, rng.uniform(0.8, 1.3), rng)
        box(3, 2, 1.6, -3.0, 4.6, 0.8, (0.55, 0.20, 0.14))
        box(2, 2, 1.2, -0.6, 5.2, 0.6, (0.62, 0.60, 0.56))
    elif biome in (GRASSLAND, COAST, LAKESHORE):
        green = (0.33, 0.38, 0.17) if biome == GRASSLAND else (0.30, 0.38, 0.17)
        colors = mix(periodic(n, seed, 4, 6), green, (0.48, 0.48, 0.25))
        if biome == COAST:
            sand = np.clip((periodic(n, seed + 9, 3, 3) - 0.55) * 6, 0, 1)
            colors = colors * (1 - sand[..., None]) + np.array((0.78, 0.70, 0.50), np.float32) * sand[..., None]
        terrain((periodic(n, seed) - 0.5) * 1.2, colors)
        for x, y in scatter(rng, 50, 4.5):
            broadleaf(x, y, 0, rng.uniform(0.9, 2.0), rng, (0.16, 0.27, 0.09))
        if biome == LAKESHORE:
            mat = material("reed", (0.42, 0.40, 0.18))
            for x, y in scatter(rng, 140, 0.9):
                for X, Y in wrapped(x, y, 1):
                    bpy.ops.mesh.primitive_cone_add(vertices=5, radius1=0.25, depth=1.2, location=(X, Y, 0.6))
                    bpy.context.active_object.data.materials.append(mat)
    elif biome == DESERT:
        u = np.arange(n)[None, :] / n
        v = np.arange(n)[:, None] / n
        warp = periodic(n, seed, 3, 2) * 2.0
        dunes = np.abs(np.sin((u * 4 + v * 2 + warp) * math.pi)) ** 1.8 * 2.4
        terrain(dunes + (periodic(n, seed + 1, 4, 6) - 0.5) * 0.6, mix(periodic(n, seed + 2, 4, 5), (0.80, 0.64, 0.42), (0.88, 0.74, 0.52)))
    elif biome == MOUNTAINS:
        n = 220
        h = periodic(n, seed, 5, 4, ridged=True) ** 3 * 26 + periodic(n, seed + 5, 4, 3) * 4
        slope = np.hypot(*np.gradient(h))
        snow = np.clip((h - np.quantile(h, 0.72)) / 3, 0, 1) * np.clip(1.6 - slope * 0.5, 0, 1)
        colors = mix(periodic(n, seed, 4, 10), (0.36, 0.33, 0.30), (0.52, 0.48, 0.43))
        low = np.clip((np.quantile(h, 0.25) - h) / 3 + 0.3, 0, 1)
        colors = colors * (1 - low[..., None]) + np.array((0.24, 0.30, 0.14), np.float32) * low[..., None]
        colors = colors * (1 - snow[..., None]) + np.array((0.93, 0.95, 0.98), np.float32) * snow[..., None]
        terrain(h, colors)
        floor = np.quantile(h, 0.2)
        for x, y in scatter(rng, 120, 2.5):
            z = height_at(h, x, y)
            if z < floor:
                conifer(x, y, z, rng.uniform(2.5, 4.0), rng)
    elif biome == ICE:
        u = np.arange(n)[None, :] / n
        v = np.arange(n)[:, None] / n
        cracks = np.abs(np.sin((v * 12 + u * 2 + periodic(n, seed + 3, 3, 4) * 2.0) * math.pi))
        crevasse = np.clip((0.07 - cracks) / 0.07, 0, 1) * np.clip((periodic(n, seed + 4, 3, 3) - 0.4) * 6, 0, 1)
        h = periodic(n, seed, 4, 3) * 3 - crevasse * 1.5
        colors = mix(periodic(n, seed, 4, 4), (0.86, 0.91, 0.96), (0.97, 0.98, 1.0))
        colors = colors * (1 - crevasse[..., None] * 0.8) + np.array((0.30, 0.50, 0.70), np.float32) * crevasse[..., None] * 0.8
        terrain(h, colors, rough=0.3)
    elif biome == TUNDRA:
        colors = mix(periodic(n, seed, 4, 9), (0.40, 0.38, 0.27), (0.52, 0.48, 0.36))
        snowpatch = np.clip((periodic(n, seed + 7, 3, 5) - 0.62) * 8, 0, 1)[..., None] * 0.75
        colors = colors * (1 - snowpatch) + np.array((0.86, 0.89, 0.93), np.float32) * snowpatch
        water = np.clip((periodic(n, seed + 8, 3, 6) - 0.68) * 12, 0, 1)[..., None]
        colors = colors * (1 - water) + np.array((0.07, 0.18, 0.25), np.float32) * water
        terrain((periodic(n, seed) - 0.5) * 1.0, colors)
        for x, y in scatter(rng, 120, 1.6):
            broadleaf(x, y, 0, rng.uniform(0.35, 0.6), rng, (0.24, 0.27, 0.13))
    elif biome == CITY:
        terrain(np.zeros((8, 8), np.float32), np.full((8, 8, 3), (0.22, 0.22, 0.23), np.float32))
        block = 10.0  # SIZE is a whole number of blocks, so streets run on across the edges
        for bx in np.arange(-SIZE / 2, SIZE / 2, block):
            for by in np.arange(-SIZE / 2, SIZE / 2, block):
                if rng.random() < 0.12:
                    box(block - 2.0, block - 2.0, 0.1, bx + block / 2, by + block / 2, 0.05, (0.25, 0.36, 0.16), wrap=False)
                    for _ in range(4):
                        broadleaf(bx + rng.uniform(2, block - 2), by + rng.uniform(2, block - 2), 0, 1.0, rng)
                    continue
                for _ in range(rng.randint(3, 6)):
                    w, d = rng.uniform(2.0, 4.0), rng.uniform(2.0, 4.0)
                    hgt = rng.uniform(2.0, 9.0)
                    roof = rng.choice([(0.55, 0.25, 0.18), (0.45, 0.45, 0.46), (0.62, 0.60, 0.56), (0.35, 0.36, 0.38)])
                    box(w, d, hgt, bx + rng.uniform(1.5 + w / 2, block - 1.5 - w / 2), by + rng.uniform(1.5 + d / 2, block - 1.5 - d / 2), hgt / 2, roof, wrap=False)
    elif biome == OASIS:
        terrain((periodic(n, seed) - 0.5) * 1.5, mix(periodic(n, seed, 4, 6), (0.80, 0.66, 0.44), (0.86, 0.72, 0.50)))
        bpy.ops.mesh.primitive_cylinder_add(vertices=32, radius=7.0, depth=0.3, location=(0, 0, 0.5))
        bpy.context.active_object.scale.y = 0.7
        bpy.context.active_object.data.materials.append(material("oasiswater", (0.06, 0.25, 0.30), 0.1))
        mat = material("palm", (0.18, 0.33, 0.10))
        for x, y in scatter(rng, 70, 2.4):
            if x * x / 49 + y * y / 25 < 1.2:
                continue
            fronds = [k / 7 * 2 * math.pi + rng.random() for k in range(7)]
            for X, Y in wrapped(x, y, 2):
                for a in fronds:
                    bpy.ops.mesh.primitive_cube_add(size=1, location=(X + math.cos(a) * 0.9, Y + math.sin(a) * 0.9, 4.0))
                    frond = bpy.context.active_object
                    frond.scale = (1.8, 0.35, 0.05)
                    frond.rotation_euler = (0, 0.25, a)
                    frond.data.materials.append(mat)


def render(biome):
    reset()
    build(biome)
    path = os.path.join(OUT, f"_tile_{biome}.png")
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(path)
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(TILE, TILE, 4)[::-1]
    bpy.data.images.remove(image)
    os.remove(path)
    return pixels


os.makedirs(OUT, exist_ok=True)
atlas = np.ones((GRID * TILE, GRID * TILE, 4), np.float32)
for biome in range(13):
    r, c = divmod(biome, GRID)
    atlas[r * TILE:(r + 1) * TILE, c * TILE:(c + 1) * TILE] = render(biome)
    print(f"FWTILES biome {biome}")
atlas[..., 3] = 1.0
image = bpy.data.images.new("world_tiles", GRID * TILE, GRID * TILE, alpha=True)
image.colorspace_settings.name = "sRGB"
image.pixels.foreach_set(np.ascontiguousarray(atlas[::-1]).ravel())
image.filepath_raw = os.path.join(OUT, "world_tiles.png")
image.file_format = "PNG"
image.save()
print("FWTILES done")
