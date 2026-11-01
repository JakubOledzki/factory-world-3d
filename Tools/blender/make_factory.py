# Builds the factory models in Blender and writes one .glb per model to RawAssets/processed/factory.
# Real-world sizes in metres for a 4 m tile (TileSizeCm=400); +X is the way an object faces, the
# origin is the middle of its tile at ground level. Material slots are named after the texture sets
# Tools/import_external.py binds to them:
#   FW_PaintedSteel (blue_metal_plate)   FW_Steel (metal_plate)        FW_Worn (rusty_painted_metal)
#   FW_Rubber (rubberized_track; UV U runs along the belt for the moving band)
#   FW_Wood (weathered_planks)           FW_Concrete (concrete_floor_worn_001)  FW_Grate (metal_grate_rusty)
#   FW_Glass (solar cells: dark glass, no texture)
# Moving parts are separate files: inserter_arm (turns about Z at its origin), turbine_rotor (spins about X).
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_factory.py
import bmesh
import bpy
import math
import os

from mathutils import Matrix, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets", "processed", "factory")
UV_METRES = 2.0  # one texture repeat


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def finish(obj, mat, bevel=0.0, segments=4, uv=True, subdivide=0):
    """Bevel the edges, smooth-shade, box-map UVs at UV_METRES and assign the material."""
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    if bevel > 0:
        mod = obj.modifiers.new("Bevel", "BEVEL")
        mod.width = bevel
        mod.segments = segments
        mod.limit_method = "ANGLE"
        mod.harden_normals = True
        bpy.ops.object.modifier_apply(modifier=mod.name)
    if subdivide:
        mod = obj.modifiers.new("Subdivision", "SUBSURF")
        mod.levels = subdivide
        mod.render_levels = subdivide
        bpy.ops.object.modifier_apply(modifier=mod.name)
    # Normals weighted by face area keep big flat faces flat next to rounded bevels
    mod = obj.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL")
    mod.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=mod.name)
    if uv:
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.cube_project(cube_size=UV_METRES, correct_aspect=False, scale_to_bounds=False)
        bpy.ops.object.mode_set(mode="OBJECT")
    obj.data.materials.clear()
    obj.data.materials.append(material(mat))
    for poly in obj.data.polygons:
        poly.use_smooth = True
    obj.select_set(False)
    return obj


def box(size, location, mat, bevel=0.01, rotation=(0, 0, 0), segments=4):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location, rotation=rotation)
    obj = bpy.context.active_object
    obj.scale = size
    return finish(obj, mat, bevel, segments)


def cylinder(radius, depth, location, mat, rotation=(0, 0, 0), vertices=48, bevel=0.005, top_radius=None):
    if top_radius is None:
        bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    else:
        bpy.ops.mesh.primitive_cone_add(radius1=radius, radius2=top_radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    return finish(bpy.context.active_object, mat, bevel)


def bolts(points, mat="FW_Steel", radius=0.018, axis=(0, 0, 1)):
    """Small hex bolt heads: the kind of detail that sells scale up close."""
    rot = Vector((0, 0, 1)).rotation_difference(Vector(axis)).to_euler()
    normal = Vector(axis).normalized()
    for p in points:
        cylinder(radius * 1.5, 0.004, Vector(p) - normal * 0.004, mat, rotation=rot, vertices=16, bevel=0.0)  # washer
        cylinder(radius, 0.012, p, mat, rotation=rot, vertices=6, bevel=0.002)
        cylinder(radius * 0.55, 0.01, Vector(p) + normal * 0.01, mat, rotation=rot, vertices=12, bevel=0.0)  # thread end


def tube(points, radius, mat, resolution=12):
    """A cable or hose through the points (bezier, smoothed)."""
    curve = bpy.data.curves.new("tube", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = radius
    curve.bevel_resolution = 4
    curve.resolution_u = resolution
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(points) - 1)
    for bp, p in zip(spline.bezier_points, points):
        bp.co = p
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    obj = bpy.data.objects.new("tube", curve)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.convert(target="MESH")
    return finish(bpy.context.active_object, mat)


def export(name):
    """Join everything in the scene into one object called `name` and write it."""
    objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = name
    obj.data.name = name
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f"{name}.glb"), export_format="GLB", use_selection=False)
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    print(f"FWFACTORY {name}: {tris} triangles")


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def band_uv(obj, length_axis=0, length=4.0):
    """UV for the belt's rubber band: U along its length (metres / 2), V across, so a panner moves it."""
    me = obj.data
    uv = me.uv_layers.active or me.uv_layers.new()
    for loop in me.loops:
        co = me.vertices[loop.vertex_index].co
        uv.data[loop.index].uv = (co[length_axis] / UV_METRES, co[1 - length_axis] / UV_METRES + 0.5)


# ---- Conveyor belt, straight: 4 m long, 2 m wide, band top 0.44 m ---------------------------------
# Half the width of the band: belts are ~1.6 m wide with their frame, centred in the 4 m tile
HW = 0.65


def belt_frame(length=4.0):
    for side in (-1, 1):
        box((length, 0.12, 0.32), (0, side * (HW + 0.1), 0.28), "FW_PaintedSteel", bevel=0.012)
        box((length, 0.04, 0.06), (0, side * (HW + 0.16), 0.42), "FW_Steel", bevel=0.008)
        for x in (-length / 2 + 0.25, length / 2 - 0.25):
            box((0.12, 0.12, 0.14), (x, side * (HW + 0.1), 0.07), "FW_Worn", bevel=0.01)
            box((0.32, 0.26, 0.02), (x, side * (HW + 0.1), 0.01), "FW_Steel", bevel=0.004)
            bolts([(x + dx, side * (HW + 0.1) + dy, 0.025) for dx in (-0.11, 0.11) for dy in (-0.08, 0.08)])
        bolts([(x, side * (HW + 0.16), 0.3) for x in [i * 0.5 - length / 2 + 0.25 for i in range(int(length / 0.5))]], axis=(0, side, 0))
    # Carrying rollers under the band, with axle ends through the frame; return rollers below
    for i in range(int(length / 0.25)):
        x = -length / 2 + 0.125 + i * 0.25
        cylinder(0.07, 2 * HW, (x, 0, 0.33), "FW_Steel", rotation=(math.pi / 2, 0, 0))
        for side in (-1, 1):
            cylinder(0.025, 0.2, (x, side * (HW + 0.08), 0.33), "FW_Worn", rotation=(math.pi / 2, 0, 0), vertices=16)
            bolts([(x, side * (HW + 0.175), 0.33)], axis=(0, side, 0), radius=0.022)
    for i in range(int(length)):
        x = -length / 2 + 0.5 + i
        cylinder(0.05, 2 * HW, (x, 0, 0.12), "FW_Steel", rotation=(math.pi / 2, 0, 0))
    # Guard lips along the top of the frame and diagonal braces between the legs
    for side in (-1, 1):
        box((length, 0.02, 0.1), (0, side * (HW + 0.04), 0.48), "FW_PaintedSteel", bevel=0.006)
        for x in (-length / 2 + 0.25, length / 2 - 0.25):
            box((0.04, 0.24, 0.3), (x, side * (HW - 0.02), 0.2), "FW_PaintedSteel", bevel=0.006)  # gusset
    for x0, x1 in ((-length / 2 + 0.25, 0.0), (0.0, length / 2 - 0.25)):
        mid = (x0 + x1) / 2
        span = math.hypot(x1 - x0, 2 * HW)
        box((0.05, span, 0.05), (mid, 0, 0.16), "FW_Steel", bevel=0.008, rotation=(0, 0, math.atan2(x1 - x0, 2 * HW)))
    # Cross braces
    for x in (-length / 2 + 0.25, 0.0, length / 2 - 0.25):
        box((0.08, 2 * HW + 0.1, 0.08), (x, 0, 0.16), "FW_PaintedSteel", bevel=0.01)


def belt_straight():
    reset()
    belt_frame()
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0.42))
    band = bpy.context.active_object
    band.scale = (4.0, 2 * HW, 0.04)
    finish(band, "FW_Rubber", bevel=0.008, uv=False)
    band_uv(band)
    export("belt_straight")


