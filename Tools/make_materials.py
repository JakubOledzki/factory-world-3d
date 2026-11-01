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
names = ["WP", "N", "Cam", "Tint", "GrassAmount", "SnowHeight", "Scale", "PlotElevation", "ForestTop", "TreeLine", "ClearX", "ClearY", "ClearHalf"]
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
for i, (name, default) in enumerate((("Tint", 1.0), ("GrassAmount", 0.6), ("SnowHeight", 60000.0), ("Scale", 600.0),
                                      ("PlotElevation", 0.0), ("ForestTop", 1300.0), ("TreeLine", 1580.0),
                                      ("ClearX", 0.0), ("ClearY", 0.0), ("ClearHalf", 0.0))):
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
    # Wear 0 (rocks, plants) leaves the surface as it is
    rust = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -1100, -300)
    rust.set_editor_property("parameter_name", "Rust")
    rust.set_editor_property("texture", unreal.load_asset("/Engine/EngineResources/DefaultTexture.DefaultTexture"))
    wear_hlsl = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -550, -420)
    wear_hlsl.set_editor_property("code", "float3 dn = fwidth(N); float dp = max(length(fwidth(WP)), 0.001); float curve = saturate(length(dn) / dp * 25.0 - 0.15); float mask = saturate(curve * (0.6 + noise)) * Wear; return lerp(Paint, Bare, mask);")
    wear_hlsl.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    wear_inputs = []
    for name in ("Paint", "Bare", "N", "WP", "Wear", "noise"):
        item = unreal.CustomInput()
        item.set_editor_property("input_name", name)
        wear_inputs.append(item)
    wear_hlsl.set_editor_property("inputs", wear_inputs)
    lib.connect_material_expressions(mul, "", wear_hlsl, "Paint")
    # Edge wear: where the surface curves sharply (the bevels), paint is chipped down to bare steel
    # (the Rust texture slot holds the bare metal); found from screen-space curvature, no bake needed
    lib.connect_material_expressions(rust, "RGB", wear_hlsl, "Bare")
    lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionVertexNormalWS, -1100, -500), "", wear_hlsl, "N")
    lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionWorldPosition, -1100, -460), "", wear_hlsl, "WP")
    lib.connect_material_expressions(scalar_param(m, "Wear", 0.0, -1100, -580), "", wear_hlsl, "Wear")
    lib.connect_material_expressions(rust, "R", wear_hlsl, "noise")
    lib.connect_material_expressions(wear_hlsl, "", grime_hlsl, "C")
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


# Foliage made in Blender (dwarf pine, Tools/blender/make_mugo_pine.py): BaseColor x Tint x vertex colour (the
# shade of each needle brush), and wind. The sway grows with height above the plant's base and is offset
# per place, so neighbouring shrubs do not move in step; components switch it off past a short distance
m = fresh("M_FWFoliage")
color = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -900, -300)
color.set_editor_property("parameter_name", "BaseColor")
color.set_editor_property("texture", unreal.load_asset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"))
tinted = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -600, -300)
lib.connect_material_expressions(color, "RGB", tinted, "A")
lib.connect_material_expressions(vector_param(m, "Tint", (1, 1, 1, 1), -900, -450), "", tinted, "B")
shaded = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -400, -300)
lib.connect_material_expressions(tinted, "", shaded, "A")
lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionVertexColor, -600, -180), "", shaded, "B")
lib.connect_material_property(shaded, "", unreal.MaterialProperty.MP_BASE_COLOR)
normal = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -900, 0)
normal.set_editor_property("parameter_name", "Normal")
normal.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL)
normal.set_editor_property("texture", unreal.load_asset("/Engine/EngineMaterials/DefaultNormal.DefaultNormal"))
lib.connect_material_property(normal, "RGB", unreal.MaterialProperty.MP_NORMAL)
lib.connect_material_property(scalar_param(m, "Roughness", 0.6, -600, 100), "", unreal.MaterialProperty.MP_ROUGHNESS)
wind = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 300)
wind.set_editor_property("code", "float h = saturate((WP.z - Base.z) / 220.0); float p = dot(Base.xy, float2(0.0011, 0.0017)) + dot(WP.xy, float2(0.004, 0.003)); "
                                 "float sway = sin(T * 1.4 + p) + 0.5 * sin(T * 2.3 + p * 1.7); float flutter = sin(T * 7.0 + dot(WP.xyz, float3(0.09, 0.07, 0.11))) * 0.25; "
                                 "return float3(sway, 0.6 * cos(T * 1.1 + p), 0.0) * (h * h * Amount) + float3(flutter, flutter, flutter * 0.5) * (h * Amount);")
