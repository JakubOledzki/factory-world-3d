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

# Real-elevation terrains carry the plot's height above sea level (from dem_<x>_<y>.json) for the game
import json
for file in sorted(os.listdir(MODELS)):
    if not (file.startswith("dem_") and file.endswith(".json")):
        continue
    name = "terrain_" + file[:-5]
    path = f"/Game/External/Models/{name}/{name}/StaticMeshes/{name}.{name}"
    if not library.does_asset_exist(path):
        continue
    mesh = unreal.load_asset(path)
    user_data = list(mesh.get_editor_property("asset_user_data") or [])
    info = next((d for d in user_data if isinstance(d, unreal.FWTerrainInfo)), None)
    if not info:
        info = unreal.new_object(unreal.FWTerrainInfo, mesh)
        user_data.append(info)
        mesh.set_editor_property("asset_user_data", user_data)
    info.set_editor_property("plot_elevation_meters", json.load(open(os.path.join(MODELS, file)))["plotElevationMeters"])
    library.save_loaded_asset(mesh)
    unreal.log(f"FWIMPORT terrain elevation {name}: {info.get_editor_property('plot_elevation_meters'):.0f} m")

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
    # Workshop furniture and kilns
    "FW_Brick": "red_brick_03", "FW_Stone": "castle_wall_slates", "FW_TableWood": "wood_table_001",
    # Hand tools (Tools/blender/make_tools.py)
    "FW_ToolSteel": "metal_plate", "FW_Grip": "rubberized_track",
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

# Dwarf pine from Tools/blender/make_mugo_pine.py: bark from Poly Haven's pine_tree_01 (CC0, imported above),
# needles a plain colour shaded by the vertex colour; both sway in the wind (M_FWFoliage)
MUGO = os.path.join(MODELS, "mugo")
foliage = unreal.load_asset("/Game/Materials/M_FWFoliage.M_FWFoliage")
PINE_TEXTURES = "/Game/External/Models/pine_tree_01/pine_tree_01/Textures/pine_tree_01_bark"
if os.path.isdir(MUGO) and foliage:
    for file in sorted(os.listdir(MUGO)):
        if not file.endswith(".glb"):
            continue
        name = file[:-4]
        destination = f"/Game/External/Mugo/{name}"
        if library.list_assets(destination, recursive=True):
            library.delete_directory(destination)  # always rebuilt from the script's latest output
        run([os.path.join(MUGO, file)], destination)
        for path in library.list_assets(destination, recursive=True, include_folder=False):
            asset = unreal.load_asset(path)
            if isinstance(asset, unreal.StaticMesh):
                settings = asset.get_editor_property("nanite_settings")
                settings.set_editor_property("enabled", True)
                asset.set_editor_property("nanite_settings", settings)
                library.save_loaded_asset(asset)
            elif isinstance(asset, unreal.MaterialInstanceConstant):
                slot = asset.get_name()
                mel.set_material_instance_parent(asset, foliage)
                if slot == "FW_MugoBark":
                    for param, kind in (("BaseColor", "diff"), ("Normal", "nor_gl")):
                        texture_path = f"{PINE_TEXTURES}_{kind}_2k"
                        if library.does_asset_exist(texture_path):
                            mel.set_material_instance_texture_parameter_value(asset, param, unreal.load_asset(texture_path))
                    # Grey-brown, flaky: darker than the Scots pine the texture was shot from
                    mel.set_material_instance_vector_parameter_value(asset, "Tint", unreal.LinearColor(0.55, 0.5, 0.46, 1))
                    mel.set_material_instance_scalar_parameter_value(asset, "Roughness", 0.85)
                    mel.set_material_instance_scalar_parameter_value(asset, "Wind", 3.0)
                elif slot == "FW_MugoNeedles":
                    # Dark, slightly blue-green needles (Pinus mugo); the vertex colour (0.4-0.9) darkens the base of each brush and lightens young tips
                    mel.set_material_instance_vector_parameter_value(asset, "Tint", unreal.LinearColor(0.042, 0.08, 0.058, 1))
                    mel.set_material_instance_scalar_parameter_value(asset, "Roughness", 0.55)
                    mel.set_material_instance_scalar_parameter_value(asset, "Wind", 5.0)
                else:
                    unreal.log_warning(f"FWIMPORT mugo: unknown material slot {slot} in {path}")
                    continue
                mel.update_material_instance(asset)
                library.save_loaded_asset(asset)
        unreal.log(f"FWIMPORT mugo {name}")

