# Builds the character's hair, facial hair and clothes as skeletal meshes on the UE5 mannequin's skeleton, one
# .fbx per piece in RawAssets/processed/character. Each piece is cut out of the mannequin's own body (exported by
# Tools/export_mannequin.py) by region, pushed out from the skin by its thickness and given body, so it keeps the
# body's skin weights exactly and moves with it; the ponytail and the hood are added shapes that take their
# weights from the nearest skin. Regions match Tools/character_material.hlsl (UE coordinates, cm: +Y forward).
# Pieces and the server's styles (character.options):
#   hair_1..5        short, spiky, long, ponytail, curly (0 = bald: none)
#   beard_1..3       moustache, goatee, full beard (0 = none)
#   shirt_0..2       t-shirt, tank top, hoodie
#   pants, shoes
# Hair and facial hair are layered cards (FW_HairCard: alpha-tested strands) over a dark shell (FW_Hair).
# Material slots, bound by the game (MI_<slot>): FW_Hair, FW_HairCard, FW_Jersey, FW_Fleece, FW_Denim, FW_Leather.
# Run headless:
#   blender --background --factory-startup --python Tools/blender/make_character_gear.py
import math
import os

import bmesh
import bpy

from mathutils import Vector, noise

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
SOURCE = os.path.join(ROOT, "RawAssets", "mannequin", "SKM_Manny_Simple.fbx")
OUT = os.path.join(ROOT, "RawAssets", "processed", "character")


def smoothstep(a, b, x):
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


# Regions on the body, from the position in UE space (cm)
def arm_t(p):
    """Along the arm: 0 at the shoulder, ~0.57 at the elbow, 1 at the wrist; None off the arm."""
    ax = abs(p.x)
    if p.z < 80.0 or ax < 18.0 + max(0.0, 140.0 - p.z) * 0.12:
        return None
    d = Vector((28.8, -39.1)).normalized()
    return ((ax - 19.0) * d.x + (p.z - 143.6) * d.y) / 48.5


def is_head(p):
    return p.z > 155.0 and arm_t(p) is None


def hairline(p):
    return 174.5 - 2.0 * (1.0 - smoothstep(4.0, 8.0, p.y))


def on_face(p):
    return is_head(p) and p.y > 5.0


def lips(p):
    return 162.9 <= p.z <= 163.9 and abs(p.x) <= 2.4


HAIR = {
    1: lambda p: is_head(p) and (p.z > hairline(p) or (p.y < -4.0 and p.z > 163.0)),
    2: lambda p: is_head(p) and (p.z > hairline(p) - 0.5),
    3: lambda p: (is_head(p) and (p.z > hairline(p) - 1.0 or (abs(p.x) > 5.5 and p.y < 1.0 and p.z > 158.0)))
                 or (arm_t(p) is None and p.y < -2.5 and 132.0 + abs(p.x) * 0.6 < p.z < 181.0 and abs(p.x) < 13.0),
    4: lambda p: is_head(p) and (p.z > hairline(p) or (p.y < -4.0 and p.z > 165.0)),
    5: lambda p: is_head(p) and (p.z > hairline(p) - 2.5 - smoothstep(4.0, 8.0, p.y) * 1.5 or (p.y < -2.0 and p.z > 161.0)),
}
BEARD = {
    1: lambda p: on_face(p) and 163.6 <= p.z <= 166.0 and abs(p.x) <= 4.2,
    2: lambda p: on_face(p) and ((156.5 <= p.z <= 162.6 and abs(p.x) <= 2.4) or (164.0 <= p.z <= 165.2 and abs(p.x) <= 3.0)),
    3: lambda p: is_head(p) and p.y > 1.5 and p.z < (165.4 if abs(p.x) < 4.0 else 166.5) and not lips(p),
}


def torso(p, top):
    return arm_t(p) is None and 101.0 < p.z < top


def neckline(p):
    return p.y > 5.0 and p.z > 147.0 and abs(p.x) < 7.0


SHIRT = {
    0: lambda p: (torso(p, 151.0) and not neckline(p)) or ((arm_t(p) or 9.0) < 0.33),
    1: lambda p: torso(p, 151.0) and not neckline(p) and not (abs(p.x) > 13.0 and p.z > 128.0),
    2: lambda p: torso(p, 156.0) or ((arm_t(p) or 9.0) < 1.02),
}