wind.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
wind_inputs = []
for name in ("WP", "Base", "T", "Amount"):
    item = unreal.CustomInput()
    item.set_editor_property("input_name", name)
    wind_inputs.append(item)
wind.set_editor_property("inputs", wind_inputs)
lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionWorldPosition, -900, 250), "", wind, "WP")
lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionObjectPositionWS, -900, 330), "", wind, "Base")
lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionTime, -900, 410), "", wind, "T")
lib.connect_material_expressions(scalar_param(m, "Wind", 0.0, -900, 490), "", wind, "Amount")
lib.connect_material_property(wind, "", unreal.MaterialProperty.MP_WORLD_POSITION_OFFSET)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW foliage material saved")


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


# Decals (leaks, oil stains): ambientCG CC0 leak textures, colour multiplied by the opacity mask
m = fresh("M_FWDecal")
m.set_editor_property("material_domain", unreal.MaterialDomain.MD_DEFERRED_DECAL)
m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
color = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -600, -200)
color.set_editor_property("parameter_name", "Color")
color.set_editor_property("texture", unreal.load_asset("/Engine/EngineResources/DefaultTexture.DefaultTexture"))
mask = lib.create_material_expression(m, unreal.MaterialExpressionTextureSampleParameter2D, -600, 100)
mask.set_editor_property("parameter_name", "Opacity")
mask.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_GRAYSCALE)
mask.set_editor_property("texture", unreal.load_asset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"))
tint = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -300, -200)
lib.connect_material_expressions(color, "RGB", tint, "A")
lib.connect_material_expressions(vector_param(m, "Tint", (0.35, 0.3, 0.25, 1), -600, -400), "", tint, "B")
lib.connect_material_property(tint, "", unreal.MaterialProperty.MP_BASE_COLOR)
strength = lib.create_material_expression(m, unreal.MaterialExpressionMultiply, -300, 100)
lib.connect_material_expressions(mask, "R", strength, "A")
lib.connect_material_expressions(scalar_param(m, "Strength", 0.8, -600, 300), "", strength, "B")
lib.connect_material_property(strength, "", unreal.MaterialProperty.MP_OPACITY)
lib.connect_material_property(scalar_param(m, "Roughness", 0.35, -300, 300), "", unreal.MaterialProperty.MP_ROUGHNESS)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW decal material saved")


# The character creator's preview: the UE5 mannequin painted as the picked character (skin, hair, eyes, face,
# facial hair, shirt, trousers, shoes) by body region in the bind pose (Tools/character_material.hlsl)
def bind_pose_position(material, x, y):
    """The bind-pose position (cm), carried from the vertices to the pixels."""
    pre = lib.create_material_expression(material, unreal.MaterialExpressionPreSkinnedPosition, x - 200, y)
    carry = lib.create_material_expression(material, unreal.MaterialExpressionVertexInterpolator, x, y)
    lib.connect_material_expressions(pre, "", carry, "")
    return carry


def arm_mask(material, position):
    """1 on the arms (the mannequin's bind pose, as in character_material.hlsl), 0 elsewhere: the first-person
    arms (\"...Arms\" materials) cut everything else away, so looking down shows no chest."""
    mask = lib.create_material_expression(material, unreal.MaterialExpressionCustom, -400, 400)
    mask.set_editor_property("code", "float ax = abs(P.x); return (P.z > 80.0 && ax >= 18.0 + max(0.0, 140.0 - P.z) * 0.12) ? 1.0 : 0.0;")
    mask.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT1)
    item = unreal.CustomInput()
    item.set_editor_property("input_name", "P")
    mask.set_editor_property("inputs", [item])
    lib.connect_material_expressions(position, "", mask, "P")
    material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
    lib.connect_material_property(mask, "", unreal.MaterialProperty.MP_OPACITY_MASK)