# Leak and stain decal textures (ambientCG, CC0): RawAssets/ambientcg/<id>/<id>_2K-PNG_{Color,Opacity}.png
DECALS = os.path.join(ROOT, "ambientcg")
if os.path.isdir(DECALS):
    for decal_id in sorted(os.listdir(DECALS)):
        folder = os.path.join(DECALS, decal_id)
        destination = f"/Game/External/Decals/{decal_id}"
        if not os.path.isdir(folder) or library.list_assets(destination, recursive=True):
            continue
        files = [os.path.join(folder, f) for f in os.listdir(folder) if f.endswith(("_Color.png", "_Opacity.png"))]
        run(files, destination)
        for path in library.list_assets(destination, recursive=True, include_folder=False):
            texture = unreal.load_asset(path)
            if isinstance(texture, unreal.Texture2D) and "opacity" in path.lower():
                texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_GRAYSCALE)
                texture.set_editor_property("srgb", False)
                library.save_loaded_asset(texture)
        unreal.log(f"FWIMPORT decal {decal_id}")

# Character hair and clothes from Tools/blender/make_character_gear.py: skeletal meshes on the mannequin's
# skeleton (Tools/copy_mannequins.ps1), one per style, in /Game/External/Character
GEAR = os.path.join(MODELS, "character")
skeleton = unreal.load_asset("/Game/Characters/Mannequins/Meshes/SK_Mannequin") if library.does_asset_exist("/Game/Characters/Mannequins/Meshes/SK_Mannequin") else None
cloth_master = unreal.load_asset("/Game/Materials/M_FWCloth.M_FWCloth")
hair_master = unreal.load_asset("/Game/Materials/M_FWHair.M_FWHair")
card_master = unreal.load_asset("/Game/Materials/M_FWHairCard.M_FWHairCard")
# Slot -> fabric (Poly Haven, CC0) and cm per repeat
FABRICS = {"FW_Jersey": ("cotton_jersey", 12.0), "FW_Fleece": ("knitted_fleece", 16.0), "FW_Denim": ("denim_fabric_05", 22.0),
           "FW_Leather": ("leather_white", 14.0)}
if os.path.isdir(GEAR) and skeleton and cloth_master and hair_master:
    destination = "/Game/External/Character"
    if library.list_assets(destination, recursive=True):
        library.delete_directory(destination)  # always rebuilt from the script's latest output
    materials = {}
    for slot in ["FW_Hair", "FW_HairCard"] + list(FABRICS):
        instance = tools.create_asset(f"MI_{slot}", f"{destination}/Materials", unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())
        mel.set_material_instance_parent(instance, {"FW_Hair": hair_master, "FW_HairCard": card_master}.get(slot, cloth_master))
        if slot in FABRICS:
            set_id, scale = FABRICS[slot]
            for param, kind in (("Diffuse", "diff"), ("NormalMap", "nor_gl"), ("ARM", "arm")):
                texture = texture_set(set_id, kind)
                if texture:
                    mel.set_material_instance_texture_parameter_value(instance, param, texture)
            mel.set_material_instance_scalar_parameter_value(instance, "Scale", scale)
        library.save_loaded_asset(instance)
        materials[slot] = instance
    for file in sorted(os.listdir(GEAR)):
        if not file.endswith(".fbx"):
            continue
        task = unreal.AssetImportTask()
        task.set_editor_property("filename", os.path.abspath(os.path.join(GEAR, file)))
        task.set_editor_property("destination_path", destination)
        task.set_editor_property("automated", True)
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)
        options = unreal.FbxImportUI()
        options.set_editor_property("import_mesh", True)
        options.set_editor_property("import_as_skeletal", True)
        options.set_editor_property("mesh_type_to_import", unreal.FBXImportType.FBXIT_SKELETAL_MESH)
        options.set_editor_property("skeleton", skeleton)
        options.set_editor_property("import_materials", False)
        options.set_editor_property("import_textures", False)
        options.set_editor_property("import_animations", False)
        options.set_editor_property("create_physics_asset", False)
        task.set_editor_property("options", options)
        tools.import_asset_tasks([task])
        name = file[:-4]
        mesh = unreal.load_asset(f"{destination}/{name}") if library.does_asset_exist(f"{destination}/{name}") else None
        if not mesh:
            unreal.log_warning(f"FWIMPORT gear {name}: import failed")
            continue
        # The game puts MI_<slot> on each slot when it shows the piece (AFWCharacterStudio::Wear)
        slots = mesh.get_editor_property("materials")
        unreal.log_warning(f"FWIMPORT gear {name}: {[str(s.get_editor_property('material_slot_name')) for s in slots]}")

