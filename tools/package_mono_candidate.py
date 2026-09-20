#!/usr/bin/env python3
"""Build a disposable 2.0.2 Mono APK with an explicitly selected DLL set.

The input APK and replacement DLLs are operator-supplied build artifacts.  This
tool writes only the requested output APK and a JSON manifest; neither belongs in
Git.  It never changes the input APK and never copies framework reference inputs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


EXPECTED_APK_SHA256 = "61c1860df9d5bb64ab28934b0fd6954c71c5410887f66260917351820b08aca4"
MANAGED_PREFIX = "assets/bin/Data/Managed/"
APPROVED = {
    "Assembly-CSharp.dll": "Assembly-CSharp.dll",
    "Assembly-CSharp-firstpass.dll": "Assembly-CSharp-firstpass.dll",
}


def digest_bytes(value):
    return hashlib.sha256(value).hexdigest()


def digest(path):
    return digest_bytes(path.read_bytes())


def read_entries(path):
    with zipfile.ZipFile(path, "r") as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        if len(names) != len(set(names)):
            raise ValueError("APK contains duplicate ZIP entry names")
        return [(info, archive.read(info.filename)) for info in infos]


def build_candidate(apk, output, replacements):
    apk = apk.resolve()
    output = output.resolve()
    if set(replacements) != set(APPROVED):
        raise ValueError("Replacement set must be exactly the two approved managed assemblies")
    if digest(apk) != EXPECTED_APK_SHA256:
        raise ValueError("Input APK SHA-256 does not match the recorded 2.0.2 Mono APK")
    if output == apk:
        raise ValueError("Output APK must be disposable and separate from the input APK")
    entries = read_entries(apk)
    original = {info.filename: (info, data) for info, data in entries}
    for filename, replacement in replacements.items():
        apk_name = MANAGED_PREFIX + filename
        if apk_name not in original:
            raise ValueError(f"Managed entry missing from input APK: {apk_name}")
        if not replacement.is_file():
            raise ValueError(f"Replacement DLL does not exist: {replacement}")
    output.parent.mkdir(parents=True, exist_ok=True)
    temp_path = output.with_suffix(output.suffix + ".tmp")
    if temp_path.exists():
        temp_path.unlink()
    changed = []
    unchanged = []
    with zipfile.ZipFile(temp_path, "w") as archive:
        for info, data in entries:
            replacement = replacements.get(Path(info.filename).name)
            if info.filename.startswith(MANAGED_PREFIX) and Path(info.filename).name in replacements:
                if info.filename != MANAGED_PREFIX + Path(info.filename).name:
                    raise ValueError(f"Unexpected managed replacement path: {info.filename}")
                new_data = replacement.read_bytes()
                changed.append({"path": info.filename, "original_sha256": digest_bytes(data),
                                "replacement_sha256": digest_bytes(new_data),
                                "original_size": len(data), "replacement_size": len(new_data)})
                archive.writestr(info, new_data)
            else:
                unchanged.append({"path": info.filename, "sha256": digest_bytes(data),
                                  "size": len(data)})
                archive.writestr(info, data)
    temp_path.replace(output)
    verification = read_entries(output)
    verified = {info.filename: data for info, data in verification}
    if set(verified) != set(original):
        raise ValueError("Candidate changed the APK entry set")
    for row in unchanged:
        if digest_bytes(verified[row["path"]]) != row["sha256"]:
            raise ValueError(f"Unchanged APK entry differs: {row['path']}")
    for row in changed:
        if digest_bytes(verified[row["path"]]) != row["replacement_sha256"]:
            raise ValueError(f"Replacement APK entry differs: {row['path']}")
    manifest = {"schema": 1, "input_apk": str(apk), "input_sha256": EXPECTED_APK_SHA256,
                "output_apk": str(output), "output_sha256": digest(output),
                "changed_entries": changed, "unchanged_entry_count": len(unchanged),
                "entry_count": len(verification), "packaging_authorized": True,
                "runtime_verified": False,
                "evidence_note": "Disposable ZIP substitution only; signing, installation, and runtime remain separate steps."}
    manifest_path = output.with_suffix(output.suffix + ".json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--firstpass", required=True, type=Path)
    parser.add_argument("--game", required=True, type=Path)
    args = parser.parse_args()
    replacements = {"Assembly-CSharp-firstpass.dll": args.firstpass.resolve(),
                    "Assembly-CSharp.dll": args.game.resolve()}
    manifest = build_candidate(args.apk, args.output, replacements)
    print(f"Manifest: {manifest}")


if __name__ == "__main__":
    main()
