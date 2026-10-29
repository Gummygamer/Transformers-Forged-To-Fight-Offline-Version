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
import json, os, re, shutil, subprocess, sys, zipfile
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
SHELL_NUGET = {  # published dll name -> (package, version, member in the package)
    "ICSharpCode.SharpZipLib.dll": ("sharpziplib", "0.86.0", "lib/20/ICSharpCode.SharpZipLib.dll"),
    "BouncyCastle.Crypto.dll": ("bouncycastle", "1.8.5", "lib/BouncyCastle.Crypto.dll"),
}


def nuget_file(pkg, ver, member):
    """One file of a NuGet package, cached under TFTF_WORK/thirdparty."""
    import urllib.request
    cache = Path(os.environ["TFTF_WORK"]) / "thirdparty" / f"{pkg}.{ver}" / member
    if not cache.exists():
        url = f"https://api.nuget.org/v3-flatcontainer/{pkg}/{ver}/{pkg}.{ver}.nupkg"
        with urllib.request.urlopen(url, timeout=120) as r:
            data = r.read()
        import io
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            cache.parent.mkdir(parents=True, exist_ok=True)
            cache.write_bytes(z.read(member))
    return cache.read_bytes()


NATIVE_PLUGIN_SKIP = {"libmain.so", "libmono.so", "libunity.so"}  # supplied by Unity itself


def rsync(src, dst, *ex):
    dst.mkdir(parents=True, exist_ok=True)
    if shutil.which("rsync") is None:   # native Windows: same merge-copy, excludes match names at any depth
        shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns(*ex))
        return
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
        if os.environ.get("TFTF_SHELL") == "1":
            # No third-party library of the original APK: the open-source builds of the same libraries from NuGet
            # (SharpZipLib 0.86.0, BouncyCastle 1.8.5). The game's own Mono.Security copy is not used by its code.
            for stale in ("crypto", "Mono.Security", "ICSharpCode.SharpZipLib"):
                (A / "Plugins" / f"{stale}.dll").unlink(missing_ok=True)
            for dll, (pkg, ver, member) in SHELL_NUGET.items():
                (A / "Plugins" / dll).write_bytes(nuget_file(pkg, ver, member))
        for d in ([] if os.environ.get("TFTF_SHELL") == "1" else PLUGIN_DLLS):
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
            if m and m.group(2) not in NATIVE_PLUGIN_SKIP and os.environ.get("TFTF_SHELL") != "1":
                tgt = android / m.group(1) / m.group(2)
                tgt.parent.mkdir(parents=True, exist_ok=True)
                tgt.write_bytes(z.read(n))
    act = REPO / "tools/unity/android-plugin/com/explodingbarrel/Activity.java"
    (android / "com/explodingbarrel").mkdir(parents=True, exist_ok=True)
    shutil.copy(act, android / "com/explodingbarrel/Activity.java")
    if os.environ.get("TFTF_SHELL") == "1" and os.environ.get("TFTF_TARGET") == "windows":
        shutil.rmtree(android, ignore_errors=True)   # PC player: no Android manifest, Java or native stand-ins
    elif os.environ.get("TFTF_SHELL") == "1":
        # The shell carries no code or manifest of the original app: authored manifest and Java stand-ins instead.
        shutil.copytree(REPO / "mono310/launcher/shell/android", android, dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("native", "icon"))
        subprocess.check_call([str(REPO / "mono310/launcher/shell/android/native/build.sh"),
                               str(android / "libs" / os.environ["TFTF_ANDROID_ABI"]),
                               os.environ["TFTF_ANDROID_ABI"]])   # stand-ins for the original native libraries
    elif os.environ.get("TFTF_TARGET") != "windows":   # the Windows player has no Android manifest (and no aapt2)
        subprocess.check_call([sys.executable, str(REPO / "tools/original_manifest_to_xml.py"), str(APK),
                               str(android / "AndroidManifest.xml")])

    # Project settings: the ripped 3.1.0 settings plus authored package manifest.
    rsync(RIP / "ProjectSettings", OUT / "ProjectSettings")
    (OUT / "Packages").mkdir(parents=True, exist_ok=True)
    shutil.copy(REPO / "mono310/unity/Packages/manifest.json", OUT / "Packages/manifest.json")
    (A / "csc.rsp").unlink(missing_ok=True)
    (A / "Editor/TftfVrBuildSetup.cs").unlink(missing_ok=True)
    if os.environ.get("TFTF_VR") == "1":
        # Meta Quest: the Oculus XR plugin and XR management, both bundled with the Unity 2020.3 editor (no download).
        pm = OUT / "Packages/manifest.json"
        m = json.loads(pm.read_text())
        m["dependencies"].update({"com.unity.xr.management": "4.2.0", "com.unity.xr.oculus": "1.11.2"})
        pm.write_text(json.dumps(m, indent=2) + "\n")
        (A / "csc.rsp").write_text("-define:TFTF_VR\n")      # every predefined assembly, editor and player alike
        (A / "Editor").mkdir(exist_ok=True)
        shutil.copy(REPO / "mono310/unity/TftfVrBuildSetup.cs", A / "Editor/TftfVrBuildSetup.cs")
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
        write_build_config(shell_dst / "TftfBuildConfig.cs")
        glue_dst = A / "Scripts/Assembly-CSharp/TftfMain"   # shell code that uses the game's types (online Arena glue)
        shutil.rmtree(glue_dst, ignore_errors=True)
        shutil.copytree(REPO / "mono310/launcher/shell/TftfMain", glue_dst)
        package_server(A)


