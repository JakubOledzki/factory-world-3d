# Builds the base materials the C++ code tints at runtime (Content/Materials):
#   M_FWSurface  opaque PBR surface: Color, Roughness, Metallic
#   M_FWGround   terrain: Color broken up by two scales of world-space noise, rough
#   M_FWGhost    translucent unlit preview for build mode: Color, Opacity
# Run headless:
#   UnrealEditor-Cmd.exe FactoryWorld3D.uproject -run=pythonscript -script="Tools/make_materials.py"
import os

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
    # Scans and the land are Nanite meshes; without this usage a cooked or -game run falls back to the
    # default (flat grey) material
    material.set_editor_property("used_with_nanite", True)
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


# Terrain: four photographed layers blended by slope, height and patches in one HLSL node
# (Tools/terrain_material.hlsl). The game sets the textures at runtime from Content/External, so this
# material keeps working (with engine defaults) when those are not set up.
m = fresh("M_FWTerrain")
hlsl = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "terrain_material.hlsl"), encoding="utf-8").read()
custom = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
custom.set_editor_property("code", "\n".join(line for line in hlsl.splitlines() if not line.startswith("//")))
custom.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
names = ["WP", "N", "Cam", "Tint", "GrassAmount", "SnowHeight", "Scale"]
textures = [f"{layer}{kind}" for layer in ("Base", "Grass", "Cliff", "Snow") for kind in ("D", "N", "M")]
inputs = []
for name in names + textures:
    item = unreal.CustomInput()
    item.set_editor_property("input_name", name)
    inputs.append(item)
custom.set_editor_property("inputs", inputs)
outputs = []
for name in ("NormalOut", "ArmOut"):
    item = unreal.CustomOutput()
    item.set_editor_property("output_name", name)
    item.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    outputs.append(item)
custom.set_editor_property("additional_outputs", outputs)

wp = lib.create_material_expression(m, unreal.MaterialExpressionWorldPosition, -900, -600)
lib.connect_material_expressions(wp, "", custom, "WP")
normal_ws = lib.create_material_expression(m, unreal.MaterialExpressionVertexNormalWS, -900, -520)
lib.connect_material_expressions(normal_ws, "", custom, "N")
camera = lib.create_material_expression(m, unreal.MaterialExpressionCameraPositionWS, -900, -480)
lib.connect_material_expressions(camera, "", custom, "Cam")
for i, (name, default) in enumerate((("Tint", 1.0), ("GrassAmount", 0.6), ("SnowHeight", 60000.0), ("Scale", 600.0))):
    lib.connect_material_expressions(scalar_param(m, name, default, -900, -440 + i * 80), "", custom, name)
default_texture = unreal.load_asset("/Engine/EngineResources/DefaultTexture.DefaultTexture")
for i, name in enumerate(textures):
    node = lib.create_material_expression(m, unreal.MaterialExpressionTextureObjectParameter, -900, -100 + i * 90)
    node.set_editor_property("parameter_name", name)
    node.set_editor_property("texture", default_texture)
    lib.connect_material_expressions(node, "", custom, name)

lib.connect_material_property(custom, "", unreal.MaterialProperty.MP_BASE_COLOR)
lib.connect_material_property(custom, "NormalOut", unreal.MaterialProperty.MP_NORMAL)
arm = lib.create_material_expression(m, unreal.MaterialExpressionComponentMask, -150, 200)
for channel, prop in (("r", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION), ("g", unreal.MaterialProperty.MP_ROUGHNESS), ("b", unreal.MaterialProperty.MP_METALLIC)):
    mask = lib.create_material_expression(m, unreal.MaterialExpressionComponentMask, -150, 200 + "rgb".index(channel) * 80)
    mask.set_editor_property("r", channel == "r")
    mask.set_editor_property("g", channel == "g")
    mask.set_editor_property("b", channel == "b")
    lib.connect_material_expressions(custom, "ArmOut", mask, "")
    lib.connect_material_property(mask, "", prop)
lib.delete_material_expression(m, arm)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW terrain material saved")


