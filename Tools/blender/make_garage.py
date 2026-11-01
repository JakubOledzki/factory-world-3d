# Builds the player's garage in Blender and writes one .glb per piece to RawAssets/processed/factory, next to the
# factory models, so Tools/import_external.py imports them the same way (Nanite, the FW_* slots bound to texture sets).
# The room: 8 x 6 m inside (the 2D game's 16 x 12 cells of 0.5 m), 3.2 m high, the roller door in the front wall (+Y).
#   garage_floor, garage_walls, garage_ceiling: the shell; the game paints floor and walls by the garage's upgrades
#   garage_door: the roller door in its opening; garage_lamp: a pendant lamp; garage_radiator; garage_board (the
#   notice board where the upgrades are planned)
# The furniture, each with its origin in the middle of its footprint at floor level and its front towards +Y:
#   garage_trash_bin, garage_shelf, garage_barrel_blue, garage_barrel_red, garage_tyres, garage_pallet,
#   garage_gas_cylinder, garage_sorting_machine, garage_wardrobe, garage_trophy_case, and plain stand-ins for the
#   workshop's workbench, anvil, furnace and research table when their factory models are not there
# Material slots as in make_factory.py, plus flat paints bound by Tools/import_external.py:
#   FW_PaintBlue, FW_PaintRed, FW_PaintWhite, FW_PaintGreen
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_garage.py
import math
import os

import bpy
from mathutils import Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "factory")
UV_METRES = 2.0  # one texture repeat
ROOM_X, ROOM_Y, ROOM_Z = 8.0, 6.0, 3.2
WALL = 0.25
DOOR_W, DOOR_H = 3.0, 2.6


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def finish(obj, mat, bevel=0.0, segments=3, uv=True):
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
    if uv:
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


def box(size, location, mat, bevel=0.008, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location, rotation=rotation)
    obj = bpy.context.active_object
    obj.scale = size
    return finish(obj, mat, bevel)


