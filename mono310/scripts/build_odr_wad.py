#!/usr/bin/env python3
"""Pack on-demand-resource (ODR) bundles from a donor (9.2.0) APK into the
toc.wad / toc.zip / manifest.json layout expected by the 3.1.0 client's
EB.Sparx.ODRManager.

Layout facts taken from the 3.1.0 decompile (decomp_firstpass/.../EB/Sparx):
  * ODRManifest(build): "cl", "platform", "cdn", "check_crc", "manifest"[]
  * WADManifest: "name" (lower-cased; "toc" is special: its toc.txt entries are
    mounted first), "url" (cdn-prefixed when it starts with "/"), "filename"
    (entry extracted from the downloaded zip), "size"/"crc" (of the extracted
    wad), "zip_size", "tocs"[{name}], "bundles"[{name, offset, deps}]
  * SparxZipEntry: "name", "offset" (UInt64, byte offset of the entry *data*
    inside the wad; 0 means "old pipeline", so it must be > 0), "index"
  * OpenWad: bundle name = FormatAssetBundleName(entry) = basename, lower-case,
    without ".assetbundle"; loaded with AssetBundle.LoadFromFile(wad, 0, offset)
    so wad entries must be STORED. Entries ending in "toc.txt" are handed to
    PackDriver.MountOdrCoroutine(entryName, json) -> AssetBundleManager.Register
    which maps every key of bundles[*].paths (lower-cased) to the bundle; the
    value is the asset name passed to LoadAssetAsync.

Only stdlib is used. Output is deterministic (fixed zip timestamps/attrs,
sorted ordering, stable JSON).
"""
import argparse
import binascii
import io
import json
import os
import struct
import sys
import zipfile

DEFAULT_DONOR = os.environ.get("TFTF_92_APK", "")
DEFAULT_OUT = os.environ.get("ODR_DIR", "odr-out")

BASE_PACKS = [
    "assets_base_odr", "assets_quest_odr", "assets_quest_fte_odr", "assets_common_odr",
    "common_odr", "primordial_odr", "primordial_base_odr", "buildings_odr",
    "characters_fx_procedural_odr",
    # story themes: ruinedcity is the 2.x.x quests; quintessa/unicron follow in later acts
    "ruinedcity_odr", "quintessa_odr", "unicron_odr",
    # characters used by the first story quests (fighters, backups, shared procedural anim/audio)
    "characters_procedural_odr", "optimusprimal_bw_mp32_odr", "sharkticon_gs_brawler_odr",
    "bludgeon_gs_rd20_odr", "optimusprime_cin_tf_odr", "megatron_gs_leader2015_odr",
]

