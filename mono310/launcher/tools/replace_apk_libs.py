#!/usr/bin/env python3
"""Replace native libraries inside a built shell APK and re-sign it (a one-minute loop for the native stand-ins).

usage: replace_apk_libs.py IN.apk OUT.apk DIR [--abi armeabi-v7a] [--keystore ~/.android/debug.keystore]
Every DIR/*.so replaces (or adds) lib/<abi>/<name> in the APK; everything else is copied unchanged.
Newly authored tool; it reads and writes only the files you name.
"""
import argparse, os, subprocess, zipfile
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("out"); ap.add_argument("libdir")
    ap.add_argument("--abi", default="armeabi-v7a")
    ap.add_argument("--keystore", default=str(Path.home() / ".android/debug.keystore"))
    a = ap.parse_args()
    new = {f"lib/{a.abi}/{p.name}": p.read_bytes() for p in sorted(Path(a.libdir).glob("*.so"))}
    out = Path(a.out)
    tmp = out.with_suffix(".unaligned.apk")
    with zipfile.ZipFile(a.src) as zi, zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zw:
        for item in zi.infolist():
            if item.filename.startswith("META-INF/") or item.filename in new:
                continue
            comp = zipfile.ZIP_STORED if item.compress_type == zipfile.ZIP_STORED else zipfile.ZIP_DEFLATED
            zw.writestr(item.filename, zi.read(item.filename), compress_type=comp)
        for name, data in new.items():
            zw.writestr(name, data, compress_type=zipfile.ZIP_DEFLATED)
    bt = sorted((Path(os.environ.get("ANDROID_HOME", Path.home() / "Android/Sdk")) / "build-tools").iterdir())[-1]
    aligned = out.with_suffix(".aligned.apk")
    subprocess.check_call([bt / "zipalign", "-f", "-p", "4", tmp, aligned])
    subprocess.check_call([bt / "apksigner", "sign", "--ks", a.keystore, "--ks-pass", "pass:android", "--out", out, aligned])
    tmp.unlink(); aligned.unlink()
    print(out)


if __name__ == "__main__":
    main()
