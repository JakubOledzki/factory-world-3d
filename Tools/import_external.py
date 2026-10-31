# Imports the prepared CC0 assets into Content/External (not in git; see Tools/setup_assets.ps1):
#   RawAssets/processed/*.glb            -> /Game/External/Models/<name>   (Nanite, collision from the mesh)
#   RawAssets/polyhaven/textures/<id>/*  -> /Game/External/Textures/<id>   (colour sRGB, normals BC5, ARM masks)
# Run headless:
#   UnrealEditor-Cmd.exe FactoryWorld3D.uproject -run=pythonscript -script="Tools/import_external.py"
import os

import unreal

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RawAssets")
MODELS = os.path.join(ROOT, "processed")
TEXTURES = os.path.join(ROOT, "polyhaven", "textures")
tools = unreal.AssetToolsHelpers.get_asset_tools()
library = unreal.EditorAssetLibrary


def run(files, destination):
    tasks = []
    for path in files:
        task = unreal.AssetImportTask()
        task.set_editor_property("filename", os.path.abspath(path))
        task.set_editor_property("destination_path", destination)
        task.set_editor_property("automated", True)
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)
        tasks.append(task)
    tools.import_asset_tasks(tasks)
    return [p for task in tasks for p in task.get_editor_property("imported_object_paths")]


# Models
# Either RawAssets/processed/<name>.glb (made in Blender) or <name>/<name>.gltf (original, untouched)
sources = {}
for file in sorted(os.listdir(MODELS)):
    if file.endswith(".glb"):
        sources[file[:-4]] = os.path.join(MODELS, file)
    elif os.path.isfile(os.path.join(MODELS, file, f"{file}.gltf")):
        sources[file] = os.path.join(MODELS, file, f"{file}.gltf")
for name, source in sources.items():
    destination = f"/Game/External/Models/{name}"
    if not library.list_assets(destination, recursive=True):  # already imported: just fix settings
        run([source], destination)
    # Import tasks do not report what Interchange made, so look in the folder
    for path in library.list_assets(destination, recursive=True, include_folder=False):
        asset = unreal.load_asset(path)
        if not isinstance(asset, unreal.StaticMesh):
            continue
        settings = asset.get_editor_property("nanite_settings")
        settings.set_editor_property("enabled", True)
        if name.startswith("terrain_"):
            # Collision comes from the fallback mesh: keep all of it, or things placed on the land by
            # traces float above (or sink into) the surface that is drawn
            settings.set_editor_property("fallback_target", unreal.NaniteFallbackTarget.PERCENT_TRIANGLES)
            settings.set_editor_property("fallback_percent_triangles", 1.0)
        asset.set_editor_property("nanite_settings", settings)
        # Walk on the land and bump into rocks as they look; plants are not solid
        body = asset.get_editor_property("body_setup")
        if body and not any(k in name for k in ("grass", "fern", "shrub", "sapling")):
            body.set_editor_property("collision_trace_flag", unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE)
        library.save_loaded_asset(asset)
        unreal.log(f"FWIMPORT mesh {path}")

# Textures
for set_id in sorted(os.listdir(TEXTURES)):
    folder = os.path.join(TEXTURES, set_id)
    files = [os.path.join(folder, f) for f in os.listdir(folder)]
    if not library.list_assets(f"/Game/External/Textures/{set_id}", recursive=True):
        run(files, f"/Game/External/Textures/{set_id}")
    for path in library.list_assets(f"/Game/External/Textures/{set_id}", recursive=True, include_folder=False):
        texture = unreal.load_asset(path)
        if not isinstance(texture, unreal.Texture2D):
            continue
        name = path.lower()
        if "_nor_" in name:
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_NORMALMAP)
            texture.set_editor_property("srgb", False)
        elif "_arm_" in name:
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_MASKS)
            texture.set_editor_property("srgb", False)
        library.save_loaded_asset(texture)
        unreal.log(f"FWIMPORT texture {path}")

unreal.log("FWIMPORT done")

# Twig and leaf cards: glTF brings them as blended (or opaque); they must be alpha-masked cards
for path in library.list_assets("/Game/External/Models", recursive=True, include_folder=False):
    lower = path.lower()
    if "/materials/" not in lower or not any(k in lower for k in ("twig", "leaf", "leaves", "needle", "grass", "fern", "shrub")):
        continue
    material = unreal.load_asset(path)
    if isinstance(material, unreal.MaterialInstanceConstant):
        overrides = material.get_editor_property("base_property_overrides")
        overrides.set_editor_property("override_blend_mode", True)
        overrides.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
        overrides.set_editor_property("override_opacity_mask_clip_value", True)
        overrides.set_editor_property("opacity_mask_clip_value", 0.5)
        overrides.set_editor_property("override_two_sided", True)
        overrides.set_editor_property("two_sided", True)
        material.set_editor_property("base_property_overrides", overrides)
        unreal.MaterialEditingLibrary.update_material_instance(material)
        library.save_loaded_asset(material)
        unreal.log(f"FWIMPORT masked {path}")
    elif isinstance(material, unreal.Material):
        material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
        material.set_editor_property("two_sided", True)
        unreal.MaterialEditingLibrary.recompile_material(material)
        library.save_loaded_asset(material)
        unreal.log(f"FWIMPORT masked {path}")

