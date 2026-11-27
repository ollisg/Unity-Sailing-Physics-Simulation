"""
Builds a super-low-detail anime-style sailor (AnimeSailor collection).

Run inside Blender (4.2+ / 5.x): open Character.blend -> Scripting workspace ->
Text > Open this file -> Run Script (Alt+P), or in the Python console:
  exec(open(r"<path>/build_anime_sailor.py").read())
Safe to re-run: it replaces only the AnimeSailor collection.

Design notes
- Z up, units in metres, character faces -Y (numpad 1 front view).
- Feet at Z = 0, top of head 1.80 m, hair to about 1.86 m (ILCA 7 sailor size).
- Real joint heights and limb lengths; the anime look comes from a slightly big
  head, big flat eyes, chunky hair spikes, flat colours and very low polygon
  counts (8-sided limbs). Outlines are best done later with a toon shader in Unity.
- Every body part is a separate rigid object with its origin on its joint,
  parented Root > Pelvis > Torso > ... (same hierarchy as RobotSailor), so it
  can be posed right away and later merged/skinned or driven by Unity transforms.
- Placed at X = ROOT_X next to RobotSailor for comparison; move AS_Root to 0 later.

Optional headless use (builds and saves the .blend):
  Blender -b Character.blend --python build_anime_sailor.py -- --save
"""
import math
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

NAME = "AnimeSailor"
PREFIX = "AS_"
ROOT_X = 0.8      # side-by-side offset from RobotSailor
SEG = 8           # sides per limb ring: keep low for the low-poly look
PIV = {}          # object name -> pivot (world position at rest, before ROOT_X)

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
def make_mat(name, rgba, rough=0.85):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    if m.node_tree is None:
        m.use_nodes = True
    bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = rgba
        bsdf.inputs["Roughness"].default_value = rough
        for key in ("Specular IOR Level", "Specular"):
            if key in bsdf.inputs:
                bsdf.inputs[key].default_value = 0.2
                break
    m.diffuse_color = rgba
    return m


SKIN = make_mat("AS_Skin", (1.00, 0.78, 0.64, 1.0))
HAIR = make_mat("AS_Hair", (0.10, 0.12, 0.24, 1.0), rough=0.6)
EYES = make_mat("AS_Eyes", (0.05, 0.07, 0.18, 1.0), rough=0.3)
SHIRT = make_mat("AS_Shirt", (0.92, 0.93, 0.95, 1.0))
SHORTS = make_mat("AS_Shorts", (0.08, 0.12, 0.28, 1.0))
VEST = make_mat("AS_Vest", (1.00, 0.30, 0.05, 1.0))
SHOES = make_mat("AS_Shoes", (0.12, 0.12, 0.14, 1.0))


# ----------------------------------------------------------------- helpers
def finish(name, bm, material, pivot, parent):
    """Turn a world-space bmesh into an object whose origin sits on `pivot`."""
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
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
    if parent is not None:
        ob.parent = parent
        ob.matrix_parent_inverse = Matrix.Translation(-PIV[parent.name])
    PIV[ob.name] = pivot
    return ob


def loft(name, rings, material, pivot, parent, axis='Z', seg=SEG):
    """Capped tube through elliptical rings (cx, cy, cz, ra, rb).
    axis 'Z': rings lie in the XY plane; axis 'Y': rings lie in the XZ plane."""
    bm = bmesh.new()
    first = prev = None
    for cx, cy, cz, ra, rb in rings:
        ring = []
        for i in range(seg):
            a = 2.0 * math.pi * i / seg
            ca, sa = math.cos(a), math.sin(a)
            co = (cx + ra * ca, cy + rb * sa, cz) if axis == 'Z' else (cx + ra * ca, cy, cz + rb * sa)
            ring.append(bm.verts.new(co))
        if prev is None:
            first = ring
        else:
            for i in range(seg):
                j = (i + 1) % seg
                bm.faces.new((prev[i], prev[j], ring[j], ring[i]))
        prev = ring
    bm.faces.new(first)
    bm.faces.new(list(reversed(prev)))
    return finish(name, bm, material, pivot, parent)


def blob(name, center, radii, material, pivot, parent, u=8, v=6):
    bm = bmesh.new()
    m = Matrix.Translation(Vector(center)) @ Matrix.Diagonal((radii[0], radii[1], radii[2], 1.0))
    bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0, matrix=m)
    return finish(name, bm, material, pivot, parent)


def spike(name, base, tip, r, material, pivot, parent, seg=5):
    base, tip = Vector(base), Vector(tip)
    d = tip - base
    rot = Vector((0.0, 0.0, 1.0)).rotation_difference(d).to_matrix().to_4x4()
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=0.003,
                          depth=d.length, matrix=Matrix.Translation((base + tip) / 2) @ rot)
    return finish(name, bm, material, pivot, parent)


# ------------------------------------------------------------------- build
root = bpy.data.objects.new(PREFIX + "Root", None)
root.empty_display_type = 'PLAIN_AXES'
root.empty_display_size = 0.3
col.objects.link(root)
PIV[root.name] = Vector((0, 0, 0))

HIP_Z, KNEE_Z, ANKLE_Z = 0.95, 0.52, 0.08
SHOULDER_Z, ELBOW_Z, WRIST_Z = 1.43, 1.17, 0.93
HIP_X, ARM_X = 0.085, 0.205