def pants(p):
    return arm_t(p) is None and 11.0 < p.z < 101.5


def shoes(p):
    return p.z < 13.0


def load_body():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=SOURCE)
    armature = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    body = next(o for o in bpy.data.objects if o.type == "MESH")
    return armature, body


def to_ue(body, co):
    """Mesh coordinates to UE space in cm (Blender's -Y is UE's +Y)."""
    w = body.matrix_world @ co
    return Vector((w.x * 100.0, -w.y * 100.0, w.z * 100.0))


def material(name):
    return bpy.data.materials.get(name) or bpy.data.materials.new(name)


def cut(armature, body, name, inside, offset, thickness, slot, bumps=None):
    """A copy of the body keeping the faces whose corners are all inside the region, moved out along the
    normals by offset (+ bumps(p) cm), with thickness for body. Skin weights come along with the vertices."""
    piece = body.copy()
    piece.data = body.data.copy()
    piece.name = name
    bpy.context.scene.collection.objects.link(piece)
    # Scale from UE cm to the mesh's own units
    unit = 1.0 / (body.matrix_world.to_scale().x * 100.0)
    bm = bmesh.new()
    bm.from_mesh(piece.data)
    keep = {v: inside(to_ue(body, v.co)) for v in bm.verts}
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not all(keep[v] for v in f.verts)], context="FACES_ONLY")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.normal_update()
    moves = []
    for v in bm.verts:
        p = to_ue(body, v.co)
        extra = bumps(p) if bumps else 0.0
        moves.append((v, v.normal.copy() * (offset + extra) * unit))
    for v, move in moves:
        v.co += move
    bm.to_mesh(piece.data)
    bm.free()
    piece.data.materials.clear()
    piece.data.materials.append(material(slot))
    for poly in piece.data.polygons:
        poly.material_index = 0
        poly.use_smooth = True
    finish(piece, thickness * unit)
    return piece


def finish(piece, thickness):
    bpy.context.view_layer.objects.active = piece
    if thickness > 0:
        mod = piece.modifiers.new("Solidify", "SOLIDIFY")
        mod.thickness = thickness
        mod.offset = -1.0
        mod.use_rim = True
        bpy.ops.object.modifier_apply(modifier=mod.name)
    # Soften the stair-steps where the region cut through the body's faces
    mod = piece.modifiers.new("Smooth", "CORRECTIVE_SMOOTH")
    mod.factor = 0.5
    mod.iterations = 4
    mod.use_only_smooth = True
    bpy.ops.object.modifier_apply(modifier=mod.name)


def weigh_from(body, piece):
    """Skin weights for an added shape: copied from the nearest body surface."""
    for group in body.vertex_groups:
        if group.name not in piece.vertex_groups:
            piece.vertex_groups.new(name=group.name)
    mod = piece.modifiers.new("Weights", "DATA_TRANSFER")
    mod.object = body
    mod.use_vert_data = True
    mod.data_types_verts = {"VGROUP_WEIGHTS"}
    mod.vert_mapping = "POLYINTERP_NEAREST"
    mod.layers_vgroup_select_src = "ALL"
    mod.layers_vgroup_select_dst = "NAME"
    bpy.context.view_layer.objects.active = piece
    bpy.ops.object.modifier_apply(modifier=mod.name)


