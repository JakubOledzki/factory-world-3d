# Builds Content/Maps/TestLevel: the realistic lighting rig plus a few blocks that cast shadows.
# Run headless:
#   UnrealEditor-Cmd.exe FactoryWorld3D.uproject -run=pythonscript -script="Tools/make_test_level.py"
import unreal

LEVEL = "/Game/Maps/TestLevel"

levels = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)

if unreal.EditorAssetLibrary.does_asset_exist(LEVEL):
    levels.load_level(LEVEL)
    for actor in actors.get_all_level_actors():
        actors.destroy_actor(actor)
else:
    levels.new_level(LEVEL)

actors.spawn_actor_from_class(unreal.FWEnvironment, unreal.Vector(0, 0, 0))

start = actors.spawn_actor_from_class(unreal.PlayerStart, unreal.Vector(-1200, -900, 250), unreal.Rotator(0, -8, 35))

cube = unreal.load_asset("/Engine/BasicShapes/Cube.Cube")
cylinder = unreal.load_asset("/Engine/BasicShapes/Cylinder.Cylinder")
blocks = [
    # mesh, location, scale
    (cube, (0, 0, 150), (6, 4, 3)),
    (cube, (900, -300, 300), (4, 4, 6)),
    (cylinder, (450, 600, 250), (3, 3, 5)),
    (cylinder, (-300, 800, 400), (1.5, 1.5, 8)),
    (cube, (-200, -400, 25), (30, 1, 0.5)),
]
for mesh, loc, scale in blocks:
    actor = actors.spawn_actor_from_class(unreal.StaticMeshActor, unreal.Vector(*loc))
    actor.static_mesh_component.set_static_mesh(mesh)
    actor.set_actor_scale3d(unreal.Vector(*scale))
    # Hidden by the game once a plot is entered
    actor.tags = ["FWTestBlock"]

levels.save_current_level()
unreal.log("TestLevel saved")