# every donor character pack (all merged character prefabs)
CHARACTER_PACKS = ['Jetfire_GS_Leader2014_odr', 'arcee_gs_deluxe2014_odr', 'barricade_cin_dotm_odr', 'blaster_gs_leader2016_odr', 'bludgeon_gs_rd20_odr', 'bonecrusher_cin_rotf_odr', 'bumblebee_cin_dotm_odr', 'bumblebee_gs_kabam_odr', 'cheetor_bw_transmetal_odr', 'cliffjumper_gs_kabam_odr', 'cyclonus_gs_uw06_odr', 'dinobot_bw_kabam_odr', 'dirge_gs_deluxe2008_odr', 'drift_cin_aoe_odr', 'galvatron_gs_voyager2016_odr', 'grimlock_gs_mp08_odr', 'grindor_cin_rotf_odr', 'hotrod_cin_tlk_odr', 'hound_cin_tlk_odr', 'ironhide_cin_rotf_odr', 'ironhide_gs_kabam_odr', 'jazz_gs_twm05_odr', 'kickback_gs_kabam_odr', 'megatron_cin_rotf_odr', 'megatron_gs_leader2015_odr', 'megatronus_gs_kabam_odr', 'mirage_gs_deluxe2016_odr', 'mixmaster_cin_rotf_odr', 'motormaster_gs_voyager2015_odr', 'necrotronus_gs_kabam_odr', 'nemesisprime_gs_voyager2015_odr', 'optimusprimal_bw_mp32_odr', 'optimusprime_cin_tf_odr', 'prowl_gs_deluxe2016_odr', 'ramjet_gs_deluxe2008_odr', 'ratchet_gs_kabam_odr', 'rhinox_gs_voyager2014_odr', 'rodimusprime_gs_mp09_odr', 'scorponok_bw_kabam_odr', 'sharkticon_gs_brawler_odr', 'sharkticon_gs_demolition_odr', 'sharkticon_gs_kabam_odr', 'sharkticon_gs_scout_odr', 'sharkticon_gs_tactician_odr', 'sharkticon_gs_tech_odr', 'sharkticon_gs_warrior_odr', 'shockwave_gs_odr', 'sideswipe_gs_odr', 'skywarp_gs_leader2015_odr', 'slipstream_gs_odr', 'soundblaster_gs_mp13b_odr', 'soundwave_gs_odr', 'sunstreaker_gs_deluxe2008_odr', 'tantrum_gs_kabam_odr', 'thundercracker_gs_leader2015_odr', 'ultramagnus_gs_leader_odr', 'waspinator_gs_deluxe_odr', 'wheeljack_gs_mp20_odr', 'windblade_gs_odr']
BASE_APK_BUNDLES = {"character_audio", "character_fx", "moves", "characters", "quest"}
TOWER_RELIC_PACKS = ["towers_odr", "relics_odr"]
# Time-of-day "<theme>_merged/<theme>_timeofday_N_<lightmap>" prefabs used by
# GameboardBuilder (_TimeOfDayPath: primordial quests, "base" for user base/raid).
TIMEOFDAY_PACKS = ["primordial", "base", "ruinedcity", "unicron", "quintessa_merged_odr", "mine"]

# Paths the 3.1.0 client requests (GameboardAssetConstants.cs, GameboardManager.cs
# RequestLibrary/GetBasePathForType, GameboardBuilder.cs).
REQUESTED_PATHS = [
    "assets_quest/QuestRoot", "assets_quest/TapIndicator", "assets_quest/TapFeedback",
    "assets_quest/ItemPickup", "assets_quest/ItemTrail", "assets_quest/Ambush",
    "assets_quest/TriggerFight", "assets_quest/PlayerMarker", "assets_quest/ItemCard",
    "assets_quest/FinalBossLight", "assets_quest/NodeNumber", "assets_quest/GateOpened",
    "assets_quest/QuestComplete",
    "assets_base/BaseRoot", "assets_base/BaseTapFeedback", "assets_base/BaseRelicTapFeedback",
    "assets_base/BaseTapIndicator", "assets_base/BotPlaced", "assets_base/BotRemoved",
    "assets_base/ModPlaced", "assets_base/ModRemoved", "assets_base/CrystalClaim",
    "assets_base/RelicActive", "assets_base/RelicPlaced", "assets_base/RelicRemoved",
    "assets_base/RelicPedestalPlaced", "assets_base/RelicPedestalRemoved",
    "assets_base/BaseShieldActivate", "assets_base/BaseShieldDeactivate",
    "assets_base/BaseShieldActive",
    "assets_common/Boss", "assets_common/BossCard", "assets_common/RelicCard",
    "assets_common/Path", "assets_common/LinkedNode", "assets_common/LinkedNodeGlobal",
    "theme/primordial/prefabs/library_primordial",
    "theme/primordial_base/prefabs/library_primordial_base",
    "theme/common/prefabs/library_common",
    "buildings/prefabs/library_buildings",
]
OPTIONAL_REQUESTED = {
    "towers": ["towers/prefabs/library_towers"],
    "relics": ["relics/prefabs/library_relics"],
    "timeofday": ["primordial_merged/primordial_timeofday_%d_%s" % (i, m)
                  for i in range(6) for m in ("forward", "deferred")]
                 + ["base_merged/base_timeofday_0_%s" % m for m in ("forward", "deferred")],
}

