#!/usr/bin/env python3
"""Convert the player data of the original 3.1.0 APK (Unity 5.3.5, no type trees) into one AssetBundle that a Unity
2020 player can load. Prototype of the on-device converter.

Usage: convert_player_data.py <apk Data dir> <typetrees bundle (TypeTreeDump)> <3.1.0 moves.assetbundle> <out bundle>
Needs UnityPy. The moves bundle is only a structural template for the bundle's AssetBundle object.
"""
import copy, hashlib, json, os, sys
from pathlib import Path
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tt_db import script_tree, engine, clone, finalize
from unityfs import write_unityfs

SHADER_MARK = "TFTF_SHADER="
STREAM_TEXTURES_OVER = 64 * 1024            # larger textures go to the streamed .resource node, like genuine bundles
DROP = ("Shader", "CGProgram")


def cab(name):
    return "CAB-" + hashlib.md5(name.encode()).hexdigest()


def main(data_dir, dump_path, tmpl_path, out_path):
    data = Path(data_dir)
    sizes = {}

    def log(*a):
        print(*a, flush=True)

    # ---- script layouts from the Editor dump
    dump = UnityPy.load(dump_path)
    db = {}
    for o in dump.objects:
        if o.type.name == "MonoBehaviour":
            m = o.read()
            sc = m.m_Script.deref().read()
            db[(sc.m_AssemblyName, sc.m_Namespace, sc.m_ClassName)] = finalize(script_tree(o.assets_file.types[o.type_id].node))
    base_only = finalize(clone(engine(114)))
    log("script layouts", len(db))

    # ---- resources path map
    ggm = UnityPy.load(str(data / "globalgamemanagers"))
    ggm_sf = list(ggm.files.values())[0]
    rm = next(o for o in ggm.objects if o.type.name == "ResourceManager").read()
    container = [(p, ggm_sf.externals[ptr.m_FileID - 1].path.split("/")[-1], ptr.m_PathID) for p, ptr in rm.m_Container
                 if ptr.m_FileID > 0]
    log("resources", len(container))

    # ---- files to convert
    only = os.environ.get("ONLY")                  # debugging: convert just these Resources paths
    if only:
        container = [c for c in container if c[0] in only.split(",")]
        names = sorted({c[1] for c in container} | {"globalgamemanagers.assets"})
    else:
        names = sorted({c[1] for c in container} | {"globalgamemanagers.assets", "sharedassets0.assets"})
    main_node = cab("main")                        # an archive is mounted under its main CAB name
    node_of = {n: cab(n) for n in names}
    loaded = {n: list(UnityPy.load(str(data / n)).files.values())[0] for n in names}
    log("files", len(names))

    script_cache = {}

    def script_key(sf, local_fid, path_id):
        owner = sf if local_fid == 0 else loaded[sf.externals[local_fid - 1].path.split("/")[-1]]
        k = (id(owner), path_id)
        if k not in script_cache:
            t = owner.objects[path_id].read_typetree()
            script_cache[k] = (t["m_AssemblyName"], t["m_Namespace"], t["m_ClassName"])
        return script_cache[k]

    # ---- shader names (for materials)
    shader_name = {}
    for n, sf in loaded.items():
        for pid, o in sf.objects.items():
            if o.type.name == "Shader":
                try:
                    shader_name[(n, pid)] = o.read_typetree()["m_ParsedForm"]["m_Name"]
                except Exception:
                    shader_name[(n, pid)] = o.read_typetree().get("m_Name", "")

    resource_blob = b"".join((data / ("sharedassets0.resource.split%d" % i)).read_bytes() for i in range(32)
                             if (data / ("sharedassets0.resource.split%d" % i)).exists())
    resource_blob = bytearray(resource_blob)       # audio stays at its offsets; big textures are appended below
    res_node = cab("sharedassets0.resource") + ".resource"
    res_path = "archive:/%s/%s" % (main_node, res_node)

    out_nodes = []
    stats = {"missing_script": 0, "dropped": 0, "materials": 0, "audio": 0, "streamed_textures": 0}
    for n in names:
        sf = loaded[n]
        # types
        for t in sf.types:
            if t.class_id < 0:
                if 0 <= t.script_type_index < len(sf.script_types):
                    st = sf.script_types[t.script_type_index]
                    key = script_key(sf, st.local_serialized_file_index, st.local_identifier_in_file)
                else:
                    key = None            # a MonoBehaviour whose script is gone: only the base fields are meaningful
                if key in db:
                    t.node = db[key]
                else:
                    t.node = base_only
                    stats["missing_script"] += 1
            else:
                t.node = finalize(clone(engine(t.class_id)))
        sf._enable_type_tree = True
        # shaders out, materials get their shader name as a keyword, audio gets the archive resource path
        for pid in [p for p, o in sf.objects.items() if o.type.name in DROP]:
            del sf.objects[pid]
            stats["dropped"] += 1
        for pid, o in sf.objects.items():
            if o.type.name == "Material":
                t = o.read_typetree()
                sp = t["m_Shader"]
                ext = loaded.get(sf.externals[sp["m_FileID"] - 1].path.split("/")[-1]) if sp["m_FileID"] else sf
                en = n if sp["m_FileID"] == 0 else sf.externals[sp["m_FileID"] - 1].path.split("/")[-1]
                nm = shader_name.get((en, sp["m_PathID"]))
                if nm:
                    kw = (t.get("m_ShaderKeywords") or "").strip()
                    t["m_ShaderKeywords"] = (kw + " " + SHADER_MARK + nm).strip()
                    o.save_typetree(t)
                    stats["materials"] += 1
            elif o.type.name == "Texture2D":
                t = o.read_typetree()
                img = t.get("image data")
                if img and len(img) >= STREAM_TEXTURES_OVER and not t["m_StreamData"]["path"]:
                    t["m_StreamData"] = {"offset": len(resource_blob), "size": len(img), "path": res_path}
                    resource_blob += img
                    t["image data"] = b""
                    o.save_typetree(t)
                    stats["streamed_textures"] += 1
            elif o.type.name == "AudioClip":
                t = o.read_typetree()
                if t["m_Resource"]["m_Source"].endswith("sharedassets0.resource"):
                    t["m_Resource"]["m_Source"] = res_path
                    o.save_typetree(t)
                    stats["audio"] += 1
        # externals -> archive paths
        for e in sf.externals:
            base = e.path.split("/")[-1]
            if base in node_of:
                e.path = ("archive:/%s/%s" % (main_node, node_of[base])).lower()
                e.guid = b"\0" * 16
                e.type = 0
            elif e.path.lower().endswith("unity_builtin_extra") or e.path == "0000000000000000f000000000000000":
                e.path, e.guid, e.type = "resources/unity_builtin_extra", bytes.fromhex("00000000000000000f00000000000000"), 0
            elif "unity default resources" in e.path.lower():
                e.path, e.guid, e.type = "library/unity default resources", bytes.fromhex("00000000000000000e00000000000000"), 0
        out_nodes.append((node_of[n], sf.save(), 4))
        log("converted", n, len(out_nodes))

    # ---- main node: AssetBundle object with the Resources container
    tmpl = UnityPy.load(tmpl_path)
    tf = list(tmpl.file.files.values())[0]
    ab_obj = next(o for o in tf.objects.values() if o.type.name == "AssetBundle")
    tree = ab_obj.read_typetree()
    ab_type = next(t for t in tf.types if t.class_id == 142)
    tf.types = [ab_type]
    ext_ids = {n: i + 1 for i, n in enumerate(names)}
    tree["m_Name"] = tree["m_AssetBundleName"] = "tftf/resources"
    tree["m_PreloadTable"] = []
    tree["m_Container"] = [(p.lower(), {"preloadIndex": 0, "preloadSize": 0,
                                         "asset": {"m_FileID": ext_ids[fn], "m_PathID": pid}}) for p, fn, pid in container]
    tf.objects = {}
    ab = copy.copy(ab_obj)
    ab.path_id = 1
    ab.assets_file = tf
    tf.objects[1] = ab
    ab.save_typetree(tree)
    first = next((s.externals[0] for s in loaded.values() if s.externals),
                 list(UnityPy.load(str(data / "sharedassets0.assets")).files.values())[0].externals[0])
    tf.externals = []
    for n in names:
        fi = copy.copy(first)                      # a real FileIdentifier; only its fields change
        fi.temp_empty, fi.guid, fi.type = "", b"\0" * 16, 0
        fi.path = ("archive:/%s/%s" % (main_node, node_of[n])).lower()
        tf.externals.append(fi)
    nodes = [(main_node, tf.save(), 4)] + out_nodes + [(res_node, bytes(resource_blob), 0)]
    Path(out_path).write_bytes(write_unityfs(nodes))
    log("wrote", out_path, Path(out_path).stat().st_size, stats)


if __name__ == "__main__":
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    main(*sys.argv[1:])
