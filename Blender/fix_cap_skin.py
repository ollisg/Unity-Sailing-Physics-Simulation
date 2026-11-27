"""
Skins the sailor's cap to Rigify's head deform bone (DEF-spine.006) so it survives
an FBX export with "Only Deform Bones".

- Finds the armature that has DEF-spine.006.
- Finds the cap: a mesh using a material named Cap/Hat, or an object named *cap*/*hat*.
  * Cap is its own object: clears its bone parent (keeping its rest-pose place),
    parents it to the rig object, gives all its vertices one vertex group
    DEF-spine.006 at weight 1.0 and adds an Armature modifier.
  * Cap is part of the body mesh: moves the cap faces' vertices to DEF-spine.006
    at weight 1.0 (removed from the other groups).
Saves the .blend afterwards (Blender keeps the previous save as Character.blend1).
"""
import bpy

BONE = "DEF-spine.006"
CAP_WORDS = ("cap", "hat")

if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')

rig = next((o for o in bpy.data.objects
            if o.type == 'ARMATURE' and BONE in o.data.bones), None)
if rig is None:
    raise RuntimeError("No armature with bone %s found" % BONE)

# Work in the rest pose so the cap keeps its rest position when it becomes skinned.
old_pose_pos = rig.data.pose_position
rig.data.pose_position = 'REST'
bpy.context.view_layer.update()


def is_cap_name(name):
    base = name.split(".")[0].lower()
    return any(w in base for w in CAP_WORDS)


def ensure_armature_mod(ob):
    mod = next((m for m in ob.modifiers if m.type == 'ARMATURE'), None)
    if mod is None:
        mod = ob.modifiers.new("Armature", 'ARMATURE')
        # keep the armature first in the stack
        while ob.modifiers.find(mod.name) > 0:
            bpy.context.view_layer.objects.active = ob
            with bpy.context.temp_override(object=ob):
                bpy.ops.object.modifier_move_up(modifier=mod.name)
    mod.object = rig
    mod.use_vertex_groups = True
    return mod


report = []
for ob in bpy.data.objects:
    if ob.type != 'MESH':
        continue
    me = ob.data
    cap_slots = [i for i, s in enumerate(ob.material_slots)
                 if s.material and is_cap_name(s.material.name)]
    cap_polys = [p for p in me.polygons if p.material_index in cap_slots]
    whole = is_cap_name(ob.name) or (cap_polys and len(cap_polys) == len(me.polygons))
    if not whole and not cap_polys:
        continue

    if whole:
        mw = ob.matrix_world.copy()
        ob.parent = None
        ob.parent_type = 'OBJECT'
        ob.parent_bone = ""
        ob.parent = rig
        ob.matrix_parent_inverse = rig.matrix_world.inverted()
        ob.matrix_world = mw
        ob.vertex_groups.clear()
        vg = ob.vertex_groups.new(name=BONE)
        vg.add([v.index for v in me.vertices], 1.0, 'REPLACE')
        ensure_armature_mod(ob)
        report.append("%s: whole object skinned to %s (%d verts)" % (ob.name, BONE, len(me.vertices)))
    else:
        verts = sorted({v for p in cap_polys for v in p.vertices})
        for g in ob.vertex_groups:
            if g.name != BONE:
                g.remove(verts)
        vg = ob.vertex_groups.get(BONE) or ob.vertex_groups.new(name=BONE)
        vg.add(verts, 1.0, 'REPLACE')
        ensure_armature_mod(ob)
        report.append("%s: %d cap vertices moved to %s" % (ob.name, len(verts), BONE))

rig.data.pose_position = old_pose_pos
bpy.context.view_layer.update()

if not report:
    print("CAP FIX: no cap found (no Cap/Hat material or object name)")
else:
    for line in report:
        print("CAP FIX:", line)
    bpy.ops.wm.save_mainfile()
    print("CAP FIX: saved", bpy.data.filepath)
