# Builds the character creator's MetaHuman heads, one per face style of the server's "character.options"
# (0 calm, 1 tired, 2 cheerful, 3 mature), from the MetaHuman Creator presets shipped with the engine.
# Each preset is copied to /Game/MetaHumans/Faces/FW_Face<n> (one skin tone, no hair or facial hair), auto-rigged and
# given its textures by Epic's MetaHuman cloud service (needs an Epic account signed in once: the first run opens a
# browser to sign in), and assembled with the optimized pipeline into /Game/MetaHumans/Built/FW_Face<n>.
# The MetaHuman license allows them only in Unreal Engine projects: Content/MetaHumans/ stays out of git.
# Needs the MetaHuman Creator plugin and its "MetaHuman Creator Core Data" (Epic Games Launcher > UE 5.8 >
# Options). The texture graphs bake only in the full editor: run
#   UnrealEditor.exe <full path>/FactoryWorld3D.uproject -ExecCmds="py <full path>/Tools/make_metahumans.py"
# which builds the heads once the editor is up and closes it. Heads already built are kept.

import os
import time
import unreal

PRESETS = ("Dominic", "Victor", "Mateo", "Walter")
FACES = "/Game/MetaHumans/Faces"
BUILT = "/Game/MetaHumans/Built"
# One skin tone for all heads (the Creator's skin tone picker); the game tints it to the server's palette
# (BakedSkin in Source/FactoryWorld3D/Game/FWGear.cpp)
SKIN_U, SKIN_V = 0.5, 0.5


def built(name):
    return any(unreal.EditorAssetLibrary.find_asset_data(path).asset_class_path.asset_name == "SkeletalMesh"
               for path in unreal.EditorAssetLibrary.list_assets(f"{BUILT}/{name}", recursive=True, include_folder=False))


def read_tga(path):
    """An uncompressed 24/32-bit TGA as (width, height, bytes per pixel, rows top first: bytearrays of BGR(A))."""
    data = open(path, "rb").read()
    width, height, depth = int.from_bytes(data[12:14], "little"), int.from_bytes(data[14:16], "little"), data[16] // 8
    start = 18 + data[0]
    rows = [bytearray(data[start + y * width * depth:start + (y + 1) * width * depth]) for y in range(height)]
    if not data[17] & 32:
        rows.reverse()
    return width, height, depth, rows


def write_tga(path, width, height, depth, rows):
    header = bytearray(18)
    header[2] = 2
    header[12:14] = width.to_bytes(2, "little")
    header[14:16] = height.to_bytes(2, "little")
    header[16] = depth * 8
    header[17] = 32
    with open(path, "wb") as out:
        out.write(header)
        for row in rows:
            out.write(row)