# Hotbar icons from Tools/blender/render_icons.py: interface textures, no mipmaps
ICONS = os.path.join(MODELS, "icons")
if os.path.isdir(ICONS):
    destination = "/Game/External/Icons"
    if library.list_assets(destination, recursive=True):
        library.delete_directory(destination)
    run([os.path.join(ICONS, f) for f in sorted(os.listdir(ICONS)) if f.endswith(".png")], destination)
    for path in library.list_assets(destination, recursive=False, include_folder=False):
        texture = unreal.load_asset(path)
        if isinstance(texture, unreal.Texture2D):
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
            texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS)
            texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
            library.save_loaded_asset(texture)
    unreal.log_warning(f"FWIMPORT icons: {len(library.list_assets(destination, recursive=False, include_folder=False))}")

# Sounds from Tools/make_audio.py (synthesized, ours): /Game/External/Audio, with the mix set up around them:
# sound classes Master > Ambience / SFX / UI, distance falloff for machines, and voice limits so a hundred belts
# play as the few nearest
AUDIO = os.path.join(MODELS, "audio")
if os.path.isdir(AUDIO):
    destination = "/Game/External/Audio"
    if library.list_assets(destination, recursive=True):
        library.delete_directory(destination)
    run([os.path.join(AUDIO, f) for f in sorted(os.listdir(AUDIO)) if f.endswith(".wav")], destination)

    def make(name, cls, factory):
        return tools.create_asset(name, f"{destination}/Mix", cls, factory)

    master = make("SC_Master", unreal.SoundClass, unreal.SoundClassFactory())
    classes = {}
    for name, volume in (("Ambience", 0.8), ("SFX", 1.0), ("UI", 0.7)):
        sound_class = make(f"SC_{name}", unreal.SoundClass, unreal.SoundClassFactory())
        properties = sound_class.get_editor_property("properties")
        properties.set_editor_property("volume", volume)
        sound_class.set_editor_property("properties", properties)
        sound_class.set_editor_property("parent_class", master)
        library.save_loaded_asset(sound_class)
        classes[name] = sound_class
    library.save_loaded_asset(master)
    for name, limit in (("SCON_Belt", 6), ("SCON_Machine", 4), ("SCON_Footstep", 3)):
        concurrency = make(name, unreal.SoundConcurrency, unreal.SoundConcurrencyFactory())
        settings = concurrency.get_editor_property("concurrency")
        settings.set_editor_property("max_count", limit)
        settings.set_editor_property("resolution_rule", unreal.MaxConcurrentResolutionRule.STOP_FARTHEST_THEN_OLDEST)
        concurrency.set_editor_property("concurrency", settings)
        library.save_loaded_asset(concurrency)
    for name, inner, falloff in (("ATT_Belt", 200.0, 1800.0), ("ATT_Machine", 300.0, 3500.0), ("ATT_World", 400.0, 6000.0)):
        attenuation = make(name, unreal.SoundAttenuation, unreal.SoundAttenuationFactory())
        settings = attenuation.get_editor_property("attenuation")
        settings.set_editor_property("attenuation_shape_extents", unreal.Vector(inner, 0, 0))
        settings.set_editor_property("falloff_distance", falloff)
        settings.set_editor_property("spatialize", True)
        settings.set_editor_property("enable_listener_focus", False)
        attenuation.set_editor_property("attenuation", settings)
        library.save_loaded_asset(attenuation)
    for path in library.list_assets(destination, recursive=False, include_folder=False):
        wave = unreal.load_asset(path)
        if not isinstance(wave, unreal.SoundWave):
            continue
        name = wave.get_name()
        wave.set_editor_property("looping", name.endswith("_loop"))
        group = "Ambience" if name.startswith("amb_") else ("UI" if name.startswith("ui_") else "SFX")
        wave.set_editor_property("sound_class_object", classes[group])
        library.save_loaded_asset(wave)
    unreal.log_warning(f"FWIMPORT audio: {len(library.list_assets(destination, recursive=False, include_folder=False))} sounds")