# Scanned rocks are captured darker than granite reads in daylight: lift their albedo
for path in library.list_assets("/Game/External/Models", recursive=True, include_folder=False):
    lower = path.lower()
    if "/materials/" not in lower or not any(k in lower for k in ("boulder", "rock")):
        continue
    material = unreal.load_asset(path)
    if isinstance(material, unreal.MaterialInstanceConstant):
        unreal.MaterialEditingLibrary.set_material_instance_vector_parameter_value(material, "BaseColorFactor", unreal.LinearColor(1.6, 1.6, 1.6, 1.0))
        library.save_loaded_asset(material)
        unreal.log(f"FWIMPORT brighter {path}")

# Point every scanned model at our own plain material (M_FWScan / M_FWScanMasked): the glTF importer's
# master lacks the Nanite and instanced-mesh usages, so -game and cooked builds drew the scans with the
# default (flat grey) material. The importer's instances are re-parented in place, keeping their textures.
scan = unreal.load_asset("/Game/Materials/M_FWScan.M_FWScan")
scan_masked = unreal.load_asset("/Game/Materials/M_FWScanMasked.M_FWScanMasked")
mel = unreal.MaterialEditingLibrary
for path in library.list_assets("/Game/External/Models", recursive=True, include_folder=False):
    if "/Materials/" not in path:
        continue
    source = unreal.load_asset(path)
    if not isinstance(source, unreal.MaterialInstanceConstant):
        continue
    parent = source.get_editor_property("parent")
    if parent in (scan, scan_masked):
        continue
    textures = {name: mel.get_material_instance_texture_parameter_value(source, name)
                for name in ("BaseColorTexture", "NormalTexture", "MetallicRoughnessTexture")}
    if not textures["BaseColorTexture"]:
        continue
    lower = path.lower()
    masked = any(k in lower for k in ("twig", "leaf", "leaves", "needle", "grass", "fern", "shrub"))
    for texture in textures.values():
        if not texture:
            continue
        tname = texture.get_name().lower()
        if "nor" in tname:
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_NORMALMAP)
            texture.set_editor_property("srgb", False)
        elif "arm" in tname or "rough" in tname:
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_MASKS)
            texture.set_editor_property("srgb", False)
        library.save_loaded_asset(texture)
    mel.set_material_instance_parent(source, scan_masked if masked else scan)
    mel.set_material_instance_texture_parameter_value(source, "BaseColor", textures["BaseColorTexture"])
    if textures["NormalTexture"]:
        mel.set_material_instance_texture_parameter_value(source, "Normal", textures["NormalTexture"])
    if textures["MetallicRoughnessTexture"]:
        mel.set_material_instance_texture_parameter_value(source, "ARM", textures["MetallicRoughnessTexture"])
        if "rough" in textures["MetallicRoughnessTexture"].get_name().lower():
            mel.set_material_instance_scalar_parameter_value(source, "MetalScale", 0.0)
    if any(k in lower for k in ("boulder", "rock")):
        # Scans are captured darker than granite reads in daylight
        mel.set_material_instance_vector_parameter_value(source, "Tint", unreal.LinearColor(1.35, 1.35, 1.35, 1.0))
    mel.update_material_instance(source)
    library.save_loaded_asset(source)
    unreal.log(f"FWIMPORT scan material {path}")

# Usage audit: every material on an external mesh must come from a master usable with Nanite and
# instanced meshes, or the game silently draws the default material instead
problems = 0
for path in library.list_assets("/Game/External/Models", recursive=True, include_folder=False):
    # The land gets M_FWTerrain at runtime (FWPlot), whatever its asset says
    if "/StaticMeshes/" not in path or "/terrain_" in path:
        continue
    mesh = unreal.load_asset(path)
    if not isinstance(mesh, unreal.StaticMesh):
        continue
    for slot in mesh.get_editor_property("static_materials"):
        material = slot.get_editor_property("material_interface")
        root = material.get_base_material() if material else None
        if not root or not root.get_editor_property("used_with_nanite") or not root.get_editor_property("used_with_instanced_static_meshes"):
            problems += 1
            unreal.log_warning(f"FWIMPORT usage missing: {path} uses {root.get_path_name() if root else None}")
