# Builds the trains that come to a plot's station and writes one .glb each to RawAssets/processed/factory, next to the
# factory models, so Tools/import_external.py imports them the same way (Nanite, the FW_* slots bound to texture sets).
#   train_locomotive: a diesel shunter, its front (cab end) towards -X, the way trains leave a station (west)
#   train_wagon: a covered goods van; train_wagon_open: an open wagon heaped with goods
# Sizes in metres for standard gauge; the origin is the middle of the vehicle at the top of the rails, so the game stands
# it straight on the track.
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_trains.py
import math
import os
import random

import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "factory")
UV_METRES = 2.0
GAUGE = 1.435
WHEEL = 0.46  # wheel radius


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def finish(obj, mat, bevel=0.0, segments=3):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    if bevel > 0:
        mod = obj.modifiers.new("Bevel", "BEVEL")
        mod.width = bevel
        mod.segments = segments
        mod.limit_method = "ANGLE"
        mod.harden_normals = True
        bpy.ops.object.modifier_apply(modifier=mod.name)
    mod = obj.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL")
    mod.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.cube_project(cube_size=UV_METRES, correct_aspect=False, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.data.materials.clear()
    obj.data.materials.append(material(mat))
    for poly in obj.data.polygons:
        poly.use_smooth = True
    obj.select_set(False)
    return obj


def box(size, location, mat, bevel=0.01, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location, rotation=rotation)
    obj = bpy.context.active_object
    obj.scale = size
    return finish(obj, mat, bevel)


def cylinder(radius, depth, location, mat, rotation=(0, 0, 0), vertices=32, bevel=0.004):
    bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    return finish(bpy.context.active_object, mat, bevel)


def export(name):
    objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = name
    obj.data.name = name
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f"{name}.glb"), export_format="GLB", use_selection=False)
    print(f"FWTRAINS {name}: {sum(len(p.vertices) - 2 for p in obj.data.polygons)} triangles")


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def bogie(x):
    """Two wheelsets in a frame with springs and an axle box on each end of the axles."""
    box((2.6, 2.0, 0.22), (x, 0, WHEEL + 0.05), "FW_Steel", 0.02)
    for dx in (-0.9, 0.9):
        for side in (-1, 1):
            y = side * GAUGE / 2
            cylinder(WHEEL, 0.13, (x + dx, y, WHEEL), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=40)
            # The flange, a little bigger, on the inside of the rail
            cylinder(WHEEL + 0.03, 0.025, (x + dx, y - side * 0.07, WHEEL), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=40)
            box((0.32, 0.18, 0.26), (x + dx, side * (GAUGE / 2 + 0.2), WHEEL), "FW_PaintedSteel", 0.02)
            for k in range(3):
                cylinder(0.05, 0.25, (x + dx - 0.12 + k * 0.12, side * (GAUGE / 2 + 0.2), WHEEL + 0.27), "FW_Steel", vertices=10)
        cylinder(0.08, GAUGE, (x + dx, 0, WHEEL), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=16)


def couplers(half):
    for end in (-1, 1):
        x = end * half
        box((0.4, 0.3, 0.25), (x + end * 0.2, 0, 1.05), "FW_Steel", 0.02)
        for y in (-0.85, 0.85):
            cylinder(0.18, 0.35, (x + end * 0.17, y, 1.05), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=24)
            cylinder(0.22, 0.04, (x + end * 0.36, y, 1.05), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=24)


