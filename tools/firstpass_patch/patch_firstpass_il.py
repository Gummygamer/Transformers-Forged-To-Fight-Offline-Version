import os
import subprocess
from pathlib import Path

repo_root = Path(__file__).resolve().parent.parent
il_file = repo_root / "firstpass.il"
helper_il = repo_root / "ShadersHelperFull.il"
fix_il = repo_root / "TestFixMaterials.il"
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

# Now find locations in orig_lines
# ShaderFallback starts at line 74747 (0-indexed 74746)
sf_start_idx = None
sf_end_idx = None
fm_start_idx = None
fm_end_idx = None

for idx, line in enumerate(orig_lines):
    if ".class public abstract auto ansi sealed beforefieldinit ShaderFallback" in line:
        sf_start_idx = idx
    if sf_start_idx is not None and sf_end_idx is None and "} // end of class ShaderFallback" in line:
        sf_end_idx = idx + 1
    if ".method public hidebysig static void  FixMaterials" in line:
        fm_start_idx = idx
    if fm_start_idx is not None and fm_end_idx is None and "} // end of method TftfResources::FixMaterials" in line:
        fm_end_idx = idx + 1

print(f"ShaderFallback lines: {sf_start_idx+1} to {sf_end_idx}")
print(f"FixMaterials lines: {fm_start_idx+1} to {fm_end_idx}")
assert sf_start_idx is not None and sf_end_idx is not None
assert fm_start_idx is not None and fm_end_idx is not None
assert sf_end_idx < fm_start_idx

# Assemble new lines
new_lines = []
new_lines.extend(orig_lines[:sf_start_idx])
new_lines.append(combined_sf)
new_lines.extend(orig_lines[sf_end_idx:fm_start_idx])
new_lines.append(fm_block)
new_lines.extend(orig_lines[fm_end_idx:])

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
    out_il
]
res = subprocess.run(cmd)
print("ilasm return code:", res.returncode)
if res.returncode == 0 and os.path.exists(out_dll):
    print(f"SUCCESS! Output DLL created: {out_dll} ({os.path.getsize(out_dll)} bytes)")
else:
    print("FAILED to reassemble!")
