import os
import sys
import subprocess
import time
from pathlib import Path

def main():
    repo_root = Path(r"E:\Agent\TFTF310Upstream")
    il_file = repo_root / "assembly.il"
    res_file = repo_root / "assembly.res"
    out_dll = repo_root / "patched_dlls" / "Assembly-CSharp.dll"
    ilasm_exe = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ilasm.exe"

    print(f"Reading {il_file}...")
    with open(il_file, "r", encoding="utf-8", errors="ignore") as f:
        lines = f.readlines()

    patched = False
    for i, line in enumerate(lines):
        if "IL_1715:  stloc.s    V_62" in line:
            # Check if already patched
            if (i + 1 < len(lines) and "brfalse    IL_1804" in lines[i+1]) or (i + 2 < len(lines) and "brfalse    IL_1804" in lines[i+2]):
                print(f"Line {i+1}: already patched with brfalse IL_1804.")
                patched = True
                break
            
            # Check if next line is IL_1717: ldloc.s V_62
            if i + 1 < len(lines) and "IL_1717:  ldloc.s    V_62" in lines[i+1]:
                print(f"Found target site at line {i+1}: {line.strip()}")
                insert_lines = [
                    "       ldloc.s    V_62\n",
                    "       brfalse    IL_1804\n"
                ]
                lines[i+1:i+1] = insert_lines
                patched = True
                print("Inserted null check (ldloc.s V_62; brfalse IL_1804).")
                
                # Create backup
                backup_file = repo_root / "assembly.il.bak"
                if not backup_file.exists():
                    print(f"Creating backup: {backup_file}...")
                    import shutil
                    shutil.copy2(il_file, backup_file)
                
                print(f"Writing updated {il_file}...")
                with open(il_file, "w", encoding="utf-8") as f_out:
                    f_out.writelines(lines)
                print("Successfully updated assembly.il!")
                break

    if not patched:
        print("ERROR: Target line 'IL_1715:  stloc.s    V_62' not found in assembly.il!")
        sys.exit(1)

    print(f"Running ilasm.exe on {il_file}...")
    cmd = [
        ilasm_exe,
        "/dll",
        "/quiet",
        f"/res={res_file}",
        f"/out={out_dll}",
        str(il_file)
    ]
    t0 = time.time()
    res = subprocess.run(cmd)
    dt = time.time() - t0
    print(f"ilasm return code: {res.returncode} in {dt:.1f}s")
    if res.returncode == 0 and out_dll.exists():
        print(f"SUCCESS! Patched Assembly-CSharp.dll generated: {out_dll} ({out_dll.stat().st_size} bytes)")
    else:
        print(f"ERROR: Failed to assemble {out_dll}!")
        sys.exit(1)

if __name__ == "__main__":
    main()