# The world map's pictures from Tools/blender/make_world_map.py and make_world_tiles.py: the overview of the Earth
# (mipmaps, from afar), the detail pieces (closer up, no mipmaps: never seen smaller than they are), the biome tiles
# seen from above (mipmaps) and the clouds (mipmaps, repeating)
WORLD_MAP = os.path.join(MODELS, "worldmap")
if os.path.isdir(WORLD_MAP):
    destination = "/Game/External/WorldMap"
    if library.list_assets(destination, recursive=True):
        library.delete_directory(destination)
    files = [os.path.join(WORLD_MAP, f) for f in sorted(os.listdir(WORLD_MAP)) if f.endswith(".png")]
    for path in run(files, destination):
        texture = unreal.load_asset(path)
        if not isinstance(texture, unreal.Texture2D):
            continue
        name = texture.get_name()
        detail = name.startswith("world_detail") or name == "world_biomes"
        texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI if detail else unreal.TextureGroup.TEXTUREGROUP_WORLD)
        texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS if detail else unreal.TextureMipGenSettings.TMGS_SIMPLE_AVERAGE)
        texture.set_editor_property("never_stream", True)
        texture.set_editor_property("filter", unreal.TextureFilter.TF_TRILINEAR if not detail else unreal.TextureFilter.TF_BILINEAR)
        if name == "world_overview" or detail:
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_DEFAULT)
        if name == "world_biomes":
            # Biome ids as grey levels, read exactly: linear, uncompressed, nearest
            texture.set_editor_property("srgb", False)
            texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_GRAYSCALE)
            texture.set_editor_property("filter", unreal.TextureFilter.TF_NEAREST)
        if name == "world_clouds":
            texture.set_editor_property("address_x", unreal.TextureAddress.TA_WRAP)
            texture.set_editor_property("address_y", unreal.TextureAddress.TA_WRAP)
        library.save_loaded_asset(texture)
        unreal.log_warning(f"FWIMPORT world map {name}")

# The map's close-up material (M_FWWorldMap, user interface): each plot's biome (world_biomes) picked from the tiles of
# world_tiles, which repeat every few plots; four neighbouring plots blended along borders made wavy by a little noise,
# so biomes run into each other rather than meeting at square edges; the relief of the overview shading the tiles. The
# game sets the view (centre, zoom, size) every frame and fades it in close up.
WORLD_MAP_HLSL = r"""
float2 view = float2(ViewW, ViewH);
float2 map = float2(MapW, MapH);
float2 plot = float2(CenterX, CenterY) + (UV * view - view * 0.5) / Zoom;
// Wavy borders: the point looked up is moved a little, more where the noise says so
float2 warp = float2(sin(plot.y * 2.3 + sin(plot.x * 1.1) * 1.7) + 0.5 * sin(plot.y * 5.1 + plot.x * 3.3),
                     cos(plot.x * 2.1 + sin(plot.y * 1.3) * 1.5) + 0.5 * cos(plot.x * 4.7 - plot.y * 3.9)) * 0.16;
float2 q = plot + warp - 0.5;
float2 cell = floor(q);
float2 f = q - cell;
// Two samplings of each texture, at different scales and offsets, mixed by slow noise: no regular repeat to the eye
float2 tile = plot / Repeat;
float2 tile2 = plot / (Repeat * 1.618) + float2(0.37, 0.71);
// Kept a few texels inside each tile of the atlas, so filtering never bleeds in the neighbouring biome
float2 tileUv = lerp(0.004, 0.996, frac(tile));
float2 tileUv2 = lerp(0.004, 0.996, frac(tile2));
float2 gx = ddx(tile) * 0.25;
float2 gy = ddy(tile) * 0.25;
float2 gx2 = ddx(tile2) * 0.25;
float2 gy2 = ddy(tile2) * 0.25;
float m = smoothstep(0.42, 0.58, 0.5 + 0.9 * (sin(plot.x * 0.41 + sin(plot.y * 0.27) * 2.1) * cos(plot.y * 0.37 + sin(plot.x * 0.23) * 1.9)));
float3 sum = 0;
float total = 0;
float best = -1;
float bestBiome = 0;
for (int j = 0; j < 2; j++)
{
    for (int i = 0; i < 2; i++)
    {
        float2 c = cell + float2(i, j);
        c.x = c.x - floor(c.x / map.x) * map.x;
        c.y = clamp(c.y, 0, map.y - 1);
        float biome = floor(Biomes.SampleLevel(BiomesSampler, (c + 0.5) / map, 0).r * 255.0 / 16.0);
        float w = (i == 1 ? f.x : 1 - f.x) * (j == 1 ? f.y : 1 - f.y);
        w = w * w * w * w;
        float2 at = float2(biome - floor(biome / 4) * 4, floor(biome / 4));
        float3 color = lerp(Tiles.SampleGrad(TilesSampler, (at + tileUv) / 4.0, gx, gy).rgb,
                            Tiles.SampleGrad(TilesSampler, (at + tileUv2) / 4.0, gx2, gy2).rgb, m);
        sum += color * w;
        total += w;
        if (w > best) { best = w; bestBiome = biome; }
    }
}
float3 col = sum / max(total, 1e-5);
// The relief: how much lighter or darker the overview is here than its own surroundings
float2 ouv = float2(plot.x / map.x, plot.y / map.y);
float3 here = Overview.SampleLevel(OverviewSampler, ouv, 0).rgb;
float3 around = Overview.SampleLevel(OverviewSampler, ouv, 4).rgb;
float lumHere = dot(here, float3(0.3, 0.59, 0.11));
float lumAround = dot(around, float3(0.3, 0.59, 0.11));
float water = (bestBiome < 1.5) ? 1.0 : 0.0;
col *= lerp(clamp((lumHere + 0.02) / (lumAround + 0.02), 0.55, 1.6), 1.0, water * 0.8);
return col;
"""