def locomotive():
    reset()
    length = 14.0
    half = length / 2
    box((length, 2.9, 0.4), (0, 0, 1.25), "FW_PaintedSteel", 0.03)
    bogie(-4.4)
    bogie(4.4)
    couplers(half)
    # The cab at the front (-X), the long hood with the engine behind it
    box((3.0, 3.0, 2.5), (-half + 1.7, 0, 2.7), "FW_PaintRed", 0.05)
    box((3.1, 3.1, 0.15), (-half + 1.7, 0, 4.0), "FW_PaintWhite", 0.04)
    box((9.6, 2.3, 2.1), (1.9, 0, 2.5), "FW_PaintRed", 0.08)
    box((9.7, 2.32, 0.18), (1.9, 0, 1.9), "FW_Worn", 0.01)
    box((3.1, 3.02, 0.25), (-half + 1.7, 0, 1.75), "FW_Worn", 0.01)
    # Windows round the cab, grilles and doors along the hood, the exhaust and the fan on top
    for y in (-1.51, 1.51):
        box((1.6, 0.03, 0.9), (-half + 1.8, y, 3.25), "FW_Glass", 0.0)
        for i in range(6):
            box((0.8, 0.03, 1.2), (-1.8 + i * 1.5, 1.17 if y > 0 else -1.17, 2.5), "FW_PaintedSteel", 0.01)
    box((0.03, 2.4, 1.0), (-half + 0.19, 0, 3.2), "FW_Glass", 0.0)
    box((0.03, 1.6, 0.8), (-half + 3.21, 0, 3.3), "FW_Glass", 0.0)
    cylinder(0.18, 0.5, (3.0, 0, 3.75), "FW_Steel", vertices=20)
    cylinder(0.6, 0.12, (5.5, 0, 3.6), "FW_Steel", vertices=32)
    cylinder(0.55, 0.13, (0.5, 0, 3.6), "FW_Steel", vertices=32)
    # Head lamps and the walkway rails along the sides
    for y in (-0.9, 0.9):
        cylinder(0.12, 0.08, (-half - 0.02, y, 1.95), "FW_PaintWhite", rotation=(0, math.pi / 2, 0), vertices=20)
        cylinder(0.12, 0.08, (half + 0.02, y, 1.95), "FW_PaintWhite", rotation=(0, math.pi / 2, 0), vertices=20)
    for side in (-1, 1):
        for i in range(8):
            cylinder(0.02, 1.0, (-2.6 + i * 1.3, side * 1.38, 1.95), "FW_Worn", vertices=8)
        cylinder(0.025, 10.0, (1.9, side * 1.38, 2.45), "FW_Worn", rotation=(0, math.pi / 2, 0), vertices=8)
    # Steps up to the cab
    for side in (-1, 1):
        for k in range(3):
            box((0.6, 0.3, 0.04), (-half + 0.9, side * 1.5, 0.75 + k * 0.32), "FW_Steel", 0.004)
    export("train_locomotive")


def wagon():
    reset()
    length = 13.0
    half = length / 2
    box((length, 2.9, 0.35), (0, 0, 1.2), "FW_Steel", 0.02)
    bogie(-4.6)
    bogie(4.6)
    couplers(half)
    box((length - 0.2, 2.8, 2.7), (0, 0, 2.75), "FW_PaintBlue", 0.04)
    # A curved roof, ribs along the sides and the sliding door in the middle
    cylinder(1.5, length - 0.2, (0, 0, 3.6), "FW_PaintWhite", rotation=(0, math.pi / 2, 0), vertices=48)
    for i in range(11):
        for y in (-1.41, 1.41):
            box((0.08, 0.04, 2.6), (-half + 0.8 + i * 1.14, y, 2.75), "FW_PaintBlue", 0.01)
    for y in (-1.42, 1.42):
        box((2.4, 0.05, 2.3), (0, y, 2.65), "FW_PaintedSteel", 0.02)
        box((2.6, 0.08, 0.08), (0, y * 1.02, 3.85), "FW_Steel", 0.01)
    box((length, 2.98, 0.06), (0, 0, 4.1), "FW_PaintWhite", 0.0)
    export("train_wagon")


def wagon_open():
    reset()
    length = 13.0
    half = length / 2
    box((length, 2.9, 0.35), (0, 0, 1.2), "FW_Steel", 0.02)
    bogie(-4.6)
    bogie(4.6)
    couplers(half)
    # Low steel sides with stakes, and the goods heaped inside
    for y in (-1.42, 1.42):
        box((length - 0.2, 0.06, 1.3), (0, y, 2.0), "FW_Worn", 0.01)
        for i in range(9):
            box((0.14, 0.12, 1.4), (-half + 0.6 + i * 1.47, y * 1.04, 2.0), "FW_PaintedSteel", 0.01)
    for x in (-half + 0.13, half - 0.13):
        box((0.06, 2.8, 1.3), (x, 0, 2.0), "FW_Worn", 0.01)
    rng = random.Random(7)
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=1.0, location=(0, 0, 2.3))
    heap = bpy.context.active_object
    heap.scale = (length / 2 - 0.4, 1.3, 0.75)
    finish(heap, "FW_Rubber", 0.0)
    for _ in range(26):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=rng.uniform(0.18, 0.32),
                                              location=(rng.uniform(-half + 1, half - 1), rng.uniform(-1.0, 1.0), rng.uniform(2.65, 2.95)))
        finish(bpy.context.active_object, "FW_Rubber", 0.0)
    export("train_wagon_open")


locomotive()
wagon()
wagon_open()
print("FWTRAINS done")