for character_name, arms_only in (("M_FWCharacter", False), ("M_FWCharacterArms", True)):
    m = fresh(character_name)
    m.set_editor_property("used_with_skeletal_mesh", True)
    hlsl = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "character_material.hlsl"), encoding="utf-8").read()
    custom = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
    custom.set_editor_property("code", "\n".join(line for line in hlsl.splitlines() if not line.startswith("//")))
    custom.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    colors = (("Skin", (0.87, 0.65, 0.53)), ("Hair", (0.1, 0.07, 0.05)), ("Eyes", (0.2, 0.12, 0.06)),
              ("Shirt", (0.8, 0.78, 0.72)), ("Pants", (0.15, 0.15, 0.17)), ("Shoes", (0.3, 0.2, 0.12)))
    styles = ("HairStyle", "ShirtStyle", "FacialHair", "FaceStyle")
    inputs = []
    for name in ["P"] + [c[0] for c in colors] + list(styles):
        item = unreal.CustomInput()
        item.set_editor_property("input_name", name)
        inputs.append(item)
    custom.set_editor_property("inputs", inputs)
    rough_out = unreal.CustomOutput()
    rough_out.set_editor_property("output_name", "RoughOut")
    rough_out.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT1)
    custom.set_editor_property("additional_outputs", [rough_out])
    # The bind-pose position exists only per vertex: carried to the pixels by an interpolator
    pre_skinned = lib.create_material_expression(m, unreal.MaterialExpressionPreSkinnedPosition, -1100, -600)
    interpolator = lib.create_material_expression(m, unreal.MaterialExpressionVertexInterpolator, -900, -600)
    lib.connect_material_expressions(pre_skinned, "", interpolator, "")
    lib.connect_material_expressions(interpolator, "", custom, "P")
    for i, (name, default) in enumerate(colors):
        lib.connect_material_expressions(vector_param(m, name, (*default, 1.0), -900, -500 + i * 90), "", custom, name)
    for i, name in enumerate(styles):
        lib.connect_material_expressions(scalar_param(m, name, 1.0, -900, 100 + i * 80), "", custom, name)
    lib.connect_material_property(custom, "", unreal.MaterialProperty.MP_BASE_COLOR)
    lib.connect_material_property(custom, "RoughOut", unreal.MaterialProperty.MP_ROUGHNESS)
    if arms_only:
        arm_mask(m, interpolator)
    else:
        # Under a MetaHuman head (Source/FactoryWorld3D/Game/FWGear.cpp): the painted head and neck cut away
        cut = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 400)
        cut.set_editor_property("code", "float ax = abs(P.x); return (Hide > 0.5 && P.z > Height && ax < 12.0) ? 0.0 : 1.0;")
        cut.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT1)
        cut_inputs = []
        for name in ("P", "Hide", "Height"):
            item = unreal.CustomInput()
            item.set_editor_property("input_name", name)
            cut_inputs.append(item)
        cut.set_editor_property("inputs", cut_inputs)
        lib.connect_material_expressions(interpolator, "", cut, "P")
        lib.connect_material_expressions(scalar_param(m, "HideHead", 0.0, -900, 450), "", cut, "Hide")
        lib.connect_material_expressions(scalar_param(m, "HeadCut", 156.0, -900, 530), "", cut, "Height")
        m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
        lib.connect_material_property(cut, "", unreal.MaterialProperty.MP_OPACITY_MASK)
    lib.recompile_material(m)
    unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW character material saved")


# Clothes made in Blender (Tools/blender/make_character_gear.py): a CC0 fabric (Diffuse, Normal, ARM) seen
# from three sides in the bind pose (no seams from the mannequin's UVs), its own colour taken out and the
# server's colour put in (Color); Scale = cm per texture repeat