def belt_curve():
    """Turns left: comes in from the back (-X) and leaves to the left (+Y), a quarter circle about (-2, 2)."""
    reset()
    corner = Vector((-2.0, 2.0, 0.0))
    steps = 12
    def arc_point(r, a, z):
        return corner + Vector((math.sin(a) * r, -math.cos(a) * r, z))
    # Band: a ring segment between radii 2 - HW and 2 + HW
    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    rings = []
    for i in range(steps + 1):
        a = (math.pi / 2) * i / steps
        rings.append([bm.verts.new(arc_point(r, a, z)) for r, z in ((2 - HW, 0.40), (2 + HW, 0.40), (2 + HW, 0.44), (2 - HW, 0.44))])
    for i in range(steps):
        for k in range(4):
            face = bm.faces.new((rings[i][k], rings[i + 1][k], rings[i + 1][(k + 1) % 4], rings[i][(k + 1) % 4]))
            for loop in face.loops:
                idx = [j for j in range(steps + 1) if loop.vert in rings[j]][0]
                along = (math.pi / 2) * idx / steps * 2.0
                across = (loop.vert.co - corner).length
                loop[uv_layer].uv = (along / UV_METRES, across / UV_METRES)
    bm.normal_update()
    me = bpy.data.meshes.new("band")
    bm.to_mesh(me)
    bm.free()
    band = bpy.data.objects.new("band", me)
    bpy.context.scene.collection.objects.link(band)
    finish(band, "FW_Rubber", uv=False)
    # Side walls along both edges
    for r in (2 - HW - 0.08, 2 + HW + 0.08):
        for i in range(steps):
            a0, a1 = (math.pi / 2) * i / steps, (math.pi / 2) * (i + 1) / steps
            mid = (a0 + a1) / 2
            p = arc_point(r, mid, 0.28)
            seg = 2 * r * math.sin((a1 - a0) / 2) + 0.02
            box((seg, 0.12, 0.32), p, "FW_PaintedSteel", bevel=0.01, rotation=(0, 0, mid))
    for a in (0.2, 0.75, 1.3):
        p = arc_point(2.0, a, 0.16)
        box((0.08, 2 * HW + 0.2, 0.08), p, "FW_PaintedSteel", bevel=0.01, rotation=(0, 0, a))
    export("belt_curve")


# ---- Inserter: base and a separate arm that turns about Z ------------------------------------------
def inserter():
    reset()
    cylinder(0.55, 0.12, (0, 0, 0.06), "FW_Concrete", vertices=32, bevel=0.02)
    cylinder(0.42, 0.1, (0, 0, 0.17), "FW_PaintedSteel", vertices=32, bevel=0.01)
    bolts([(math.cos(a) * 0.36, math.sin(a) * 0.36, 0.225) for a in [i * math.pi / 4 for i in range(8)]])
    cylinder(0.22, 0.6, (0, 0, 0.52), "FW_Worn", vertices=24, bevel=0.01)
    box((0.3, 0.2, 0.25), (-0.2, 0.0, 0.45), "FW_Steel", bevel=0.02)  # motor housing
    for z in (0.24, 0.78):
        cylinder(0.27, 0.05, (0, 0, z), "FW_Steel")  # bearing rings
    bolts([(math.cos(a) * 0.25, math.sin(a) * 0.25, 0.81) for a in [i * math.pi / 6 for i in range(12)]], radius=0.012)
    for k in range(6):
        box((0.04, 0.3, 0.02), (-0.2, 0.0, 0.33 + k * 0.045), "FW_Steel", bevel=0.004)  # cooling fins
    tube([(-0.2, 0.12, 0.35), (-0.05, 0.3, 0.2), (0.25, 0.32, 0.12), (0.5, 0.2, 0.1)], 0.018, "FW_Rubber")
    cylinder(0.05, 0.4, (-0.2, 0.18, 0.45), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=12)
    export("inserter_base")

    reset()
    # Arm pivots at the origin (top of the column, z = 0.82 in the base's frame), reaches +X
    cylinder(0.24, 0.16, (0, 0, 0.0), "FW_PaintedSteel", vertices=24, bevel=0.01)
    box((1.25, 0.16, 0.14), (0.62, 0, 0.12), "FW_PaintedSteel", bevel=0.02, rotation=(0, -0.18, 0))
    cylinder(0.09, 0.22, (1.22, 0, 0.23), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=16)
    box((0.7, 0.12, 0.1), (1.5, 0, 0.0), "FW_PaintedSteel", bevel=0.015, rotation=(0, 0.55, 0))
    box((0.18, 0.3, 0.12), (1.78, 0, -0.22), "FW_Worn", bevel=0.01)
    for side in (-1, 1):
        box((0.05, 0.04, 0.22), (1.78, side * 0.12, -0.38), "FW_Steel", bevel=0.005)
    # Hydraulic piston from the turret to the elbow, bearings at the joints, a cable loom along the arm
    cylinder(0.06, 0.7, (0.4, 0.13, 0.32), "FW_PaintedSteel", rotation=(0, math.pi / 2 - 0.35, 0))
    cylinder(0.03, 0.6, (0.9, 0.13, 0.3), "FW_Steel", rotation=(0, math.pi / 2 - 0.35, 0))
    for p in ((0.0, 0.0, 0.1), (1.22, 0.0, 0.23)):
        cylinder(0.11, 0.26, p, "FW_Steel", rotation=(math.pi / 2, 0, 0))
        bolts([(p[0], k * 0.135, p[2]) for k in (-1, 1)], axis=(0, 1, 0), radius=0.03)
    tube([(0.05, -0.12, 0.2), (0.6, -0.12, 0.34), (1.2, -0.14, 0.32), (1.6, -0.1, 0.0), (1.75, -0.08, -0.2)], 0.022, "FW_Rubber")
    for side in (-1, 1):
        box((0.06, 0.03, 0.12), (1.78, side * 0.16, -0.3), "FW_Worn", bevel=0.005)  # gripper pads
    export("inserter_arm")


# ---- Wooden chest with steel corners ----------------------------------------------------------------
def chest():
    reset()
    w, d, h = 1.7, 1.25, 0.95
    for i in range(5):
        z = 0.1 + i * 0.18
        box((w, d, 0.16), (0, 0, z), "FW_Wood", bevel=0.012)
    box((w + 0.04, d + 0.04, 0.12), (0, 0, h + 0.03), "FW_Wood", bevel=0.02)
    for x in (-w / 2, w / 2):
        for y in (-d / 2, d / 2):
            box((0.1, 0.1, h + 0.1), (x, y, h / 2 + 0.03), "FW_Worn", bevel=0.01)
            bolts([(x, y + (0.06 if y > 0 else -0.06), z) for z in (0.25, 0.55, 0.85)], axis=(0, 1 if y > 0 else -1, 0))
    for y in (-d / 2 - 0.01, d / 2 + 0.01):
        box((w, 0.03, 0.08), (0, y, 0.55), "FW_Worn", bevel=0.005)
    box((0.22, 0.05, 0.16), (w / 2 + 0.02, 0, 0.8), "FW_Steel", bevel=0.01, rotation=(0, 0, math.pi / 2))
    for y in (-0.4, 0.4):
        cylinder(0.035, 0.22, (-w / 2 - 0.03, y, h + 0.0), "FW_Worn", rotation=(math.pi / 2, 0, 0))  # hinge barrel
        box((0.2, 0.18, 0.012), (-w / 2 + 0.06, y, h + 0.095), "FW_Worn", bevel=0.004)
        bolts([(-w / 2 + 0.06 + dx, y + dy, h + 0.103) for dx in (-0.06, 0.06) for dy in (-0.05, 0.05)], radius=0.01)
    for x in (-w / 2 + 0.25, w / 2 - 0.25):
        for y in (-d / 2 - 0.02, d / 2 + 0.02):
            box((0.26, 0.012, 0.26), (x, y, 0.2), "FW_Worn", bevel=0.004)  # corner brackets
            bolts([(x + dx, y + (0.01 if y > 0 else -0.01), 0.2 + dz) for dx in (-0.08, 0.08) for dz in (-0.08, 0.08)], axis=(0, 1 if y > 0 else -1, 0), radius=0.01)
    export("chest")


