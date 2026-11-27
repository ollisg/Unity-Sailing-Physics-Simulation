"""
Builds a simple cartoony robot sailor test character (RobotSailor collection).

Run inside Blender (4.2+): open Character.blend -> Scripting workspace ->
Text > Open this file -> Run Script (Alt+P). Safe to re-run: it replaces the
previous RobotSailor collection and does not touch anything else in the file.

Design notes
- Z up, units in metres, character faces -Y (visible from numpad 1 front view).
- Feet at Z = 0, total height about 1.84 m (typical heavyweight ILCA 7 sailor).
- Real adult proportions (joint heights, limb lengths, shoulder/hip width),
  simplified chunky shapes, no small details.
- Every body part is a separate rigid object with its origin on its joint and
  a parent chain (Root > Pelvis > Torso > ... ), so it can be posed right away
  (rotate RS_Thigh_L about X to see the knee swing) and later parented to bones
  or Unity transforms without skinning.

Optional headless use (builds and saves the .blend):
  Blender -b Character.blend --python build_robot_sailor.py -- --save
"""
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

NAME = "RobotSailor"
PREFIX = "RS_"
PIV = {}  # object name -> pivot (world position at rest)

# ----------------------------------------------------------------- cleanup
if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')

old = bpy.data.collections.get(NAME)
if old is not None:
    for o in list(old.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.collections.remove(old)
for m in list(bpy.data.meshes):
    if m.users == 0 and m.name.startswith(PREFIX):
        bpy.data.meshes.remove(m)

col = bpy.data.collections.new(NAME)
bpy.context.scene.collection.children.link(col)


# --------------------------------------------------------------- materials
def make_mat(name, rgba, rough=0.5, metal=0.0, emit=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = rgba
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = metal
        if emit > 0.0:
            try:
                bsdf.inputs["Emission Color"].default_value = rgba
                bsdf.inputs["Emission Strength"].default_value = emit
            except KeyError:
                pass
    m.diffuse_color = rgba
    return m


MAT_BODY = make_mat("RS_Body", (0.88, 0.90, 0.93, 1.0), rough=0.45)
MAT_JOINT = make_mat("RS_Joint", (0.07, 0.08, 0.10, 1.0), rough=0.6, metal=0.3)
MAT_VEST = make_mat("RS_Vest", (1.00, 0.35, 0.05, 1.0), rough=0.7)
MAT_VISOR = make_mat("RS_Visor", (0.10, 0.75, 0.95, 1.0), rough=0.2, emit=1.5)


# ----------------------------------------------------------------- helpers
def finish(name, bm, material, pivot, parent, bevel=None):
    """Turn a world-space bmesh into an object whose origin sits on `pivot`."""
    pivot = Vector(pivot)
    bmesh.ops.translate(bm, vec=-pivot, verts=bm.verts)
    me = bpy.data.meshes.new(PREFIX + name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(material)
    for p in me.polygons:
        p.use_smooth = True
    ob = bpy.data.objects.new(PREFIX + name, me)
    ob.location = pivot
    col.objects.link(ob)
    if bevel:
        mod = ob.modifiers.new("Bevel", 'BEVEL')
        mod.width = bevel
        mod.segments = 4
        mod.limit_method = 'NONE'
        mod.harden_normals = True
    if parent is not None:
        ob.parent = parent
        ob.matrix_parent_inverse = Matrix.Translation(-PIV[parent.name])
    PIV[ob.name] = pivot
    return ob


def rbox(name, center, size, material, pivot, parent, bevel):
    bm = bmesh.new()
    m = Matrix.Translation(Vector(center)) @ Matrix.Diagonal((size[0], size[1], size[2], 1.0))
    bmesh.ops.create_cube(bm, size=1.0, matrix=m)
    return finish(name, bm, material, pivot, parent, bevel)


def capsule(name, p1, p2, r, material, pivot, parent):
    p1, p2 = Vector(p1), Vector(p2)
    d = p2 - p1
    rot = Vector((0.0, 0.0, 1.0)).rotation_difference(d).to_matrix().to_4x4()
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r, radius2=r,
                          depth=d.length, matrix=Matrix.Translation((p1 + p2) / 2) @ rot)
    for p in (p1, p2):
        bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=14, radius=r,
                                  matrix=Matrix.Translation(p))
    return finish(name, bm, material, pivot, parent)


def ball(name, center, r, material, parent):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=14, radius=r,
                              matrix=Matrix.Translation(Vector(center)))
    return finish(name, bm, material, center, parent)


