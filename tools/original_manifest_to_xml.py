#!/usr/bin/env python3
"""Render the original 2.0.2 APK's binary manifest as a text AndroidManifest.xml
for Unity's Assets/Plugins/Android override, resolving the few resource
references to literals (Unity 2020 owns the resource table).

usage: original_manifest_to_xml.py ORIGINAL.apk OUT.xml
"""
import re, subprocess, sys
from pathlib import Path
from xml.sax.saxutils import quoteattr

REFS = {  # resource id -> literal / framework style, from `aapt2 dump resources`
    "0x01030007": "@android:style/Theme.NoTitleBar.Fullscreen",
    "0x01030010": "@android:style/Theme.Translucent.NoTitleBar",
    "0x01030011": "@android:style/Theme.Translucent.NoTitleBar.Fullscreen",
    "0x7f090013": "@android:style/Theme.Translucent.NoTitleBar",
    "0x7f060031": "Transformers",
    "0x7f070000": "9256000",
}
DROP = {"0x7f02004b", "0x7f02004c"}  # banner/icon: Unity supplies its own icon
FLAGS = {"mcc": 0x1, "mnc": 0x2, "locale": 0x4, "touchscreen": 0x8, "keyboard": 0x10,
         "keyboardHidden": 0x20, "navigation": 0x40, "orientation": 0x80, "screenLayout": 0x100,
         "uiMode": 0x200, "screenSize": 0x400, "smallestScreenSize": 0x800, "density": 0x1000,
         "layoutDirection": 0x2000, "fontScale": 0x40000000}
ENUMS = {
    "launchMode": {0: "standard", 1: "singleTop", 2: "singleTask", 3: "singleInstance"},
    "screenOrientation": {0: "landscape", 1: "portrait", 4: "sensor", 6: "sensorLandscape", 7: "sensorPortrait"},
    "installLocation": {0: "auto", 1: "internalOnly", 2: "preferExternal"},
    "protectionLevel": {0: "normal", 1: "dangerous", 2: "signature", 3: "signatureOrSystem"},
}


def literal(name, val):
    if name == "configChanges" and val.startswith("0x"):
        bits = int(val, 16)
        return "|".join(k for k, v in FLAGS.items() if bits & v)
    if name in ENUMS and re.fullmatch(r"(0x[0-9a-f]+|\d+)", val):
        return ENUMS[name][int(val, 0)]
    return val


INDENT = re.compile(r"^( *)(E|A|N): (.*)$")


def main(apk, out):
    aapt2 = sorted((Path.home() / "Android/Sdk/build-tools").iterdir())[-1] / "aapt2"
    dump = subprocess.check_output([aapt2, "dump", "xmltree", "--file", "AndroidManifest.xml", apk], text=True)
    root = None; stack = []; ns = {}
    for line in dump.splitlines():
        m = INDENT.match(line)
        if not m:
            continue
        depth, kind, rest = len(m.group(1)), m.group(2), m.group(3)
        if kind == "N":
            continue
        if kind == "E":
            name = rest.split(" ")[0]
            el = {"name": name, "attrs": [], "kids": [], "depth": depth}
            while stack and stack[-1]["depth"] >= depth:
                stack.pop()
            (stack[-1]["kids"] if stack else []).append(el) if stack else None
            if not stack:
                root = el
            stack.append(el)
        else:
            am = re.match(r"(?:(\S+):)?([\w.\-]+)\((0x[0-9a-f]+)\)=(.*?)(?: \(Raw: .*\))?$", rest)
            if not am:
                am2 = re.match(r"([\w.\-]+)=(.*?)(?: \(Raw: .*\))?$", rest)
                if am2:
                    stack[-1]["attrs"].append((False, am2.group(1), am2.group(2).strip('"')))
                continue
            n, val = am.group(2), am.group(4)
            if val.startswith("@0x"):
                rid = val[1:]
                if rid in DROP:
                    continue
                val = REFS.get(rid) or sys.exit(f"unmapped resource reference {rid}")
            elif val.startswith('"'):
                val = val[1:-1]
            stack[-1]["attrs"].append((am.group(1) is not None, n, literal(n, val)))
    def emit(e, ind=0):
        pad = "  " * ind
        attrs = "".join(f" {'android:' if a else ''}{n}={quoteattr(v)}" for a, n, v in e["attrs"]
                        if not n.startswith("platformBuild")
                        # The Gradle launcher module owns the application
                        # label/theme; the library manifest may not redefine them.
                        and not (e["name"] == "application" and n in ("label", "theme"))
                        and not (e["name"] == "manifest" and n in ("versionCode", "versionName")))
        if e["name"] == "manifest":
            attrs = ' xmlns:android="http://schemas.android.com/apk/res/android"' + attrs
        if e["name"] == "uses-sdk":  # Gradle owns min/target SDK
            return ""
        if not e["kids"]:
            return f"{pad}<{e['name']}{attrs} />\n"
        return (f"{pad}<{e['name']}{attrs}>\n" + "".join(emit(k, ind + 1) for k in e["kids"]) + f"{pad}</{e['name']}>\n")
    Path(out).write_text('<?xml version="1.0" encoding="utf-8"?>\n' + emit(root))


if __name__ == "__main__":
    main(*sys.argv[1:3])
