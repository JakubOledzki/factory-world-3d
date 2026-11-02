# Exports the MetaHuman heads (Tools/make_metahumans.py, /Game/MetaHumans/Built/FW_Face<n>) with their skeleton
# to RawAssets/metahuman/FW_Face<n>_Face.fbx, so Tools/blender/make_character_gear.py can grow each hairstyle on
# each head's own scalp.
# Run headless:
#   UnrealEditor-Cmd.exe FactoryWorld3D.uproject -run=pythonscript -script="Tools/export_metahuman_heads.py" -AllowCommandletRendering
# (the export draws the mesh: without -AllowCommandletRendering the commandlet stops on an assertion)
import os

import unreal

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RawAssets", "metahuman")
os.makedirs(OUT, exist_ok=True)

face = 0
while True:
    name = f"SKM_FW_Face{face}_FaceMesh"
    path = f"/Game/MetaHumans/Built/FW_Face{face}/Face/{name}.{name}"
    if not unreal.EditorAssetLibrary.does_asset_exist(path):
        break
    mesh = unreal.load_asset(path)
    options = unreal.FbxExportOption()
    options.set_editor_property("level_of_detail", False)
    options.set_editor_property("export_morph_targets", False)
    options.set_editor_property("collision", False)
    task = unreal.AssetExportTask()
    task.set_editor_property("object", mesh)
    task.set_editor_property("filename", os.path.abspath(os.path.join(OUT, f"FW_Face{face}_Face.fbx")))
    task.set_editor_property("automated", True)
    task.set_editor_property("replace_identical", True)
    task.set_editor_property("prompt", False)
    task.set_editor_property("options", options)
    ok = unreal.Exporter.run_asset_export_task(task)
    unreal.log_warning(f"FWEXPORT FW_Face{face}: {ok}")
    face += 1
