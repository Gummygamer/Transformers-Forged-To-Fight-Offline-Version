#!/usr/bin/env python3
"""Export the type-tree database the converter needs into one binary file (read by the on-device converter).

Usage: export_tree_db.py <typetrees bundle from TypeTreeDump> <out trees.bin>
Contents: 5.3.5 trees of the engine classes (UnityPy class database) and the 5.3.5 trees of the game's scripts
(Editor-built layouts of the recompiled scripts, see tt_db.py). Layouts only; no game data.
"""
import os, struct, sys
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tt_db import engine, clone, finalize, script_tree


def w_str(b, s):
    e = s.encode("utf-8")
    b += struct.pack("<H", len(e)) + e


def w_tree(b, root):
    nodes = list(root.traverse())
    b += struct.pack("<I", len(nodes))
    for n in nodes:
        b += struct.pack("<hBB", n.m_Version, n.m_Level, n.m_TypeFlags)
        w_str(b, n.m_Type)
        w_str(b, n.m_Name)
        b += struct.pack("<iii", n.m_ByteSize, n.m_Index, n.m_MetaFlag)


def main(dump_path, out_path):
    eng = []
    for cid in range(1, 1300):
        try:
            eng.append((cid, finalize(clone(engine(cid)))))
        except Exception:
            pass
    scripts = []
    skipped = 0
    for o in UnityPy.load(dump_path).objects:
        if o.type.name == "MonoBehaviour":
            try:
                sc = o.read().m_Script.deref().read()
            except FileNotFoundError:        # script object lives in a built-in Unity file that is not part of the dump
                skipped += 1
                continue
            scripts.append(((sc.m_AssemblyName, sc.m_Namespace, sc.m_ClassName),
                            finalize(script_tree(o.assets_file.types[o.type_id].node))))
    b = bytearray(b"TFTT" + struct.pack("<II", 1, len(eng)))
    for cid, t in eng:
        b += struct.pack("<i", cid)
        w_tree(b, t)
    b += struct.pack("<I", len(scripts))
    for (a, n, c), t in scripts:
        w_str(b, a); w_str(b, n); w_str(b, c)
        w_tree(b, t)
    open(out_path, "wb").write(b)
    print("engine classes", len(eng), "scripts", len(scripts), "skipped", skipped, "bytes", len(b))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(*sys.argv[1:])