# ------------------------------------------------------------------- build
root = bpy.data.objects.new(PREFIX + "Root", None)
root.empty_display_type = 'PLAIN_AXES'
root.empty_display_size = 0.3
col.objects.link(root)
PIV[root.name] = Vector((0, 0, 0))

HIP_Z, KNEE_Z, ANKLE_Z = 0.95, 0.52, 0.09
SHOULDER_Z, ELBOW_Z, WRIST_Z = 1.47, 1.15, 0.89
HIP_X, SHOULDER_X = 0.095, 0.225

pelvis = rbox("Pelvis", (0, 0, 0.99), (0.31, 0.21, 0.17), MAT_BODY, (0, 0, HIP_Z), root, 0.04)
torso = rbox("Torso", (0, 0, 1.285), (0.34, 0.21, 0.43), MAT_BODY, (0, 0, 1.07), pelvis, 0.07)
rbox("Vest", (0, 0, 1.27), (0.37, 0.24, 0.34), MAT_VEST, (0, 0, 1.07), torso, 0.07)

neck = capsule("Neck", (0, 0, 1.50), (0, 0, 1.58), 0.05, MAT_JOINT, (0, 0, 1.50), torso)
head = rbox("Head", (0, 0, 1.71), (0.22, 0.25, 0.26), MAT_BODY, (0, 0, 1.58), neck, 0.08)
rbox("Visor", (0, -0.115, 1.72), (0.17, 0.06, 0.075), MAT_VISOR, (0, 0, 1.58), head, 0.028)

for side, sx in (("L", 1.0), ("R", -1.0)):   # facing -Y: left = +X
    # arm
    sh = (sx * SHOULDER_X, 0, SHOULDER_Z)
    el = (sx * SHOULDER_X, 0, ELBOW_Z)
    wr = (sx * SHOULDER_X, 0, WRIST_Z)
    ball("Joint_Shoulder_" + side, sh, 0.06, MAT_JOINT, torso)
    upper = capsule("UpperArm_" + side, sh, el, 0.042, MAT_BODY, sh, torso)
    ball("Joint_Elbow_" + side, el, 0.05, MAT_JOINT, upper)
    fore = capsule("Forearm_" + side, el, wr, 0.038, MAT_BODY, el, upper)
    ball("Joint_Wrist_" + side, wr, 0.04, MAT_JOINT, fore)
    rbox("Hand_" + side, (sx * SHOULDER_X, 0, 0.80), (0.085, 0.05, 0.18), MAT_BODY, wr, fore, 0.025)

    # leg
    hp = (sx * HIP_X, 0, HIP_Z)
    kn = (sx * HIP_X, 0, KNEE_Z)
    an = (sx * HIP_X, 0, ANKLE_Z)
    ball("Joint_Hip_" + side, hp, 0.065, MAT_JOINT, pelvis)
    thigh = capsule("Thigh_" + side, hp, kn, 0.072, MAT_BODY, hp, pelvis)
    ball("Joint_Knee_" + side, kn, 0.06, MAT_JOINT, thigh)
    shin = capsule("Shin_" + side, kn, an, 0.055, MAT_BODY, kn, thigh)
    ball("Joint_Ankle_" + side, an, 0.05, MAT_JOINT, shin)
    rbox("Foot_" + side, (sx * HIP_X, -0.05, 0.04), (0.105, 0.27, 0.08), MAT_JOINT, an, shin, 0.03)

print("RobotSailor built: %d objects in collection '%s' (about 1.84 m tall, facing -Y)"
      % (len(col.objects), NAME))

if "--save" in sys.argv:
    bpy.ops.wm.save_mainfile()
