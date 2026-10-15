#!/usr/bin/env python3
"""Finish a Unity-2020 build of the recovered 2.0.2 client.

Unity emits a thin classes.dex (its own player classes only). The game's Java
plugins (com.explodingbarrel.*, Play Games glue, ...) live in the original APK's
classes.dex; without them BugReport/Download JNI lookups throw
ClassNotFoundException at boot. (The manifest comes from
tools/original_manifest_to_xml.py via Unity's Plugins/Android override.) Add the original dex as classes2.dex, optionally
copy the base APK's streaming assets, rewrite the baked endpointconfig, then zipalign and sign.

usage: finalize_recompiled_202_apk.py BUILT.apk ORIGINAL.apk OUT.apk
           [--endpoint http://127.0.0.1:8080] [--keystore ~/.android/debug.keystore]
"""
import argparse, os, re, subprocess, sys, tempfile, zipfile
from pathlib import Path

ENDPOINT_RE = re.compile(rb'\{"Default":\{"Prod":"[^"]*"\}\}')


def build_tools():
    root = Path(os.environ.get("ANDROID_HOME", Path.home() / "Android/Sdk")) / "build-tools"
    return sorted(root.iterdir())[-1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("built"); ap.add_argument("original"); ap.add_argument("out")
    ap.add_argument("--endpoint")
    ap.add_argument("--keystore", default=str(Path.home() / ".android/debug.keystore"))
    a = ap.parse_args()
    out = Path(a.out)
    tmp = out.with_suffix(".unaligned.apk")
    with zipfile.ZipFile(a.built) as zi, zipfile.ZipFile(a.original) as zo, \
            zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zw:
        patched = 0
        for item in zi.infolist():
            if item.filename.startswith("META-INF/"):
                continue
            data = zi.read(item.filename)
            if a.endpoint and item.filename.startswith("assets/bin/Data/") and b'"Default"' in data:
                # Serialized TextAsset carries a length prefix: keep the size
                # identical by padding with JSON whitespace.
                m = ENDPOINT_RE.search(data)
                if m:
                    new = ('{"Default":{"Prod":"%s"}}' % a.endpoint).encode()
                    if len(new) > len(m.group(0)):
                        sys.exit("endpoint longer than the baked endpointconfig")
                    data = data.replace(m.group(0), new.ljust(len(m.group(0))), 1)
                    patched += 1
            comp = zipfile.ZIP_STORED if item.compress_type == zipfile.ZIP_STORED else zipfile.ZIP_DEFLATED
            zw.writestr(item.filename, data, compress_type=comp)
        # Streaming assets (packs.txt, per-pack toc.txt and bundles) are game
        # content outside Unity's player data; carry them over from the base APK.
        built_names = set(zi.namelist())
        for info in zo.infolist():
            n = info.filename
            if (n.startswith("assets/") and not n.startswith("assets/bin/")
                    and not n.endswith("/") and n not in built_names):
                zw.writestr(n, zo.read(n), compress_type=(
                    zipfile.ZIP_STORED if info.compress_type == zipfile.ZIP_STORED else zipfile.ZIP_DEFLATED))
        names = set(zi.namelist())
        if "classes2.dex" not in names:
            zw.writestr("classes2.dex", zo.read("classes.dex"))
        if a.endpoint and not patched:
            sys.exit("endpointconfig asset not found in built APK")
    bt = build_tools()
    aligned = out.with_suffix(".aligned.apk")
    subprocess.check_call([bt / "zipalign", "-f", "-p", "4", tmp, aligned])
    subprocess.check_call([bt / "apksigner", "sign", "--ks", a.keystore,
                           "--ks-pass", "pass:android", "--out", out, aligned])
    tmp.unlink(); aligned.unlink()
    print(out)


if __name__ == "__main__":
    main()
