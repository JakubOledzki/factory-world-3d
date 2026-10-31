# Builds the hand tools held in first person and writes them to RawAssets/processed/factory (imported with the
# factory models by Tools/import_external.py). Sizes in metres; the origin is where the hand grips the tool, the
# handle runs up +Z and the head points along +X. Material slots, bound by the import:
#   FW_ToolSteel (metal_plate)   FW_Wood (weathered_planks)   FW_Grip (rubberized_track, a rubber wrap)
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_tools.py
import math
import os

import bmesh
import bpy

from mathutils import Matrix, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "factory")


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def finish(obj, mat, bevel=0.0, subdivide=0, uv_scale=0.25):
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    if bevel > 0:
        mod = obj.modifiers.new("Bevel", "BEVEL")
        mod.width = bevel
        mod.segments = 3
        mod.limit_method = "ANGLE"
        bpy.ops.object.modifier_apply(modifier=mod.name)
    if subdivide:
        mod = obj.modifiers.new("Subdivision", "SUBSURF")
        mod.levels = subdivide
        mod.render_levels = subdivide
        bpy.ops.object.modifier_apply(modifier=mod.name)
    mod = obj.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL")
    mod.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.cube_project(cube_size=uv_scale, correct_aspect=False, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.data.materials.clear()
    obj.data.materials.append(material(mat))
    for poly in obj.data.polygons:
        poly.use_smooth = True
    return obj


def loft(sections, name):
    """A solid through closed cross-sections (lists of points), capped at both ends."""
    bm = bmesh.new()
    rings = [[bm.verts.new(p) for p in section] for section in sections]
    n = len(sections[0])
    for a, b in zip(rings, rings[1:]):
        for i in range(n):
            bm.faces.new([a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]])
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def ellipse(center, rx, ry, segments=16, axis="z"):
    points = []
    for i in range(segments):
        a = 2 * math.pi * i / segments
        if axis == "z":
            points.append(center + Vector((math.cos(a) * rx, math.sin(a) * ry, 0)))
        else:  # section across the X axis (for the head)
            points.append(center + Vector((0, math.cos(a) * rx, math.sin(a) * ry)))
    return points


def pickaxe():
    parts = []
    # Handle: oval ash, 72 cm, a swell at the butt, thinner in the middle, thicker again under the head
    length = 0.72
    sections = []
    for i in range(25):
        t = i / 24
        z = -0.14 + t * length
        swell = 1.0 + 0.25 * math.exp(-((t - 0.02) / 0.05) ** 2) - 0.12 * math.sin(t * math.pi) + 0.15 * max(0.0, t - 0.85) / 0.15
        sections.append(ellipse(Vector((0, 0, z)), 0.019 * swell, 0.014 * swell, 16))
    parts.append(finish(loft(sections, "handle"), "FW_Wood", subdivide=1, uv_scale=0.4))
    # Grip: a rubber wrap over the lower handle, slightly proud of the wood, with a ridge every 2 cm
    sections = []
    for i in range(31):
        t = i / 30
        z = -0.12 + t * 0.24
        ridge = 1.0 + 0.04 * (0.5 + 0.5 * math.cos(t * 2 * math.pi * 12))
        sections.append(ellipse(Vector((0, 0, z)), 0.0215 * ridge, 0.0165 * ridge, 16))
    parts.append(finish(loft(sections, "grip"), "FW_Grip", uv_scale=0.1))
    # Head: forged steel, a long curved pick on one side and a flat chisel blade on the other, thick at the eye
    top = -0.14 + length - 0.04
    sections = []
    steps = 22
    for i in range(steps + 1):
        u = i / steps * 2 - 1  # -1 chisel end .. +1 pick point
        x = u * 0.27
        droop = -0.07 * u * u  # both arms curve down towards the work
        thick = 0.022 * (1 - 0.75 * abs(u) ** 1.4) + 0.004
        if u < 0:  # chisel: flat and wide at the edge
            height = 0.028 * (1 - 0.35 * abs(u))
            width = thick * (0.6 + 0.4 * (1 - abs(u)))
        else:  # pick: a square point
            height = 0.03 * (1 - 0.9 * u ** 1.2) + 0.002
            width = thick
        sections.append(ellipse(Vector((x, 0, top + droop)), width, height, 8, axis="x"))
    parts.append(finish(loft(sections, "head"), "FW_ToolSteel", subdivide=1, uv_scale=0.15))
    # Eye socket: a collar around the handle where the head sits, and the wedge driven into the top
    bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=0.03, depth=0.07, location=(0, 0, top))
    collar = bpy.context.active_object
    collar.scale = (1.0, 0.8, 1.0)
    parts.append(finish(collar, "FW_ToolSteel", bevel=0.004, uv_scale=0.15))
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, top + 0.04))
    wedge = bpy.context.active_object
    wedge.scale = (0.036, 0.006, 0.012)
    parts.append(finish(wedge, "FW_ToolSteel", bevel=0.002, uv_scale=0.15))
    # One mesh
    bpy.ops.object.select_all(action="DESELECT")
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    tool = bpy.context.active_object
    tool.name = "tool_pickaxe"
    return tool


os.makedirs(OUT, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
tool = pickaxe()
bpy.ops.object.select_all(action="DESELECT")
tool.select_set(True)
path = os.path.join(OUT, "tool_pickaxe.glb")
bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True)
print(f"FWTOOL tool_pickaxe: {len(tool.data.polygons)} faces -> {path}")
