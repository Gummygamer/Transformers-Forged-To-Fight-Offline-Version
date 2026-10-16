#!/usr/bin/env python3
"""Spike S1: wrap one Resources TextAsset of the original 3.1.0 player data into a type-tree AssetBundle that a
Unity 2020 player can load. Uses an existing 3.1.0 bundle only as a structural template (types, header).
Usage: s1_textasset_bundle.py <3.1.0 apk extract dir> <out bundle>   (needs UnityPy)"""
import copy, sys, struct
from pathlib import Path
import UnityPy

root, out = Path(sys.argv[1]), Path(sys.argv[2])
data_dir = root / "assets/bin/Data"
tmpl = UnityPy.load(str(root / "assets/characters/moves.assetbundle"))
tf = list(tmpl.file.files.values())[0]
text_tmpl = next(o for o in tf.objects.values() if o.type.name == "TextAsset")
ab_obj = next(o for o in tf.objects.values() if o.type.name == "AssetBundle")
ab_tree = ab_obj.read_typetree()

# pick a few Resources TextAssets through the ResourceManager container of globalgamemanagers
ggm = UnityPy.load(str(data_dir / "globalgamemanagers"))
rm = next(o for o in ggm.objects if o.type.name == "ResourceManager").read()
ggm_file = list(ggm.files.values())[0]
picked = []
for path, pptr in rm.m_Container:
    ext = ggm_file.externals[pptr.m_FileID - 1] if pptr.m_FileID > 0 else None
    if ext is None:
        continue
    fname = ext.path.split("/")[-1]
    f = data_dir / fname
    if not f.exists():
        continue
    env = UnityPy.load(str(f))
    sf = list(env.files.values())[0]
    o = sf.objects.get(pptr.m_PathID)
    if o is not None and o.type.name == "TextAsset":
        picked.append((path, o.get_raw_data()))
    if len(picked) >= 3:
        break
print("picked", [(p, len(b)) for p, b in picked])

# rebuild the template serialized file with only our objects
tf.objects = {}
container = []
for i, (path, raw) in enumerate(picked, start=2):
    o = copy.copy(text_tmpl)
    o.path_id = i
    o.data = raw
    tf.objects[i] = o
    container.append((path, {"preloadIndex": i - 2, "preloadSize": 1, "asset": {"m_FileID": 0, "m_PathID": i}}))
ab_tree["m_Name"] = "tftf/spike"
ab_tree["m_AssetBundleName"] = "tftf/spike"
ab_tree["m_PreloadTable"] = [{"m_FileID": 0, "m_PathID": i} for i in range(2, 2 + len(picked))]
ab_tree["m_Container"] = container
ab = copy.copy(ab_obj)
ab.path_id = 1
tf.objects[1] = ab
ab.assets_file = tf
ab.save_typetree(ab_tree)
tmpl.file.mark_changed()
out.write_bytes(tmpl.file.save(packer="none"))
print("wrote", out, out.stat().st_size)
