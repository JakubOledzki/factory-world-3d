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
