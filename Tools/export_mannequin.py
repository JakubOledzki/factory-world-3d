# Exports the UE5 mannequin (Content/Characters/Mannequins, see Tools/copy_mannequins.ps1) to
# RawAssets/mannequin/SKM_Manny_Simple.fbx, so Tools/blender/make_character_gear.py can build hair and clothes
# skinned to the same skeleton. Run in the editor (it closes itself):
#   UnrealEditor.exe FactoryWorld3D.uproject -ExecutePythonScript="Tools/export_mannequin.py"
import os

import unreal

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RawAssets", "mannequin")
os.makedirs(OUT, exist_ok=True)
mesh = unreal.load_asset("/Game/Characters/Mannequins/Meshes/SKM_Manny_Simple")
task = unreal.AssetExportTask()
task.object = mesh
task.filename = os.path.abspath(os.path.join(OUT, "SKM_Manny_Simple.fbx"))
task.automated = True
task.replace_identical = True
task.prompt = False
options = unreal.FbxExportOption()
options.export_morph_targets = False
options.level_of_detail = False
task.options = options
ok = unreal.Exporter.run_asset_export_task(task)
unreal.log_warning(f"FWEXPORT mannequin {'ok' if ok else 'FAILED'} -> {task.filename}")
# Run with -ExecutePythonScript in the full editor (the FBX exporter needs a renderer); close it when done
unreal.SystemLibrary.quit_editor()
