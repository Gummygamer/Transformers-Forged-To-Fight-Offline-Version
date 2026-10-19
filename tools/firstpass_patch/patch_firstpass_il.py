import os
import subprocess
from pathlib import Path

repo_root = Path(__file__).resolve().parent.parent
il_file = repo_root / "firstpass.il"
helper_il = repo_root / "ShadersHelperFull.il"
fix_il = repo_root / "TestFixMaterials.il"
targets_il = repo_root / "target_launcher_classes.il"
out_il = repo_root / "firstpass_patched.il"
out_dll = repo_root / "patched_dlls" / "Assembly-CSharp-firstpass.dll"
res_file = repo_root / "firstpass.res"

print("Reading input files...")
with open(il_file, "r", encoding="utf-8", errors="ignore") as f:
    orig_lines = f.readlines()

with open(helper_il, "r", encoding="utf-8", errors="ignore") as f:
    sh_text = f.read()

with open(fix_il, "r", encoding="utf-8", errors="ignore") as f:
    tf_text = f.read()

with open(targets_il, "r", encoding="utf-8", errors="ignore") as f:
    tgt_text = f.read()

# 1. Extract TftfShaders
c_tftf_start = sh_text.find(".class public abstract auto ansi sealed beforefieldinit TftfShaders")
c_tftf_end = sh_text.find("} // end of class TftfShaders", c_tftf_start) + len("} // end of class TftfShaders")
tftf_shaders_block = sh_text[c_tftf_start:c_tftf_end] + "\n"

# 2. Extract ShaderFallback
c_sf_start = sh_text.find(".class public abstract auto ansi sealed beforefieldinit ShaderFallback")
c_sf_end = sh_text.find("} // end of class ShaderFallback", c_sf_start) + len("} // end of class ShaderFallback")
sf_block = sh_text[c_sf_start:c_sf_end] + "\n"

# Combine TftfShaders + ShaderFallback
combined_sf = tftf_shaders_block + "\n" + sf_block

# 3. Extract FixMaterials
m_fm_start = tf_text.find(".method public hidebysig static void  FixMaterials")
m_fm_end = tf_text.find("} // end of method TestTftfFix::FixMaterials", m_fm_start) + len("} // end of method TestTftfFix::FixMaterials")
fm_block = tf_text[m_fm_start:m_fm_end]
fm_block = fm_block.replace("TestTftfFix::FixMaterials", "TftfResources::FixMaterials").replace("[ShadersHelperFull]", "") + "\n"

# 4. Extract Chunk 1 from targets_il: ZipDataSource + TftfInstaller + TftfLauncher
c_zip_start = tgt_text.find(".class public auto ansi sealed beforefieldinit ZipDataSource")
c_launch_end = tgt_text.find("} // end of class TftfLauncher", c_zip_start) + len("} // end of class TftfLauncher")
chunk1_launcher = tgt_text[c_zip_start:c_launch_end] + "\n"

# 5. Extract Chunk 2 from targets_il: Tftf.Odr.* + Tftf.Convert.*
c_odr_start = tgt_text.find(".class public auto ansi sealed beforefieldinit Tftf.Odr.JObj")
c_conv_end = tgt_text.find("} // end of class Tftf.Convert.UnityFsWriter", c_odr_start) + len("} // end of class Tftf.Convert.UnityFsWriter")
chunk2_odr_conv = tgt_text[c_odr_start:c_conv_end] + "\n"

# Now find locations in orig_lines
sf_start_idx = None
sf_end_idx = None
chunk1_start_idx = None
chunk1_end_idx = None
fm_start_idx = None
fm_end_idx = None
chunk2_start_idx = None
chunk2_end_idx = None