def bake_brows(name):
    """The close-up skin (levels of detail 0-4) has no eyebrows: in the optimized pipeline they are hair cards, which
    this game does not draw. The far skin (5-7) has them baked in, on the same UV layout: copied over where it is
    darker, around the brows, into a new close-up texture the skin material is given."""
    face = f"{BUILT}/{name}/Face"
    target = f"{face}/Baked/T_Head_LOD3_BC_Brows"
    material = unreal.load_asset(f"{face}/Materials/MI_Face_Skin_Baked_LOD3_VT")
    near = unreal.load_asset(f"{face}/Baked/T_Head_LOD3_BC_VT")
    far = unreal.load_asset(f"{face}/Baked/T_Head_LOD5to7_BC_VT")
    if material is None or near is None or far is None or unreal.EditorAssetLibrary.does_asset_exist(target):
        return
    folder = os.path.join(unreal.Paths.project_saved_dir(), "MetaHumanBrows")
    os.makedirs(folder, exist_ok=True)
    files = {}
    for key, texture in (("near", near), ("far", far)):
        task = unreal.AssetExportTask()
        task.object = texture
        task.filename = files[key] = os.path.join(folder, f"{name}_{key}.tga")
        task.automated = True
        task.prompt = False
        task.replace_identical = True
        unreal.Exporter.run_asset_export_task(task)
    width, height, depth, rows = read_tga(files["near"])
    far_width, far_height, far_depth, far_rows = read_tga(files["far"])
    # The brows' box on the MetaHuman head UVs (0-1, top first)
    for y in range(int(height * 0.26), int(height * 0.38)):
        row, far_row = rows[y], far_rows[y * far_height // height]
        for x in range(int(width * 0.26), int(width * 0.74)):
            i, j = x * depth, (x * far_width // width) * far_depth
            near_light = row[i] * 0.11 + row[i + 1] * 0.59 + row[i + 2] * 0.3
            far_light = far_row[j] * 0.11 + far_row[j + 1] * 0.59 + far_row[j + 2] * 0.3
            mix = min(1.0, max(0.0, (near_light - far_light - 6.0) / 18.0))
            if mix > 0.0:
                for c in range(3):
                    row[i + c] = int(row[i + c] + (far_row[j + c] - row[i + c]) * mix)
    baked = os.path.join(folder, f"{name}_brows.tga")
    write_tga(baked, width, height, depth, rows)
    task = unreal.AssetImportTask()
    task.filename = baked
    task.destination_path = f"{face}/Baked"
    task.destination_name = "T_Head_LOD3_BC_Brows"
    task.automated = True
    task.replace_existing = True
    task.save = False
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    texture = unreal.load_asset(target)
    for setting in ("srgb", "virtual_texture_streaming", "compression_settings", "lod_group"):
        texture.set_editor_property(setting, near.get_editor_property(setting))
    for parameter in material.get_editor_property("texture_parameter_values"):
        if parameter.parameter_value == near:
            unreal.MaterialEditingLibrary.set_material_instance_texture_parameter_value(
                material, parameter.parameter_info.name, texture)
    unreal.EditorAssetLibrary.save_loaded_asset(texture)
    unreal.EditorAssetLibrary.save_loaded_asset(material)
    unreal.log_warning(f"FW MetaHuman {name}: eyebrows baked into the close-up skin")


def build_all():
    registry = unreal.AssetRegistryHelpers.get_asset_registry()
    registry.scan_paths_synchronous(["/MetaHumanCharacter/Optional/Presets"], True)
    subsystem = unreal.get_editor_subsystem(unreal.MetaHumanCharacterEditorSubsystem)
    tools = unreal.AssetToolsHelpers.get_asset_tools()

    for index, preset in enumerate(PRESETS):
        name = f"FW_Face{index}"
        if built(name):
            unreal.log_warning(f"FW MetaHuman {name}: already built")
            continue
        character = unreal.load_asset(f"{FACES}/{name}.{name}")
        if character is None:
            source = unreal.load_asset(f"/MetaHumanCharacter/Optional/Presets/{preset}.{preset}")
            if source is None:
                raise RuntimeError(f"No MetaHuman preset {preset}: install MetaHuman Creator Core Data")
            character = tools.duplicate_asset(name, FACES, source)
        if not subsystem.try_add_object_to_edit(character):
            raise RuntimeError(f"{name} is open in an editor")
        try:
            started = time.time()
            if not subsystem.can_build_meta_human(character=character):
                # A still face: joints are enough
                rig = unreal.MetaHumanCharacterAutoRiggingRequestParams()
                rig.blocking = True
                rig.report_progress = False
                rig.rig_type = unreal.MetaHumanRigType.JOINTS_ONLY
                subsystem.request_auto_rigging(character=character, params=rig)
            # The game puts its own hair and facial hair on: the preset's are left out (also from the baked skin)
            for slot in ("Hair", "Beard", "Mustache"):
                character.internal_collection.default_instance.set_single_slot_selection(slot, unreal.MetaHumanPaletteItemKey())
            skin = character.get_editor_property("skin_settings")
            skin.skin.u = SKIN_U
            skin.skin.v = SKIN_V
            subsystem.commit_skin_settings(character, skin)
            textures = unreal.MetaHumanCharacterTextureRequestParams()
            textures.blocking = True
            textures.report_progress = False
            subsystem.request_texture_sources(character=character, params=textures)
            if not subsystem.can_build_meta_human(character=character, log_error=True):
                raise RuntimeError(f"{name} cannot be built (signed in to Epic?)")
            build = unreal.MetaHumanCharacterEditorBuildParameters()
            build.pipeline_type = unreal.MetaHumanDefaultPipelineType.OPTIMIZED
            build.pipeline_quality = unreal.MetaHumanQualityLevel.MEDIUM
            build.absolute_build_path = BUILT
            build.common_folder_path = f"{BUILT}/Common"
            try:
                subsystem.build_meta_human(character=character, params=build)
            except RuntimeError as error:
                # Errors logged while copying Epic's control rigs come back as an exception; the build goes on
                unreal.log_warning(f"FW MetaHuman {name}: {str(error)[:300]}")
            unreal.EditorAssetLibrary.save_loaded_asset(character)
            unreal.EditorLoadingAndSavingUtils.save_dirty_packages(True, True)
            unreal.log_warning(f"FW MetaHuman {name} ({preset}) built in {time.time() - started:.0f} s")
        finally:
            if subsystem.is_object_added_for_editing(character):
                subsystem.remove_object_to_edit(character)

    for index in range(len(PRESETS)):
        bake_brows(f"FW_Face{index}")

    unreal.EditorLoadingAndSavingUtils.save_dirty_packages(True, True)
    for path in unreal.EditorAssetLibrary.list_assets(BUILT, recursive=True, include_folder=False):
        data = unreal.EditorAssetLibrary.find_asset_data(path)
        unreal.log_warning(f"FW MetaHuman asset {path} {data.asset_class_path.asset_name}")


# Started with the editor: wait until it is up and ticking, build, then close it
ticks = 0


def on_tick(delta):
    global ticks
    ticks += 1
    if ticks != 120:
        return
    unreal.unregister_slate_post_tick_callback(handle)
    try:
        build_all()
    finally:
        unreal.SystemLibrary.quit_editor()


handle = unreal.register_slate_post_tick_callback(on_tick)
