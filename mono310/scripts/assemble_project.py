#!/usr/bin/env python3
"""Assemble the 3.1.0 Unity 2020.3.31f1 recompile project from the operator's own APK.

Inputs (all produced locally by run.sh; nothing game-derived is in this repository):
  $RIPPED      AssetRipper export of the 3.1.0 APK (project skeleton, GUID .meta files, assets)
  $ILSPY_OUT   ILSpy decompilation of the two game assemblies
  $TFTF_310_APK the APK itself (managed plugin DLLs and native plugin libraries)
Authored inputs in this repository: unity/Recompile310Build.cs, unity/Packages/manifest.json,
unity/shaders/*.shader, tools/unity/android-plugin/*, repairs in scripts/compat310.py.
Usage: assemble_project.py
"""
import os, re, shutil, subprocess, sys, zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
RIP = Path(os.environ["RIPPED"]) / "ExportedProject"
GAME_DLL = Path(os.environ["RIPPED"]) / "AuxiliaryFiles/GameAssemblies"
OUT = Path(os.environ["PROJECT"])
ILSPY = Path(os.environ["ILSPY_OUT"])
APK = Path(os.environ["TFTF_310_APK"])
DECOMP_MAIN = ILSPY / "Assembly-CSharp"
DECOMP_FP = ILSPY / "Assembly-CSharp-firstpass"
CASEFIX = "Com_ComGoogleCaseFix"  # R1: Com/ vs com/ collide for Unity's importer
# Third-party assemblies: ILSpy-decompiled from the APK's own DLLs, overlaid on the ripped paths
# (AssetRipper's script output for these does not compile; its .meta GUIDs are kept).
THIRD_PARTY_SCRIPT_DIRS = ("NBidi", "Fabric.Core", "Facebook.Unity", "Facebook.Unity.Settings",
                           "Facebook.Unity.Android", "Facebook.Unity.IOS")
PLUGIN_DLLS = ("crypto", "Mono.Security", "ICSharpCode.SharpZipLib")
NATIVE_PLUGIN_SKIP = {"libmain.so", "libmono.so", "libunity.so"}  # supplied by Unity itself


def rsync(src, dst, *ex):
    dst.mkdir(parents=True, exist_ok=True)
    cmd = ["rsync", "-a"] + [f"--exclude={e}" for e in ex] + [str(src) + "/", str(dst) + "/"]
    subprocess.check_call(cmd)


def overlay(decomp: Path, dest: Path, dotted: bool):
    existing = {}
    for p in dest.rglob("*.cs"):
        existing[str(p.relative_to(dest)).lower()] = p.relative_to(dest)
        p.unlink()
    n_match = n_new = 0
    for p in decomp.rglob("*.cs"):
        rel = p.relative_to(decomp)
        key = rel.as_posix()
        if dotted:
            key = (rel.parent.as_posix().replace(".", "/") + "/" if rel.parent.as_posix() != "." else "") + rel.name
        tgt = existing.get(key.lower())
        if tgt is None and key.lower().startswith("com/"):
            tgt = existing.get((CASEFIX + key[3:]).lower())
        if tgt is None:
            tgt = Path(key); n_new += 1
        else:
            n_match += 1
        (dest / tgt).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(p, dest / tgt)
    print(f"{dest.name}: {n_match} matched ripped paths, {n_new} new")


def set_key(text, key, value):
    pat = re.compile(r"(?m)^(\s*%s:) .*$" % re.escape(key))
    if pat.search(text):
        return pat.sub(lambda m: f"{m.group(1)} {value}", text, count=1)
    return text.replace("PlayerSettings:\n", f"PlayerSettings:\n  {key}: {value}\n", 1)