def shape_from_ue(body, points, radii, slot, name, sides=12):
    """A tube through UE-space points (cm), as a mesh in the body's space, weighted from the skin."""
    unit = 1.0 / (body.matrix_world.to_scale().x * 100.0)
    inverse = body.matrix_world.inverted()
    local = [inverse @ Vector((p.x / 100.0, -p.y / 100.0, p.z / 100.0)) for p in points]
    bm = bmesh.new()
    rings = []
    normal = (local[1] - local[0]).orthogonal().normalized()
    for i, c in enumerate(local):
        tangent = (local[min(i + 1, len(local) - 1)] - local[max(i - 1, 0)]).normalized()
        normal = (normal - tangent * normal.dot(tangent)).normalized()
        binormal = tangent.cross(normal)
        rings.append([bm.verts.new(c + (normal * math.cos(a) + binormal * math.sin(a)) * radii[i] * unit)
                      for a in (2 * math.pi * s / sides for s in range(sides))])
    for a, b in zip(rings, rings[1:]):
        for s in range(sides):
            bm.faces.new([a[s], a[(s + 1) % sides], b[(s + 1) % sides], b[s]])
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    piece = bpy.data.objects.new(name, mesh)
    piece.parent = body.parent
    piece.matrix_world = body.matrix_world.copy()
    bpy.context.scene.collection.objects.link(piece)
    mesh.materials.append(material(slot))
    for poly in mesh.polygons:
        poly.use_smooth = True
    mod = piece.modifiers.new("Subdivision", "SUBSURF")
    mod.levels = 1
    bpy.context.view_layer.objects.active = piece
    bpy.ops.object.modifier_apply(modifier=mod.name)
    weigh_from(body, piece)
    arm = piece.modifiers.new("Armature", "ARMATURE")
    arm.object = next(m.object for m in body.modifiers if m.type == "ARMATURE")
    return piece


def join(pieces):
    bpy.ops.object.select_all(action="DESELECT")
    for piece in pieces:
        piece.select_set(True)
    bpy.context.view_layer.objects.active = pieces[0]
    bpy.ops.object.join()
    return pieces[0]


def export(armature, piece, name):
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    piece.select_set(True)
    bpy.context.view_layer.objects.active = armature
    path = os.path.join(OUT, f"{name}.fbx")
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"}, add_leaf_bones=False,
                             bake_anim=False, mesh_smooth_type="FACE", use_armature_deform_only=False)
    print(f"FWGEAR {name}: {len(piece.data.polygons)} faces -> {path}")


def hair_bumps(style):
    def bumps(p):
        n = noise.noise(p * 0.35)
        if style == 2:  # spiky: tufts standing out of the crown
            tuft = max(0.0, noise.noise(p * 0.45 + Vector((3.1, 0, 0))))
            return 0.4 * n + 3.2 * tuft ** 1.5 * smoothstep(168.0, 178.0, p.z)
        if style == 5:  # curly: dense round bumps
            return 0.9 + 0.8 * abs(noise.noise(p * 0.9))
        if style == 3:  # long: thicker over the back
            return 0.5 * n + (0.6 if p.z < 160.0 else 0.0)
        return 0.35 * n
    return bumps


# Hair cards: thin strips (UV: U across, V root -> tip) whose alpha cuts them into strands (M_FWHairCard), laid
# in layers over the shell, which stays as a dark underlayer so no scalp shows between the cards. Each card grows
# from a point of the style's region along a path that depends on the style, kept outside the skull.
SKULL = Vector((0.0, 1.0, 170.0))
CARDS = {
    # style: (cards, length cm (min, max), width cm, layers cm out from the skin, droop, stiffness)
    1: (900, (3.0, 5.5), 1.4, (1.0, 1.7), 0.25, 0.7),
    2: (700, (4.0, 7.5), 1.2, (1.0, 1.8), 0.0, 1.0),
    3: (1700, (16.0, 34.0), 1.3, (1.2, 1.9, 2.6), 2.2, 0.15),
    4: (900, (5.0, 9.0), 1.4, (1.0, 1.7), 0.35, 0.6),
    5: (1300, (3.0, 5.0), 1.1, (1.3, 2.0, 2.6), 0.15, 0.5),
}
BEARD_CARDS = {1: (160, (0.8, 1.6), 0.5), 2: (260, (1.0, 2.2), 0.6), 3: (1100, (1.0, 2.6), 0.7)}


def surface_points(body, inside):
    """Points of the body inside a region, in UE space, with their normals."""
    points = []
    matrix = body.matrix_world.to_3x3()
    for v in body.data.vertices:
        p = to_ue(body, v.co)
        if inside(p):
            n = matrix @ v.normal
            points.append((p, Vector((n.x, -n.y, n.z)).normalized()))
    return points


def keep_out(q, layer):
    """Pushes a point of a card outside the skull (a sphere) and the face (no cards hang in front of it)."""
    d = q - SKULL
    r = 10.4 + layer
    if q.z > 156.0 and d.length < r:
        q = SKULL + d.normalized() * r
    if q.y > 4.0 and q.z > 158.0 and abs(q.x) < 7.5:
        q.y = 4.0
    # Below the brows nothing hangs in front of the face or out over the shoulders
    if q.z < 168.0 and q.y > 1.0 and abs(q.x) < 10.5:
        q.y = 1.0
    if q.z < 162.0 and abs(q.x) > 12.0:
        q.x = math.copysign(12.0 + (abs(q.x) - 12.0) * 0.3, q.x)
    return q


