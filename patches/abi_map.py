#!/usr/bin/env python3
"""
Translate arm64-v8a addresses and field offsets to their armeabi-v7a equivalents.

Everything in this project (the six native patches, the hook sites in
tools/nativehook/hook.c, and every struct field offset the hook pokes) was
derived against the arm64 library. The APK also ships an armeabi-v7a build of
exactly the same game, so all of it has an equivalent -- at a different address,
and with different field offsets, because pointers are 4 bytes instead of 8.

Both libraries are generated from the SAME `global-metadata.dat`, so the two
Il2CppDumper outputs describe the same types and methods. Two independent keys
fall out of that, and this script uses both:

  * `script.json` lists every method (including generic instantiations) with a
    fully-qualified name. The two builds produce IDENTICAL name sets, so the
    name -- plus the position within a group of same-named overloads -- is an
    exact key. This is the map that gets used.
  * `dump.cs` lists non-generic methods in metadata order, so position alone is
    a key there. `verify` cross-checks the two; they agree on all 83421 methods
    the dumps share, which is what makes the name map trustworthy.

Generic methods MUST come from the name map: they are absent from `dump.cs`, and
several reference-type instantiations share one gshared body, so guessing from
the nearest preceding symbol silently gives the wrong function.

Prepare the two dumps first (Il2CppDumper is a public tool; it needs a .NET
runtime, and the libraries and metadata come out of your own copy of the APK):

    Il2CppDumper lib/arm64-v8a/libil2cpp.so   .../global-metadata.dat  out_a64
    Il2CppDumper lib/armeabi-v7a/libil2cpp.so .../global-metadata.dat  out_v7

Then:

    python3 patches/abi_map.py out_a64 out_v7 verify
    python3 patches/abi_map.py out_a64 out_v7 method 0x123A73C 0xFC21B4
    python3 patches/abi_map.py out_a64 out_v7 fields Hub Act

The address `method` returns is the armv7 *function start*. A mid-function patch
site still has to be located by disassembling that function, because the two
builds do not emit the same instruction sequence -- the ARM32 codegen differs in
branch polarity and scheduling, and it calls the il2cpp null-check throw helper
where the arm64 build branches away. `patches/disasm_fn.py` is the tool for that.
Every armv7 address recorded in `patch_il2cpp.py` was found this way and then
confirmed by reading the surrounding code.
"""
import argparse
import bisect
import collections
import json
import os
import re
import sys

RVA_LINE = re.compile(r"^\s*// RVA: (0x[0-9A-F]+) Offset: 0x[0-9A-F]+ VA: 0x[0-9A-F]+")
TYPE_LINE = re.compile(r"\b(?:class|struct|enum|interface)\s+([\w.<>`]+)")
FIELD_LINE = re.compile(r"(.*);\s*//\s*(0x[0-9A-Fa-f]+)\s*$")

# A hit further than this into a function almost certainly means the address was
# never in the map and we latched onto an unrelated preceding symbol.
MAX_SANE_DELTA = 0x4000


def dump_path(d, name):
    p = d if os.path.isfile(d) else os.path.join(d, name)
    if not os.path.isfile(p):
        sys.exit(f"[!] {p} not found (pass the Il2CppDumper output directory)")
    return p


def parse_methods(path):
    """dump.cs -> [(rva, owning_type, declaration)] in file order (non-generic only)."""
    out = []
    owner = "?"
    pending = None
    for line in open(path, encoding="utf-8", errors="replace"):
        m = RVA_LINE.match(line)
        if m:
            pending = int(m.group(1), 16)
            continue
        stripped = line.strip()
        if not stripped:
            continue
        if pending is not None and not stripped.startswith(("[", "//")):
            out.append((pending, owner, stripped))
            pending = None
        if TYPE_LINE.search(line) and re.match(r"^[\w\[\]<>., ]*\b(class|struct|enum|interface)\b", stripped):
            owner = TYPE_LINE.search(line).group(1)
    return out


def parse_fields(path, type_name):
    """dump.cs -> [(declaration, offset)] for one type, in declaration order."""
    lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
    # The trailing guard stops `Hub` from matching a nested type such as `Hub.Inner`.
    want = re.compile(r"\b(?:class|struct)\s+" + re.escape(type_name) + r"(?![\w.<>`])")
    for i, line in enumerate(lines):
        if want.search(line) and not line.strip().startswith("//"):
            out = []
            for nxt in lines[i + 1:]:
                if nxt.startswith("}"):
                    break
                m = FIELD_LINE.match(nxt.strip())
                if m:
                    out.append((m.group(1).strip(), int(m.group(2), 16)))
            return out
    return []


