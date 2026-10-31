# Prepares the downloaded Poly Haven models for the game: reports triangle counts, brings the heavy
# scans down to Nanite budgets and writes .glb files with their textures.
# Trees cannot simply be decimated: their needles and twigs are thousands of small cards that collapse
# into nothing. So bark and trunks are decimated, while whole twig cards are thinned out at random,
# each kept card staying intact. Twig materials become alpha-masked (cheap with Nanite).
# Run headless:
#   blender --background --factory-startup --python Tools/blender/prepare_polyhaven.py
import bmesh
import bpy
import os
import random

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets")
SOURCE = os.path.join(ROOT, "polyhaven", "models")
OUT = os.path.join(ROOT, "processed")

# name: (triangles for wood, triangles for twigs and needles)
TREES = {
    "fir_tree_01": (60_000, 700_000),
    "pine_tree_01": (60_000, 700_000),
    "fir_sapling_medium": (15_000, 200_000),
}
DEFAULT_BUDGET = 400_000


def triangles(objects):
    return sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects if o.type == "MESH")


def decimate(obj, budget):
    count = triangles([obj])
    if count <= budget:
        return
    modifier = obj.modifiers.new("Decimate", "DECIMATE")
    modifier.ratio = budget / count
    modifier.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=modifier.name)


def thin_cards(obj, budget):
    # Keep a random share of the connected pieces (cards), whole
    count = triangles([obj])
    if count <= budget:
        return
    keep = budget / count
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    seen = set()
    doomed = []
    rng = random.Random(7)
    for face in bm.faces:
        if face.index in seen:
            continue
        island = []
        stack = [face]
        seen.add(face.index)
        while stack:
            f = stack.pop()
            island.append(f)
            for edge in f.edges:
                for other in edge.link_faces:
                    if other.index not in seen:
                        seen.add(other.index)
                        stack.append(other)
        if rng.random() > keep:
            doomed.extend(island)
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    bm.to_mesh(obj.data)
    bm.free()


def merge_alpha(material, name):
    # Put the separate twig alpha map into the colour image (RGBA PNG) and clip on it, so the glTF
    # exporter writes an alpha-masked material instead of opaque cards
    folder = os.path.join(SOURCE, name, "textures")
    alpha_path = next((os.path.join(folder, f) for f in os.listdir(folder) if "twig_alpha" in f), None)
    done = material.use_nodes and any(n.type == "MATH" and n.operation == "ROUND" for n in material.node_tree.nodes)
    if not alpha_path or not material.use_nodes or done:
        return
    tree = material.node_tree
    bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    # The colour image, wherever it sits in the node tree (some materials mix it with vertex colour first)
    color = next((n for n in tree.nodes if n.type == "TEX_IMAGE" and n.image and "diff" in n.image.name), None)
    if not color or not bsdf:
        return
    import numpy as np
    alpha_image = bpy.data.images.load(alpha_path)
    width, height = color.image.size
    if tuple(alpha_image.size) != (width, height):
        alpha_image.scale(width, height)
    rgba = np.empty(width * height * 4, dtype=np.float32)
    color.image.pixels.foreach_get(rgba)
    mask = np.empty(width * height * 4, dtype=np.float32)
    alpha_image.pixels.foreach_get(mask)
    rgba[3::4] = mask[0::4]
    merged = bpy.data.images.new(f"{name}_twig_diffalpha_2k", width, height, alpha=True)
    merged.pixels.foreach_set(rgba)
    merged.filepath_raw = os.path.join(folder, f"{name}_twig_diffalpha_2k.png")
    merged.file_format = "PNG"
    merged.save()
    color.image = merged
    # Round(alpha) is what the glTF exporter reads as an alpha mask with a 0.5 cutoff
    clip = tree.nodes.new("ShaderNodeMath")
    clip.operation = "ROUND"
    tree.links.new(color.outputs["Alpha"], clip.inputs[0])
    tree.links.new(clip.outputs[0], bsdf.inputs["Alpha"])


def is_twig(material):
    return material is not None and any(k in material.name.lower() for k in ("twig", "leaf", "leaves", "needle"))


os.makedirs(OUT, exist_ok=True)
# Optional: only these assets (blender ... -- fir_tree_01 pine_tree_01)
import sys
ONLY = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for name in sorted(os.listdir(SOURCE)):
    if ONLY and name not in ONLY:
        continue
    path = os.path.join(SOURCE, name, f"{name}.gltf")
    if not os.path.exists(path):
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=path)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    before = triangles(meshes)
    if name in TREES:
        wood_budget, twig_budget = TREES[name]
        share = 1.0 / max(1, len(meshes))
        for obj in meshes:
            # Split by material so wood and twigs can be treated apart, then join back under the same name
            original = obj.name
            bpy.ops.object.select_all(action="DESELECT")
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            bpy.ops.mesh.separate(type="MATERIAL")
            parts = [o for o in bpy.context.selected_objects if o.type == "MESH"]
            twigs = [o for o in parts if o.data.materials and is_twig(o.data.materials[0])]
            wood = [o for o in parts if o not in twigs]
            twig_total = max(1, triangles(twigs))
            wood_total = max(1, triangles(wood))
            for part in twigs:
                thin_cards(part, twig_budget * share * triangles([part]) / twig_total)
                for material in part.data.materials:
                    merge_alpha(material, name)
            for part in wood:
                decimate(part, wood_budget * share * triangles([part]) / wood_total)
            bpy.ops.object.select_all(action="DESELECT")
            for part in parts:
                part.select_set(True)
            keeper = next(p for p in parts if p.name == original) if any(p.name == original for p in parts) else parts[0]
            bpy.context.view_layer.objects.active = keeper
            bpy.ops.object.join()
            keeper.name = original
        meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    else:
        for obj in meshes:
            decimate(obj, DEFAULT_BUDGET * triangles([obj]) / max(1, before))
    after = triangles(meshes)
    # AUTO keeps PNG (and so alpha) where the source had it
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f"{name}.glb"), export_format="GLB", export_image_format="AUTO")
    print(f"FWPREP {name}: {before} -> {after} triangles")
