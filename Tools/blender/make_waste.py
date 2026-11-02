# Builds the waste of the sorting game at the garage bin (WasteSorting on the server) and its three bins, and renders
# their icons for the game's window:
#   waste_can, waste_tin, waste_lid (metal), waste_pet, waste_cup, waste_jug (plastic), waste_bottle, waste_jar,
#   waste_vial (glass); bin_metal, bin_plastic, bin_glass
# One .glb per model in RawAssets/processed/waste and one 256 px PNG with a transparent background per model in
# RawAssets/processed/icons (icon_<name>.png, imported to /Game/External/Icons by Tools/import_external.py).
# The models are ours (made here, CC0). Tools/blender/make_garage.py runs this at its end; on its own:
#   blender --background --factory-startup --python Tools/blender/make_waste.py
import math
import os

import bpy
from mathutils import Vector

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed")
OUT = os.path.join(ROOT, "waste")
ICONS = os.environ.get("FW_ICON_OUT") or os.path.join(ROOT, "icons")
SIZE = int(os.environ.get("FW_ICON_SIZE", "256"))


def material(name, color, metallic=0.0, roughness=0.5, transmission=0.0, alpha=1.0):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Transmission Weight"].default_value = transmission
    bsdf.inputs["IOR"].default_value = 1.5
    if alpha < 1:
        bsdf.inputs["Alpha"].default_value = alpha
        mat.blend_method = "BLEND"
    return mat