def card_path(style, p, n, length, layer, rng, droop, stiff):
    """The spine of one card in UE space: from the root, along the flow of the style."""
    away = Vector((p.x, p.y + 2.0, 0.0))
    away = away.normalized() if away.length > 0.01 else Vector((0, -1, 0))
    if style == 2:  # spiky: out and up from the crown
        flow = (n * 1.4 + Vector((0, 0, 0.8)) + away * 0.2).normalized()
    elif style == 4:  # ponytail cap: combed back to the band at the back of the head
        flow = (Vector((0, -11.0, 167.0)) - p).normalized()
    else:  # combed down and outwards from the crown, the fringe forward a little
        crown = Vector((0.0, -1.0, 179.0))
        flow = (p - crown)
        flow = (flow - n * flow.dot(n)).normalized()
    root = p + n * layer
    steps = 6 if length < 10 else 10
    spine = [root]
    direction = flow
    for k in range(1, steps + 1):
        t = k / steps
        bend = Vector((rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), -droop * t)) * (1.0 - stiff * 0.7)
        if style == 5:  # curly: the strand turns round as it goes
            angle = t * 9.0 + rng.uniform(0, 6.28)
            bend += (n.cross(flow) * math.cos(angle) + n * math.sin(angle)) * 0.6
        direction = (direction + bend).normalized()
        q = spine[-1] + direction * length / steps
        spine.append(keep_out(q, layer))
    return spine


def cards_mesh(body, spines, width, name, slot, rng):
    """Strips along the spines (UE space, cm) as one mesh in the body's space, weighted from the skin."""
    inverse = body.matrix_world.inverted()
    unit = 1.0 / (body.matrix_world.to_scale().x * 100.0)
    bm = bmesh.new()
    # The same UV layer name as the body's, so joined to the shell the cards keep their UVs in channel 0
    uv_layer = bm.loops.layers.uv.new(body.data.uv_layers.active.name)
    for spine, normal in spines:
        # Each card a different stretch of the strand pattern: U offset by a whole number (the material hashes it)
        offset = float(rng.randint(0, 63))
        side_prev = None
        rows = []
        for k, q in enumerate(spine):
            along = (spine[min(k + 1, len(spine) - 1)] - spine[max(k - 1, 0)]).normalized()
            side = along.cross(normal).normalized()
            if side_prev is not None and side.dot(side_prev) < 0:
                side = -side
            side_prev = side
            half = width * 0.5 * (1.0 - 0.55 * k / (len(spine) - 1))  # narrower towards the tip
            row = []
            for sign in (-1.0, 1.0):
                w = q + side * half * sign
                row.append(bm.verts.new(inverse @ Vector((w.x / 100.0, -w.y / 100.0, w.z / 100.0))))
            rows.append(row)
        for k in range(len(rows) - 1):
            face = bm.faces.new([rows[k][0], rows[k][1], rows[k + 1][1], rows[k + 1][0]])
            v0, v1 = k / (len(rows) - 1), (k + 1) / (len(rows) - 1)
            for loop, uv in zip(face.loops, ((offset, v0), (offset + 1, v0), (offset + 1, v1), (offset, v1))):
                loop[uv_layer].uv = uv
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    piece = bpy.data.objects.new(name, mesh)
    piece.parent = body.parent
    piece.matrix_world = body.matrix_world.copy()
    bpy.context.scene.collection.objects.link(piece)
    mesh.materials.append(material(slot))
    for poly in mesh.polygons:
        poly.use_smooth = True
    weigh_from(body, piece)
    arm = piece.modifiers.new("Armature", "ARMATURE")
    arm.object = next(m.object for m in body.modifiers if m.type == "ARMATURE")
    return piece