def main():
    A = OUT / "Assets"
    for d in (A / "Scripts", A / "Plugins/Assembly-CSharp-firstpass"):
        shutil.rmtree(d, ignore_errors=True)
    for d in THIRD_PARTY_SCRIPT_DIRS:  # stale plugin DLLs from an earlier layout would duplicate types
        for suffix in (".dll", ".dll.meta"):
            (A / "Plugins" / (d + suffix)).unlink(missing_ok=True)
    ex = ["crypto", "crypto.meta", "UnityEngine.UI", "UnityEngine.UI.meta", "UnityEngine.Networking",
          "UnityEngine.Networking.meta", "System.Runtime.CompilerServices.Unsafe",
          "System.Runtime.CompilerServices.Unsafe.meta"]
    rsync(RIP / "Assets/Scripts", A / "Scripts", *ex)
    rsync(RIP / "Assets/Plugins", A / "Plugins")
    if os.environ.get("TFTF_SHELL") != "1":     # the shell has its own bootstrap scene and no game scenes
        rsync(RIP / "Assets/Scenes", A / "Scenes")

    # Managed plugin DLLs straight from the APK (no .meta: Unity generates plugin importers).
    with zipfile.ZipFile(APK) as z:
        for d in PLUGIN_DLLS:
            src = GAME_DLL / f"{d}.dll"
            if src.exists():
                shutil.copy(src, A / "Plugins" / f"{d}.dll")
            else:
                (A / "Plugins" / f"{d}.dll").write_bytes(z.read(f"assets/bin/Data/Managed/{d}.dll"))
        # Native plugin libraries of the game (breakpad, enet, play-games, ...).
        android = A / "Plugins/Android"
        shutil.rmtree(android, ignore_errors=True)
        for n in z.namelist():
            m = re.match(r"lib/(armeabi-v7a|x86)/([^/]+\.so)$", n)
            if m and m.group(2) not in NATIVE_PLUGIN_SKIP:
                tgt = android / m.group(1) / m.group(2)
                tgt.parent.mkdir(parents=True, exist_ok=True)
                tgt.write_bytes(z.read(n))
    act = REPO / "tools/unity/android-plugin/com/explodingbarrel/Activity.java"
    (android / "com/explodingbarrel").mkdir(parents=True, exist_ok=True)
    shutil.copy(act, android / "com/explodingbarrel/Activity.java")
    subprocess.check_call([sys.executable, str(REPO / "tools/original_manifest_to_xml.py"), str(APK),
                           str(android / "AndroidManifest.xml")])

    # Project settings: the ripped 3.1.0 settings plus authored package manifest.
    rsync(RIP / "ProjectSettings", OUT / "ProjectSettings")
    (OUT / "Packages").mkdir(parents=True, exist_ok=True)
    shutil.copy(REPO / "mono310/unity/Packages/manifest.json", OUT / "Packages/manifest.json")
    ps = OUT / "ProjectSettings/ProjectSettings.asset"
    t = ps.read_text()
    for k, v in (("bundleVersion", "3.1.0"), ("AndroidBundleVersionCode", "822754"),
                 ("AndroidMinSdkVersion", "19"), ("apiCompatibilityLevel", "3")):
        t = set_key(t, k, v)
    ps.write_text(t)
    dm = OUT / "ProjectSettings/DynamicsManager.asset"
    if dm.exists():  # the 3.1.0 export zeroes these; Unity 2020 needs the working values
        t = dm.read_text()
        for k, v in (("m_DefaultMaxDepenetrationVelocity", "10"), ("m_DefaultSolverIterations", "1"),
                     ("m_DefaultSolverVelocityIterations", "1"), ("m_AutoSyncTransforms", "1")):
            t = set_key(t, k, v)
        dm.write_text(t)
    q = OUT / "ProjectSettings/QualitySettings.asset"
    if q.exists():  # ripped value 0 is invalid in Unity 2020 and flattens every skinned mesh
        q.write_text(re.sub(r"(?m)^(\s*skinWeights:) 0$", r"\1 4", q.read_text()))

    (A / "Editor").mkdir(exist_ok=True)
    shutil.copy(REPO / "mono310/unity/Recompile310Build.cs", A / "Editor/Recompile310Build.cs")
    fp = A / "Plugins/Assembly-CSharp-firstpass"
    if (fp / "Com").is_dir():
        (fp / "Com").rename(fp / CASEFIX)
        m = fp / "Com.meta"
        if m.exists():
            m.rename(fp / (CASEFIX + ".meta"))
    for d in THIRD_PARTY_SCRIPT_DIRS:  # keep ripped .meta GUIDs, replace the non-compilable ripped sources
        if (ILSPY / d).is_dir() and (A / "Scripts" / d).is_dir():
            overlay(ILSPY / d, A / "Scripts" / d, dotted=False)
    overlay(DECOMP_MAIN, A / "Scripts/Assembly-CSharp", dotted=True)
    overlay(DECOMP_FP, fp, dotted=True)
    sys.path.insert(0, str(HERE)); import compat310; compat310.apply(A)
    for p in list(A.rglob("-PrivateImplementationDetails-*")):
        p.unlink()
    if os.environ.get("TFTF_SHELL") == "1":
        shell_src = REPO / "mono310/launcher/shell/Tftf"
        shell_dst = fp / "Tftf"            # first-pass assembly: the game's first-pass code calls TftfResources
        shutil.rmtree(shell_dst, ignore_errors=True)
        shutil.copytree(shell_src, shell_dst)
        package_server(A)


SERVER_FILES = ["run_local.lbl", "server_paths.lbl", "fakeserver.lbl", "gamedata.lbl", "jsonout.lbl",
                "mods_catalog.json", "sp3_timings.json"]
SERVER_DIRS = ["data", "fixtures", "responses"]


def package_server(A):
    """Shell only: authored server scripts as a streaming asset, plus the Legible interpreter as an executable
    native library (TFTF_LEGIBLE_ARMV7 = a release build of Legible for armv7-linux-androideabi)."""
    import zipfile
    sa = A / "StreamingAssets"
    sa.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(sa / "tftf_server.zip", "w", zipfile.ZIP_DEFLATED) as z:
        for f in SERVER_FILES:
            z.write(REPO / "Server" / f, f"Server/{f}")
        for d in SERVER_DIRS:
            for p in sorted((REPO / "Server" / d).rglob("*")):
                if p.is_file():
                    z.write(p, f"Server/{d}/{p.relative_to(REPO / 'Server' / d)}")
    lib = A / "Plugins/Android/libs/armeabi-v7a"
    lib.mkdir(parents=True, exist_ok=True)
    shutil.copy(os.environ["TFTF_LEGIBLE_ARMV7"], lib / "liblegible.so")


if __name__ == "__main__":
    main()