# ---- Power pole: 8 m wooden pole, crossarm, insulators ----------------------------------------------
def pole():
    reset()
    cylinder(0.16, 8.0, (0, 0, 4.0), "FW_Wood", vertices=16, top_radius=0.12)
    box((0.16, 2.2, 0.16), (0, 0, 7.5), "FW_Wood", bevel=0.01)
    for y in (-0.95, 0.0, 0.95):
        z = 7.62 if y != 0.0 else 7.9
        for k in range(6):
            cylinder(0.075 - k * 0.005, 0.04, (0, y, z + k * 0.05), "FW_Glass", bevel=0.008)
        tube([(0, y, z + 0.32), (0.6, y, z + 0.1), (1.6, y, z - 0.25)], 0.012, "FW_Steel")  # line stub
        cylinder(0.015, 0.3, (0, y, z + 0.1), "FW_Steel", vertices=8)
    box((0.04, 1.2, 0.04), (0.0, 0.5, 7.05), "FW_Steel", rotation=(0.9, 0, 0))  # brace
    box((0.04, 1.2, 0.04), (0.0, -0.5, 7.05), "FW_Steel", rotation=(-0.9, 0, 0))
    box((0.5, 0.5, 0.04), (0, 0, 0.02), "FW_Concrete", bevel=0.02)
    export("pole")


# ---- Solar panel: 3 x 2 m tilted panel on a steel stand ----------------------------------------------
def solar():
    reset()
    tilt = math.radians(28)
    centre = Vector((0, 0, 1.25))
    rot = Matrix.Rotation(tilt, 4, "Y")
    def put(local):
        return centre + (rot @ Vector(local).to_4d()).to_3d()
    box((2.0, 3.0, 0.06), put((0, 0, 0)), "FW_Steel", bevel=0.01, rotation=(0, tilt, 0))
    for i in range(6):
        for j in range(10):
            box((0.3, 0.28, 0.012), put((-0.8 + i * 0.32, -1.35 + j * 0.3, 0.04)), "FW_Glass", bevel=0.002, rotation=(0, tilt, 0))
    for y in (-1.5, 1.5):
        box((2.04, 0.04, 0.09), put((0, y, 0.03)), "FW_Steel", bevel=0.006, rotation=(0, tilt, 0))
    for x in (-1.0, 1.0):
        box((0.04, 3.04, 0.09), put((x, 0, 0.03)), "FW_Steel", bevel=0.006, rotation=(0, tilt, 0))
    tube([(0.0, 0.0, 0.55), (0.1, 0.4, 0.9), tuple(put((0.9, 0.6, -0.05)))], 0.012, "FW_Rubber")
    for y in (-1.1, 1.1):
        box((0.1, 0.1, 1.3), (0.35, y, 0.65), "FW_PaintedSteel", bevel=0.01)
        box((0.1, 0.1, 0.8), (-0.35, y, 0.4), "FW_PaintedSteel", bevel=0.01)
        box((1.0, 0.12, 0.06), (0, y, 0.05), "FW_Concrete", bevel=0.01)
    box((0.3, 0.2, 0.4), (0.0, 0, 0.35), "FW_Worn", bevel=0.02)  # inverter box
    export("solar_panel")


# ---- Wind turbine: tower and nacelle; rotor separate (spins about X at its origin) -----------------
def turbine():
    reset()
    cylinder(0.55, 14.0, (0, 0, 7.0), "FW_PaintedSteel", vertices=32, top_radius=0.32, bevel=0.0)
    for z in (4.6, 9.3):
        cylinder(0.5 - z * 0.016, 0.06, (0, 0, z), "FW_Steel", vertices=32)
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0.3, 0, 14.4))
    nacelle = bpy.context.active_object
    nacelle.scale = (2.6, 1.1, 1.1)
    finish(nacelle, "FW_PaintedSteel", bevel=0.15, segments=3, subdivide=2)
    for k in range(5):
        box((0.04, 0.9, 0.3), (-0.4 + k * 0.25, 0.56, 14.35), "FW_Grate", bevel=0.005)  # vents
    box((0.6, 0.4, 0.1), (-0.5, 0, 15.0), "FW_Steel", bevel=0.01)  # sensors
    cylinder(0.03, 0.6, (-0.7, 0, 15.3), "FW_Steel")
    cylinder(0.65, 0.4, (0, 0, 13.8), "FW_Steel")  # yaw bearing
    cylinder(1.6, 0.4, (0, 0, 0.2), "FW_Concrete", vertices=32, bevel=0.03)
    export("turbine_tower")

    reset()
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, location=(0.2, 0, 0), segments=24, ring_count=12)
    hub = bpy.context.active_object
    hub.scale = (1.4, 1.0, 1.0)
    finish(hub, "FW_PaintedSteel", subdivide=1)
    for k in range(3):
        a = k * 2 * math.pi / 3
        bpy.ops.mesh.primitive_cube_add(size=1)
        blade = bpy.context.active_object
        blade.scale = (0.12, 0.55, 5.2)
        bpy.ops.object.transform_apply(scale=True)
        # Cut along the span so the twist and taper bend smoothly
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.subdivide(number_cuts=12)
        bpy.ops.object.mode_set(mode="OBJECT")
        # Twist and taper the blade a little
        bm = bmesh.new()
        bm.from_mesh(blade.data)
        for v in bm.verts:
            t = (v.co.z + 0.5)
            v.co.y *= 1.0 - 0.6 * t
            v.co.x *= 1.0 - 0.5 * t
        bm.to_mesh(blade.data)
        bm.free()
        blade.location = (0.1, 0, 0)
        blade.rotation_euler = (a, 0.12, 0)
        blade.location = Vector((0.1, 0, 0)) + Matrix.Rotation(a, 3, "X") @ Vector((0, 0, 2.95))
        finish(blade, "FW_PaintedSteel", bevel=0.0, subdivide=2)
    export("turbine_rotor")


# ---- Rails (one 4 m piece running along X) and the station platform tile ---------------------------
def rails():
    reset()
    for i in range(6):
        box((0.22, 2.6, 0.14), (-1.67 + i * 0.667, 0, 0.07), "FW_Wood", bevel=0.01)
    for y in (-0.72, 0.72):
        box((4.0, 0.07, 0.16), (0, y, 0.22), "FW_Steel", bevel=0.01)
        box((4.0, 0.16, 0.03), (0, y, 0.155), "FW_Steel", bevel=0.005)
    box((4.0, 3.2, 0.1), (0, 0, -0.02), "FW_Concrete", bevel=0.02)  # ballast bed
    export("rails")

    reset()
    box((4.0, 4.0, 0.8), (0, 0, 0.4), "FW_Concrete", bevel=0.03)
    box((4.0, 0.3, 0.05), (0, -1.85, 0.82), "FW_PaintedSteel", bevel=0.005)
    export("platform")


# ---- Generic machine: housing on a concrete pad, scaled to the footprint in the game ---------------
def machine():
    reset()
    box((3.7, 3.7, 0.25), (0, 0, 0.125), "FW_Concrete", bevel=0.03)
    box((3.0, 3.0, 2.4), (0, 0, 1.45), "FW_PaintedSteel", bevel=0.05, segments=3)
    box((3.06, 3.06, 0.12), (0, 0, 2.7), "FW_Worn", bevel=0.02)
    for side in (-1, 1):
        box((0.06, 1.6, 1.4), (side * 1.52, 0, 1.3), "FW_Grate", bevel=0.01)
    cylinder(0.25, 1.6, (0.8, 0.8, 3.4), "FW_Worn", vertices=20)
    cylinder(0.3, 0.1, (0.8, 0.8, 4.2), "FW_Steel", vertices=20)
    for z in (0.7, 1.3):
        cylinder(0.08, 3.0, (-1.6, 0, z), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=12)
    box((0.6, 0.12, 0.8), (1.0, -1.55, 1.0), "FW_Steel", bevel=0.02)  # control panel
    for x in (-1.2, -0.4, 0.4, 1.2):
        box((0.08, 3.08, 0.08), (x, 0, 2.55), "FW_Steel", bevel=0.01)  # roof ribs
        bolts([(x, y, 2.6) for y in (-1.4, -0.7, 0.0, 0.7, 1.4)], radius=0.015)
    for z in (2.6, 4.15):
        cylinder(0.34, 0.06, (0.8, 0.8, z), "FW_Steel")  # flanges
    for side in (-1, 1):
        bolts([(side * 1.51, y, z) for y in (-0.75, 0.75) for z in (0.65, 1.95)], axis=(side, 0, 0), radius=0.02)
    tube([(-1.6, 1.5, 1.3), (-1.0, 1.7, 2.0), (0.8, 1.6, 2.8), (0.8, 0.9, 3.0)], 0.07, "FW_Worn")
    export("machine")


