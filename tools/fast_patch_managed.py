#!/usr/bin/env python3
"""Swap freshly compiled managed DLLs into a finished 2.0.2 APK (no player build).

usage: fast_patch_managed.py IN.apk SCRIPTS_DIR OUT.apk [--keystore ...]
SCRIPTS_DIR is the TFTF_202_SCRIPTS_OUT folder written by
Recompile202Build.CompileScriptsOnly (a Unity batch run of a few seconds).
"""
import argparse, os, subprocess, zipfile
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("apk"); ap.add_argument("scripts"); ap.add_argument("out")
    ap.add_argument("--assets-dir", help="folder whose files replace same-named APK entries under assets/ (the StreamingAssets files)")
    ap.add_argument("--keystore", default=str(Path.home() / ".android/debug.keystore"))
    a = ap.parse_args()
    dlls = {f"assets/bin/Data/Managed/{p.name}": p.read_bytes()
            for p in Path(a.scripts).glob("*.dll") if p.name.startswith("Assembly-CSharp")}
    if not dlls:
        raise SystemExit("no Assembly-CSharp*.dll in " + a.scripts)
    out = Path(a.out); tmp = out.with_suffix(".unaligned.apk"); al = out.with_suffix(".aligned.apk")
    with zipfile.ZipFile(a.apk) as zi, zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zw:
        for info in zi.infolist():
            if info.filename.startswith("META-INF/"):
                continue
            data = dlls.pop(info.filename, None)
            if data is None and a.assets_dir and info.filename.startswith("assets/") and info.filename.count("/") == 1 \
                    and (Path(a.assets_dir) / info.filename[7:]).is_file():
                data = (Path(a.assets_dir) / info.filename[7:]).read_bytes()
            if data is None:
                data = zi.read(info.filename)
            zw.writestr(info.filename, data, compress_type=(
                zipfile.ZIP_STORED if info.compress_type == zipfile.ZIP_STORED else zipfile.ZIP_DEFLATED))
    if dlls:
        raise SystemExit("not present in APK: " + ", ".join(dlls))
    bt = sorted((Path(os.environ.get("ANDROID_HOME", Path.home() / "Android/Sdk")) / "build-tools").iterdir())[-1]
    subprocess.check_call([bt / "zipalign", "-f", "-p", "4", tmp, al])
    subprocess.check_call([bt / "apksigner", "sign", "--ks", a.keystore, "--ks-pass", "pass:android",
                           "--out", out, al])
    tmp.unlink(); al.unlink(); print(out)


if __name__ == "__main__":
    main()