# body
pelvis = loft("Pelvis", [(0, 0, 0.84, 0.140, 0.095),
                         (0, 0, 0.95, 0.155, 0.100),
                         (0, 0, 1.05, 0.135, 0.090)], SHORTS, (0, 0, HIP_Z), root)
torso = loft("Torso", [(0, 0, 1.02, 0.130, 0.085),
                       (0, 0, 1.15, 0.135, 0.090),
                       (0, 0, 1.30, 0.160, 0.100),
                       (0, 0, 1.42, 0.170, 0.095),
                       (0, 0, 1.47, 0.100, 0.070)], SHIRT, (0, 0, 1.04), pelvis)
loft("Vest", [(0, 0, 1.12, 0.150, 0.105),
              (0, 0, 1.30, 0.176, 0.118),
              (0, 0, 1.40, 0.180, 0.112),
              (0, 0, 1.45, 0.140, 0.090)], VEST, (0, 0, 1.04), torso)

# head
neck = loft("Neck", [(0, 0, 1.44, 0.040, 0.040),
                     (0, 0, 1.57, 0.036, 0.036)], SKIN, (0, 0, 1.46), torso)
HEAD_P = (0, 0, 1.55)
head = loft("Head", [(0, -0.030, 1.525, 0.030, 0.025),   # pointy anime chin
                     (0, -0.012, 1.580, 0.078, 0.080),
                     (0, 0.000, 1.640, 0.100, 0.105),
                     (0, 0.000, 1.710, 0.105, 0.110),
                     (0, 0.000, 1.770, 0.090, 0.095),
                     (0, 0.000, 1.800, 0.050, 0.055)], SKIN, HEAD_P, neck)
for side, sx in (("L", 1.0), ("R", -1.0)):
    blob("Eye_" + side, (sx * 0.040, -0.100, 1.645), (0.020, 0.008, 0.026), EYES, HEAD_P, head)

blob("Hair", (0, 0.010, 1.720), (0.115, 0.122, 0.110), HAIR, HEAD_P, head, u=10, v=7)
hair_spikes = [
    # bangs (stop above the eyes)
    ((-0.060, -0.095, 1.750), (-0.075, -0.112, 1.685), 0.032),
    ((-0.020, -0.105, 1.760), (-0.022, -0.120, 1.680), 0.034),
    ((0.025, -0.105, 1.760), (0.035, -0.120, 1.683), 0.034),
    ((0.065, -0.095, 1.750), (0.080, -0.110, 1.690), 0.030),
    # side locks
    ((0.100, -0.020, 1.710), (0.112, -0.030, 1.575), 0.034),
    ((-0.100, -0.020, 1.710), (-0.112, -0.030, 1.575), 0.034),
    # back
    ((0.000, 0.100, 1.720), (0.000, 0.150, 1.580), 0.045),
    ((0.070, 0.085, 1.730), (0.100, 0.130, 1.600), 0.040),
    ((-0.070, 0.085, 1.730), (-0.100, 0.130, 1.600), 0.040),
    # ahoge (the single anime cowlick)
    ((0.010, -0.010, 1.810), (0.045, -0.040, 1.870), 0.015),
]
for i, (b, t, r) in enumerate(hair_spikes):
    spike("HairSpike_%02d" % i, b, t, r, HAIR, HEAD_P, head)

# limbs
for side, sx in (("L", 1.0), ("R", -1.0)):   # facing -Y: left = +X
    ax = sx * ARM_X
    upper = loft("UpperArm_" + side, [(ax, 0, 1.450, 0.050, 0.050),
                                      (ax, 0, 1.300, 0.043, 0.043),
                                      (ax, 0, 1.160, 0.038, 0.038)],
                 SHIRT, (ax, 0, SHOULDER_Z), torso)
    fore = loft("Forearm_" + side, [(ax, 0, 1.190, 0.037, 0.037),
                                    (ax, 0, 1.050, 0.035, 0.035),
                                    (ax, 0, 0.925, 0.029, 0.029)],
                SHIRT, (ax, 0, ELBOW_Z), upper)
    loft("Hand_" + side, [(ax, 0, 0.940, 0.028, 0.022),
                          (ax, 0, 0.880, 0.036, 0.020),
                          (ax, 0, 0.820, 0.030, 0.017),
                          (ax, 0, 0.790, 0.012, 0.010)],
         SKIN, (ax, 0, WRIST_Z), fore)

    lx = sx * HIP_X
    thigh = loft("Thigh_" + side, [(lx, 0, 0.970, 0.075, 0.075),
                                   (lx, 0, 0.750, 0.060, 0.062),
                                   (lx, 0, 0.535, 0.046, 0.048)],
                 SHORTS, (lx, 0, HIP_Z), pelvis)
    shin = loft("Shin_" + side, [(lx, 0, 0.560, 0.045, 0.046),
                                 (lx, 0, 0.300, 0.040, 0.042),
                                 (lx, 0, 0.090, 0.032, 0.032)],
                SKIN, (lx, 0, KNEE_Z), thigh)
    loft("Foot_" + side, [(lx, 0.060, 0.050, 0.045, 0.045),
                          (lx, -0.040, 0.045, 0.050, 0.045),
                          (lx, -0.150, 0.035, 0.040, 0.030)],
         SHOES, (lx, 0, ANKLE_Z), shin, axis='Y')

root.location.x = ROOT_X

print("AnimeSailor built: %d objects in collection '%s' (head 1.80 m, facing -Y, at X=%.1f)"
      % (len(col.objects), NAME, ROOT_X))

if "--save" in sys.argv:
    bpy.ops.wm.save_mainfile()