# ---- Workshop furniture and machines: each built at the real size of its footprint (N x 4 m) ---------
# The game places these at scale 1 (FWArt::FurnitureModel); front (+X) is where the player works.
def mesh_object(verts, faces, mat, bevel=0.0, location=(0, 0, 0)):
    """An object from raw vertices and faces (for shapes the primitives do not give)."""
    me = bpy.data.meshes.new("part")
    me.from_pydata([Vector(v) for v in verts], [], faces)
    me.update()
    obj = bpy.data.objects.new("part", me)
    obj.location = location
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    return finish(obj, mat, bevel)


def prism(profile, length, mat, location=(0, 0, 0), bevel=0.0):
    """A profile in the XZ plane (counter-clockwise) pushed along Y: gable roofs, ramps, troughs."""
    n = len(profile)
    verts = [(x, -length / 2, z) for x, z in profile] + [(x, length / 2, z) for x, z in profile]
    faces = [list(range(n))[::-1], list(range(n, 2 * n))]
    faces += [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
    return mesh_object(verts, faces, mat, bevel, location)


def legs(width, depth, height, inset, mat, size=0.08):
    for x in (-width / 2 + inset, width / 2 - inset):
        for y in (-depth / 2 + inset, depth / 2 - inset):
            box((size, size, height), (x, y, height / 2), mat, bevel=0.008)


def hammer(location, yaw=0.0, lying=True):
    x, y, z = location
    rot = (0, math.pi / 2, yaw) if lying else (0, 0, yaw)
    cylinder(0.016, 0.34, (x, y, z), "FW_Wood", rotation=rot, vertices=12, bevel=0.002)
    hx, hy = (x + math.cos(yaw) * 0.17, y + math.sin(yaw) * 0.17) if lying else (x, y)
    hz = z if lying else z + 0.17
    box((0.04, 0.12, 0.04), (hx, hy, hz), "FW_Steel", bevel=0.006, rotation=(0, 0, yaw))


def workbench():
    reset()
    w, d, h = 2.3, 1.0, 0.92
    box((w, d, 0.08), (0, 0, h - 0.04), "FW_TableWood", bevel=0.012)
    legs(w, d, h - 0.08, 0.08, "FW_Wood", 0.1)
    box((w - 0.2, d - 0.2, 0.04), (0, 0, 0.25), "FW_Wood", bevel=0.006)  # lower shelf
    for y in (-d / 2 + 0.08, d / 2 - 0.08):
        box((w - 0.25, 0.05, 0.1), (0, y, h - 0.14), "FW_Wood", bevel=0.006)  # aprons
    # Pegboard with tools on the back edge
    box((w, 0.04, 1.0), (0, d / 2 - 0.02, h + 0.5), "FW_Wood", bevel=0.008)
    for x in (-w / 2 + 0.05, w / 2 - 0.05):
        box((0.06, 0.06, 1.05), (x, d / 2 - 0.02, h + 0.5), "FW_Wood", bevel=0.006)
    box((w, 0.25, 0.03), (0, d / 2 - 0.13, h + 0.95), "FW_Wood", bevel=0.005)  # top shelf
    for i, x in enumerate((-0.85, -0.6, -0.35)):
        box((0.03, 0.02, 0.22 + 0.05 * i), (x, d / 2 - 0.06, h + 0.45), "FW_Steel", bevel=0.004)  # files/chisels
        box((0.035, 0.03, 0.1), (x, d / 2 - 0.06, h + 0.6 + 0.025 * i), "FW_Wood", bevel=0.006)
    for k, x in enumerate((0.0, 0.18, 0.36)):
        box((0.035, 0.012, 0.18 + 0.04 * k), (x, d / 2 - 0.05, h + 0.42), "FW_Steel", bevel=0.003)  # spanners
    box((0.5, 0.01, 0.14), (0.75, d / 2 - 0.05, h + 0.55), "FW_Steel", bevel=0.002)  # saw blade
    box((0.12, 0.03, 0.12), (0.47, d / 2 - 0.05, h + 0.55), "FW_Wood", bevel=0.01)  # its handle
    for i in range(4):
        cylinder(0.05, 0.1, (-0.8 + i * 0.16, d / 2 - 0.13, h + 1.02), "FW_PaintedSteel", vertices=16)  # tins
    # Steel vise on the front left corner
    vx = -w / 2 + 0.3
    box((0.26, 0.2, 0.12), (vx, -d / 2 + 0.12, h + 0.06), "FW_PaintedSteel", bevel=0.015)
    box((0.1, 0.22, 0.14), (vx, -d / 2 + 0.05, h + 0.16), "FW_PaintedSteel", bevel=0.01)
    box((0.1, 0.06, 0.14), (vx, -d / 2 - 0.12, h + 0.16), "FW_PaintedSteel", bevel=0.01)
    cylinder(0.018, 0.4, (vx, -d / 2 - 0.1, h + 0.12), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=12)
    cylinder(0.012, 0.3, (vx, -d / 2 - 0.3, h + 0.12), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=10)
    # Work on the top: a plank being cut, a hammer, nails, a square
    box((0.9, 0.22, 0.04), (0.35, -0.1, h + 0.02), "FW_Wood", bevel=0.004, rotation=(0, 0, 0.12))
    hammer((-0.35, 0.05, h + 0.02), yaw=0.6)
    box((0.3, 0.03, 0.006), (0.9, 0.2, h + 0.003), "FW_Steel", bevel=0.001)
    box((0.03, 0.2, 0.006), (1.04, 0.105, h + 0.003), "FW_Steel", bevel=0.001)
    box((0.18, 0.12, 0.08), (-0.75, 0.2, h + 0.04), "FW_Worn", bevel=0.008)  # nail box
    # Toolbox and offcuts on the shelf
    box((0.5, 0.25, 0.22), (0.6, 0.1, 0.38), "FW_Worn", bevel=0.02)
    for i in range(3):
        box((0.7 - i * 0.12, 0.12, 0.05), (-0.5, -0.1 + i * 0.13, 0.295), "FW_Wood", bevel=0.004)
    export("workbench")


def anvil():
    reset()
    cylinder(0.32, 0.55, (0, 0, 0.275), "FW_Wood", vertices=24, bevel=0.02)  # stump
    for i in range(3):
        cylinder(0.325, 0.03, (0, 0, 0.1 + i * 0.18), "FW_Steel", vertices=24, bevel=0.004)  # hoops
    # Anvil: waist, body, face and horn
    box((0.22, 0.3, 0.12), (0, 0, 0.61), "FW_Steel", bevel=0.02)
    box((0.14, 0.18, 0.14), (0, 0, 0.74), "FW_Steel", bevel=0.02)
    box((0.42, 0.2, 0.13), (-0.03, 0, 0.875), "FW_Steel", bevel=0.015)
    cylinder(0.085, 0.3, (0.32, 0, 0.88), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=24, top_radius=0.012)
    box((0.06, 0.04, 0.02), (-0.18, 0, 0.945), "FW_Steel", bevel=0.002)  # hardy hole rim
    hammer((0.0, 0.06, 0.95), yaw=-0.4)
    # Tongs leaning on the stump and a quench bucket
    for s in (-1, 1):
        box((0.02, 0.012, 0.7), (-0.36, s * 0.02, 0.37), "FW_Steel", bevel=0.002, rotation=(0, -0.25, s * 0.04))
    cylinder(0.2, 0.36, (-0.25, 0.55, 0.18), "FW_Steel", vertices=24, top_radius=0.23)
    cylinder(0.19, 0.01, (-0.25, 0.55, 0.32), "FW_Glass", vertices=24, bevel=0.0)  # water
    export("anvil")


def computer():
    reset()
    w, d, h = 1.6, 0.8, 0.75
    box((w, d, 0.04), (0, 0, h - 0.02), "FW_TableWood", bevel=0.008)
    for x in (-w / 2 + 0.05, w / 2 - 0.05):
        box((0.05, d - 0.1, 0.05), (x, 0, 0.03), "FW_PaintedSteel", bevel=0.006)
        box((0.05, 0.05, h - 0.06), (x, 0, (h - 0.06) / 2 + 0.03), "FW_PaintedSteel", bevel=0.006)
    box((w - 0.1, 0.03, 0.3), (0, d / 2 - 0.05, h - 0.2), "FW_PaintedSteel", bevel=0.004)  # modesty panel
    # Two monitors on stands, keyboard, mouse, tower
    for x, yaw in ((-0.3, 0.12), (0.3, -0.12)):
        box((0.2, 0.15, 0.015), (x, 0.15, h + 0.008), "FW_Steel", bevel=0.004)
        box((0.04, 0.03, 0.3), (x, 0.17, h + 0.16), "FW_Steel", bevel=0.006)
        box((0.03, 0.6, 0.36), (x - 0.02, 0.12, h + 0.38), "FW_PaintedSteel", bevel=0.008, rotation=(0, 0, math.pi / 2 + yaw))
        box((0.004, 0.56, 0.32), (x - 0.02 - 0.017 * math.cos(yaw), 0.12 - 0.017 * math.sin(yaw), h + 0.38), "FW_Glass", bevel=0.0,
            rotation=(0, 0, math.pi / 2 + yaw))
    box((0.45, 0.15, 0.025), (0, -0.15, h + 0.013), "FW_PaintedSteel", bevel=0.004)
    for i in range(5):
        box((0.42, 0.018, 0.008), (0, -0.2 + i * 0.025, h + 0.03), "FW_Steel", bevel=0.002)  # key rows
    box((0.06, 0.1, 0.03), (0.35, -0.15, h + 0.015), "FW_PaintedSteel", bevel=0.012)
    box((0.2, 0.45, 0.45), (0.62, 0.05, 0.27), "FW_PaintedSteel", bevel=0.01)
    cylinder(0.04, 0.005, (0.62, -0.18, 0.4), "FW_Glass", rotation=(math.pi / 2, 0, 0), vertices=16, bevel=0.0)
    tube([(0.62, 0.25, 0.3), (0.4, 0.35, 0.1), (0.0, 0.38, 0.05), (-0.3, 0.3, 0.6), (-0.3, 0.2, h + 0.1)], 0.008, "FW_Rubber")
    # Office chair
    cx, cy = 0.0, -0.75
    for a in range(5):
        ang = a * 2 * math.pi / 5
        box((0.32, 0.04, 0.03), (cx + math.cos(ang) * 0.16, cy + math.sin(ang) * 0.16, 0.06), "FW_PaintedSteel", bevel=0.006, rotation=(0, 0, ang))
        cylinder(0.025, 0.03, (cx + math.cos(ang) * 0.31, cy + math.sin(ang) * 0.31, 0.025), "FW_Rubber", rotation=(math.pi / 2, 0, ang), vertices=12)
    cylinder(0.03, 0.38, (cx, cy, 0.26), "FW_Steel", vertices=12)
    box((0.48, 0.46, 0.08), (cx, cy, 0.48), "FW_Rubber", bevel=0.03)
    box((0.46, 0.06, 0.55), (cx, cy - 0.24, 0.8), "FW_Rubber", bevel=0.03, rotation=(-0.12, 0, 0))
    export("computer")


def research_table():
    reset()
    w, d, h = 2.4, 0.9, 0.92
    box((w, d, 0.05), (0, 0, h - 0.025), "FW_Concrete", bevel=0.01)  # resin top
    for x in (-w / 2 + 0.35, w / 2 - 0.35):
        box((0.6, d - 0.1, h - 0.12), (x, 0, (h - 0.12) / 2 + 0.05), "FW_PaintedSteel", bevel=0.01)  # cabinets
        for z in (0.3, 0.6):
            box((0.12, 0.02, 0.025), (x, -d / 2 + 0.04, z), "FW_Steel", bevel=0.005)  # handles
    box((w - 1.3, d - 0.15, 0.05), (0, 0.05, 0.08), "FW_PaintedSteel", bevel=0.006)
    # Reagent shelf on uprights along the back
    for x in (-w / 2 + 0.05, w / 2 - 0.05):
        box((0.04, 0.04, 0.7), (x, d / 2 - 0.08, h + 0.35), "FW_Steel", bevel=0.004)
    box((w, 0.22, 0.025), (0, d / 2 - 0.12, h + 0.55), "FW_TableWood", bevel=0.004)
    for i in range(9):
        r = 0.035 + 0.015 * (i % 3)
        cylinder(r, 0.12 + 0.03 * (i % 2), (-1.0 + i * 0.24, d / 2 - 0.12, h + 0.63 + 0.015 * (i % 2)), "FW_Glass", vertices=16, bevel=0.002)
    # Microscope
    mx, my = -0.6, 0.0
    box((0.16, 0.22, 0.03), (mx, my, h + 0.015), "FW_PaintedSteel", bevel=0.008)
    box((0.05, 0.05, 0.3), (mx, my + 0.08, h + 0.17), "FW_PaintedSteel", bevel=0.01, rotation=(-0.25, 0, 0))
    cylinder(0.025, 0.2, (mx, my, h + 0.27), "FW_Steel", rotation=(0.45, 0, 0), vertices=16)
    box((0.12, 0.12, 0.012), (mx, my - 0.02, h + 0.11), "FW_Steel", bevel=0.003)
    # Flasks, a beaker, a test tube rack, a notebook
    for i, (x, y, r) in enumerate(((0.2, -0.1, 0.07), (0.38, 0.05, 0.05), (0.55, -0.12, 0.06))):
        cylinder(r, 0.14, (x, y, h + 0.07), "FW_Glass", vertices=24, top_radius=r * 0.3, bevel=0.002)
        cylinder(r * 0.3, 0.07, (x, y, h + 0.175), "FW_Glass", vertices=16, bevel=0.0)
    cylinder(0.05, 0.12, (0.0, 0.15, h + 0.06), "FW_Glass", vertices=24, bevel=0.002)
    box((0.3, 0.08, 0.02), (0.85, 0.15, h + 0.1), "FW_TableWood", bevel=0.003)
    for i in range(6):
        cylinder(0.01, 0.14, (0.73 + i * 0.048, 0.15, h + 0.09), "FW_Glass", vertices=8, bevel=0.0)
    for x in (0.71, 0.99):
        box((0.02, 0.08, 0.1), (x, 0.15, h + 0.05), "FW_TableWood", bevel=0.003)
    box((0.3, 0.22, 0.012), (-0.15, -0.2, h + 0.006), "FW_TableWood", bevel=0.002, rotation=(0, 0, 0.2))
    # Stool
    cylinder(0.18, 0.05, (0.0, -0.75, 0.65), "FW_PaintedSteel", vertices=24, bevel=0.01)
    for a in range(3):
        ang = a * 2 * math.pi / 3
        box((0.025, 0.025, 0.66), (math.cos(ang) * 0.12, -0.75 + math.sin(ang) * 0.12, 0.32), "FW_Steel", bevel=0.004,
            rotation=(math.sin(ang) * 0.12, -math.cos(ang) * 0.12, 0))
    export("research_table")


def coal_pile(cx, cy, count, spread, seed):
    import random
    rng = random.Random(seed)
    for i in range(count):
        a = rng.random() * 2 * math.pi
        r = spread * math.sqrt(rng.random())
        s = 0.08 + 0.08 * rng.random()
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=s, location=(cx + math.cos(a) * r, cy + math.sin(a) * r, s * 0.6 + (spread - r) * 0.25))
        obj = bpy.context.active_object
        obj.rotation_euler = (rng.random() * 3, rng.random() * 3, rng.random() * 3)
        finish(obj, "FW_Rubber", bevel=0.0)