for idx, line in enumerate(orig_lines):
    if ".class public abstract auto ansi sealed beforefieldinit ShaderFallback" in line:
        sf_start_idx = idx
    if sf_start_idx is not None and sf_end_idx is None and "} // end of class ShaderFallback" in line:
        sf_end_idx = idx + 1

    if ".class public auto ansi sealed beforefieldinit ZipDataSource" in line:
        chunk1_start_idx = idx
    if chunk1_start_idx is not None and chunk1_end_idx is None and "} // end of class TftfLauncher" in line:
        chunk1_end_idx = idx + 1

    if ".method public hidebysig static void  FixMaterials" in line:
        fm_start_idx = idx
    if fm_start_idx is not None and fm_end_idx is None and "} // end of method TftfResources::FixMaterials" in line:
        fm_end_idx = idx + 1

    if ".class public auto ansi sealed beforefieldinit Tftf.Odr.JObj" in line:
        chunk2_start_idx = idx
    if chunk2_start_idx is not None and chunk2_end_idx is None and "} // end of class Tftf.Convert.UnityFsWriter" in line:
        chunk2_end_idx = idx + 1

print(f"ShaderFallback lines: {sf_start_idx+1} to {sf_end_idx}")
print(f"Chunk 1 (Installer/Launcher) lines: {chunk1_start_idx+1} to {chunk1_end_idx}")
print(f"FixMaterials lines: {fm_start_idx+1} to {fm_end_idx}")
print(f"Chunk 2 (Odr/Convert) lines: {chunk2_start_idx+1} to {chunk2_end_idx}")

assert all(x is not None for x in [sf_start_idx, sf_end_idx, chunk1_start_idx, chunk1_end_idx, fm_start_idx, fm_end_idx, chunk2_start_idx, chunk2_end_idx])
assert sf_end_idx < chunk1_start_idx < fm_start_idx < chunk2_start_idx

# Middle section containing UITextureRef
mid_text = "".join(orig_lines[fm_end_idx:chunk2_start_idx])

# Target sequence in UITextureRef::LoadViaTexturePoolManager:
target_seq = "    IL_00a1:  ldloc.0\n    IL_00a2:  ldfld      class EB.Action`2<class [UnityEngine.CoreModule]UnityEngine.Texture2D,bool> UITextureRef/'<>c__DisplayClass67_0'::callback"
replacement_seq = "    IL_00a1:  ldarg.0\n    ldc.i4.0\n    call       instance void UITextureRef::UpdateLoadingIcon(bool)\n    ldloc.0\n    IL_00a2:  ldfld      class EB.Action`2<class [UnityEngine.CoreModule]UnityEngine.Texture2D,bool> UITextureRef/'<>c__DisplayClass67_0'::callback"

if target_seq in mid_text:
    print("Found UITextureRef spinner target sequence in mid_text, patching...")
    mid_text = mid_text.replace(target_seq, replacement_seq, 1)
else:
    raise Exception("target_seq for UITextureRef not found in mid_text!")

# Assemble new lines
new_lines = []
new_lines.extend(orig_lines[:sf_start_idx])
new_lines.append(combined_sf)
new_lines.extend(orig_lines[sf_end_idx:chunk1_start_idx])
new_lines.append(chunk1_launcher)
new_lines.extend(orig_lines[chunk1_end_idx:fm_start_idx])
new_lines.append(fm_block)
new_lines.append(mid_text)
new_lines.append(chunk2_odr_conv)
new_lines.extend(orig_lines[chunk2_end_idx:])

print(f"Writing {out_il} (total lines: {len(new_lines)})...")
with open(out_il, "w", encoding="utf-8") as f:
    f.writelines(new_lines)

print(f"Running ilasm.exe on {out_il}...")
ilasm_exe = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ilasm.exe"
cmd = [
    ilasm_exe,
    "/dll",
    "/quiet",
    f"/res={res_file}",
    f"/out={out_dll}",
    str(out_il)
]
res = subprocess.run(cmd)
print("ilasm return code:", res.returncode)
if res.returncode == 0 and os.path.exists(out_dll):
    print(f"SUCCESS! Output DLL created: {out_dll} ({os.path.getsize(out_dll)} bytes)")
else:
    print("FAILED to reassemble!")
    exit(1)
