import os
import sys
import subprocess
import shutil
from pathlib import Path

def main():
    print("=== Step 1 Build Pipeline: Black UI Text & Shader Fixes ===")
    
    # Paths
    upstream_dir = Path(r"e:\Agent\TFTF-upstream")
    tftf310_dir = Path(r"E:\Agent\TFTF310Upstream")
    unity_proj_dir = Path(r"E:\Agent\TFTF-blender\toolchain\unity_build_project")
    unity_exe = Path(r"E:\Agent\TFTF-blender\toolchain\Unity_2020.3.31f1\Editor\Unity.exe")
    unity_data = Path(r"E:\Agent\TFTF-blender\toolchain\Unity_2020.3.31f1\Editor\Data")
    mcs_bat = unity_data / "MonoBleedingEdge/bin/mcs.bat"
    mono_lib = unity_data / "MonoBleedingEdge/lib/mono/4.5"
    managed_engine = unity_data / "Managed/UnityEngine"
    ildasm_exe = Path(r"C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7 Tools\ildasm.exe")
    ilasm_exe = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ilasm.exe")

    # 1. Update Shaders in Unity Project
    print("\n--- [1/5] Updating Shader sources in Unity project ---")
    update_shaders_script = upstream_dir / "update_and_build_shaders.py"
    subprocess.run([sys.executable, str(update_shaders_script)], check=True)

    # 2. Build shaders.assetbundle in Unity Headless
    print("\n--- [2/5] Building shaders.assetbundle with Unity headless ---")
    log_path = unity_proj_dir / "build_shaders.log"
    cmd_unity = [
        str(unity_exe),
        "-quit",
        "-batchmode",
        "-projectPath", str(unity_proj_dir),
        "-executeMethod", "BuildShaderBundle.Execute",
        "-logFile", str(log_path)
    ]
    res_unity = subprocess.run(cmd_unity)
    print(f"Unity return code: {res_unity.returncode}")
    bundle_path = unity_proj_dir / "AssetBundles" / "shaders.assetbundle"
    if not bundle_path.exists():
        print(f"ERROR: {bundle_path} was not created!")
        if log_path.exists():
            print(log_path.read_text(encoding="utf-8", errors="replace")[-2000:])
        sys.exit(1)
    print(f"Built shaders.assetbundle: {bundle_path.stat().st_size} bytes")

    # 3. Compile updated C# helpers and disassemble to IL
    print("\n--- [3/5] Compiling and disassembling ShadersHelperFull and TestFixMaterials ---")
    refs = [
        f"-r:{mono_lib / 'mscorlib.dll'}",
        f"-r:{mono_lib / 'System.dll'}",
        f"-r:{mono_lib / 'System.Core.dll'}",
        f"-r:{mono_lib / 'System.IO.Compression.dll'}",
        f"-r:{managed_engine / 'UnityEngine.CoreModule.dll'}",
        f"-r:{managed_engine / 'UnityEngine.AssetBundleModule.dll'}",
    ]

    # Sync C# source files from upstream repo to TFTF310Upstream scratch
    src_sh = upstream_dir / "tools" / "firstpass_patch" / "ShadersHelperFull.cs"
    src_tf = upstream_dir / "tools" / "firstpass_patch" / "TestFixMaterials.cs"
    src_patch_py = upstream_dir / "tools" / "firstpass_patch" / "patch_firstpass_il.py"
    sh_cs = tftf310_dir / "tools" / "scratch" / "ShadersHelperFull.cs"
    tf_cs = tftf310_dir / "tools" / "scratch" / "TestFixMaterials.cs"
    dst_patch_py = tftf310_dir / "tools" / "patch_firstpass_il.py"
    shutil.copy2(src_sh, sh_cs)
    shutil.copy2(src_tf, tf_cs)
    shutil.copy2(src_patch_py, dst_patch_py)
    print("Synced C# and Python patch files to TFTF310Upstream.")

    # Compile ShadersHelperFull.cs
    sh_dll = tftf310_dir / "ShadersHelperFull.dll"
    sh_il = tftf310_dir / "ShadersHelperFull.il"
    cmd_mcs_sh = [str(mcs_bat), "-noconfig", "-nostdlib+", "-target:library", f"-out:{sh_dll}"] + refs + [str(sh_cs)]
    res = subprocess.run(cmd_mcs_sh, capture_output=True, text=True)
    if res.returncode != 0:
        print(f"mcs error for ShadersHelperFull: {res.stderr}\n{res.stdout}")
        sys.exit(1)
    # ildasm
    subprocess.run([str(ildasm_exe), "/text", "/nobar", f"/out={sh_il}", str(sh_dll)], check=True)
    print(f"Compiled & disassembled ShadersHelperFull.il ({sh_il.stat().st_size} bytes)")

    # Compile TestFixMaterials.cs
    tf_cs = tftf310_dir / "tools" / "scratch" / "TestFixMaterials.cs"
    tf_dll = tftf310_dir / "TestFixMaterials.dll"
    tf_il = tftf310_dir / "TestFixMaterials.il"
    # Need reference to ShadersHelperFull.dll for TftfShaders and ShaderFallback
    cmd_mcs_tf = [str(mcs_bat), "-noconfig", "-nostdlib+", "-target:library", f"-r:{sh_dll}", f"-out:{tf_dll}"] + refs + [str(tf_cs)]
    res = subprocess.run(cmd_mcs_tf, capture_output=True, text=True)
    if res.returncode != 0:
        print(f"mcs error for TestFixMaterials: {res.stderr}\n{res.stdout}")
        sys.exit(1)
    # ildasm
    subprocess.run([str(ildasm_exe), "/text", "/nobar", f"/out={tf_il}", str(tf_dll)], check=True)
    print(f"Compiled & disassembled TestFixMaterials.il ({tf_il.stat().st_size} bytes)")

    # 4. Patch firstpass IL and reassemble Assembly-CSharp-firstpass.dll
    print("\n--- [4/5] Patching and assembling Assembly-CSharp-firstpass.dll ---")
    patch_script = tftf310_dir / "tools" / "patch_firstpass_il.py"
    subprocess.run([sys.executable, str(patch_script)], check=True)
    firstpass_dll = tftf310_dir / "patched_dlls" / "Assembly-CSharp-firstpass.dll"
    if not firstpass_dll.exists():
        print(f"ERROR: {firstpass_dll} was not created!")
        sys.exit(1)
    print(f"Assembly-CSharp-firstpass.dll verified: {firstpass_dll.stat().st_size} bytes")

    # 5. Package and Sign APK
    print("\n--- [5/5] Packaging, aligning, and signing final APK ---")
    import zipfile
    src_apk = Path(r"o:\agent\tftf-revival-launcher-0.2.0-fixed-armv7.apk")
    temp_unsigned = upstream_dir / "build" / "temp_step1_unsigned.apk"
    temp_aligned = upstream_dir / "build" / "temp_step1_aligned.apk"
    out_apk_o = Path(r"o:\agent\tftf-revival-launcher-step1-fontfixed-armv7.apk")
    out_apk_upstream = upstream_dir / "build" / "tftf-revival-launcher-step1-fontfixed-armv7.apk"

    patch_csharp = tftf310_dir / "patched_dlls" / "Assembly-CSharp.dll"
    zipalign_exe = Path(r"E:\Agent\TFTF\toolchain\android-sdk\build-tools\34.0.0\zipalign.exe")
    apksigner_bat = Path(r"E:\Agent\TFTF\toolchain\android-sdk\build-tools\34.0.0\apksigner.bat")
    keystore = upstream_dir / "build" / "debug.keystore"
    java_home = r"C:\Program Files\Java\jdk-17"

    with open(patch_csharp, "rb") as f:
        csharp_data = f.read()
    with open(firstpass_dll, "rb") as f:
        firstpass_data = f.read()
    with open(bundle_path, "rb") as f:
        shader_data = f.read()

    if temp_unsigned.exists():
        temp_unsigned.unlink()

    written_shader_bundle = False
    with zipfile.ZipFile(src_apk, "r") as zin, zipfile.ZipFile(temp_unsigned, "w") as zout:
        for item in zin.infolist():
            if item.filename.startswith("META-INF/") and (item.filename.endswith(".SF") or item.filename.endswith(".RSA") or item.filename.endswith(".MF")):
                continue
            if item.filename == "assets/bin/Data/Managed/Assembly-CSharp.dll":
                print("Injecting startup-fixed Assembly-CSharp.dll...")
                zout.writestr(item.filename, csharp_data, compress_type=zipfile.ZIP_DEFLATED)
            elif item.filename == "assets/bin/Data/Managed/Assembly-CSharp-firstpass.dll":
                print("Injecting font-fixed Assembly-CSharp-firstpass.dll...")
                zout.writestr(item.filename, firstpass_data, compress_type=zipfile.ZIP_DEFLATED)
            elif item.filename == "assets/shaders.bundle":
                print("Injecting updated assets/shaders.bundle (ZIP_STORED)...")
                zout.writestr(item.filename, shader_data, compress_type=zipfile.ZIP_STORED)
                written_shader_bundle = True
            else:
                data = zin.read(item.filename)
                zout.writestr(item, data)

        if not written_shader_bundle:
            print("Adding entry assets/shaders.bundle (ZIP_STORED)...")
            zinfo = zipfile.ZipInfo("assets/shaders.bundle")
            zinfo.compress_type = zipfile.ZIP_STORED
            zout.writestr(zinfo, shader_data)

    print(f"Unsigned APK created: {temp_unsigned.stat().st_size} bytes")

    if temp_aligned.exists():
        temp_aligned.unlink()
    print("Running zipalign...")
    subprocess.run([str(zipalign_exe), "-f", "-p", "4", str(temp_unsigned), str(temp_aligned)], check=True)

    if out_apk_upstream.exists():
        out_apk_upstream.unlink()

    print("Signing APK with apksigner...")
    env = os.environ.copy()
    env["JAVA_HOME"] = java_home
    env["PATH"] = f"{java_home}\\bin;{env.get('PATH', '')}"

    cmd_sign = [
        str(apksigner_bat), "sign",
        "--ks", str(keystore),
        "--ks-pass", "pass:android",
        "--out", str(out_apk_upstream),
        str(temp_aligned)
    ]
    subprocess.run(cmd_sign, check=True, env=env)
    print(f"Signed APK created: {out_apk_upstream} ({out_apk_upstream.stat().st_size} bytes)")

    # Copy to O: drive destination
    if out_apk_o.parent.exists():
        shutil.copy2(out_apk_upstream, out_apk_o)
        print(f"Copied signed APK -> {out_apk_o}")

    # Cleanup temp
    if temp_unsigned.exists():
        temp_unsigned.unlink()
    if temp_aligned.exists():
        temp_aligned.unlink()

    print("\n=======================================================")
    print("=== BUILD COMPLETE AND VERIFIED! ===")
    print(f"Target APK: {out_apk_o}")
    print("=======================================================")

if __name__ == "__main__":
    main()
