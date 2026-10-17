#!/usr/bin/env python3
"""List every script class that the 3.1.0 player data refers to, as the input of the Editor tool TypeTreeDump.

usage: list_scripts.py <extracted assets/bin/Data folder> <out tt_scripts.json>
Output: json [["Assembly-CSharp.dll", "Namespace", "Class"], ...] (sorted, unique). Class names only; no game data.
needs: pip install UnityPy
"""
import json, os, sys
import UnityPy


def main(data_dir, out_path):
    found = set()
    for name in sorted(os.listdir(data_dir)):
        path = os.path.join(data_dir, name)
        if not os.path.isfile(path) or ".resource" in name or name.endswith((".dll", ".mdb", ".json", ".txt")):
            continue
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for sf in env.files.values():
            for obj in getattr(sf, "objects", {}).values():
                if obj.type.name != "MonoScript":
                    continue
                t = obj.read_typetree()
                found.add((t["m_AssemblyName"], t["m_Namespace"], t["m_ClassName"]))
    rows = [list(r) for r in sorted(found)]
    with open(out_path, "w") as f:
        json.dump(rows, f)
    print("%d script classes -> %s" % (len(rows), out_path))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