# Scanned models (rocks, plants): plain textured PBR, so their textures sample on Nanite meshes
# (the glTF importer's own master did not). Tools/import_external.py points every scan at an instance.
def scan_material(name, masked):
    m = fresh(name)
    if masked:
        m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
        m.set_editor_property("two_sided", True)
    color = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -700, -300)
    color.set_editor_property("parameter_name", "BaseColor")
    color.set_editor_property("texture", unreal.load_asset("/Engine/EngineResources/DefaultTexture.DefaultTexture"))
    tint = vector_param(m, "Tint", (1, 1, 1, 1), -700, -500)
    mul = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -350, -300)
    lib.connect_material_expressions(color, "RGB", mul, "A")
    lib.connect_material_expressions(tint, "", mul, "B")
    # Grime: the first ~60 cm above the object's base get darker and browner, as dust and mud splash up
    # (Grime 0 turns it off; rocks and plants keep 0)
    grime_hlsl = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -350, -500)
    grime_hlsl.set_editor_property("code", "float h = saturate((WP.z - Base.z) / 60.0); float n = frac(sin(dot(floor(WP.xy / 7.0), float2(12.9898, 78.233))) * 43758.5453); float g = (1.0 - h) * (1.0 - h) * Amount * (0.7 + 0.3 * n); return lerp(C, C * float3(0.55, 0.48, 0.4), g);")
    grime_hlsl.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    grime_inputs = []
    for name in ("C", "WP", "Base", "Amount"):
        item = unreal.CustomInput()
        item.set_editor_property("input_name", name)
        grime_inputs.append(item)
    grime_hlsl.set_editor_property("inputs", grime_inputs)
    lib.connect_material_expressions(mul, "", grime_hlsl, "C")
    lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionWorldPosition, -700, -700), "", grime_hlsl, "WP")
    lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionObjectPositionWS, -700, -620), "", grime_hlsl, "Base")
    lib.connect_material_expressions(scalar_param(m, "Grime", 0.0, -700, -540), "", grime_hlsl, "Amount")
    lib.connect_material_property(grime_hlsl, "", unreal.MaterialProperty.MP_BASE_COLOR)
    if masked:
        lib.connect_material_property(color, "A", unreal.MaterialProperty.MP_OPACITY_MASK)
    normal = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -700, 0)
    normal.set_editor_property("parameter_name", "Normal")
    normal.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL)
    normal.set_editor_property("texture", unreal.load_asset("/Engine/EngineMaterials/DefaultNormal.DefaultNormal"))
    lib.connect_material_property(normal, "RGB", unreal.MaterialProperty.MP_NORMAL)
    arm = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -700, 300)
    arm.set_editor_property("parameter_name", "ARM")
    arm.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_MASKS)
    arm.set_editor_property("texture", unreal.load_asset("/Engine/EngineMaterials/DefaultWhiteGrain.DefaultWhiteGrain") or unreal.load_asset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"))
    lib.connect_material_property(arm, "G", unreal.MaterialProperty.MP_ROUGHNESS)
    # Some scans ship a roughness map instead of AO/rough/metal: MetalScale 0 then
    metal = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -350, 350)
    lib.connect_material_expressions(arm, "B", metal, "A")
    lib.connect_material_expressions(scalar_param(m, "MetalScale", 1.0, -700, 550), "", metal, "B")
    lib.connect_material_property(metal, "", unreal.MaterialProperty.MP_METALLIC)
    lib.recompile_material(m)
    unreal.EditorAssetLibrary.save_asset(m.get_path_name())


scan_material("M_FWScan", False)
scan_material("M_FWScanMasked", True)
unreal.log("FW scan materials saved")


# Conveyor band: textured like the scans, its UVs pan along the belt (U) at Speed repeats per second.
# Belts move items 1 m per 0.2 s tick and the band's UV repeats every 2 m, so Speed = 2.5 for a plain belt.
m = fresh("M_FWBelt")
coords = lib.create_material_expression(m, unreal.MaterialExpressionTextureCoordinate, -1100, 0)
time = lib.create_material_expression(m, unreal.MaterialExpressionTime, -1100, 150)
speed = scalar_param(m, "Speed", 2.5, -1100, 250)
offset = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -900, 200)
lib.connect_material_expressions(time, "", offset, "A")
lib.connect_material_expressions(speed, "", offset, "B")
pan = lib.create_material_expression(m, unreal.MaterialExpressionAppendVector, -750, 200)
lib.connect_material_expressions(offset, "", pan, "A")
zero = lib.create_material_expression(m, unreal.MaterialExpressionConstant, -900, 320)
lib.connect_material_expressions(zero, "", pan, "B")
uv = lib.create_material_expression(m, unreal.MaterialExpressionSubtract, -600, 50)
lib.connect_material_expressions(coords, "", uv, "A")
lib.connect_material_expressions(pan, "", uv, "B")
for i, (name, kind, prop) in enumerate((("BaseColor", unreal.MaterialSamplerType.SAMPLERTYPE_COLOR, unreal.MaterialProperty.MP_BASE_COLOR),
                                         ("Normal", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL, unreal.MaterialProperty.MP_NORMAL))):
    sample = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -400, -200 + i * 250)
    sample.set_editor_property("parameter_name", name)
    sample.set_editor_property("sampler_type", kind)
    sample.set_editor_property("texture", unreal.load_asset("/Engine/EngineMaterials/DefaultNormal.DefaultNormal" if name == "Normal" else "/Engine/EngineResources/DefaultTexture.DefaultTexture"))
    lib.connect_material_expressions(uv, "", sample, "UVs")
    if name == "BaseColor":
        # The rubber texture is a red running track: keep its detail, make it black rubber
        grey = lib.create_material_expression(m, unreal.MaterialExpressionDesaturation, -200, -200)
        lib.connect_material_expressions(sample, "RGB", grey, "")
        dark = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -50, -200)
        lib.connect_material_expressions(grey, "", dark, "A")
        lib.connect_material_expressions(scalar_param(m, "Brightness", 0.12, -200, -50), "", dark, "B")
        lib.connect_material_property(dark, "", prop)
    else:
        lib.connect_material_property(sample, "RGB", prop)
lib.connect_material_property(scalar_param(m, "Roughness", 0.85, -400, 350), "", unreal.MaterialProperty.MP_ROUGHNESS)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW belt material saved")