ZIP_DATE = (1980, 1, 1, 0, 0, 0)
# Key order of a bundle record in the 3.1.0 APK's own toc.txt files.
BUNDLE_KEY_ORDER = ["hash", "mount", "deterministic", "file", "ts", "manifest", "directory",
                    "typetreehash", "compression", "deps", "crc", "contenthash", "size",
                    "paths", "parent"]


def die(msg):
    sys.stderr.write("error: %s\n" % msg)
    sys.exit(1)


def to_int32(v):
    v = int(v) & 0xFFFFFFFF
    return v - 0x100000000 if v >= 0x80000000 else v


def unity_header(data):
    """Return (signature, engine_version) of a UnityFS bundle."""
    # layout: "UnityFS\0" u32be(format) "5.x.x\0" "2020.3.31f1\0" ...
    sig = data[:8].split(b"\0")[0].decode("latin1", "replace")
    rest = data[12:96].split(b"\0")
    eng = rest[1].decode("latin1", "replace") if len(rest) > 1 else ""
    return sig, eng


def rewrite_bundle(rec):
    out = {}
    for k in BUNDLE_KEY_ORDER:
        if k in rec:
            out[k] = rec[k]
    for k in sorted(rec):
        if k not in out:
            out[k] = rec[k]
    if "hash" in out and out["hash"] is not None:
        out["hash"] = to_int32(out["hash"])  # AssetBundleInfo.hash is a C# int
    out["paths"] = dict(sorted(out.get("paths", {}).items()))
    out["deps"] = list(out.get("deps") or [])
    return out


def data_offset(raw, header_offset):
    sig, = struct.unpack_from("<I", raw, header_offset)
    if sig != 0x04034B50:
        raise ValueError("bad local header at %d" % header_offset)
    nlen, xlen = struct.unpack_from("<HH", raw, header_offset + 26)
    return header_offset + 30 + nlen + xlen


def zinfo(name, compress):
    zi = zipfile.ZipInfo(name, date_time=ZIP_DATE)
    zi.compress_type = compress
    zi.external_attr = 0o644 << 16
    zi.create_system = 3
    return zi


def resolve_requests(requests, tocs):
    """tocs: {pack: toc_dict}. Adds alias keys in place. Returns report rows."""
    index = {}  # lower key -> (pack, bundle, asset)
    for pack in sorted(tocs):
        for bname, rec in sorted(tocs[pack]["bundles"].items()):
            for k, v in rec["paths"].items():
                index.setdefault(k.lower(), (pack, bname, v))
    rows = []
    for req in requests:
        key = req.replace("\\", "/").lower()
        if key in index:
            pack, bname, asset = index[key]
            rows.append((req, "native", pack, bname, key, asset))
            continue
        prefix, _, leaf = key.rpartition("/")
        cands = [(k, t) for k, t in index.items()
                 if k.rpartition("/")[2] == leaf and k.rpartition("/")[0].startswith(prefix)]
        if not cands:
            cands = [(k, t) for k, t in index.items() if k.rpartition("/")[2] == leaf]
        if not cands:
            rows.append((req, "UNRESOLVED", "-", "-", "-", "-"))
            continue
        cands.sort()
        src_key, (pack, bname, asset) = cands[0]
        tocs[pack]["bundles"][bname]["paths"][key] = asset
        index[key] = (pack, bname, asset)
        rows.append((req, "alias", pack, bname, src_key, asset))
    for pack in tocs:
        for rec in tocs[pack]["bundles"].values():
            rec["paths"] = dict(sorted(rec["paths"].items()))
    return rows


