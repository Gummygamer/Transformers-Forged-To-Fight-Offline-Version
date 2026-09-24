#!/usr/bin/env python3
"""Audited compatibility port: real behaviour from the 2.0.2 Mono assemblies.

WHAT THIS IS
------------
The 9.2 stub pipeline (tools/stubs92) reproduces the 9.2 *data contract* -- class
names, namespaces, serialized field layouts -- so 9.2 bundles deserialize. Every
stub method body is empty, so nothing renders and nothing responds to input. The
2.0.2 client is Mono, not IL2CPP, so its assemblies decompile to real C# with real
bodies. This tool emits those bodies into the Unity project.

WHAT THIS IS NOT
----------------
It is not a proof that 2.0.2 and 9.2 types are interchangeable, and not a proof
that a ported class works at runtime. It is an audited compatibility port: every
field mismatch, base-type mismatch and unresolved dependency is REPORTED.

Two rules keep the audit honest:

  1. The 9.2 contract comes from assembly METADATA (tools/port202/ContractDump.cs
     over the 9.2 DummyDll), recorded as an immutable tracked snapshot. It is never
     read back out of the filtered stub tree: the stub generator skips ported
     types, so those files vanish on the next cycle and a stub-derived audit would
     silently start reporting "no 9.2-only fields" while the fields were still
     missing.
  2. Nothing is rewritten speculatively. An earlier revision decompiled without a
     reference path and then tried to repair the resulting malformed C#; with
     -r pointing at the 2.0.2 Managed directory the whole assembly decompiles with
     ZERO artifacts (NGUIMath: 233 IL_ comments, 231 unknown-result notes, 105
     escapes -> all 0). This tool GUARDS against artifacts instead of patching them.

DECOMPILER REFERENCES ARE MANDATORY, and so is the contract snapshot. Neither is
inferred from generated output.

USAGE
-----
    python3 tools/port202/port_202.py
    python3 tools/port202/port_202.py --all
    python3 tools/port202/port_202.py --refresh-contract
    python3 tools/port202/port_202.py --verify-stability
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
UNITY_PROJECT = REPO_ROOT / "unity" / "StoryPort"
FIRSTPASS_STUBS = UNITY_PROJECT / "Assets" / "Plugins" / "Firstpass92"
OUT_DIR = UNITY_PROJECT / "Assets" / "Plugins" / "Port202"
TOOL_DIR = REPO_ROOT / "tools" / "port202"
# Everything this tool emits is generated and stays under build/ (git-ignored):
# the user's rule is that only core authored code is committed, never generated
# artifacts. "Immutable contract source" means the contract is derived from fixed
# input metadata rather than from filtered output -- it does not mean committing
# a generated dump.
WORK_DIR = REPO_ROOT / "build" / "port202"
MANIFEST = WORK_DIR / "ported.txt"
REPORT = WORK_DIR / "port-report.txt"
CONTRACT_92 = WORK_DIR / "contract92.json"
UNIVERSE = WORK_DIR / "type-universe.txt"
MANAGED_DIR = WORK_DIR / "managed"              # 2.0.2 reference assemblies
DECOMPILED = WORK_DIR / "firstpass"
FINGERPRINT = WORK_DIR / "firstpass.fingerprint.json"
CONTRACT_202 = WORK_DIR / "contract202.json"
HASHES = WORK_DIR / "ported-hashes.json"

APK_202 = REPO_ROOT / "com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk"
MANAGED_IN_APK = "assets/bin/Data/Managed"
DLL_NAME = "Assembly-CSharp-firstpass.dll"

# Authoritative 9.2 metadata: DUMP INPUTS, never generated output.
DUMMY_DLL_92 = REPO_ROOT / "build" / "dumps" / "v7" / "DummyDll" / DLL_NAME

ILSPY = REPO_ROOT / "build" / "mono-investigation" / "tooling" / "ilspycmd"
CONTRACT_TOOL = TOOL_DIR / "bin" / "Debug" / "net8.0" / "ContractDump.dll"
DOTNET = Path.home() / ".dotnet" / "dotnet"

ARTIFACT_PATTERNS = {
    "//IL_ comments": re.compile(r"//IL_[0-9a-fA-F]+:"),
    "unresolved-type notes": re.compile(r"Unknown result type|Expected O, but got"),
    "IL2CPP-style escapes": re.compile(r"_00[0-9A-Fa-f]{2}[A-Za-z]"),
    "by-ref receiver casts": re.compile(r"\(\([A-Za-z_][\w\.<>]*\)\(ref "),
    "named op_ conversions": re.compile(r"\.op_(?:Implicit|Explicit|Equality|Inequality)\("),
}

CURATED = """
UIRect UIPanel UIWidget UIDrawCall UIGeometry UISpriteData UIWidgetContainer
UIAtlas UIFont UISprite UILabel UITexture UIRoot UICamera UIAnchor UIStretch
UISpriteAnimation UIScrollView UIGrid UITable UILayout UICenterOnChild
UIPlayTween UIPlayAnimation UIPlaySound UIProgressBar UISlider UIButton
UIButtonColor UIButtonOffset UIButtonRotation UIButtonScale UIToggle UIInput
UIPopupList UIKeyNavigation UIDragObject UIDragScrollView UICursor
NGUITools NGUIMath NGUIText NGUIDebug NGUISettings
BetterList BMFont BMGlyph BMSymbol ByteReader EventDelegate
UITweener TweenPosition TweenAlpha TweenColor TweenScale TweenRotation
TweenWidth TweenHeight TweenFOV TweenOrthoSize TweenTransform TweenVolume
TweenLetters ActiveAnimation AnimParams AnimatedAlpha AnimatedColor
AnimatedWidget AnimatePosition AnimateRotation AnimateScale
PropertyBinding SpringPanel SpringPosition LanguageSelection Localization
AlignUIElements AlignUIElementColumns BidirectionalLayout
UIEventListener UIForwardEvents UIHoveredObjectTrigger DynamicScrollView
UIViewport UIPanelAlpha UISoundVolume UISavedOption UIRectEditor
EZAnimation EZAnimator EZTransition EZTransitionList EZLinkedList
EZLinkedListNode EZLinkedListIterator RealTime Crash Shake PunchPosition
PunchRotation PunchScale FadeAudio IGUIHelper EBGWidgetContainer UIBasicSprite
""".split()


def ilspy_env():
    return {**os.environ, "DOTNET_ROOT": str(DOTNET.parent)}


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def dir_fingerprint(d: Path) -> str:
    h = hashlib.sha256()
    for p in sorted(d.glob("*.dll")):
        h.update(p.name.encode())
        h.update(sha256_file(p).encode())
    return h.hexdigest()


# --------------------------------------------------------------------------
# inputs
# --------------------------------------------------------------------------
def ensure_managed() -> tuple[Path, Path]:
    dll = MANAGED_DIR / DLL_NAME
    if dll.exists() and len(list(MANAGED_DIR.glob("*.dll"))) > 5:
        return MANAGED_DIR, dll
    if not APK_202.exists():
        sys.exit(f"missing the 2.0.2 APK at {APK_202}")
    MANAGED_DIR.mkdir(parents=True, exist_ok=True)
    print(f"  extracting {MANAGED_IN_APK} from the 2.0.2 APK")
    r = run(["unzip", "-o", "-j", str(APK_202), f"{MANAGED_IN_APK}/*.dll", "-d", str(MANAGED_DIR)])
    if r.returncode != 0 or not dll.exists():
        sys.exit(f"could not extract the 2.0.2 assemblies\n{r.stdout[-600:]}{r.stderr[-600:]}")
    print(f"  {len(list(MANAGED_DIR.glob('*.dll')))} reference assemblies")
    return MANAGED_DIR, dll


def ensure_decompiled(ilspy: Path, dll: Path, references: Path) -> None:
    version = run([str(ilspy), "--version"], env=ilspy_env()).stdout.strip()
    want = {"dll_sha256": sha256_file(dll),
            "references_sha256": dir_fingerprint(references),
            "ilspy_version": version, "langversion": "CSharp8_0", "with_references": True}
    if DECOMPILED.exists() and FINGERPRINT.exists():
        try:
            have = json.loads(FINGERPRINT.read_text())
        except (OSError, ValueError):
            have = None
        if have == want and any(DECOMPILED.glob("*.cs")):
            print(f"  reusing fingerprinted decompile ({len(list(DECOMPILED.glob('*.cs')))} files)")
            return
        print("  cached decompile does not match current inputs; redoing it")

    tmp = WORK_DIR / "firstpass.tmp"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)
    print(f"  decompiling {dll.name} with -r {references.name} (real bodies)")
    r = run([str(ilspy), "-p", "-lv", "CSharp8_0", "-r", str(references),
             "-o", str(tmp), str(dll)], env=ilspy_env())
    produced = sorted(tmp.glob("*.cs"))
    if r.returncode != 0 or not produced:
        shutil.rmtree(tmp, ignore_errors=True)
        sys.exit(f"ilspycmd failed (exit {r.returncode}); cache left untouched\n"
                 f"{r.stdout[-1000:]}\n{r.stderr[-1000:]}")
    if DECOMPILED.exists():
        shutil.rmtree(DECOMPILED)
    tmp.rename(DECOMPILED)
    FINGERPRINT.write_text(json.dumps(want, indent=2))
    print(f"  {len(produced)} files (fingerprint recorded)")


def dump_contract(assembly: Path, out: Path) -> dict:
    if not CONTRACT_TOOL.exists():
        sys.exit(f"missing {CONTRACT_TOOL}; build it with:\n"
                 f"  {DOTNET} build tools/port202/ContractDump.csproj")
    r = run([str(DOTNET), str(CONTRACT_TOOL), str(assembly), str(out)])
    if r.returncode != 0 or not out.exists():
        sys.exit(f"ContractDump failed on {assembly.name}\n{r.stdout[-800:]}{r.stderr[-800:]}")
    return json.loads(out.read_text())["types"]


def ensure_contract92(assembly: Path, refresh: bool) -> dict:
    """The 9.2 contract is an immutable tracked snapshot.

    It must never be derived from Assets/Plugins/Firstpass92, because the stub
    generator skips ported types: on the next cycle those files are gone and a
    stub-derived audit would report "no 9.2-only fields" while the fields were
    still missing from the ported classes.
    """
    if refresh or not CONTRACT_92.exists():
        types = dump_contract(assembly, CONTRACT_92)
        print(f"  recorded 9.2 contract snapshot: {len(types)} types -> {CONTRACT_92.name}")
        return types
    return json.loads(CONTRACT_92.read_text())["types"]


def load_universe(ilspy: Path, refresh: bool) -> list[str]:
    if refresh or not UNIVERSE.exists():
        types = root_types_of(DUMMY_DLL_92, ilspy)
        UNIVERSE.write_text(
            "# Root-level (namespace-less) types in the 9.2 Assembly-CSharp-firstpass\n"
            "# DummyDll. Recorded from the DUMP INPUT, never from generated stubs.\n"
            "# Refresh deliberately with: --refresh-contract\n"
            + "\n".join(types) + "\n", encoding="utf-8")
        print(f"  recorded 9.2 type universe: {len(types)} types")
    return [l.strip() for l in UNIVERSE.read_text().splitlines()
            if l.strip() and not l.startswith("#")]


def root_types_of(dll: Path, ilspy: Path) -> list[str]:
    r = run([str(ilspy), "-l", "c", str(dll)], env=ilspy_env())
    if r.returncode != 0:
        sys.exit(f"ilspycmd -l failed on {dll.name}:\n{r.stderr[-1200:]}")
    out = set()
    for line in r.stdout.splitlines():
        line = line.strip()
        if line.startswith("Class "):
            name = line[len("Class "):].strip()
            if "." not in name and name != "<Module>":
                out.add(name)
    return sorted(out)


def audit_artifacts(sources: Path) -> dict[str, int]:
    found = {}
    for name, rx in ARTIFACT_PATTERNS.items():
        hits = 0
        for f in sources.glob("*.cs"):
            hits += len(rx.findall(f.read_text(encoding="utf-8-sig", errors="replace")))
        if hits:
            found[name] = hits
    return found


# --------------------------------------------------------------------------
# contract comparison (metadata, not source regex)
# --------------------------------------------------------------------------
def contract_index(contract: dict) -> dict:
    """Map a bare type name to its contract entry.

    Cecil keys generic types by arity (`BetterList`1`), while ilspycmd's type list
    and the generated stub file stems use the bare name. Nested types are skipped
    here so `BetterList`1/CacheableType`1` can never be mistaken for a root type.
    """
    idx: dict[str, list[tuple[str, dict]]] = {}
    for k, v in contract.items():
        if "/" in k:
            continue
        idx.setdefault(k.split("`")[0], []).append((k, v))
    return idx


def lookup(idx: dict, name: str, report: dict) -> dict | None:
    hits = idx.get(name) or []
    if len(hits) == 1:
        return hits[0][1]
    if len(hits) > 1:
        exact = [v for k, v in hits if k == name]
        if len(exact) == 1:
            return exact[0]
        report["failures"].append(
            f"{name}: ambiguous in the contract snapshot ({[k for k, _ in hits]})")
        return None
    return None


def is_serialization_candidate(f: dict) -> bool:
    """Whether a field is a CANDIDATE for Unity serialization, from real metadata.

    Unity serializes instance fields that are public or [SerializeField], and skips
    static, const, readonly and [NonSerialized] ones. [HideInInspector] only affects
    Inspector VISIBILITY -- it neither enables nor disables serialization, so it is
    deliberately not consulted here.

    This is a candidate filter, not a verdict: Unity also refuses field types it
    cannot serialize (interfaces, dictionaries, open generics), which is not
    checked. Treat a False as "not serialized", a True as "probably serialized".
    """
    if f["static"] or f["const"] or f["readonly"] or f["nonserialized"]:
        return False
    return f["public"] or f["serializefield"]


def compare_type(name: str, a: dict, b: dict) -> dict:
    """Field/base differences between the 9.2 (a) and 2.0.2 (b) type."""
    fa = {f["name"]: f for f in a["fields"]}
    fb = {f["name"]: f for f in b["fields"]}
    only92 = [fa[n] for n in fa if n not in fb]
    only202 = [fb[n] for n in fb if n not in fa]
    retyped = [(n, fa[n], fb[n]) for n in fa if n in fb and fa[n]["type"] != fb[n]["type"]]
    return {
        "base_match": a["base"] == b["base"],
        "base92": a["base"], "base202": b["base"],
        "only92": only92, "only202": only202, "retyped": retyped,
    }


# --------------------------------------------------------------------------
# source placement
# --------------------------------------------------------------------------
FIELD_DECL = re.compile(
    r"(?:^|\n)[ \t]*(?:\[[^\]]*\][ \t]*\n[ \t]*)*"
    r"(?:public|private|protected|internal)[ \t]+"
    r"(?:static[ \t]+|readonly[ \t]+|const[ \t]+)*"
    r"[^\n;{}()]*?[ \t](?P<name>[A-Za-z_]\w*)[ \t]*(?:=[^;]*)?;")


def class_body_span(src: str, type_name: str) -> tuple[int, int] | None:
    """Character span of the OUTER class body for type_name (brace matched)."""
    m = re.search(rf"\b(?:class|struct)\s+{re.escape(type_name)}\b[^{{;]*\{{", src)
    if not m:
        return None
    start = m.end() - 1
    depth, i = 0, start
    while i < len(src):
        c = src[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return start, i
        i += 1
    return None


def declared_in_scope(src: str, span: tuple[int, int], field_name: str) -> bool:
    body = src[span[0]:span[1]]
    return any(m.group("name") == field_name for m in FIELD_DECL.finditer(body))


def csharp_type_name(cecil_name: str) -> str:
    """Render a Cecil type name as valid C#.

    Cecil reports metadata names: generic arity carries a backtick (`BetterList`1`)
    and nested types use a slash (`GameObjectItemPool/Item`). Neither is legal C#,
    so injecting the raw name produces syntax errors (CS1056 "Unexpected character
    '`'"). Strip the arity marker and convert the nesting separator.
    """
    return re.sub(r"`\d+", "", cecil_name).replace("/", ".")


def field_declaration(f: dict) -> str:
    """A faithful declaration for a 9.2 field, including its SerializeField state."""
    attrs = "[SerializeField] " if f["serializefield"] and not f["public"] else ""
    vis = "public" if f["public"] else "private"
    return f"{attrs}{vis} {csharp_type_name(f['type'])} {f['name']};"


def inject_fields(ported: str, type_name: str, fields: list[dict], report: dict) -> tuple[str, list[str]]:
    """Add 9.2 instance fields the 2.0.2 class does not declare.

    Scoped to the outer class body: a whole-file search would match fields that
    belong to a nested type and is not a declaration test.
    """
    span = class_body_span(ported, type_name)
    if span is None:
        report["failures"].append(
            f"{type_name}: could not locate the outer class body to place {len(fields)} field(s)")
        return ported, []
    added = []
    for f in fields:
        if declared_in_scope(ported, span, f["name"]):
            continue
        decl = field_declaration(f)
        ported = ported[:span[0] + 1] + \
            f"\n\t// 9.2-only field preserved from the 9.2 contract snapshot\n\t{decl}\n" + \
            ported[span[0] + 1:]
        span = (span[0], span[1] + len(decl) + 70)
        added.append(f["name"])
    return ported, added


def inject_reading_direction(ported: str, type_name: str) -> str:
    """Keep UILabel's serialized 9.2 enum when porting the older NGUI class."""
    if type_name != "UILabel" or "ReadingDirection readingDirection" not in ported:
        return ported
    if re.search(r"\benum\s+ReadingDirection\b", ported):
        return ported
    span = class_body_span(ported, type_name)
    if span is None:
        return ported
    declaration = (
        "\n\tpublic enum ReadingDirection\n\t{\n"
        "\t\tAutomatic,\n\t\tForceLeftToRight,\n\t\tForceRightToLeft\n\t}\n"
    )
    return ported[:span[0] + 1] + declaration + ported[span[0] + 1:]


def repair_known_decompiler_shapes(ported: str, type_name: str) -> str:
    """Normalize source/API differences between 2.0.2 NGUI and the 9.2 project."""
    if type_name == "DynamicScrollView":
        for old, new in (
            ("public void Initialize(", "public virtual void Initialize("),
            ("private Vector3 GetPositionForIndex(", "public virtual Vector3 GetPositionForIndex("),
            ("private IEnumerator DoRecreateScrollView(", "protected virtual IEnumerator DoRecreateScrollView("),
            ("private void CheckScrollViewNeedsUpdate(", "protected virtual void CheckScrollViewNeedsUpdate("),
            ("private GameObjectItemPool.Item ReleaseItem(", "protected virtual GameObjectItemPool.Item ReleaseItem("),
            ("private void UpdateDisplayedScrollViewItems(", "protected virtual void UpdateDisplayedScrollViewItems("),
            ("private void OnAboutToUpdateDisplayedItemPosition(", "protected virtual void OnAboutToUpdateDisplayedItemPosition("),
            ("private bool CalculateNewIndexOffsets(", "protected virtual bool CalculateNewIndexOffsets("),
            ("private void Update()", "protected virtual void Update()"),
        ):
            ported = ported.replace(old, new)
        ported = re.sub(
            r"\bvirtual\s+GameObject\s+ControllerInputHandler\.get_gameObject\(\)\s*"
            r"\{\s*return\s+base\.gameObject;\s*\}",
            "GameObject ControllerInputHandler.gameObject\\n\\t{\\n"
            "\\t\\tget { return base.gameObject; }\\n\\t}",
            ported,
        )
        ported = ported.replace("item.widget", "WidgetFor(item)")
        if "WidgetFor(item)" in ported and "private UIWidget WidgetFor(" not in ported:
            span = class_body_span(ported, type_name)
            if span is not None:
                helper = (
                    "\n\tprivate UIWidget WidgetFor(GameObjectItemPool.Item item)\n"
                    "\t{\n"
                    "\t\tif (item == null || item.gameObject == null) return null;\n"
                    "\t\tif (_cachedWidgets == null) _cachedWidgets = new Dictionary<GameObjectItemPool.Item, UIWidget>();\n"
                    "\t\tif (!_cachedWidgets.TryGetValue(item, out UIWidget widget))\n"
                    "\t\t{\n"
                    "\t\t\twidget = item.gameObject.GetComponent<UIWidget>();\n"
                    "\t\t\t_cachedWidgets[item] = widget;\n"
                    "\t\t}\n"
                    "\t\treturn widget;\n"
                    "\t}\n"
                )
                close = span[1] - 1
                ported = ported[:close] + helper + ported[close:]
        if "OnAboutToUpdateDisplayedItemPosition(" not in ported:
            span = class_body_span(ported, type_name)
            if span is not None:
                hook = (
                    "\n\tprotected virtual void OnAboutToUpdateDisplayedItemPosition("
                    "int index, GameObjectItemPool.Item item)\n\t{\n\t}\n"
                )
                close = span[1] - 1
                ported = ported[:close] + hook + ported[close:]
    if type_name in {"NGUITools", "NGUIMath"}:
        ported = re.sub(r"(?<![.\w])Debug\.", "UnityEngine.Debug.", ported)
    if type_name == "NGUITools":
        ported = ported.replace(
            "Application.platform != RuntimePlatform.WindowsWebPlayer && Application.platform != RuntimePlatform.OSXWebPlayer",
            "Application.platform != RuntimePlatform.WebGLPlayer",
        )
    if type_name == "UIPanel":
        ported = ported.replace(" || Application.platform == RuntimePlatform.WindowsWebPlayer", "")
    if type_name == "UIInput":
        ported = ported.replace("mipmap: false", "mipChain: false")
    return ported


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--refresh-contract", action="store_true",
                    help="re-record contract92.json and type-universe.txt from the dump inputs")
    ap.add_argument("--verify-stability", action="store_true",
                    help="compare generated file hashes with the previous run")
    ap.add_argument("--out", default=str(OUT_DIR))
    args = ap.parse_args()

    ilspy = ILSPY if ILSPY.exists() else Path("ilspycmd")
    references, dll = ensure_managed()
    ensure_decompiled(ilspy, dll, references)

    artifacts = audit_artifacts(DECOMPILED)
    if artifacts:
        print("  REFUSING TO PORT: the decompile shows reference-resolution failures:")
        for k, v in artifacts.items():
            print(f"    {k}: {v}")
        return 1
    print("  decompile is clean: no ilspy artifacts")

    contract92 = contract_index(ensure_contract92(DUMMY_DLL_92, args.refresh_contract))
    contract202 = contract_index(dump_contract(dll, CONTRACT_202))
    universe = load_universe(ilspy, args.refresh_contract)

    shared = sorted(set(universe) & set(root_types_of(dll, ilspy)))
    print(f"  9.2 universe: {len(universe)}; also in 2.0.2 firstpass: {len(shared)}")

    previously = set()
    if MANIFEST.exists():
        previously = {l.strip() for l in MANIFEST.read_text().splitlines()
                      if l.strip() and not l.startswith("#")}
    chosen = set(shared if args.all else [t for t in shared if t in set(CURATED)])
    chosen |= {t for t in previously if t in shared}
    chosen = sorted(chosen)
    lost = sorted(previously - set(chosen))
    print(f"  porting {len(chosen)} types" + (f" (lost: {lost})" if lost else ""))

    out = Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    report = {"failures": [], "injected": [], "excluded": [], "base_mismatch": [],
              "retyped_serialized": [], "only202_serialized": [], "no_source": [],
              "dependencies": {}}
    written = []
    for t in chosen:
        src = DECOMPILED / f"{t}.cs"
        if not src.exists():
            report["no_source"].append(t)
            continue
        body = src.read_text(encoding="utf-8-sig")

        a, b = lookup(contract92, t, report), lookup(contract202, t, report)
        if a and b:
            diff = compare_type(t, a, b)
            if not diff["base_match"]:
                report["base_mismatch"].append(
                    f"{t}: 9.2 base {diff['base92']} vs 2.0.2 base {diff['base202']}")
            for f in diff["only92"]:
                if f["static"] or f["const"] or f["compilergenerated"]:
                    report["excluded"].append(
                        f"{t}.{f['name']}: 9.2-only but "
                        f"{'static' if f['static'] else 'const' if f['const'] else 'compiler-generated'}"
                        f" -> not injected")
                else:
                    report["injected"].append(
                        f"{t}.{f['name']} ({f['type']}) "
                        f"{'SERIALIZED' if is_serialization_candidate(f) else 'not serialized'}"
                        + (f" [FormerlySerializedAs {f['formerlyserializedas']}]"
                           if f["formerlyserializedas"] else ""))
            for f in diff["only202"]:
                if is_serialization_candidate(f):
                    report["only202_serialized"].append(f"{t}.{f['name']} ({f['type']})")
            for n, x, y in diff["retyped"]:
                if is_serialization_candidate(x):
                    report["retyped_serialized"].append(
                        f"{t}.{n}: 9.2 {x['type']} vs 2.0.2 {y['type']}")

            to_inject = [f for f in diff["only92"]
                         if not (f["static"] or f["const"] or f["compilergenerated"])]
            body, added = inject_fields(body, t, to_inject, report)
            body = inject_reading_direction(body, t)
            body = repair_known_decompiler_shapes(body, t)
        elif not a:
            report["failures"].append(f"{t}: absent from the 9.2 contract snapshot")

        (out / f"{t}.cs").write_text(body, encoding="utf-8")
        written.append(t)

    remaining = {p.stem for p in FIRSTPASS_STUBS.glob("*.cs")} - set(written)
    for t in written:
        txt = (out / f"{t}.cs").read_text(encoding="utf-8")
        used = sorted({n for n in remaining if re.search(rf"\b{re.escape(n)}\b", txt)})
        if used:
            report["dependencies"][t] = used

    hashes = {t: sha256_file(out / f"{t}.cs") for t in written}
    if args.verify_stability and HASHES.exists():
        prev = json.loads(HASHES.read_text())
        changed = sorted(t for t in hashes if prev.get(t) != hashes[t])
        gone = sorted(set(prev) - set(hashes))
        print(f"  stability: {len(changed)} changed, {len(gone)} dropped, "
              f"{len(set(hashes) - set(prev))} new")
        if changed:
            print(f"    changed: {changed[:10]}")
        if gone:
            print(f"    dropped: {gone[:10]}")
    HASHES.write_text(json.dumps(hashes, indent=2, sort_keys=True))

    MANIFEST.write_text(
        "# Types whose real bodies are ported from the 2.0.2 Mono assemblies by\n"
        "# tools/port202/port_202.py. gen_92_stubs.py skips these so the generated\n"
        "# 9.2 stubs never collide with the ported implementations.\n"
        "# Audited per type: see tools/port202/port-report.txt\n"
        "# Regenerate with: python3 tools/port202/port_202.py\n"
        + "\n".join(written) + "\n", encoding="utf-8")

    n_stub_calls = sum(len(v) for v in report["dependencies"].values())
    lines = [
        "PORT 202 REPORT",
        "===============",
        f"types ported                     : {len(written)}",
        f"types with no 2.0.2 source       : {len(report['no_source'])}",
        f"generated files differing between cycles: see --verify-stability",
        "",
        "This is an AUDITED COMPATIBILITY PORT, not a correctness proof.",
        "",
        "STUB CALLS THROW, THEY ARE NOT NO-OPS. A generated non-void stub body is",
        "literally `throw null;` (the EB.Time failure proved this at runtime: a stub",
        "getter threw from EBLight.LateUpdate). A void stub body does nothing. So a",
        "ported class reaching a still-stubbed dependency will either throw or",
        "silently skip work. The dependency list below is produced by matching type",
        "NAMES in the generated source: those are CANDIDATES to trace, not call-graph",
        "proof. Trace the active path of the screen being built and implement that",
        "closure; do not import unrelated systems.",
        "",
        "9.2-only fields INJECTED into the ported class:",
    ]
    lines += [f"  {x}" for x in report["injected"]] or ["  none"]
    lines += ["", "9.2-only fields NOT injected (static / const / compiler-generated):"]
    lines += [f"  {x}" for x in report["excluded"]] or ["  none"]
    lines += ["", "Base type mismatches (UNRESOLVED until each is judged):"]
    lines += [f"  {x}" for x in report["base_mismatch"]] or ["  none"]
    lines += ["", "Serialization candidates whose TYPE changed (UNRESOLVED; 9.2 type wins for layout):"]
    lines += [f"  {x}" for x in report["retyped_serialized"]] or ["  none"]
    lines += ["", "2.0.2-only serialization candidates (NOT in the 9.2 contract).",
        "  Whether these matter is UNRESOLVED: they are extra data 9.2 dropped, so",
        "  the prefabs cannot supply them, but code may still read them."]
    lines += [f"  {x}" for x in report["only202_serialized"]] or ["  none"]
    lines += ["", f"Ported classes referencing {n_stub_calls} name(s) of still-stubbed types:"]
    if report["dependencies"]:
        for t, used in sorted(report["dependencies"].items()):
            lines.append(f"  {t}: {', '.join(used[:12])}" + (" ..." if len(used) > 12 else ""))
    else:
        lines.append("  none")
    if report["failures"]:
        lines += ["", "AUDIT FAILURES (resolve before trusting this port):"]
        lines += [f"  {x}" for x in report["failures"]]
    if report["no_source"]:
        lines += ["", "No 2.0.2 source found (left as generated stubs):"]
        lines += [f"  {x}" for x in report["no_source"]]
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"  wrote {len(written)} files -> {out.relative_to(REPO_ROOT)}")
    print(f"  injected {len(report['injected'])} 9.2-only field(s); "
          f"excluded {len(report['excluded'])}; base mismatches {len(report['base_mismatch'])}; "
          f"retyped serialized {len(report['retyped_serialized'])}")
    if report["failures"]:
        print(f"  AUDIT FAILURES: {len(report['failures'])} (see {REPORT.name})")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
