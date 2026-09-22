#!/usr/bin/env python3
"""Build a disposable 2.0.2 Mono APK with an explicitly selected DLL set.

The input APK and replacement DLLs are operator-supplied build artifacts.  This
tool writes only the requested output APK and a JSON manifest; neither belongs in
Git.  It never changes the input APK and never copies framework reference inputs.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile


EXPECTED_APK_SHA256 = "61c1860df9d5bb64ab28934b0fd6954c71c5410887f66260917351820b08aca4"
MANAGED_PREFIX = "assets/bin/Data/Managed/"
APPROVED = {
    "Assembly-CSharp.dll": "Assembly-CSharp.dll",
    "Assembly-CSharp-firstpass.dll": "Assembly-CSharp-firstpass.dll",
}
ODR_APK_PATH = "assets/mono_offline/quest_fte.wad"
ODR_TOC_ENTRY = "assets_quest_fte_odr/toc.txt"
ODR_BUNDLE_ENTRY = "assets_quest_fte_odr/assets_quest_fte.assetbundle"


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


def build_offline_odr_wad(donor):
    donor = donor.resolve()
    if not donor.is_file():
        raise ValueError(f"Donor APK does not exist: {donor}")
    with zipfile.ZipFile(donor, "r") as archive:
        source_entries = {
            ODR_TOC_ENTRY: archive.read("assets/" + ODR_TOC_ENTRY),
            ODR_BUNDLE_ENTRY: archive.read("assets/assetpack/" + ODR_BUNDLE_ENTRY),
        }
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_STORED) as archive:
        for name in (ODR_TOC_ENTRY, ODR_BUNDLE_ENTRY):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            archive.writestr(info, source_entries[name])
        bundle_info = archive.getinfo(ODR_BUNDLE_ENTRY)
        bundle_offset = bundle_info.header_offset + 30 + len(bundle_info.filename.encode("utf-8")) + len(bundle_info.extra)
    wad = stream.getvalue()
    return wad, {
        "apk_path": ODR_APK_PATH,
        "wad_sha256": digest_bytes(wad),
        "wad_size": len(wad),
        "bundle_name": "assets_quest_fte",
        "bundle_offset": bundle_offset,
        "donor_apk": str(donor),
        "donor_entries": [
            "assets/" + ODR_TOC_ENTRY,
            "assets/assetpack/" + ODR_BUNDLE_ENTRY,
        ],
    }


def read_offline_odr_wad(path):
    path = path.resolve()
    if not path.is_file():
        raise ValueError(f"Offline ODR WAD does not exist: {path}")
    wad = path.read_bytes()
    with zipfile.ZipFile(io.BytesIO(wad), "r") as archive:
        names = set(archive.namelist())
        required = {ODR_TOC_ENTRY, ODR_BUNDLE_ENTRY}
        if not required.issubset(names):
            raise ValueError(f"Offline ODR WAD is missing required entries: {sorted(required - names)}")
        bundle_info = archive.getinfo(ODR_BUNDLE_ENTRY)
        bundle_offset = bundle_info.header_offset + 30 + len(bundle_info.filename.encode("utf-8")) + len(bundle_info.extra)
    return wad, {
        "apk_path": ODR_APK_PATH,
        "wad_sha256": digest_bytes(wad),
        "wad_size": len(wad),
        "bundle_name": "assets_quest_fte",
        "bundle_offset": bundle_offset,
        "wad_source": str(path),
    }


def build_candidate(apk, output, replacements, donor=None, wad=None):
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
    grafted = []
    odr_wad = None
    odr_metadata = None
    if donor is not None and wad is not None:
        raise ValueError("Specify either --donor or --wad, not both")
    if donor is not None:
        odr_wad, odr_metadata = build_offline_odr_wad(donor)
    elif wad is not None:
        odr_wad, odr_metadata = read_offline_odr_wad(wad)
    if odr_wad is not None and ODR_APK_PATH in original:
        raise ValueError(f"Offline ODR graft path already exists in input APK: {ODR_APK_PATH}")
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
        if odr_wad is not None:
            info = zipfile.ZipInfo(ODR_APK_PATH, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            archive.writestr(info, odr_wad)
            grafted.append({"path": ODR_APK_PATH, "sha256": digest_bytes(odr_wad),
                            "size": len(odr_wad), "source": "explicit donor ODR graft"})
    temp_path.replace(output)
    verification = read_entries(output)
    verified = {info.filename: data for info, data in verification}
    expected_entries = set(original) | ({ODR_APK_PATH} if odr_wad is not None else set())
    if set(verified) != expected_entries:
        raise ValueError("Candidate changed the APK entry set outside approved replacements/grafts")
    for row in unchanged:
        if digest_bytes(verified[row["path"]]) != row["sha256"]:
            raise ValueError(f"Unchanged APK entry differs: {row['path']}")
    for row in changed:
        if digest_bytes(verified[row["path"]]) != row["replacement_sha256"]:
            raise ValueError(f"Replacement APK entry differs: {row['path']}")
    for row in grafted:
        if digest_bytes(verified[row["path"]]) != row["sha256"]:
            raise ValueError(f"Grafted APK entry differs: {row['path']}")
    manifest = {"schema": 1, "input_apk": str(apk), "input_sha256": EXPECTED_APK_SHA256,
                "output_apk": str(output), "output_sha256": digest(output),
                "changed_entries": changed, "unchanged_entry_count": len(unchanged),
                "grafted_entries": grafted, "entry_count": len(verification),
                "odr_graft": odr_metadata, "packaging_authorized": True,
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
    parser.add_argument("--donor", type=Path,
                        help="Explicit donor APK for the disposable Quest ODR WAD graft")
    parser.add_argument("--wad", type=Path,
                        help="Explicit prebuilt Quest ODR WAD for the disposable graft")
    args = parser.parse_args()
    replacements = {"Assembly-CSharp-firstpass.dll": args.firstpass.resolve(),
                    "Assembly-CSharp.dll": args.game.resolve()}
    manifest = build_candidate(args.apk, args.output, replacements, args.donor, args.wad)
    print(f"Manifest: {manifest}")


if __name__ == "__main__":
    main()
