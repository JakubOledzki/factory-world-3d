# Renders the hotbar icons from the game's own models (RawAssets/processed/factory/*.glb) with the textures the
# game gives their material slots (as Tools/import_external.py binds them): one 256 px PNG with a transparent
# background per model, in RawAssets/processed/icons (imported to /Game/External/Icons).
# Run headless:
#   blender --background --factory-startup --python Tools/blender/render_icons.py
import math
import os

import bpy

from mathutils import Vector

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets")
MODELS = os.path.join(ROOT, "processed", "factory")
TEXTURES = os.path.join(ROOT, "polyhaven", "textures")
# FW_ICON_OUT / FW_ICON_SIZE / FW_ICON_ONLY (comma list) render larger previews elsewhere, e.g. to check new models
OUT = os.environ.get("FW_ICON_OUT") or os.path.join(ROOT, "processed", "icons")
SIZE = int(os.environ.get("FW_ICON_SIZE", "256"))
ICONS = ["belt_straight", "inserter_base", "chest", "pole", "solar_panel", "turbine_tower", "machine", "chunk_ore",
         "chunk_stone", "ingot", "crate", "tool_pickaxe", "workbench", "anvil", "computer", "research_table", "furnace",
         "brick_furnace", "blast_furnace", "sawmill", "assembler", "mill"]
if os.environ.get("FW_ICON_ONLY"):
    ICONS = os.environ["FW_ICON_ONLY"].split(",")
# The same as the import: painted parts a flat colour, the rest a photographed set
PAINTED = {"FW_PaintedSteel": (0.12, 0.15, 0.12), "FW_Worn": (0.42, 0.3, 0.04), "FW_Steel": (0.2, 0.2, 0.21)}
SETS = {"FW_Rubber": "rubberized_track", "FW_Wood": "weathered_planks", "FW_Concrete": "concrete_floor_worn_001",
        "FW_Grate": "metal_grate_rusty", "FW_ToolSteel": "metal_plate", "FW_Grip": "rubberized_track",
        "FW_Brick": "red_brick_03", "FW_Stone": "castle_wall_slates", "FW_TableWood": "wood_table_001"}


def texture_node(tree, set_id, kind, non_color):
    path = os.path.join(TEXTURES, set_id, f"{set_id}_{kind}_2k.jpg")
    if not os.path.isfile(path):
        path = path[:-4] + ".png"
    if not os.path.isfile(path):
        return None
    node = tree.nodes.new("ShaderNodeTexImage")
    node.image = bpy.data.images.load(path, check_existing=True)
    if non_color:
        node.image.colorspace_settings.name = "Non-Color"
    # Box mapping in object space, as the game's textures are box-mapped
    node.projection = "BOX"
    node.projection_blend = 0.2
    coords = tree.nodes.new("ShaderNodeTexCoord")
    scale = tree.nodes.new("ShaderNodeMapping")
    scale.inputs["Scale"].default_value = (2.0, 2.0, 2.0)
    tree.links.new(coords.outputs["Object"], scale.inputs["Vector"])
    tree.links.new(scale.outputs["Vector"], node.inputs["Vector"])
    return node


def dress(material):
    material.use_nodes = True
    tree = material.node_tree
    bsdf = tree.nodes.get("Principled BSDF") or tree.nodes.new("ShaderNodeBsdfPrincipled")
    name = material.name.split(".")[0]
    if name in PAINTED:
        bsdf.inputs["Base Color"].default_value = (*PAINTED[name], 1)
        bsdf.inputs["Metallic"].default_value = 0.2
        bsdf.inputs["Roughness"].default_value = 0.55
    elif name in SETS:
        color = texture_node(tree, SETS[name], "diff", False)
        arm = texture_node(tree, SETS[name], "arm", True)
        if color:
            tree.links.new(color.outputs["Color"], bsdf.inputs["Base Color"])
        if arm:
            split = tree.nodes.new("ShaderNodeSeparateColor")
            tree.links.new(arm.outputs["Color"], split.inputs["Color"])
            tree.links.new(split.outputs["Green"], bsdf.inputs["Roughness"])
            tree.links.new(split.outputs["Blue"], bsdf.inputs["Metallic"])
    elif name == "FW_Glass":
        bsdf.inputs["Base Color"].default_value = (0.012, 0.02, 0.05, 1)
        bsdf.inputs["Roughness"].default_value = 0.08
        bsdf.inputs["Metallic"].default_value = 0.35
    else:  # goods (ore, ingots, crates): as tinted in the game, a neutral look
        bsdf.inputs["Base Color"].default_value = (0.45, 0.38, 0.3, 1)
        bsdf.inputs["Roughness"].default_value = 0.6


def stage():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = SIZE
    scene.view_settings.view_transform = "AgX"
    world = bpy.data.worlds.new("Studio")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.6, 0.68, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.7
    # Key from the upper left, a cool rim from behind
    for name, rotation, energy, color in (("Key", (math.radians(50), 0, math.radians(-40)), 4.0, (1, 0.95, 0.88)),
                                          ("Rim", (math.radians(60), 0, math.radians(150)), 2.5, (0.8, 0.88, 1))):
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


os.makedirs(OUT, exist_ok=True)
for name in ICONS:
    source = os.path.join(MODELS, f"{name}.glb")
    if not os.path.isfile(source):
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    camera = stage()
    bpy.ops.import_scene.gltf(filepath=source)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    for material in bpy.data.materials:
        dress(material)
    # Frame the model: a three-quarter view from above, the whole of it in view
    corners = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    middle = (low + high) / 2
    radius = (high - low).length / 2
    direction = Vector((math.cos(math.radians(-50)), math.sin(math.radians(-50)), 0.55)).normalized()
    distance = radius / math.tan(camera.data.angle / 2) * 1.08
    camera.location = middle + direction * distance
    camera.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
    camera.data.clip_end = distance * 4
    bpy.context.scene.render.filepath = os.path.join(OUT, f"icon_{name}.png")
    bpy.ops.render.render(write_still=True)
    print(f"FWICON {name}")