def log_stack(cx, cy, length, rows, along_x=True):
    for r in range(rows):
        for i in range(rows - r):
            off = (i - (rows - r - 1) / 2) * 0.32
            loc = (cx, cy + off, 0.16 + r * 0.27) if along_x else (cx + off, cy, 0.16 + r * 0.27)
            rot = (0, math.pi / 2, 0) if along_x else (math.pi / 2, 0, 0)
            cylinder(0.15, length, loc, "FW_Wood", rotation=rot, vertices=14, bevel=0.01)


def furnace():
    """Stone furnace, 2 x 2 tiles (8 m): a squat stone body, the fire door at the front, a brick chimney."""
    reset()
    box((7.4, 7.4, 0.2), (0, 0, 0.1), "FW_Concrete", bevel=0.03)
    box((3.4, 3.4, 1.8), (0, 0, 1.1), "FW_Stone", bevel=0.04)
    prism([(-1.7, 0), (1.7, 0), (1.2, 1.1), (-1.2, 1.1)], 3.4, "FW_Stone", location=(0, 0, 2.0), bevel=0.03)
    box((2.5, 2.5, 0.12), (0, 0, 3.16), "FW_Steel", bevel=0.02)
    cylinder(0.55, 4.0, (0, 0, 5.2), "FW_Brick", vertices=24, top_radius=0.45, bevel=0.01)
    cylinder(0.52, 0.2, (0, 0, 7.25), "FW_Steel", vertices=24, bevel=0.01)
    for z in (4.0, 5.5, 6.6):
        cylinder(0.57 - (z - 3.2) * 0.025, 0.06, (0, 0, z), "FW_Steel", vertices=24, bevel=0.005)
    # Fire door: steel frame, dark mouth, door leaf ajar, ash tray below
    box((0.08, 1.4, 1.1), (1.72, 0, 1.0), "FW_Steel", bevel=0.01)
    box((0.06, 1.1, 0.8), (1.73, 0, 1.0), "FW_Steel", bevel=0.005)
    box((0.04, 0.9, 0.75), (1.95, 0.75, 1.0), "FW_Steel", bevel=0.008, rotation=(0, 0, -0.9))
    cylinder(0.03, 0.85, (1.75, -0.6, 1.0), "FW_Steel", vertices=10)
    box((0.5, 1.0, 0.12), (1.95, 0, 0.3), "FW_Steel", bevel=0.01)
    for z in (0.6, 1.5, 2.4):
        box((3.5, 3.5, 0.08), (0, 0, z), "FW_Steel", bevel=0.01)  # binding bands
    bolts([(1.76, y, z) for y in (-0.6, 0.6) for z in (0.5, 1.5)], axis=(1, 0, 0), radius=0.03)
    coal_pile(2.6, -1.6, 40, 0.9, 11)
    log_stack(-2.4, 2.4, 1.8, 3, along_x=True)
    box((0.06, 0.4, 1.4), (2.5, 1.0, 0.7), "FW_Wood", bevel=0.005, rotation=(0, -0.3, 0))  # poker/shovel
    export("furnace")


