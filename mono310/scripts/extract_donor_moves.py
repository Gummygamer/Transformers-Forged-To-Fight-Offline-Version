#!/usr/bin/env python3
"""Extract the move-data TextAssets of the operator's 9.2.0 APK into a Unity Resources folder.

The 9.2.0 character prefabs (Optimus Primal etc.) reference move files that the 3.1.0 'moves' bundle does
not contain, so their MoveInfo._asset comes out null and the fighter has no hit events. The client falls back
to Resources/movedata92/<move name> (see repair P38 in compat310.py). Generated locally; never committed.
Usage: extract_donor_moves.py <9.2.0 apk> <Assets/Resources/movedata92 dir>   (needs: pip install UnityPy)
"""
import sys, zipfile
from pathlib import Path

BUNDLE = "assets/assetpack/characters/moves.assetbundle"

def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    apk, out = sys.argv[1], Path(sys.argv[2])
    try:
        import UnityPy
    except ImportError:
        sys.exit("extract_donor_moves: UnityPy is missing (pip install UnityPy)")
    with zipfile.ZipFile(apk) as z:
        data = z.read(BUNDLE)
    out.mkdir(parents=True, exist_ok=True)
    n = 0
    for obj in UnityPy.load(data).objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        script = ta.m_Script
        body = script.encode("utf-8", "surrogateescape") if isinstance(script, str) else bytes(script)
        (out / (ta.m_Name + ".txt")).write_bytes(body)
        n += 1
    print("extract_donor_moves: %d move files -> %s" % (n, out))

if __name__ == "__main__":
    main()
