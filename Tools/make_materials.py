# Builds the base materials the C++ code tints at runtime (Content/Materials):
#   M_FWSurface  opaque PBR surface: Color, Roughness, Metallic
#   M_FWGround   terrain: Color broken up by two scales of world-space noise, rough
#   M_FWGhost    translucent unlit preview for build mode: Color, Opacity
# Run headless:
#   UnrealEditor-Cmd.exe FactoryWorld3D.uproject -run=pythonscript -script="Tools/make_materials.py"
import unreal

FOLDER = "/Game/Materials"
assets = unreal.AssetToolsHelpers.get_asset_tools()
lib = unreal.MaterialEditingLibrary


def fresh(name):
    path = f"{FOLDER}/{name}"
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        unreal.EditorAssetLibrary.delete_asset(path)
    material = assets.create_asset(name, FOLDER, unreal.Material, unreal.MaterialFactoryNew())
    # Drawn on instanced meshes (terrain, items on belts), so the usage must be set up front
    material.set_editor_property("used_with_instanced_static_meshes", True)
    return material


def vector_param(material, name, default, x, y):
    node = lib.create_material_expression(material, unreal.MaterialExpressionVectorParameter, x, y)
    node.set_editor_property("parameter_name", name)
    node.set_editor_property("default_value", unreal.LinearColor(*default))
    return node


def scalar_param(material, name, default, x, y):
    node = lib.create_material_expression(material, unreal.MaterialExpressionScalarParameter, x, y)
    node.set_editor_property("parameter_name", name)
    node.set_editor_property("default_value", default)
    return node


def noise(material, scale, x, y):
    node = lib.create_material_expression(material, unreal.MaterialExpressionNoise, x, y)
    node.set_editor_property("scale", scale)
    node.set_editor_property("output_min", 0.0)
    node.set_editor_property("output_max", 1.0)
    node.set_editor_property("levels", 4)
    return node


# Surface
m = fresh("M_FWSurface")
color = vector_param(m, "Color", (0.5, 0.5, 0.5, 1), -600, -200)
rough = scalar_param(m, "Roughness", 0.6, -600, 0)
metal = scalar_param(m, "Metallic", 0.0, -600, 150)
lib.connect_material_property(color, "", unreal.MaterialProperty.MP_BASE_COLOR)
lib.connect_material_property(rough, "", unreal.MaterialProperty.MP_ROUGHNESS)
lib.connect_material_property(metal, "", unreal.MaterialProperty.MP_METALLIC)
lib.recompile_material(m)

# Ground: colour * lerp(0.7, 1.15, coarse noise) * lerp(0.85, 1.1, fine noise)
m = fresh("M_FWGround")
color = vector_param(m, "Color", (0.3, 0.3, 0.28, 1), -900, -300)
coarse = noise(m, 0.0004, -900, -100)
fine = noise(m, 0.01, -900, 150)
lerp_coarse = lib.create_material_expression(m, unreal.MaterialExpressionLinearInterpolate, -600, -100)
lerp_coarse.set_editor_property("const_a", 0.7)
lerp_coarse.set_editor_property("const_b", 1.15)
lib.connect_material_expressions(coarse, "", lerp_coarse, "Alpha")
lerp_fine = lib.create_material_expression(m, unreal.MaterialExpressionLinearInterpolate, -600, 150)
lerp_fine.set_editor_property("const_a", 0.85)
lerp_fine.set_editor_property("const_b", 1.1)
lib.connect_material_expressions(fine, "", lerp_fine, "Alpha")
mul1 = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -350, -200)
lib.connect_material_expressions(color, "", mul1, "A")
lib.connect_material_expressions(lerp_coarse, "", mul1, "B")
mul2 = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -150, -100)
lib.connect_material_expressions(mul1, "", mul2, "A")
lib.connect_material_expressions(lerp_fine, "", mul2, "B")
lib.connect_material_property(mul2, "", unreal.MaterialProperty.MP_BASE_COLOR)
rough = scalar_param(m, "Roughness", 0.92, -350, 250)
lib.connect_material_property(rough, "", unreal.MaterialProperty.MP_ROUGHNESS)
lib.recompile_material(m)

# Ghost
m = fresh("M_FWGhost")
m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
m.set_editor_property("shading_model", unreal.MaterialShadingModel.MSM_UNLIT)
color = vector_param(m, "Color", (0.2, 0.8, 1.0, 1), -600, -200)
opacity = scalar_param(m, "Opacity", 0.4, -600, 0)
lib.connect_material_property(color, "", unreal.MaterialProperty.MP_EMISSIVE_COLOR)
lib.connect_material_property(opacity, "", unreal.MaterialProperty.MP_OPACITY)
lib.recompile_material(m)

unreal.EditorAssetLibrary.save_directory(FOLDER)
unreal.log("FW materials saved")