def write_build_config(path):
    """Shell only: the build's TftfBuildConfig from environment variables (the checked-in copy is empty)."""
    bootstrap = os.environ.get("TFTF_P2P_BOOTSTRAP", "").strip()
    if bootstrap and not re.fullmatch(r"[A-Za-z0-9.-]+:[0-9]{1,5}", bootstrap):
        sys.exit(f"error: TFTF_P2P_BOOTSTRAP must be host:port, not {bootstrap!r}")
    if not bootstrap:
        print("warning: TFTF_P2P_BOOTSTRAP is not set; the Arena of this build cannot go online", file=sys.stderr)
    text = path.read_text()
    text = text.replace('P2PBootstrap = "";', f'P2PBootstrap = "{bootstrap}";')
    path.write_text(text)


SERVER_FILES = ["run_local.lbl", "server_paths.lbl", "fakeserver.lbl", "gamedata.lbl", "jsonout.lbl",
                "mods_catalog.json", "sp3_timings.json"]
SERVER_DIRS = ["data", "fixtures", "responses"]


def package_server(A):
    """Shell only: authored server scripts as a streaming asset, plus the Legible interpreter as an executable
    native library (TFTF_LEGIBLE_ARMV7 or TFTF_LEGIBLE_ARM64, matching TFTF_ANDROID_ABI)."""
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
    # The ripped shaders are empty stand-ins; the game loads them by Resources path and swaps in authored ones by name.
    for p in (RIP / "Assets/Resources").rglob("*.shader*"):
        t = A / "Resources" / p.relative_to(RIP / "Assets/Resources")
        t.parent.mkdir(parents=True, exist_ok=True)
        if not t.exists():
            shutil.copy(p, t)
    shutil.copy(REPO / "mono310/launcher/shell/android/icon/icon-512.png", sa / "revival-icon.png")   # first-run screen logo
    shutil.copy(REPO / "mono310/launcher/shell/android/icon/icon-1024.png", sa / "revival-splash.png")   # in-game splash
    (A / "Icons").mkdir(exist_ok=True)
    shutil.copy(REPO / "mono310/launcher/shell/android/icon/icon-512.png", A / "Icons/revival-icon.png")   # app icon (set by the build script)
    # Type-tree database the on-phone converter needs (generated locally by launcher/convert/export_tree_db.py).
    shutil.copy(os.environ["TFTF_TREES_BIN"], sa / "trees.bin")
    if os.environ.get("TFTF_TARGET") == "windows":
        return   # legible.exe goes next to the player executable (mono310/winbuild.ps1)
    abi = os.environ["TFTF_ANDROID_ABI"]
    legible_var = "TFTF_LEGIBLE_ARM64" if abi == "arm64-v8a" else "TFTF_LEGIBLE_ARMV7"
    if not os.environ.get(legible_var):
        raise SystemExit(f"{legible_var} is required for TFTF_ANDROID_ABI={abi}")
    lib = A / "Plugins/Android/libs" / abi
    lib.mkdir(parents=True, exist_ok=True)
    shutil.copy(os.environ[legible_var], lib / "liblegible.so")


if __name__ == "__main__":
    main()