def name_map(a_dir, b_dir):
    """-> ({a64_addr: v7_addr}, {a64_addr: name}) keyed on method name + overload index."""
    a = json.load(open(dump_path(a_dir, "script.json")))["ScriptMethod"]
    b = json.load(open(dump_path(b_dir, "script.json")))["ScriptMethod"]
    ga, gb = collections.defaultdict(list), collections.defaultdict(list)
    for x in a:
        ga[x["Name"]].append(x["Address"])
    for y in b:
        gb[y["Name"]].append(y["Address"])
    if set(ga) != set(gb):
        only = len(set(ga) ^ set(gb))
        sys.exit(f"[!] the two dumps disagree on {only} method names; "
                 "they must come from the same global-metadata.dat")
    m, names = {}, {}
    for nm, la in ga.items():
        lb = gb[nm]
        if len(la) != len(lb):
            continue          # ambiguous overload set; skip rather than guess
        for u, v in zip(la, lb):
            m.setdefault(u, v)
            names.setdefault(u, nm)
    return m, names


def cmd_verify(args):
    m, _ = name_map(args.a64, args.v7)
    print(f"[*] name map: {len(m)} methods (generics included)")
    a = parse_methods(dump_path(args.a64, "dump.cs"))
    b = parse_methods(dump_path(args.v7, "dump.cs"))
    if len(a) != len(b):
        sys.exit(f"[!] dump.cs method counts differ ({len(a)} vs {len(b)})")
    bad = [i for i, (x, y) in enumerate(zip(a, b)) if x[1:] != y[1:]]
    if bad:
        sys.exit(f"[!] {len(bad)} dump.cs declarations differ; first at index {bad[0]}")
    print(f"[*] dump.cs positional map: {len(a)} methods, all declarations match")
    agree = disagree = absent = 0
    for (ra, _, _), (rb, _, _) in zip(a, b):
        if ra not in m:
            absent += 1
        elif m[ra] == rb:
            agree += 1
        else:
            disagree += 1
    print(f"[*] cross-check: {agree} agree, {disagree} disagree, {absent} absent from the name map")
    if disagree:
        sys.exit("[!] the two mappings disagree; do not trust either")
    print("[+] both mappings agree")


def cmd_method(args):
    m, names = name_map(args.a64, args.v7)
    starts = sorted(m)
    for q in args.addr:
        addr = int(q, 16)
        if addr in m:
            print(f"arm64 {q} -> armv7 0x{m[addr]:X}\n    {names[addr]}")
            continue
        i = bisect.bisect_right(starts, addr) - 1
        if i < 0:
            print(f"arm64 {q}: no match")
            continue
        st = starts[i]
        delta = addr - st
        if delta > MAX_SANE_DELTA:
            print(f"arm64 {q}: no match (nearest symbol is 0x{delta:X} back; "
                  "treating as unmapped rather than guessing)")
            continue
        print(f"arm64 {q} -> armv7 0x{m[st]:X} (+0x{delta:X} into the function)\n"
              f"    {names[st]}")


def cmd_fields(args):
    da, dv = dump_path(args.a64, "dump.cs"), dump_path(args.v7, "dump.cs")
    for t in args.type:
        fa, fb = parse_fields(da, t), parse_fields(dv, t)
        if not fa:
            print(f"[!] type {t} not found")
            continue
        if len(fa) != len(fb):
            print(f"[!] {t}: field counts differ ({len(fa)} vs {len(fb)})")
            continue
        print(f"=== {t}")
        for (decl, oa), (_, ob) in zip(fa, fb):
            print(f"    arm64 0x{oa:<5X} -> armv7 0x{ob:<5X}  {decl}")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("a64", help="Il2CppDumper output dir for lib/arm64-v8a/libil2cpp.so")
    ap.add_argument("v7", help="Il2CppDumper output dir for lib/armeabi-v7a/libil2cpp.so")
    sub = ap.add_subparsers(dest="mode", required=True)

    sub.add_parser("verify", help="cross-check the name map against the dump.cs order")
    m = sub.add_parser("method", help="translate arm64 addresses to armv7")
    m.add_argument("addr", nargs="+", help="arm64 RVA(s), hex")
    f = sub.add_parser("fields", help="show a type's field offsets in both ABIs")
    f.add_argument("type", nargs="+", help="type name(s), e.g. Hub")

    args = ap.parse_args()
    {"verify": cmd_verify, "method": cmd_method, "fields": cmd_fields}[args.mode](args)


if __name__ == "__main__":
    main()
