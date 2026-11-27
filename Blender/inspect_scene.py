import json
import bpy

out = {"file": bpy.data.filepath, "collections": {}, "objects": [], "materials": []}
for c in bpy.data.collections:
    out["collections"][c.name] = [o.name for o in c.objects][:80]
for o in bpy.data.objects:
    if o.type not in {'MESH', 'ARMATURE'}:
        continue
    if o.name.startswith(("AS_", "RS_")):
        continue
    d = {"name": o.name, "type": o.type, "parent": o.parent.name if o.parent else None,
         "parent_type": o.parent_type, "parent_bone": o.parent_bone,
         "hidden": o.hide_get() if bpy.context.view_layer.objects.get(o.name) else None}
    if o.type == 'MESH':
        me = o.data
        d["verts"] = len(me.vertices)
        d["uv_layers"] = [u.name for u in me.uv_layers]
        d["slots"] = [(s.material.name if s.material else None) for s in o.material_slots]
        d["modifiers"] = [(m.name, m.type) for m in o.modifiers]
        d["dims"] = [round(x, 3) for x in o.dimensions]
    out["objects"].append(d)
for m in bpy.data.materials:
    nodes = [n.type for n in m.node_tree.nodes] if m.node_tree else []
    base = None
    if m.node_tree:
        b = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
        if b:
            base = [round(x, 3) for x in b.inputs["Base Color"].default_value]
    out["materials"].append({"name": m.name, "users": m.users, "nodes": nodes, "base": base})
path = bpy.path.abspath("//scene_inspect.json")
with open(path, "w") as f:
    json.dump(out, f, indent=1)
print("inspect written", path)