for cloth_name, arms_only in (("M_FWCloth", False), ("M_FWClothArms", True)):
    m = fresh(cloth_name)
    m.set_editor_property("used_with_skeletal_mesh", True)
    cloth = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
    cloth.set_editor_property("code", "float3 n = abs(normalize(N)); n = pow(n, 4.0); n /= (n.x + n.y + n.z); "
                                     "float2 ux = P.yz / Scale, uy = P.xz / Scale, uz = P.xy / Scale; "
                                     "float3 d = Texture2DSample(D, DSampler, ux).rgb * n.x + Texture2DSample(D, DSampler, uy).rgb * n.y + Texture2DSample(D, DSampler, uz).rgb * n.z; "
                                     "float3 a = Texture2DSample(A, ASampler, ux).rgb * n.x + Texture2DSample(A, ASampler, uy).rgb * n.y + Texture2DSample(A, ASampler, uz).rgb * n.z; "
                                     "float3 t = Texture2DSample(T, TSampler, ux).rgb * n.x + Texture2DSample(T, TSampler, uy).rgb * n.y + Texture2DSample(T, TSampler, uz).rgb * n.z; "
                                     "NormalOut = float3((t.xy * 2 - 1) * Bump, 1); RoughOut = a.g; "
                                     "float l = dot(d, float3(0.3, 0.59, 0.11)); return Color * lerp(0.55, 1.25, saturate(l * 2.2)) * a.r;")
    cloth.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    inputs = []
    for name in ("P", "N", "Color", "Scale", "Bump", "D", "A", "T"):
        item = unreal.CustomInput()
        item.set_editor_property("input_name", name)
        inputs.append(item)
    cloth.set_editor_property("inputs", inputs)
    outs = []
    for name, kind in (("NormalOut", unreal.CustomMaterialOutputType.CMOT_FLOAT3), ("RoughOut", unreal.CustomMaterialOutputType.CMOT_FLOAT1)):
        item = unreal.CustomOutput()
        item.set_editor_property("output_name", name)
        item.set_editor_property("output_type", kind)
        outs.append(item)
    cloth.set_editor_property("additional_outputs", outs)




    cloth_position = bind_pose_position(m, -900, -700)
    lib.connect_material_expressions(cloth_position, "", cloth, "P")
    pre_normal = lib.create_material_expression(m, unreal.MaterialExpressionPreSkinnedNormal, -1100, -620)
    carry_normal = lib.create_material_expression(m, unreal.MaterialExpressionVertexInterpolator, -900, -620)
    lib.connect_material_expressions(pre_normal, "", carry_normal, "")
    lib.connect_material_expressions(carry_normal, "", cloth, "N")
    lib.connect_material_expressions(vector_param(m, "Color", (0.5, 0.5, 0.5, 1), -900, -540), "", cloth, "Color")
    lib.connect_material_expressions(scalar_param(m, "Scale", 18.0, -900, -460), "", cloth, "Scale")
    lib.connect_material_expressions(scalar_param(m, "Bump", 1.0, -900, -380), "", cloth, "Bump")
    for i, (name, default, sampler) in enumerate((
            ("D", "/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture", unreal.MaterialSamplerType.SAMPLERTYPE_COLOR),
            ("A", "/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture", unreal.MaterialSamplerType.SAMPLERTYPE_MASKS),
            ("T", "/Engine/EngineMaterials/DefaultNormal.DefaultNormal", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL))):
        node = lib.create_material_expression(m, unreal.MaterialExpressionTextureObjectParameter, -900, -280 + i * 100)
        node.set_editor_property("parameter_name", {"D": "Diffuse", "A": "ARM", "T": "NormalMap"}[name])
        node.set_editor_property("texture", unreal.load_asset(default))
        node.set_editor_property("sampler_type", sampler)
        lib.connect_material_expressions(node, "", cloth, name)
    lib.connect_material_property(cloth, "", unreal.MaterialProperty.MP_BASE_COLOR)
    lib.connect_material_property(cloth, "NormalOut", unreal.MaterialProperty.MP_NORMAL)
    lib.connect_material_property(cloth, "RoughOut", unreal.MaterialProperty.MP_ROUGHNESS)
    if arms_only:
        arm_mask(m, cloth_position)
    lib.recompile_material(m)
    unreal.EditorAssetLibrary.save_asset(m.get_path_name())

# Hair and facial hair: the server's colour broken into fine strands that run down from the crown (around the
# head and along its height), with a darker root and a moving sheen; strand normals tilt across the strands
m = fresh("M_FWHair")
m.set_editor_property("used_with_skeletal_mesh", True)
hair = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
hair.set_editor_property("code", "float3 h = P - float3(0, 2, 170); float a = atan2(h.y, h.x); "
                                 "float s = frac(sin(floor(a * 140.0 + sin(P.z * 0.35) * 2.0) * 12.9898) * 43758.5453); "
                                 "float s2 = frac(sin(floor(a * 420.0 + P.z * 0.2) * 78.233) * 24634.6345); "
                                 "float strand = lerp(0.6, 1.15, s) * lerp(0.85, 1.05, s2); "
                                 "NormalOut = normalize(float3(cos(a * 140.0) * 0.35, 0, 1)); RoughOut = lerp(0.32, 0.5, s2); "
                                 "return Color * strand;")