def lathe(name, profile, mat, segments=48, cap_top=True, cap_bottom=True):
    """A body of revolution about Z from (radius, height) points, bottom to top."""
    verts, faces = [], []
    rings = len(profile)
    for i in range(segments):
        a = 2 * math.pi * i / segments
        for r, z in profile:
            verts.append((r * math.cos(a), r * math.sin(a), z))
    for i in range(segments):
        j = (i + 1) % segments
        for k in range(rings - 1):
            faces.append((i * rings + k, j * rings + k, j * rings + k + 1, i * rings + k + 1))
    if cap_bottom:
        verts.append((0, 0, profile[0][1]))
        faces += [(len(verts) - 1, ((i + 1) % segments) * rings, i * rings) for i in range(segments)]
    if cap_top:
        verts.append((0, 0, profile[-1][1]))
        faces += [(len(verts) - 1, i * rings + rings - 1, ((i + 1) % segments) * rings + rings - 1) for i in range(segments)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.data.materials.append(mat)
    for poly in mesh.polygons:
        poly.use_smooth = True
    return obj


def box(name, size, location, mat, bevel=0.01):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel:
        mod = obj.modifiers.new("Bevel", "BEVEL")
        mod.width = bevel
        mod.segments = 4
        bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.data.materials.append(mat)
    for poly in obj.data.polygons:
        poly.use_smooth = True
    return obj


def ring(r, z):
    return [(r, z)]


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


ALU = lambda: material("Aluminium", (0.82, 0.82, 0.84), 1.0, 0.22)
STEEL = lambda: material("TinSteel", (0.72, 0.72, 0.74), 1.0, 0.32)
CLEAR = lambda: material("ClearGlass", (0.92, 0.97, 1.0), 0.0, 0.02, 1.0)


def can():
    paint = material("CanPaint", (0.72, 0.04, 0.05), 0.85, 0.3)
    lathe("body", [(0.026, 0.0), (0.032, 0.006), (0.033, 0.012), (0.033, 0.103), (0.029, 0.112), (0.027, 0.115)], paint)
    lathe("top", [(0.027, 0.115), (0.0275, 0.117), (0.024, 0.116)], ALU())
    lathe("ring", [(0.008, 0.117), (0.009, 0.119), (0.004, 0.119)], ALU(), cap_bottom=False)
    lathe("bottom", [(0.0, -0.0005), (0.026, -0.0005), (0.026, 0.001)], ALU())


def tin():
    lathe("tin", [(0.0, 0.0), (0.04, 0.0), (0.042, 0.004), (0.042, 0.016), (0.0435, 0.018), (0.042, 0.02),
                  (0.042, 0.088), (0.0435, 0.09), (0.042, 0.092), (0.042, 0.104), (0.04, 0.108)], STEEL())
    lathe("label", [(0.0425, 0.022), (0.0426, 0.086)], material("TinLabel", (0.86, 0.58, 0.12), 0.0, 0.55),
          cap_top=False, cap_bottom=False)
    # The opened lid bent up on one side
    lid = lathe("lid", [(0.0, 0.0), (0.04, 0.0), (0.04, 0.002)], STEEL())
    lid.location = (0.02, 0.0, 0.108)
    lid.rotation_euler = (0, math.radians(-55), 0)


def lid():
    gold = material("LidGold", (0.78, 0.55, 0.18), 1.0, 0.3)
    lathe("lid", [(0.0, 0.0), (0.035, 0.0), (0.04, 0.003), (0.041, 0.006), (0.041, 0.014), (0.039, 0.016), (0.0, 0.016)], gold)
    # Knurled rim
    for i in range(36):
        a = 2 * math.pi * i / 36
        rib = box("rib", (0.003, 0.002, 0.008), (0.0412 * math.cos(a), 0.0412 * math.sin(a), 0.01), gold, 0.0)
        rib.rotation_euler = (0, 0, a)
    printed = lathe("print", [(0.0, 0.0161), (0.026, 0.0161)], material("LidPrint", (0.75, 0.08, 0.06), 0.3, 0.4), cap_top=False)
    for poly in printed.data.polygons:
        poly.use_smooth = False


def pet():
    plastic = material("PetBlue", (0.78, 0.9, 1.0), 0.0, 0.04, 1.0)
    lathe("bottle", [(0.0, 0.0), (0.028, 0.0), (0.033, 0.006), (0.033, 0.06), (0.029, 0.07), (0.033, 0.08), (0.033, 0.15),
                     (0.03, 0.18), (0.02, 0.205), (0.0125, 0.215), (0.0125, 0.225)], plastic, cap_top=False)
    lathe("cap", [(0.0, 0.225), (0.0145, 0.225), (0.0145, 0.243), (0.0, 0.243)], material("CapBlue", (0.03, 0.22, 0.75), 0.0, 0.45))
    lathe("label", [(0.0335, 0.085), (0.0336, 0.14)], material("PetLabel", (0.1, 0.55, 0.85), 0.0, 0.4),
          cap_top=False, cap_bottom=False)


def cup():
    white = material("CupWhite", (0.95, 0.94, 0.9), 0.0, 0.45)
    lathe("cup", [(0.0, 0.0), (0.024, 0.0), (0.026, 0.002), (0.034, 0.068), (0.037, 0.07)], white, cap_top=False)
    lathe("inside", [(0.033, 0.069), (0.0235, 0.004), (0.0, 0.004)], white, cap_bottom=False, cap_top=False)
    lathe("band", [(0.0285, 0.02), (0.0325, 0.055)], material("CupBand", (0.92, 0.35, 0.55), 0.0, 0.5),
          cap_top=False, cap_bottom=False)
    # The foil lid peeled half back
    foil = box("foil", (0.074, 0.074, 0.001), (0.0, 0.0, 0.0), material("Foil", (0.85, 0.85, 0.88), 1.0, 0.35), 0.0)
    foil.location = (-0.03, 0.0, 0.085)
    foil.rotation_euler = (0, math.radians(40), 0)


def jug():
    orange = material("JugOrange", (0.95, 0.42, 0.04), 0.0, 0.38)
    box("jug", (0.085, 0.05, 0.17), (0.0, 0.0, 0.085), orange, 0.018)
    lathe("shoulder", [(0.03, 0.165), (0.016, 0.19), (0.014, 0.2)], orange, cap_bottom=False, cap_top=False)
    lathe("cap", [(0.0, 0.2), (0.017, 0.2), (0.017, 0.222), (0.0, 0.222)], material("CapWhite", (0.95, 0.95, 0.95), 0.0, 0.4))
    box("label", (0.06, 0.002, 0.08), (0.0, -0.026, 0.08), material("JugLabel", (0.98, 0.95, 0.85), 0.0, 0.5), 0.003)


def bottle():
    green = material("GreenGlass", (0.18, 0.62, 0.22), 0.0, 0.03, 1.0)
    lathe("bottle", [(0.0, 0.004), (0.025, 0.0), (0.033, 0.004), (0.035, 0.012), (0.035, 0.16), (0.03, 0.19), (0.016, 0.22),
                     (0.0135, 0.26), (0.0155, 0.268), (0.0155, 0.276), (0.012, 0.278)], green)


def jar():
    lathe("jar", [(0.0, 0.002), (0.034, 0.0), (0.04, 0.006), (0.041, 0.016), (0.041, 0.085), (0.036, 0.096), (0.034, 0.1),
                  (0.036, 0.103), (0.036, 0.112), (0.033, 0.114)], CLEAR(), cap_top=False)
    lathe("label", [(0.0415, 0.03), (0.0416, 0.07)], material("JarLabel", (0.9, 0.85, 0.7), 0.0, 0.6),
          cap_top=False, cap_bottom=False)


def vial():
    brown = material("BrownGlass", (0.5, 0.22, 0.05), 0.0, 0.03, 1.0)
    lathe("vial", [(0.0, 0.002), (0.02, 0.0), (0.024, 0.005), (0.024, 0.075), (0.019, 0.09), (0.011, 0.097), (0.011, 0.104)], brown,
          cap_top=False)
    lathe("cap", [(0.0, 0.103), (0.013, 0.103), (0.013, 0.122), (0.0, 0.122)], material("CapWhite", (0.95, 0.95, 0.95), 0.0, 0.4))
    lathe("label", [(0.0243, 0.015), (0.0244, 0.06)], material("VialLabel", (0.97, 0.97, 0.95), 0.0, 0.6),
          cap_top=False, cap_bottom=False)


def waste_bin(color, mark):
    body = material(f"Bin{mark}", color, 0.0, 0.45)
    dark = material(f"Bin{mark}Lid", tuple(c * 0.7 for c in color), 0.0, 0.4)
    # A tapered wheelie bin with a lid, a handle at the back, wheels and a round slot in the lid
    lathe_body = box("body", (0.5, 0.56, 0.9), (0.0, 0.0, 0.47), body, 0.04)
    for v in lathe_body.data.vertices:
        if v.co.z < 0:
            v.co.x *= 0.86
            v.co.y *= 0.86
    box("lid", (0.56, 0.62, 0.05), (0.0, 0.01, 0.945), dark, 0.02)
    box("handle", (0.44, 0.04, 0.04), (0.0, 0.33, 0.9), dark, 0.015)
    slot = lathe("slot", [(0.0, 0.0), (0.11, 0.0), (0.12, 0.004)], material("BinSlot", (0.02, 0.02, 0.02), 0.0, 0.8))
    slot.location = (0.0, -0.05, 0.972)
    for x in (-0.2, 0.2):
        wheel = lathe("wheel", [(0.0, -0.025), (0.07, -0.025), (0.08, -0.015), (0.08, 0.015), (0.07, 0.025), (0.0, 0.025)],
                      material("Rubber", (0.03, 0.03, 0.03), 0.0, 0.85))
        wheel.rotation_euler = (0, math.radians(90), 0)
        wheel.location = (x, 0.25, 0.08)
    box("panel", (0.3, 0.004, 0.22), (0.0, -0.27, 0.6), material("BinPanel", (0.97, 0.97, 0.95), 0.0, 0.5), 0.01)


MODELS = {
    "waste_can": can, "waste_tin": tin, "waste_lid": lid, "waste_pet": pet, "waste_cup": cup, "waste_jug": jug,
    "waste_bottle": bottle, "waste_jar": jar, "waste_vial": vial,
    "bin_metal": lambda: waste_bin((0.22, 0.25, 0.3), "Metal"),
    "bin_plastic": lambda: waste_bin((1.0, 0.55, 0.0), "Plastic"),
    "bin_glass": lambda: waste_bin((0.01, 0.32, 0.06), "Glass"),
}


def stage():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 96
    scene.cycles.use_denoising = True
    scene.cycles.film_transparent_glass = True
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = SIZE
    scene.view_settings.view_transform = "AgX"
    world = bpy.data.worlds.new("Studio")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.6, 0.64, 0.7, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.9
    for name, rotation, energy, color in (("Key", (math.radians(50), 0, math.radians(-40)), 4.0, (1, 0.95, 0.88)),
                                          ("Rim", (math.radians(60), 0, math.radians(150)), 3.0, (0.8, 0.88, 1))):
        light = bpy.data.objects.new(name, bpy.data.lights.new(name, "SUN"))
        light.data.energy = energy
        light.data.color = color
        light.data.angle = math.radians(8)
        light.rotation_euler = rotation
        scene.collection.objects.link(light)
    camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    camera.data.lens = 85
    scene.collection.objects.link(camera)
    scene.camera = camera
    return camera


def export(name):
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f"{name}.glb"), export_format="GLB", use_selection=False)


def render(name, camera):
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.context.view_layer.update()
    corners = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    middle = (low + high) / 2
    radius = (high - low).length / 2
    # Three-quarter view from the front and a little above; the bins from a bit lower, as they stand on the floor
    direction = Vector((math.cos(math.radians(-60)), math.sin(math.radians(-60)), 0.45)).normalized()
    distance = radius / math.tan(camera.data.angle / 2) * 1.05
    camera.location = middle + direction * distance
    camera.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
    camera.data.clip_start = distance / 100
    camera.data.clip_end = distance * 4
    os.makedirs(ICONS, exist_ok=True)
    bpy.context.scene.render.filepath = os.path.join(ICONS, f"icon_{name}.png")
    bpy.ops.render.render(write_still=True)


only = os.environ.get("FW_WASTE_ONLY")
for name, build in MODELS.items():
    if only and name not in only.split(","):
        continue
    reset()
    build()
    export(name)
    camera = stage()
    render(name, camera)
    print(f"FWWASTE {name}")
print("FWWASTE done")