if os.path.isdir(WORLD_MAP):
    folder = "/Game/Materials"
    path = f"{folder}/M_FWWorldMap"
    if library.does_asset_exist(path):
        library.delete_asset(path)
    m = tools.create_asset("M_FWWorldMap", folder, unreal.Material, unreal.MaterialFactoryNew())
    m.set_editor_property("material_domain", unreal.MaterialDomain.MD_UI)
    m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
    custom = mel.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
    custom.set_editor_property("code", WORLD_MAP_HLSL)
    custom.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    names = ["UV", "CenterX", "CenterY", "Zoom", "ViewW", "ViewH", "MapW", "MapH", "Repeat", "Biomes", "Tiles", "Overview"]
    inputs = []
    for name in names:
        item = unreal.CustomInput()
        item.set_editor_property("input_name", name)
        inputs.append(item)
    custom.set_editor_property("inputs", inputs)
    mel.connect_material_expressions(mel.create_material_expression(m, unreal.MaterialExpressionTextureCoordinate, -900, -400), "", custom, "UV")
    defaults = {"CenterX": 3466.0, "CenterY": 1471.0, "Zoom": 40.0, "ViewW": 1920.0, "ViewH": 1080.0, "MapW": 6933.0, "MapH": 2942.0, "Repeat": 5.0}
    y = -300
    for name, value in defaults.items():
        node = mel.create_material_expression(m, unreal.MaterialExpressionScalarParameter, -900, y)
        node.set_editor_property("parameter_name", name)
        node.set_editor_property("default_value", value)
        mel.connect_material_expressions(node, "", custom, name)
        y += 60
    for name, texture in (("Biomes", "world_biomes"), ("Tiles", "world_tiles"), ("Overview", "world_overview")):
        node = mel.create_material_expression(m, unreal.MaterialExpressionTextureObjectParameter, -900, y)
        node.set_editor_property("parameter_name", name)
        node.set_editor_property("texture", unreal.load_asset(f"/Game/External/WorldMap/{texture}"))
        if name == "Biomes":
            node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_GRAYSCALE)
        mel.connect_material_expressions(node, "", custom, name)
        y += 80
    fade = mel.create_material_expression(m, unreal.MaterialExpressionScalarParameter, -400, 300)
    fade.set_editor_property("parameter_name", "Fade")
    fade.set_editor_property("default_value", 1.0)
    mel.connect_material_property(custom, "", unreal.MaterialProperty.MP_EMISSIVE_COLOR)
    mel.connect_material_property(fade, "", unreal.MaterialProperty.MP_OPACITY)
    mel.recompile_material(m)
    library.save_asset(path)
    unreal.log_warning("FWIMPORT world map material")
