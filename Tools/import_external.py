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
for file in sorted(os.listdir(MODELS)):
    if not file.endswith(".glb"):
        continue
    name = file[:-4]
    destination = f"/Game/External/Models/{name}"
    if not library.list_assets(destination, recursive=True):  # already imported: just fix settings
        run([os.path.join(MODELS, file)], destination)
    # Import tasks do not report what Interchange made, so look in the folder
    for path in library.list_assets(destination, recursive=True, include_folder=False):
        asset = unreal.load_asset(path)
        if not isinstance(asset, unreal.StaticMesh):
            continue
        settings = asset.get_editor_property("nanite_settings")
        settings.set_editor_property("enabled", True)
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
    if "/materials/" not in lower or not any(k in lower for k in ("twig", "leaf", "leaves", "needle")):
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