hair.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
inputs = []
for name in ("P", "Color"):
    item = unreal.CustomInput()
    item.set_editor_property("input_name", name)
    inputs.append(item)
hair.set_editor_property("inputs", inputs)
outs = []
for name, kind in (("NormalOut", unreal.CustomMaterialOutputType.CMOT_FLOAT3), ("RoughOut", unreal.CustomMaterialOutputType.CMOT_FLOAT1)):
    item = unreal.CustomOutput()
    item.set_editor_property("output_name", name)
    item.set_editor_property("output_type", kind)
    outs.append(item)
hair.set_editor_property("additional_outputs", outs)
lib.connect_material_expressions(bind_pose_position(m, -900, -400), "", hair, "P")
lib.connect_material_expressions(vector_param(m, "Color", (0.1, 0.07, 0.05, 1), -900, -300), "", hair, "Color")
lib.connect_material_property(hair, "", unreal.MaterialProperty.MP_BASE_COLOR)
lib.connect_material_property(hair, "NormalOut", unreal.MaterialProperty.MP_NORMAL)
lib.connect_material_property(hair, "RoughOut", unreal.MaterialProperty.MP_ROUGHNESS)
lib.connect_material_property(scalar_param(m, "Specular", 0.6, -900, -200), "", unreal.MaterialProperty.MP_SPECULAR)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
unreal.log("FW character gear materials saved")

# Hair cards (make_character_gear.py): each card cut into strands by its UVs (U across, offset by a whole number
# per card; V root -> tip), strands of staggered length tapering to the tip, darker at the root, anisotropic
# sheen along the strands (dithered alpha: TSR softens the strand edges)
m = fresh("M_FWHairCard")
m.set_editor_property("used_with_skeletal_mesh", True)
m.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
m.set_editor_property("two_sided", True)
m.set_editor_property("dither_opacity_mask", True)
card = lib.create_material_expression(m, unreal.MaterialExpressionCustom, -400, 0)
card.set_editor_property("code", "float card = floor(UV.x); float u = frac(UV.x); float v = saturate(UV.y); "
                                 "float n = 14.0; float i = floor(u * n) + card * 31.0; "
                                 "float h = frac(sin(i * 12.9898) * 43758.5453); float h2 = frac(sin(i * 78.233) * 24634.6345); "
                                 "float w = abs(frac(u * n) - 0.5) * 2.0; "
                                 "float reach = 0.7 + 0.3 * h; float taper = 1.0 - smoothstep(reach - 0.25, reach, v); "
                                 "OpacityOut = step(w, lerp(0.75, 0.25, v)) * taper * (h2 > 0.12 ? 1.0 : 0.0); "
                                 "return Color * lerp(0.45, 1.1, pow(v, 0.7)) * lerp(0.8, 1.15, h);")
card.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
inputs = []
for name in ("UV", "Color"):
    item = unreal.CustomInput()
    item.set_editor_property("input_name", name)
    inputs.append(item)
card.set_editor_property("inputs", inputs)
outs = []
for name in ("OpacityOut",):
    item = unreal.CustomOutput()
    item.set_editor_property("output_name", name)
    item.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT1)
    outs.append(item)
card.set_editor_property("additional_outputs", outs)
lib.connect_material_expressions(lib.create_material_expression(m, unreal.MaterialExpressionTextureCoordinate, -900, -300), "", card, "UV")
lib.connect_material_expressions(vector_param(m, "Color", (0.1, 0.07, 0.05, 1), -900, -200), "", card, "Color")
lib.connect_material_property(card, "", unreal.MaterialProperty.MP_BASE_COLOR)
lib.connect_material_property(card, "OpacityOut", unreal.MaterialProperty.MP_OPACITY_MASK)
lib.connect_material_property(scalar_param(m, "Roughness", 0.38, -900, -100), "", unreal.MaterialProperty.MP_ROUGHNESS)
lib.connect_material_property(scalar_param(m, "Specular", 0.55, -900, -20), "", unreal.MaterialProperty.MP_SPECULAR)
lib.connect_material_property(scalar_param(m, "Anisotropy", 0.75, -900, 60), "", unreal.MaterialProperty.MP_ANISOTROPY)
m.set_editor_property("opacity_mask_clip_value", 0.4)
lib.recompile_material(m)
unreal.EditorAssetLibrary.save_asset(m.get_path_name())