unreal.log(f"FWIMPORT usage audit: {problems} problem(s)")

# Factory models from Tools/blender/make_factory.py: their material slots (FW_*) are named after the
# texture sets to use
FACTORY = os.path.join(MODELS, "factory")
# Painted parts are flat paint (a palette colour) with the subtle roughness of worn concrete, not a
# photographed rusty plate, which tiled as blotches; grime darkens their lower parts
PAINTED = {"FW_PaintedSteel": (0.12, 0.15, 0.12), "FW_Worn": (0.42, 0.3, 0.04), "FW_Steel": (0.2, 0.2, 0.21)}
SLOT_TEXTURES = {
    "FW_Rubber": "rubberized_track", "FW_Wood": "weathered_planks", "FW_Concrete": "concrete_floor_worn_001",
    "FW_Grate": "metal_grate_rusty",
}
belt_master = unreal.load_asset("/Game/Materials/M_FWBelt.M_FWBelt")
surface = unreal.load_asset("/Game/Materials/M_FWSurface.M_FWSurface")


def texture_set(set_id, kind):
    path = f"/Game/External/Textures/{set_id}/{set_id}_{kind}_2k"
    return unreal.load_asset(path) if library.does_asset_exist(path) else None


if os.path.isdir(FACTORY):
    for file in sorted(os.listdir(FACTORY)):
        if not file.endswith(".glb"):
            continue
        name = file[:-4]
        destination = f"/Game/External/Factory/{name}"
        if library.list_assets(destination, recursive=True):
            library.delete_directory(destination)  # always rebuilt from the script's latest output
        run([os.path.join(FACTORY, file)], destination)
        for path in library.list_assets(destination, recursive=True, include_folder=False):
            asset = unreal.load_asset(path)
            if isinstance(asset, unreal.StaticMesh):
                settings = asset.get_editor_property("nanite_settings")
                settings.set_editor_property("enabled", True)
                asset.set_editor_property("nanite_settings", settings)
                body = asset.get_editor_property("body_setup")
                if body:
                    body.set_editor_property("collision_trace_flag", unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE)
                library.save_loaded_asset(asset)
            elif isinstance(asset, unreal.MaterialInstanceConstant):
                slot = asset.get_name()
                if slot == "FW_Glass":
                    mel.set_material_instance_parent(asset, surface)
                    mel.set_material_instance_vector_parameter_value(asset, "Color", unreal.LinearColor(0.012, 0.02, 0.05, 1))
                    mel.set_material_instance_scalar_parameter_value(asset, "Roughness", 0.08)
                    mel.set_material_instance_scalar_parameter_value(asset, "Metallic", 0.35)
                elif slot in SLOT_TEXTURES:
                    set_id = SLOT_TEXTURES[slot]
                    mel.set_material_instance_parent(asset, belt_master if slot == "FW_Rubber" else scan)
                    if slot != "FW_Rubber":
                        mel.set_material_instance_scalar_parameter_value(asset, "Grime", 0.85)
                    for param, kind in (("BaseColor", "diff"), ("Normal", "nor_gl"), ("ARM", "arm")):
                        texture = texture_set(set_id, kind)
                        if texture and not (slot == "FW_Rubber" and param == "ARM"):
                            mel.set_material_instance_texture_parameter_value(asset, param, texture)
                elif slot in PAINTED:
                    mel.set_material_instance_parent(asset, scan)
                    white = unreal.load_asset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture")
                    mel.set_material_instance_texture_parameter_value(asset, "BaseColor", white)
                    rough = texture_set("concrete_floor_worn_001", "arm")
                    if rough:
                        mel.set_material_instance_texture_parameter_value(asset, "ARM", rough)
                    mel.set_material_instance_scalar_parameter_value(asset, "MetalScale", 0.0)
                    mel.set_material_instance_vector_parameter_value(asset, "Tint", unreal.LinearColor(*PAINTED[slot], 1.0))
                    # Chipped edges show the bare steel underneath
                    bare = texture_set("metal_grate_rusty", "diff")
                    if bare and slot != "FW_Steel":
                        mel.set_material_instance_texture_parameter_value(asset, "Rust", bare)
                        mel.set_material_instance_scalar_parameter_value(asset, "Wear", 1.0)
                    mel.set_material_instance_scalar_parameter_value(asset, "Grime", 0.85)
                else:
                    unreal.log_warning(f"FWIMPORT factory: unknown material slot {slot} in {path}")
                    continue
                mel.update_material_instance(asset)
                library.save_loaded_asset(asset)
        unreal.log(f"FWIMPORT factory {name}")