def hair_cards(body, style):
    import random
    rng = random.Random(style * 977)
    count, (shortest, longest), width, layers, droop, stiff = CARDS[style]
    roots = surface_points(body, HAIR[style])
    spines = []
    for i in range(count):
        p, n = roots[rng.randrange(len(roots))]
        layer = layers[i % len(layers)]
        length = rng.uniform(shortest, longest)
        if style == 3 and p.z < 160.0:
            length *= 0.5  # the hair already lying on the back
        spines.append((card_path(style, p, n, length, layer, rng, droop, stiff), n))
    if style == 4:
        # The tail: cards from the band down between the shoulder blades
        for i in range(260):
            a = rng.uniform(0, 2 * math.pi)
            r = rng.uniform(0.3, 2.2)
            start = Vector((math.cos(a) * r, -11.0 + math.sin(a) * r * 0.6, 167.0))
            spine = [start + Vector((rng.uniform(-0.6, 0.6) * t, -3.0 * t - 0.5 * t * t, -30.0 * t ** 1.15)) for t in (k / 8 for k in range(9))]
            spines.append((spine, Vector((0, -1, 0))))
    return cards_mesh(body, spines, width, f"hair_{style}_cards", "FW_HairCard", rng)


def beard_cards(body, style):
    import random
    rng = random.Random(style * 331 + 7)
    count, (shortest, longest), width = BEARD_CARDS[style]
    roots = surface_points(body, BEARD[style])
    spines = []
    for i in range(count):
        p, n = roots[rng.randrange(len(roots))]
        length = rng.uniform(shortest, longest)
        flow = (n * 0.6 + Vector((0, 0.15, -1.0))).normalized()
        root = p + n * 0.25
        spine = [root + flow * length * t + Vector((rng.uniform(-0.15, 0.15), 0, 0)) * t for t in (k / 3 for k in range(4))]
        spines.append((spine, n))
    return cards_mesh(body, spines, width, f"beard_{style}_cards", "FW_HairCard", rng)


os.makedirs(OUT, exist_ok=True)
for kind, builders in (
    ("hair", {s: (lambda s=s: (HAIR[s], 0.6 if s != 5 else 0.9, 0.3, "FW_Hair", hair_bumps(s))) for s in HAIR}),
    ("beard", {s: (lambda s=s: (BEARD[s], 0.35 if s == 1 else 0.45, 0.25, "FW_Hair",
                                (lambda p: 0.25 * abs(noise.noise(p * 0.8))) if s == 3 else None)) for s in BEARD}),
    ("shirt", {s: (lambda s=s: (SHIRT[s], 1.6 if s == 2 else 0.6, 0.35 if s == 2 else 0.2,
                                "FW_Fleece" if s == 2 else "FW_Jersey", None)) for s in SHIRT}),
):
    for style, make in builders.items():
        armature, body = load_body()
        inside, offset, thickness, slot, bumps = make()
        piece = cut(armature, body, f"{kind}_{style}", inside, offset, thickness, slot, bumps)
        extra = []
        if kind == "hair":
            extra.append(hair_cards(body, style))
        if kind == "beard":
            extra.append(beard_cards(body, style))
        if kind == "hair" and style == 4:
            # Ponytail: from the back of the head down between the shoulder blades
            points = [Vector((0, -9.0 - 3.0 * t, 169.0 - 32.0 * t ** 1.2)) for t in (i / 10 for i in range(11))]
            extra.append(shape_from_ue(body, points, [3.2 - 1.6 * (i / 10) for i in range(11)], "FW_Hair", "ponytail"))
        if kind == "shirt" and style == 2:
            # Hood folded down behind the neck
            points = [Vector((math.sin(a) * 9.5, -6.5 - math.cos(a) * 3.0, 151.0 + math.cos(a) * 3.5))
                      for a in (math.radians(-110 + 22 * i) for i in range(11))]
            extra.append(shape_from_ue(body, points, [3.5] * 11, "FW_Fleece", "hood"))
        bpy.data.objects.remove(body, do_unlink=True)
        export(armature, join([piece] + extra), f"{kind}_{style}")

for name, inside, offset, thickness, slot in (("pants", pants, 0.9, 0.3, "FW_Denim"), ("shoes", shoes, 0.9, 0.4, "FW_Leather")):
    armature, body = load_body()
    piece = cut(armature, body, name, inside, offset, thickness, slot)
    bpy.data.objects.remove(body, do_unlink=True)
    export(armature, piece, name)