def build(args):
    packs = []
    requests = list(REQUESTED_PATHS)
    if args.towers_relics:
        requests += OPTIONAL_REQUESTED["towers"] + OPTIONAL_REQUESTED["relics"]
    if args.timeofday:
        packs += TIMEOFDAY_PACKS
        requests += OPTIONAL_REQUESTED["timeofday"]
    packs = sorted(set(packs))

    os.makedirs(args.out, exist_ok=True)
    apk = zipfile.ZipFile(args.donor)
    names = set(apk.namelist())
    # Keep the package in step with the donor's current content catalog instead
    # of a hand-maintained subset that can silently omit new ODR packs.
    packs = sorted(set(packs + [
        n[len("assets/"):-len("/toc.txt")]
        for n in names
        if n.startswith("assets/") and n.endswith("_odr/toc.txt")
    ]))

    tocs, bundle_src = {}, {}
    for pack in packs:
        tp = "assets/%s/toc.txt" % pack
        if tp not in names:
            die("donor has no %s" % tp)
        raw = json.loads(apk.read(tp).decode("utf-8"))
        toc = {"bundles": {}, "files": raw.get("files", []), "tags": raw.get("tags", []),
               "version": raw.get("version", ""), "scenes": raw.get("scenes", {})}
        for bname, rec in sorted(raw.get("bundles", {}).items()):
            rec = rewrite_bundle(rec)
            src = "assets/assetpack/" + rec["file"]
            if src not in names:
                low = {n.lower(): n for n in names} if not hasattr(apk, "_lowmap") else apk._lowmap
                apk._lowmap = low
                if src.lower() in low:
                    rec["file"] = low[src.lower()][len("assets/assetpack/"):]
                    src = low[src.lower()]
            if src not in names:
                die("donor bundle missing: %s" % src)
            if bname in bundle_src:
                die("duplicate bundle name %s" % bname)
            if os.path.basename(rec["file"]).lower()[:-len(".assetbundle")] != bname.lower():
                die("bundle %s file basename mismatch: %s" % (bname, rec["file"]))
            bundle_src[bname] = (pack, src, rec["file"])
            toc["bundles"][bname] = rec
        tocs[pack] = toc

    rows = resolve_requests(requests, tocs)

    # ---- toc.wad (STORED) ----
    wad_path = os.path.join(args.out, "toc.wad")
    tmp = wad_path + ".tmp"
    entries = []  # (entry_name, kind, bundle_name)
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_STORED, allowZip64=False) as zw:
        for pack in packs:
            tname = "%s/toc.txt" % pack
            body = json.dumps(tocs[pack], indent=4, ensure_ascii=False).encode("utf-8") + b"\n"
            zw.writestr(zinfo(tname, zipfile.ZIP_STORED), body)
            entries.append((tname, "toc", None))
            for bname in sorted(tocs[pack]["bundles"]):
                _, src, fname = bundle_src[bname]
                zw.writestr(zinfo(fname, zipfile.ZIP_STORED), apk.read(src))
                entries.append((fname, "bundle", bname))
        # Keep loose portraits, localization and other catalog files alongside
        # the bundles, as the on-phone builder does.
        lower_names = {name.lower(): name for name in names}
        for pack in packs:
            for filename in tocs[pack]["files"]:
                filename = filename.replace("\\", "/")
                src = lower_names.get(("assets/assetpack/" + pack + "/" + filename).lower())
                if src is not None:
                    zw.writestr(zinfo(filename, zipfile.ZIP_STORED), apk.read(src))
                    entries.append((filename, "raw", None))
    os.replace(tmp, wad_path)

    with open(wad_path, "rb") as f:
        wad = f.read()
    wad_crc = binascii.crc32(wad) & 0xFFFFFFFF
    info = {}
    with zipfile.ZipFile(wad_path) as zr:
        for zi in zr.infolist():
            info[zi.filename] = zi
    bundles_json, bundle_meta, raw_json = [], [], []
    for index, (ename, kind, _) in enumerate(entries):
        if kind == "raw":
            zi = info[ename]
            for name in dict.fromkeys((ename, ename.lower())):
                raw_json.append({"name": name, "offset": data_offset(wad, zi.header_offset),
                                 "index": index, "size": zi.file_size})
    for ename, kind, bname in entries:
        if kind != "bundle":
            continue
        zi = info[ename]
        off = data_offset(wad, zi.header_offset)
        rec = tocs[bundle_src[bname][0]]["bundles"][bname]
        bundles_json.append({"name": bname.lower(), "offset": off, "deps": list(rec["deps"])})
        sig, eng = unity_header(wad[off:off + 128])
        bundle_meta.append((bname, off, zi.file_size, sig, eng))

    # ---- toc.zip ----
    zip_path = os.path.join(args.out, "toc.zip")
    tmp = zip_path + ".tmp"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zw:
        zw.writestr(zinfo("toc.wad", zipfile.ZIP_DEFLATED), wad, compresslevel=6)
    os.replace(tmp, zip_path)
    zip_size = os.path.getsize(zip_path)

    manifest = {"error": None, "result": {"build": [{
        "cl": args.cl, "platform": "android", "check_crc": False, "cdn": args.cdn,
        "manifest": [{
            "name": "toc", "url": "/odr/wad/toc.zip", "filename": "toc.wad",
            "size": len(wad), "crc": wad_crc, "zip_size": zip_size,
            "tocs": [{"name": e[0]} for e in entries if e[1] == "toc"],
            "bundles": bundles_json,
            "raw": raw_json,
        }],
    }]}}
    man_path = os.path.join(args.out, "manifest.json")
    with open(man_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    # ---- mapping report ----
    all_bundles = {b.lower() for b in bundle_src}
    lines = ["# 3.1.0 requested path -> donor bundle / asset (status: native|alias|UNRESOLVED)", ""]
    w = max(len(r[0]) for r in rows)
    for req, st, pack, bname, key, asset in rows:
        lines.append("%-*s  %-10s  %s:%s  key=%s  asset=%s" % (w, req, st, pack, bname, key, asset))
    lines += ["", "# bundles in toc.wad (name, data offset, size, signature, engine)"]
    for bname, off, size, sig, eng in bundle_meta:
        lines.append("%-22s offset=%-10d size=%-10d %s %s" % (bname, off, size, sig, eng))
    missing_deps = sorted({d for b in bundles_json for d in b["deps"] if d.lower() not in all_bundles})
    lines += ["", "# deps not packed: %s" % (", ".join(missing_deps) or "none")]
    unresolved = [r[0] for r in rows if r[1] == "UNRESOLVED"]
    lines += ["# unresolved: %s" % (", ".join(unresolved) or "none")]
    if not args.timeofday:
        lines.append("# note: time-of-day paths (<theme>_merged/...) not packed; use --timeofday")
    rep_path = os.path.join(args.out, "odr_mapping_report.txt")
    with open(rep_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    print("packs: %s" % ", ".join(packs))
    print("toc.wad  %d bytes crc32=%d (%s)" % (len(wad), wad_crc, wad_path))
    print("toc.zip  %d bytes (%s)" % (zip_size, zip_path))
    print("manifest %s; report %s" % (man_path, rep_path))
    print("bundles: %d; aliases: %d; unresolved: %s" % (
        len(bundles_json), sum(1 for r in rows if r[1] == "alias"), unresolved or "none"))
    return 0


def selftest(args):
    man_path = os.path.join(args.out, "manifest.json")
    wad_path = os.path.join(args.out, "toc.wad")
    zip_path = os.path.join(args.out, "toc.zip")
    with open(man_path, encoding="utf-8") as f:
        man = json.load(f)
    errs = []

    def check(cond, msg):
        if not cond:
            errs.append(msg)

    check(man.get("error") is None, "error not null")
    build_ = man["result"]["build"][0]
    for k, t in (("cl", str), ("platform", str), ("cdn", str), ("check_crc", bool), ("manifest", list)):
        check(isinstance(build_.get(k), t), "build.%s type" % k)
    w = build_["manifest"][0]
    for k, t in (("name", str), ("url", str), ("filename", str), ("size", int), ("crc", int),
                 ("zip_size", int), ("tocs", list), ("bundles", list)):
        check(isinstance(w.get(k), t), "wad.%s type" % k)
    with open(wad_path, "rb") as f:
        wad = f.read()
    check(len(wad) == w["size"], "wad size %d != %d" % (len(wad), w["size"]))
    check((binascii.crc32(wad) & 0xFFFFFFFF) == w["crc"], "wad crc mismatch")
    check(os.path.getsize(zip_path) == w["zip_size"], "zip_size mismatch")
    with zipfile.ZipFile(zip_path) as zz:
        check(zz.namelist() == [w["filename"]], "toc.zip entries %r" % zz.namelist())
        check(zz.getinfo(w["filename"]).CRC == w["crc"], "toc.zip entry CRC mismatch")
        check(zz.getinfo(w["filename"]).file_size == w["size"], "toc.zip entry size mismatch")
    paths = {}
    with zipfile.ZipFile(wad_path) as zw:
        check(zw.testzip() is None, "wad testzip failed")
        byname = {}
        for zi in zw.infolist():
            check(zi.compress_type == zipfile.ZIP_STORED, "%s not STORED" % zi.filename)
            if zi.filename.endswith(".assetbundle"):
                byname[os.path.basename(zi.filename)[:-len(".assetbundle")].lower()] = zi
        names = set(zw.namelist())
        for t in w["tocs"]:
            check(t["name"] in names, "toc %s missing" % t["name"])
            toc = json.loads(zw.read(t["name"]).decode("utf-8"))
            for bname, rec in toc["bundles"].items():
                check(bname.lower() in byname, "toc %s references unpacked bundle %s" % (t["name"], bname))
                for k, v in rec["paths"].items():
                    paths[k.lower()] = bname
    mb = {b["name"]: b for b in w["bundles"]}
    check(set(mb) == set(byname), "manifest bundles != wad bundles")
    for name, b in mb.items():
        zi = byname.get(name)
        if zi is None:
            continue
        off = b["offset"]
        check(off > 0 and off == data_offset(wad, zi.header_offset), "%s offset mismatch" % name)
        data = wad[off:off + zi.file_size]
        check(data[:7] == b"UnityFS", "%s does not start with UnityFS" % name)
        check((binascii.crc32(data) & 0xFFFFFFFF) == zi.CRC, "%s data crc mismatch" % name)
        for d in b["deps"]:
            # these ship inside the 3.1.0 APK's own packs, so the client already has them
            check(d.lower() in mb or d.lower() in BASE_APK_BUNDLES, "%s dep %s not packed" % (name, d))
    for req in ("assets_quest/questroot", "assets_base/baseroot",
                "theme/common/prefabs/library_common", "buildings/prefabs/library_buildings"):
        check(req in paths, "toc path %s not registered" % req)
    if errs:
        print("SELFTEST FAILED:\n  " + "\n  ".join(errs))
        return 1
    print("selftest OK: %d bundles, %d tocs, %d toc paths, wad %d bytes" % (
        len(mb), len(w["tocs"]), len(paths), len(wad)))
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--donor", default=DEFAULT_DONOR)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--cdn", default="CDN_PLACEHOLDER")
    ap.add_argument("--cl", default="822754")
    ap.add_argument("--towers-relics", action="store_true", help="also register tower/relic lookup aliases")
    ap.add_argument("--timeofday", action="store_true",
                    help="also pack primordial/base *_merged time-of-day bundles")
    ap.add_argument("--selftest", action="store_true", help="verify existing output in --out")
    ap.add_argument("--no-selftest", action="store_true", help="skip verification after build")
    args = ap.parse_args()
    if args.selftest:
        return selftest(args)
    rc = build(args)
    if rc == 0 and not args.no_selftest:
        rc = selftest(args)
    return rc


if __name__ == "__main__":
    sys.exit(main())