def brick_furnace():
    """Brick kiln, 3 x 3 tiles (12 m): a vaulted brick body with two firing doors and a tall chimney."""
    reset()
    box((11.4, 11.4, 0.2), (0, 0, 0.1), "FW_Concrete", bevel=0.03)
    box((6.4, 5.2, 2.6), (0, 0, 1.5), "FW_Brick", bevel=0.03)
    vault = [(math.cos(a) * 2.6, 2.8 + math.sin(a) * 1.4) for a in [i * math.pi / 16 for i in range(17)]]
    prism([(2.6, 2.8)] + vault[1:-1] + [(-2.6, 2.8)], 6.4, "FW_Brick", location=(0, 0, 0), bevel=0.0)
    bpy.context.active_object.rotation_euler = (0, 0, math.pi / 2)
    for x in (-3.0, -1.0, 1.0, 3.0):
        box((0.25, 5.5, 0.25), (x, 0, 2.85), "FW_Steel", bevel=0.02)  # tie beams
        for y in (-2.72, 2.72):
            box((0.25, 0.12, 2.8), (x, y, 1.4), "FW_Steel", bevel=0.01)  # buckstays
            bolts([(x, y + (0.07 if y > 0 else -0.07), z) for z in (0.6, 2.2)], axis=(0, 1 if y > 0 else -1, 0), radius=0.035)
    for y in (-1.2, 1.2):
        box((0.1, 1.2, 1.2), (3.23, y, 0.9), "FW_Steel", bevel=0.01)
        box((0.05, 0.95, 0.95), (3.25, y, 0.9), "FW_Steel", bevel=0.005)
        cylinder(0.04, 0.3, (3.35, y - 0.35, 0.9), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=10)
    box((1.4, 1.4, 9.5), (-2.4, 0, 4.75), "FW_Brick", bevel=0.02)
    box((1.6, 1.6, 0.3), (-2.4, 0, 9.6), "FW_Brick", bevel=0.02)
    for z in (3.5, 5.5, 7.5):
        box((1.46, 1.46, 0.08), (-2.4, 0, z), "FW_Steel", bevel=0.005)
    for k in range(10):
        box((0.04, 0.3, 0.04), (-1.68, 0, 3.2 + k * 0.6), "FW_Steel", bevel=0.005)  # ladder rungs
    for y in (-0.17, 0.17):
        box((0.05, 0.04, 6.2), (-1.66, y, 6.0), "FW_Steel", bevel=0.005)
    coal_pile(4.5, -3.2, 70, 1.3, 23)
    for i in range(4):
        box((1.0, 1.0, 0.5 + 0.1 * (i % 2)), (4.4, 2.3 + (i % 2) * 1.05, 0.3 + (i // 2) * 0.55), "FW_Brick", bevel=0.01)  # stacked bricks
    export("brick_furnace")


def blast_furnace():
    """Blast furnace, 4 x 4 tiles (16 m): a tall steel shell in a column frame, two stoves and a skip incline."""
    reset()
    box((15.4, 15.4, 0.3), (0, 0, 0.15), "FW_Concrete", bevel=0.04)
    cylinder(2.6, 1.6, (0, 0, 1.1), "FW_Brick", vertices=48, bevel=0.02)  # hearth
    cylinder(2.4, 3.5, (0, 0, 3.65), "FW_Steel", vertices=48, top_radius=2.9)  # bosh
    cylinder(2.9, 6.0, (0, 0, 8.4), "FW_Steel", vertices=48, top_radius=2.1)  # stack
    cylinder(2.1, 1.5, (0, 0, 12.15), "FW_PaintedSteel", vertices=48, top_radius=1.2)  # top cone
    for z in (2.0, 4.5, 6.5, 8.5, 10.5):
        cylinder(2.95 - abs(z - 5.4) * 0.11, 0.12, (0, 0, z), "FW_Steel", vertices=48, bevel=0.01)  # bands
    # Tuyere ring and blowpipes
    cylinder(2.95, 0.4, (0, 0, 2.3), "FW_PaintedSteel", vertices=48, bevel=0.02)
    for k in range(10):
        a = k * 2 * math.pi / 10
        tube([(math.cos(a) * 2.9, math.sin(a) * 2.9, 2.3), (math.cos(a) * 2.75, math.sin(a) * 2.75, 1.9), (math.cos(a) * 2.6, math.sin(a) * 2.6, 1.6)], 0.09, "FW_Steel", resolution=6)
    # Column frame and platforms
    for x in (-4.0, 4.0):
        for y in (-4.0, 4.0):
            box((0.4, 0.4, 13.0), (x, y, 6.5), "FW_PaintedSteel", bevel=0.02)
    for z in (6.0, 10.0):
        for s in (-1, 1):
            box((8.4, 0.3, 0.4), (0, s * 4.0, z), "FW_PaintedSteel", bevel=0.02)
            box((0.3, 8.4, 0.4), (s * 4.0, 0, z), "FW_PaintedSteel", bevel=0.02)
        box((8.2, 8.2, 0.06), (0, 0, z + 0.23), "FW_Grate", bevel=0.0)
        for s in (-1, 1):
            box((8.2, 0.05, 0.05), (0, s * 4.05, z + 1.25), "FW_Steel", bevel=0.005)  # handrails
            box((0.05, 8.2, 0.05), (s * 4.05, 0, z + 1.25), "FW_Steel", bevel=0.005)
    # Downcomer from the top to the ground
    tube([(0.8, 0.8, 12.9), (2.2, 2.2, 14.2), (4.8, 4.8, 13.0), (5.4, 5.4, 8.0), (5.4, 5.4, 1.0)], 0.45, "FW_Worn", resolution=16)
    # Two hot stoves beside it
    for y in (-5.5, -2.0):
        cylinder(1.4, 9.0, (-5.6, y, 4.8), "FW_Steel", vertices=40)
        bpy.ops.mesh.primitive_uv_sphere_add(radius=1.4, location=(-5.6, y, 9.3), segments=40, ring_count=20)
        dome = bpy.context.active_object
        dome.scale = (1, 1, 0.6)
        finish(dome, "FW_Steel")
        for z in (2.0, 4.5, 7.0):
            cylinder(1.45, 0.1, (-5.6, y, z), "FW_Steel", vertices=40, bevel=0.01)
    tube([(-5.6, -3.75, 3.0), (-3.5, -2.5, 2.9), (-2.4, -1.4, 2.5)], 0.35, "FW_Steel", resolution=10)
    # Skip incline up to the top from the front
    length = math.hypot(8.0, 11.5)
    box((length, 1.0, 0.5), (4.0 + 2.0, 0, 6.6), "FW_PaintedSteel", bevel=0.03, rotation=(0, -math.atan2(11.5, 8.0), 0))
    for x in (6.5, 9.0):
        box((0.3, 0.3, 12.0 - (x - 4.0) * 1.43), (x, 0, (12.0 - (x - 4.0) * 1.43) / 2), "FW_PaintedSteel", bevel=0.02)
    box((1.2, 1.4, 0.9), (7.2, 0, 3.6), "FW_Worn", bevel=0.03, rotation=(0, -math.atan2(11.5, 8.0), 0))  # skip car
    coal_pile(6.0, -5.0, 90, 1.6, 31)
    export("blast_furnace")


def saw_blade(radius, location):
    """A circular saw blade with teeth around the rim, standing in the XZ plane."""
    n = 96
    verts = [(0.0, 0.0, 0.0)]
    for i in range(n):
        a = i * 2 * math.pi / n
        r = radius * (1.0 if i % 2 == 0 else 0.93)
        verts.append((math.cos(a) * r, 0.0, math.sin(a) * r))
    faces = [(0, 1 + i, 1 + (i + 1) % n) for i in range(n)]
    obj = mesh_object(verts, faces, "FW_Steel", 0.0, location)
    mod = obj.modifiers.new("Solidify", "SOLIDIFY")
    mod.thickness = 0.008
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return obj


def sawmill():
    """Sawmill, 2 x 2 tiles (8 m): a log carriage running past a big circular saw under a lean-to roof."""
    reset()
    box((7.6, 7.6, 0.2), (0, 0, 0.1), "FW_Concrete", bevel=0.03)
    # Carriage track along X with rollers
    for y in (-0.55, 0.55):
        box((6.8, 0.12, 0.18), (0, y, 0.6), "FW_PaintedSteel", bevel=0.01)
    for x in [-3.2 + i * 0.8 for i in range(9)]:
        box((0.12, 1.3, 0.5), (x, 0, 0.25), "FW_PaintedSteel", bevel=0.01)
        cylinder(0.09, 1.1, (x, 0, 0.75), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=16)
    cylinder(0.32, 3.4, (-1.6, 0, 1.15), "FW_Wood", rotation=(0, math.pi / 2, 0), vertices=24, bevel=0.02)  # log on the carriage
    for x in (-3.0, -0.2):
        box((0.12, 1.0, 0.4), (x, 0, 0.95), "FW_Steel", bevel=0.01)  # dogs holding it
    # The saw: blade, guard, arbor, motor
    saw_blade(0.75, (0.8, -0.05, 1.0))
    cylinder(0.12, 0.6, (0.8, -0.35, 1.0), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=20)
    prism([(-0.85, 0.0), (0.85, 0.0), (0.85, 0.35), (0.4, 0.75), (-0.4, 0.75), (-0.85, 0.35)], 0.3, "FW_PaintedSteel", location=(0.8, -0.05, 1.55), bevel=0.02)
    box((0.9, 0.7, 0.7), (0.8, -1.3, 0.55), "FW_PaintedSteel", bevel=0.03)
    cylinder(0.3, 0.8, (0.8, -1.3, 1.15), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=24)
    for k in range(8):
        box((0.02, 0.82, 0.05), (0.8, -1.3, 0.92 + k * 0.06), "FW_Steel", bevel=0.003)  # motor fins
    box((0.12, 0.9, 0.1), (0.8, -0.8, 1.0), "FW_Rubber", bevel=0.02)  # drive belt guard
    box((0.4, 0.15, 0.5), (1.6, -1.2, 1.1), "FW_Steel", bevel=0.02)  # switch box
    # Planks out on the far side, logs waiting on the near side
    for r in range(3):
        for i in range(4):
            box((2.6, 0.24, 0.06), (2.4, 1.9 + i * 0.26, 0.24 + r * 0.07), "FW_Wood", bevel=0.004)
    for z in (0.18, 0.4):
        box((0.12, 1.2, 0.08), (1.4 + (z > 0.3) * 2.0, 2.3, 0.2), "FW_Wood", bevel=0.004)
    log_stack(-1.5, -2.6, 3.4, 3, along_x=True)
    # Lean-to roof on four posts
    for x in (-3.4, 3.4):
        for y, hgt in ((-3.2, 3.2), (3.2, 4.0)):
            box((0.2, 0.2, hgt), (x, y, hgt / 2), "FW_Wood", bevel=0.01)
        box((0.2, 6.6, 0.25), (x, 0, 3.7), "FW_Wood", bevel=0.01, rotation=(math.atan2(0.8, 6.4), 0, 0))
    for y in (-3.2, 0.0, 3.2):
        box((7.0, 0.18, 0.18), (0, y, 3.25 + (y + 3.2) / 6.4 * 0.8), "FW_Wood", bevel=0.01)
    box((7.4, 7.0, 0.05), (0, 0, 3.75), "FW_Steel", bevel=0.005, rotation=(math.atan2(0.8, 6.4), 0, 0))
    for i in range(14):
        box((0.04, 7.0, 0.05), (-3.5 + i * 0.54, 0, 3.81), "FW_Steel", bevel=0.005, rotation=(math.atan2(0.8, 6.4), 0, 0))  # corrugations
    export("sawmill")


def assembler():
    """Assembler, 1 tile: a robot arm over a work table, a parts bin, a control cabinet and a guard fence."""
    reset()
    box((3.8, 3.8, 0.15), (0, 0, 0.075), "FW_Concrete", bevel=0.02)
    box((1.6, 1.0, 0.85), (0.6, 0, 0.5), "FW_PaintedSteel", bevel=0.02)
    box((1.7, 1.1, 0.05), (0.6, 0, 0.95), "FW_Steel", bevel=0.01)
    box((0.5, 0.35, 0.15), (0.9, 0.2, 1.05), "FW_Worn", bevel=0.01)  # fixture
    for i in range(3):
        box((0.18, 0.12, 0.08), (0.3 + i * 0.22, -0.3, 1.02), "FW_Steel", bevel=0.01)  # parts
    # Robot: base, turret, upper and lower arm, wrist, gripper
    rx, ry = -0.6, 0.0
    cylinder(0.32, 0.25, (rx, ry, 0.27), "FW_PaintedSteel", vertices=32, bevel=0.02)
    cylinder(0.24, 0.35, (rx, ry, 0.57), "FW_Worn", vertices=32, bevel=0.02)
    cylinder(0.16, 0.42, (rx, ry, 0.82), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=24)
    box((0.2, 0.2, 1.0), (rx + 0.15, ry, 1.25), "FW_Worn", bevel=0.04, rotation=(0, 0.3, 0))
    cylinder(0.12, 0.34, (rx + 0.3, ry, 1.72), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=24)
    box((0.85, 0.16, 0.16), (rx + 0.7, ry, 1.62), "FW_Worn", bevel=0.03, rotation=(0, 0.35, 0))
    cylinder(0.08, 0.16, (rx + 1.1, ry, 1.45), "FW_Steel", rotation=(0, math.pi / 2, 0), vertices=16)
    box((0.08, 0.2, 0.12), (rx + 1.2, ry, 1.35), "FW_Steel", bevel=0.01)
    for s in (-1, 1):
        box((0.04, 0.03, 0.14), (rx + 1.22, ry + s * 0.07, 1.23), "FW_Steel", bevel=0.005)
    tube([(rx - 0.2, ry + 0.2, 0.3), (rx - 0.1, ry + 0.25, 1.0), (rx + 0.25, ry + 0.15, 1.8), (rx + 0.9, ry + 0.12, 1.65)], 0.025, "FW_Rubber")
    # Control cabinet with a pendant, parts bin, guard fence on two sides
    box((0.6, 0.4, 1.6), (-1.3, -1.3, 0.95), "FW_PaintedSteel", bevel=0.02)
    box((0.02, 0.3, 0.2), (-0.99, -1.3, 1.45), "FW_Glass", bevel=0.0)
    for k in range(3):
        cylinder(0.03, 0.02, (-0.99, -1.4 + k * 0.1, 1.15), "FW_Worn", rotation=(0, math.pi / 2, 0), vertices=12, bevel=0.0)
    box((0.7, 0.6, 0.5), (0.9, 1.3, 0.4), "FW_Worn", bevel=0.02)
    for x in (-1.8, -0.6, 0.6, 1.8):
        box((0.06, 0.06, 1.8), (x, 1.85, 1.05), "FW_PaintedSteel", bevel=0.005)
    for x in (-1.2, 0.0, 1.2):
        box((1.14, 0.03, 1.4), (x, 1.85, 1.1), "FW_Grate", bevel=0.0)
    for y in (-1.8, -0.6, 0.6):
        box((0.06, 0.06, 1.8), (-1.85, y, 1.05), "FW_PaintedSteel", bevel=0.005)
    for y in (-1.2, 0.0, 1.2):
        box((0.03, 1.14, 1.4), (-1.85, y, 1.1), "FW_Grate", bevel=0.0)
    export("assembler")


def mill():
    """Mill, 2 x 2 tiles (8 m): a stone mill house with a timber roof, a water wheel and millstones."""
    reset()
    box((7.6, 7.6, 0.2), (0, 0, 0.1), "FW_Concrete", bevel=0.03)
    box((4.6, 4.0, 3.2), (0, 0, 1.8), "FW_Stone", bevel=0.04)
    prism([(-2.6, 0.0), (2.6, 0.0), (0.0, 2.0)], 4.6, "FW_Wood", location=(0, 0, 3.4), bevel=0.02)
    bpy.context.active_object.rotation_euler = (0, 0, 0)
    for i in range(10):
        x = -2.4 + i * 0.53
        box((0.06, 4.7, 0.05), (x, 0, 3.43 + 2.0 * (1 - abs(x) / 2.6)), "FW_Wood", bevel=0.005)  # shingle lines
    # Door, window, a beam with a hoist over the door
    box((0.08, 1.2, 2.0), (2.33, 0, 1.2), "FW_Wood", bevel=0.01)
    for z in (0.6, 1.8):
        box((0.06, 1.25, 0.1), (2.38, 0, z), "FW_Steel", bevel=0.005)
    box((0.08, 0.8, 0.7), (2.33, 1.3, 2.4), "FW_Glass", bevel=0.0)
    box((0.1, 0.9, 0.8), (2.32, 1.3, 2.4), "FW_Wood", bevel=0.01)
    box((1.2, 0.18, 0.18), (2.8, 0, 3.6), "FW_Wood", bevel=0.01)
    tube([(3.3, 0, 3.55), (3.3, 0, 2.6), (3.3, 0, 1.8)], 0.015, "FW_Rubber", resolution=4)
    # Water wheel on the side
    wy = -2.35
    for s in (-0.35, 0.35):
        bpy.ops.mesh.primitive_torus_add(major_radius=1.6, minor_radius=0.07, location=(0, wy + s, 1.9), rotation=(math.pi / 2, 0, 0), major_segments=48, minor_segments=8)
        finish(bpy.context.active_object, "FW_Wood")
    for k in range(16):
        a = k * 2 * math.pi / 16
        box((0.08, 0.75, 0.5), (math.cos(a) * 1.55, wy, 1.9 + math.sin(a) * 1.55), "FW_Wood", bevel=0.005, rotation=(0, -a, 0))
        box((1.5, 0.06, 0.06), (math.cos(a) * 0.78, wy - 0.35, 1.9 + math.sin(a) * 0.78), "FW_Wood", bevel=0.004, rotation=(0, -a, 0))
    cylinder(0.14, 1.2, (0, wy + 0.2, 1.9), "FW_Steel", rotation=(math.pi / 2, 0, 0), vertices=16)
    prism([(-1.8, 0.0), (1.8, 0.0), (1.8, 0.4), (-1.8, 0.4)], 1.0, "FW_Stone", location=(0, wy, 0.2), bevel=0.02)  # race
    box((3.6, 0.8, 0.05), (0, wy, 0.55), "FW_Glass", bevel=0.0)  # water
    # Spare millstones leaning outside, sacks of flour as worn boxes
    for i, x in enumerate((-1.4, -0.6)):
        cylinder(0.65, 0.22, (x, 2.5, 0.65), "FW_Stone", rotation=(math.pi / 2 - 0.15, 0, 0), vertices=32, bevel=0.02)
        cylinder(0.12, 0.24, (x, 2.5, 0.65), "FW_Wood", rotation=(math.pi / 2 - 0.15, 0, 0), vertices=12, bevel=0.0)
    for i in range(5):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.3, location=(2.9 + (i % 3) * 0.45, -1.4 + (i // 3) * 0.5, 0.35 + (i // 3) * 0.35), segments=16, ring_count=8)
        sack = bpy.context.active_object
        sack.scale = (0.8, 0.6, 0.55)
        finish(sack, "FW_Concrete")
    export("mill")


# ---- Items on belts ------------------------------------------------------------------------------
def chunk(name, seed, size=0.34):
    reset()
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=size / 2)
    obj = bpy.context.active_object
    import random
    rng = random.Random(seed)
    for v in obj.data.vertices:
        v.co *= 0.75 + 0.35 * rng.random()
    obj.scale = (1.0, 0.85, 0.7)
    finish(obj, "FW_Worn", bevel=0.0)
    obj.location.z = size * 0.3
    export(name)


def ingot():
    reset()
    bpy.ops.mesh.primitive_cube_add(size=1)
    obj = bpy.context.active_object
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    for v in bm.verts:
        if v.co.z > 0:
            v.co.x *= 0.8
            v.co.y *= 0.75
    bm.to_mesh(obj.data)
    bm.free()
    obj.scale = (0.46, 0.2, 0.12)
    obj.location.z = 0.06
    finish(obj, "FW_Steel", bevel=0.015)
    export("ingot")


def crate():
    reset()
    box((0.36, 0.36, 0.3), (0, 0, 0.15), "FW_Wood", bevel=0.02)
    export("crate")


belt_straight()
belt_curve()
inserter()
chest()
pole()
solar()
turbine()
rails()
machine()
workbench()
anvil()
computer()
research_table()
furnace()
brick_furnace()
blast_furnace()
sawmill()
assembler()
mill()
chunk("chunk_ore", 3)
chunk("chunk_stone", 7, 0.38)
ingot()
crate()
print("FWFACTORY done")