def cylinder(radius, depth, location, mat, rotation=(0, 0, 0), vertices=32, bevel=0.004, top_radius=None):
    if top_radius is None:
        bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    else:
        bpy.ops.mesh.primitive_cone_add(radius1=radius, radius2=top_radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    return finish(bpy.context.active_object, mat, bevel)


def torus(major, minor, location, mat, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_torus_add(major_radius=major, minor_radius=minor, location=location, rotation=rotation,
                                     major_segments=40, minor_segments=14)
    return finish(bpy.context.active_object, mat, 0.0)


def sphere(radius, location, mat, scale=(1, 1, 1)):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, location=location, segments=24, ring_count=12)
    obj = bpy.context.active_object
    obj.scale = scale
    return finish(obj, mat, 0.0)


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
    print(f"FWGARAGE {name}: {sum(len(p.vertices) - 2 for p in obj.data.polygons)} triangles")


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


# --- The room ------------------------------------------------------------------------------------------------------

def floor():
    reset()
    box((ROOM_X + 2 * WALL, ROOM_Y + 2 * WALL, 0.2), (0, 0, -0.1), "FW_Concrete", bevel=0.0)
    export("garage_floor")


def walls():
    reset()
    # Walls reach 10 cm into the floor and the ceiling slabs, so no seam lets light in
    half_x, half_y = ROOM_X / 2 + WALL / 2, ROOM_Y / 2 + WALL / 2
    box((ROOM_X + 2 * WALL, WALL, ROOM_Z + 0.2), (0, -half_y, ROOM_Z / 2), "FW_Concrete", 0.0)
    box((WALL, ROOM_Y + 2 * WALL, ROOM_Z + 0.2), (-half_x, 0, ROOM_Z / 2), "FW_Concrete", 0.0)
    box((WALL, ROOM_Y + 2 * WALL, ROOM_Z + 0.2), (half_x, 0, ROOM_Z / 2), "FW_Concrete", 0.0)
    # The front wall round the door's opening
    side = (ROOM_X + 2 * WALL - DOOR_W) / 2
    box((side, WALL, ROOM_Z + 0.2), (-(DOOR_W / 2 + side / 2), half_y, ROOM_Z / 2), "FW_Concrete", 0.0)
    box((side, WALL, ROOM_Z + 0.2), (DOOR_W / 2 + side / 2, half_y, ROOM_Z / 2), "FW_Concrete", 0.0)
    box((DOOR_W, WALL, ROOM_Z - DOOR_H + 0.1), (0, half_y, DOOR_H + (ROOM_Z - DOOR_H + 0.1) / 2), "FW_Concrete", 0.0)
    # A skirting of bare concrete along the floor
    for x, y, sx, sy in ((0, -ROOM_Y / 2 + 0.03, ROOM_X, 0.06), (-ROOM_X / 2 + 0.03, 0, 0.06, ROOM_Y), (ROOM_X / 2 - 0.03, 0, 0.06, ROOM_Y)):
        box((sx, sy, 0.12), (x, y, 0.06), "FW_Steel", 0.004)
    export("garage_walls")


def ceiling():
    reset()
    box((ROOM_X + 2 * WALL, ROOM_Y + 2 * WALL, 0.2), (0, 0, ROOM_Z + 0.1), "FW_Concrete", 0.0)
    # Steel beams across
    for x in (-2.7, 0.0, 2.7):
        box((0.12, ROOM_Y, 0.22), (x, 0, ROOM_Z - 0.11), "FW_PaintedSteel", 0.006)
        box((0.2, ROOM_Y, 0.02), (x, 0, ROOM_Z - 0.21), "FW_PaintedSteel", 0.004)
    export("garage_ceiling")


def door():
    reset()
    y = ROOM_Y / 2 + 0.02
    # Slats of the roller door, the drum box over it and the guide rails
    slats = 13
    for i in range(slats):
        z = 0.1 + i * (DOOR_H - 0.1) / slats
        box((DOOR_W - 0.04, 0.05, (DOOR_H - 0.1) / slats - 0.012), (0, y, z + (DOOR_H - 0.1) / slats / 2), "FW_Steel", 0.006)
    box((DOOR_W - 0.04, 0.07, 0.1), (0, y, 0.05), "FW_PaintedSteel", 0.01)
    # A sheet behind the slats, so no daylight shows through the gaps between them
    box((DOOR_W + 0.1, 0.01, DOOR_H), (0, y + 0.04, DOOR_H / 2), "FW_Steel", 0.0)
    box((DOOR_W + 0.2, 0.32, 0.36), (0, y - 0.18, DOOR_H + 0.2), "FW_PaintedSteel", 0.01)
    for x in (-DOOR_W / 2 - 0.04, DOOR_W / 2 + 0.04):
        box((0.08, 0.1, DOOR_H), (x, y - 0.02, DOOR_H / 2), "FW_Steel", 0.004)
    # A handle low down
    box((0.3, 0.05, 0.04), (0, y - 0.05, 0.35), "FW_Steel", 0.01)
    export("garage_door")


def lamp():
    reset()
    cylinder(0.006, 0.9, (0, 0, -0.45), "FW_Rubber", vertices=8)
    cylinder(0.32, 0.22, (0, 0, -1.0), "FW_PaintGreen", vertices=48, top_radius=0.06)
    sphere(0.06, (0, 0, -1.08), "FW_PaintWhite")
    export("garage_lamp")


def radiator():
    reset()
    for i in range(14):
        box((0.06, 0.08, 0.6), (-0.42 + i * 0.065, 0, 0.45), "FW_PaintWhite", 0.012)
    for z in (0.17, 0.73):
        cylinder(0.02, 0.92, (0, 0, z), "FW_PaintWhite", rotation=(0, math.pi / 2, 0), vertices=12)
    cylinder(0.012, 0.15, (-0.46, 0, 0.1), "FW_Steel", vertices=10)
    export("garage_radiator")


def board():
    reset()
    box((1.3, 0.04, 0.9), (0, 0, 1.55), "FW_Wood", 0.01)
    box((1.18, 0.02, 0.78), (0, 0.02, 1.55), "FW_PaintRed", 0.002)
    # Plans and price lists pinned up
    for x, z, w, h in ((-0.35, 1.7, 0.3, 0.4), (0.05, 1.62, 0.3, 0.22), (0.38, 1.72, 0.26, 0.34), (-0.05, 1.33, 0.42, 0.2), (0.36, 1.33, 0.2, 0.26)):
        box((w, 0.004, h), (x, 0.034, z), "FW_PaintWhite", 0.0)
        cylinder(0.012, 0.01, (x, 0.04, z + h / 2 - 0.03), "FW_PaintBlue", rotation=(math.pi / 2, 0, 0), vertices=10)
    export("garage_board")


# --- Furniture -------------------------------------------------------------------------------------------------------

def trash_bin():
    reset()
    box((0.78, 0.68, 0.95), (0, 0, 0.55), "FW_PaintGreen", 0.03)
    box((0.84, 0.74, 0.06), (0, -0.02, 1.05), "FW_PaintGreen", 0.02)
    box((0.7, 0.04, 0.04), (0, -0.38, 0.98), "FW_Steel", 0.01)
    for x in (-0.3, 0.3):
        cylinder(0.08, 0.06, (x, -0.3, 0.08), "FW_Rubber", rotation=(0, math.pi / 2, 0), vertices=20)
    # Bags waiting to be sorted next to it
    for x, y, s in ((0.42, 0.3, 0.26), (0.2, 0.42, 0.22), (-0.35, 0.4, 0.24)):
        sphere(s, (x, y, s * 0.8), "FW_Rubber", scale=(1.0, 0.9, 0.85))
    export("garage_trash_bin")


def shelf(name="garage_shelf", width=2.0, depth=0.5, height=1.9):
    reset()
    for x in (-width / 2 + 0.03, width / 2 - 0.03):
        for y in (-depth / 2 + 0.03, depth / 2 - 0.03):
            box((0.04, 0.04, height), (x, y, height / 2), "FW_Steel", 0.004)
    for z in (0.12, 0.6, 1.08, 1.56):
        box((width, depth, 0.03), (0, 0, z), "FW_Wood", 0.004)
    # Boxes, bins and tins on the shelves
    items = ((-0.6, 0.6, "FW_PaintBlue", (0.4, 0.35, 0.25)), (0.1, 0.6, "FW_Wood", (0.45, 0.38, 0.3)), (0.65, 1.08, "FW_PaintRed", (0.3, 0.3, 0.22)),
             (-0.3, 1.08, "FW_Wood", (0.5, 0.4, 0.32)), (0.5, 0.12, "FW_PaintedSteel", (0.5, 0.42, 0.28)), (-0.55, 1.56, "FW_PaintBlue", (0.35, 0.3, 0.2)))
    for x, z, mat, size in items:
        box(size, (x, 0, z + size[2] / 2 + 0.015), mat, 0.01)
    for i in range(4):
        cylinder(0.06, 0.14, (0.2 + i * 0.14, 0.05, 1.56 + 0.085), "FW_Worn", vertices=16)
    export(name)


def barrel(name, paint):
    reset()
    cylinder(0.28, 0.88, (0, 0, 0.44), paint, vertices=40, bevel=0.01)
    for z in (0.02, 0.3, 0.58, 0.86):
        torus(0.282, 0.012, (0, 0, z), paint)
    cylinder(0.035, 0.02, (0.12, 0.08, 0.89), "FW_Steel", vertices=12)
    export(name)


def tyres():
    reset()
    for i in range(4):
        torus(0.27, 0.1, (0.02 * (i % 2), 0.01 * i, 0.1 + i * 0.2), "FW_Rubber")
    export("garage_tyres")


def pallet():
    reset()
    for x in (-0.4, 0.0, 0.4):
        box((0.1, 0.9, 0.1), (x, 0, 0.05), "FW_Wood", 0.004)
    for y in range(7):
        box((0.92, 0.1, 0.022), (0, -0.42 + y * 0.14, 0.111), "FW_Wood", 0.003)
    # Deliveries waiting on it
    box((0.45, 0.4, 0.35), (-0.18, -0.15, 0.3), "FW_Wood", 0.015)
    box((0.35, 0.32, 0.3), (0.22, 0.2, 0.27), "FW_PaintWhite", 0.01)
    export("garage_pallet")


def gas_cylinder():
    reset()
    cylinder(0.15, 1.1, (0, 0, 0.58), "FW_PaintRed", vertices=32, bevel=0.0)
    sphere(0.15, (0, 0, 1.13), "FW_PaintRed")
    cylinder(0.04, 0.1, (0, 0, 1.3), "FW_Steel", vertices=16)
    torus(0.08, 0.012, (0, 0, 1.33), "FW_Steel")
    torus(0.15, 0.015, (0, 0, 0.04), "FW_Steel")
    export("garage_gas_cylinder")


def sorting_machine():
    reset()
    # Body on legs, the hopper the bags go into on top, the belt that carries the sorted things out
    for x in (-0.6, 0.6):
        for y in (-0.38, 0.38):
            box((0.08, 0.08, 0.5), (x, y, 0.25), "FW_Steel", 0.004)
    box((1.4, 0.9, 0.7), (0, 0, 0.85), "FW_PaintedSteel", 0.02)
    cylinder(0.42, 0.55, (-0.25, 0, 1.47), "FW_Steel", vertices=4, top_radius=0.62, rotation=(0, 0, math.pi / 4))
    box((0.5, 0.36, 0.06), (0.4, 0.45, 0.82), "FW_Rubber", 0.01)
    box((0.3, 0.06, 0.4), (0.45, 0.47, 1.0), "FW_Worn", 0.01)
    # Bins for what comes out, the control panel with its lamps
    for i, mat in enumerate(("FW_PaintBlue", "FW_PaintRed", "FW_PaintGreen")):
        box((0.3, 0.3, 0.32), (-0.5 + i * 0.32, 0.62, 0.16), mat, 0.02)
    box((0.3, 0.04, 0.24), (0.45, -0.47, 1.0), "FW_Steel", 0.01)
    for i, mat in enumerate(("FW_PaintGreen", "FW_PaintRed")):
        cylinder(0.025, 0.02, (0.38 + i * 0.1, -0.5, 1.05), mat, rotation=(math.pi / 2, 0, 0), vertices=12)
    export("garage_sorting_machine")


def wardrobe():
    reset()
    box((1.4, 0.48, 1.95), (0, 0, 0.99), "FW_Wood", 0.012)
    box((0.68, 0.02, 1.8), (-0.35, 0.25, 1.02), "FW_Wood", 0.006)
    box((0.68, 0.02, 1.8), (0.35, 0.25, 1.02), "FW_Wood", 0.006)
    for x in (-0.05, 0.05):
        box((0.02, 0.03, 0.18), (x, 0.27, 1.1), "FW_Steel", 0.005)
    box((1.46, 0.52, 0.05), (0, 0, 1.98), "FW_Wood", 0.01)
    export("garage_wardrobe")


def trophy_case():
    reset()
    box((1.4, 0.45, 0.8), (0, 0, 0.4), "FW_Wood", 0.012)
    for x in (-0.69, 0.69):
        box((0.03, 0.45, 1.0), (x, 0, 1.3), "FW_Wood", 0.005)
    box((1.4, 0.45, 0.04), (0, 0, 1.82), "FW_Wood", 0.008)
    box((1.34, 0.02, 0.98), (0, 0.21, 1.3), "FW_Glass", 0.0)
    box((1.36, 0.4, 0.02), (0, 0, 1.3), "FW_Glass", 0.0)
    # Cups and medals behind the glass
    for i, x in enumerate((-0.45, 0.0, 0.45)):
        cylinder(0.05, 0.05, (x, 0, 0.83), "FW_Worn", vertices=16)
        cylinder(0.02, 0.12, (x, 0, 0.92), "FW_Worn", vertices=12)
        cylinder(0.09 - 0.015 * i, 0.16, (x, 0, 1.06), "FW_Worn", vertices=24, top_radius=0.05)
    for x in (-0.3, 0.3):
        cylinder(0.05, 0.01, (x, 0.05, 1.45), "FW_Worn", rotation=(math.pi / 2, 0, 0), vertices=20)
    export("garage_trophy_case")


def workbench():
    reset()
    box((2.3, 0.85, 0.06), (0, 0, 0.9), "FW_Wood", 0.01)
    for x in (-1.05, 1.05):
        for y in (-0.35, 0.35):
            box((0.07, 0.07, 0.87), (x, y, 0.435), "FW_Steel", 0.004)
    box((2.2, 0.75, 0.03), (0, 0, 0.2), "FW_Wood", 0.004)
    box((2.3, 0.04, 0.7), (0, -0.42, 1.28), "FW_PaintedSteel", 0.006)
    # A vice and a few tools
    box((0.18, 0.14, 0.12), (0.9, 0.3, 0.99), "FW_PaintBlue", 0.01)
    for i in range(5):
        box((0.03, 0.02, 0.24), (-0.8 + i * 0.12, -0.39, 1.35), "FW_Steel", 0.004)
    export("garage_workbench")


def anvil():
    reset()
    box((0.35, 0.3, 0.5), (0, 0, 0.25), "FW_Wood", 0.02)
    box((0.6, 0.22, 0.14), (0, 0, 0.62), "FW_Steel", 0.02)
    cylinder(0.11, 0.2, (0.38, 0, 0.64), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=24, top_radius=0.02)
    export("garage_anvil")


def furnace():
    reset()
    box((0.9, 0.9, 1.1), (0, 0, 0.55), "FW_Concrete", 0.03)
    box((0.4, 0.04, 0.3), (0, 0.45, 0.45), "FW_Steel", 0.01)
    cylinder(0.12, 0.9, (0.2, -0.2, 1.5), "FW_Steel", vertices=20)
    export("garage_furnace")


def research_table():
    reset()
    box((1.4, 0.9, 0.05), (0, 0, 0.82), "FW_Wood", 0.01)
    for x in (-0.65, 0.65):
        for y in (-0.4, 0.4):
            box((0.05, 0.05, 0.8), (x, y, 0.4), "FW_Wood", 0.004)
    # Papers, a lamp and a microscope-like instrument
    box((0.5, 0.35, 0.01), (-0.3, 0.1, 0.85), "FW_PaintWhite", 0.0)
    cylinder(0.08, 0.04, (0.4, -0.2, 0.86), "FW_PaintGreen", vertices=20)
    cylinder(0.012, 0.35, (0.4, -0.2, 1.02), "FW_Steel", vertices=10)
    cylinder(0.09, 0.1, (0.4, -0.12, 1.18), "FW_PaintGreen", vertices=20, top_radius=0.03)
    box((0.15, 0.2, 0.3), (0.0, -0.25, 1.0), "FW_PaintedSteel", 0.01)
    export("garage_research_table")


floor()
walls()
ceiling()
door()
lamp()
radiator()
board()
trash_bin()
shelf()
barrel("garage_barrel_blue", "FW_PaintBlue")
barrel("garage_barrel_red", "FW_PaintRed")
tyres()
pallet()
gas_cylinder()
sorting_machine()
wardrobe()
trophy_case()
workbench()
anvil()
furnace()
research_table()
print("FWGARAGE done")
