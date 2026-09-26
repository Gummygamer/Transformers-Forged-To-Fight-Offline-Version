#!/usr/bin/env python3
"""Generate Unity 2020-compatible stub classes from 9.2 IL2CPP DummyDll assemblies.

Pipeline: StripCG (Mono.Cecil) -> ilspycmd -p -lv CSharp8_0 -r -> attribute cleanup -> copy

Usage:
    python3 tools/stubs92/gen_92_stubs.py [OPTIONS]

Options:
    --dummy-dll-dir PATH     Directory containing Il2CppDumper v7 DummyDll assemblies
    --ilspycmd PATH          Path to ilspycmd.dll
    --unity-project PATH     Unity project root directory
    --dotnet PATH            Path to dotnet executable

All paths default to the standard locations in this repository's build/ tree.
Generated stubs are derived artifacts and should NOT be committed to git.
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
STRIP_CG_PROJECT = REPO_ROOT / "tools" / "stubs92" / "StripCG.csproj"
IMPLEMENTED_MANIFEST = REPO_ROOT / "tools" / "stubs92" / "implemented.txt"
PORTED_MANIFEST = REPO_ROOT / "build" / "port202" / "ported.txt"
PORTED_OUTPUT = REPO_ROOT / "unity" / "StoryPort" / "Assets" / "Plugins" / "Port202"
DEFAULT_UNITY_PROJECT = REPO_ROOT / "unity" / "StoryPort"


def find_dotnet(cli_arg):
    """Resolve dotnet from CLI arg, $DOTNET env var, PATH, or ~/.dotnet fallback."""
    if cli_arg:
        p = Path(cli_arg)
        if p.exists():
            return p
        sys.exit(f"ERROR: dotnet not found at specified path: {cli_arg}")

    env_dotnet = os.environ.get("DOTNET")
    if env_dotnet:
        p = Path(env_dotnet)
        if p.exists():
            return p

    # Try PATH
    result = subprocess.run(["which", "dotnet"], capture_output=True, text=True)
    if result.returncode == 0:
        return Path(result.stdout.strip())

    # Fallback
    fallback = Path.home() / ".dotnet" / "dotnet"
    if fallback.exists():
        return fallback

    sys.exit("ERROR: dotnet not found. Specify with --dotnet, set $DOTNET, or install .NET SDK.")


def find_ilspycmd(cli_arg):
    """Resolve ilspycmd from CLI arg or default .store location."""
    if cli_arg:
        p = Path(cli_arg)
        if p.exists():
            return p
        sys.exit(f"ERROR: ilspycmd not found at specified path: {cli_arg}")

    pattern = str(REPO_ROOT / "build" / "mono-investigation" / "tooling" / ".store" /
                  "ilspycmd" / "*" / "ilspycmd" / "*" / "tools" / "net8.0" / "any" / "ilspycmd.dll")
    matches = sorted(glob.glob(pattern))
    if not matches:
        sys.exit(f"ERROR: ilspycmd not found. Specify with --ilspycmd or run decompilation export first.")
    return Path(matches[-1])


def run(cmd, description):
    print(f"  {description}...")
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"  FAILED: {result.stderr[:1000]}")
        sys.exit(f"FATAL: {description} failed. Aborting pipeline.")
    if result.stdout.strip():
        for line in result.stdout.strip().split('\n'):
            if 'latest version' not in line.lower():
                print(f"    {line}")


# Unity's real reference assemblies. Used ahead of the IL2CPP DummyDll's BCL
# copies, whose signatures are degraded (IList.Item loses its index parameter),
# so that reconstructed explicit indexers and accessors resolve correctly.
BCL_REFERENCE_DIR = (REPO_ROOT / "build" / "mono-investigation" / "unity-2020.3.31f1"
                     / "Editor" / "Data" / "MonoBleedingEdge" / "lib" / "mono" / "4.7.1-api")


def strip_cg(dotnet, input_dll, output_dll, search_dir):
    """Strip compiler-generated types at the IL level using Mono.Cecil.

    The real BCL reference directory is passed so the resolver prefers exact BCL
    signatures over the DummyDll's degraded copies."""
    cmd = [str(dotnet), "run", "--project", str(STRIP_CG_PROJECT), "--",
           str(input_dll), str(output_dll), str(search_dir)]
    if BCL_REFERENCE_DIR.exists():
        cmd.append(str(BCL_REFERENCE_DIR))
    run(cmd, f"StripCG {input_dll.name}")


def decompile(dotnet, input_dll, output_dir, reference_dir, ilspycmd):
    """Decompile a stripped DLL to C# source files.

    The real BCL references come first: ilspy asks the reference assemblies what
    a BCL member looks like, and the DummyDll's copies drop parameters (so e.g.
    IList.Item looks like a plain property instead of an indexer)."""
    cmd = [str(dotnet), str(ilspycmd), "-p", "-lv", "CSharp8_0"]
    if BCL_REFERENCE_DIR.exists():
        cmd += ["-r", str(BCL_REFERENCE_DIR)]
    cmd += ["-r", str(reference_dir), "-o", str(output_dir), str(input_dll)]
    run(cmd, f"Decompile {input_dll.name}")


def convert_explicit_layout_fieldoffsets(content):
    """For [StructLayout(LayoutKind.Explicit)] structs (including (LayoutKind)2 form),
    convert IL2CPP FieldOffset attributes to real [FieldOffset(N)]. Also normalize
    the StructLayout attribute itself."""
    # Normalize [StructLayout((LayoutKind)2)] to [StructLayout(LayoutKind.Explicit)]
    content = re.sub(
        r'\[StructLayout\(\(LayoutKind\)2\)\]',
        '[StructLayout(LayoutKind.Explicit)]',
        content)

    # For explicit-layout structs, convert FieldOffset(Offset = "0xNN") to FieldOffset(N)
    # We process line-by-line to track whether we're inside an explicit struct
    lines = content.split('\n')
    in_explicit_struct = False
    brace_depth = 0
    result = []

    for line in lines:
        if '[StructLayout(LayoutKind.Explicit)]' in line:
            in_explicit_struct = True
            brace_depth = 0

        if in_explicit_struct:
            brace_depth += line.count('{') - line.count('}')
            # Convert FieldOffset in explicit structs
            def convert_fo(m):
                hex_val = m.group(1)
                int_val = int(hex_val, 16)
                return f'[FieldOffset({int_val})]'
            line = re.sub(
                r'\[(?:Il2CppDummyDll\.)?FieldOffset\(Offset\s*=\s*"(0x[0-9A-Fa-f]+)"\)\]',
                convert_fo, line)
            if brace_depth <= 0 and '{' in ''.join(result[-5:]) + line:
                # Check if we've closed the struct
                pass
            if brace_depth <= 0 and '}' in line:
                in_explicit_struct = False

        result.append(line)

    return '\n'.join(result)


def fix_optional_params(content):
    """CS1737: Fix [Optional] parameters that lack a default value.
    IL2CPP/ilspy emits `[Optional] T name` for optional params without a constant.
    C# requires a default value, so rewrite to `T name = default`.
    
    Handles nested generics like Action<string, string, PVPMatchData, bool> by
    matching balanced angle brackets in the type."""
    def replace_optional(m):
        attrs = m.group(1) or ''
        type_str = m.group(2)
        name = m.group(3)
        return f'{attrs}{type_str} {name} = default'
    
    # Match [Optional] followed by a type (with possibly nested generics) and a name
    # The type pattern handles one level of nested <> via non-greedy matching
    # For deeply nested generics, we use a more permissive pattern
    content = re.sub(
        r'\[Optional\]\s*((?:\[[^\]]*\]\s*)*)((?:\w+\.)*\w+(?:<(?:[^<>]|<(?:[^<>]|<[^<>]*>)*>)*>)?(?:\[\])?\??)\s+(\w+)(?=\s*[,)])',
        replace_optional,
        content)
    return content


def fix_circular_const(content):
    """CS0110: Fix const fields whose name shadows their type (e.g., BindingFlags)."""
    # Pattern: private const TypeName TypeName = TypeName.Value
    content = re.sub(
        r'(private\s+const\s+(\w+))\s+\2\b',
        r'\1 _\2',
        content)
    return content


def fix_ambiguous_references(content):
    """CS0104: Disambiguate types that conflict between game code and UnityEngine.
    Only replaces usage sites (parameter types, variable types, casts), not declarations."""
    # Skip files that ARE the declaration of these types
    if 'enum RPCMode' in content or 'enum NetworkStateSynchronization' in content:
        return content
    # Replace usage sites: after ':' (type annotations), in generics, as parameter types
    content = re.sub(r'(?<!\.)(?<!\w)RPCMode(?!\s*(?:\{|;|=))', 'EB.Extension.Unity2018x.RPCMode', content)
    content = re.sub(r'(?<!\.)(?<!\w)NetworkStateSynchronization(?!\s*(?:\{|;|=))', 'EB.Extension.Unity2018x.NetworkStateSynchronization', content)
    return content


def fix_inline_attribute_artifacts(content):
    """Remove inline [AttributeAttribute(...)] that appear within parameter lists."""
    content = re.sub(r'\[AttributeAttribute\([^]]*\)\]\s*', '', content)
    content = re.sub(r'\[Attribute\(Name\s*=\s*"[^"]*"[^)]*\)\]\s*', '', content)
    return content


def clean_content(content):
    """Strip IL2CPP metadata from decompiled source.

    Explicit interface implementations are reconstructed at the IL level by
    StripCG (MethodImpl/.override records), not by renaming mangled method
    names here: a rename cannot emit properties, indexers, or the
    modifier-less form that explicit implementations require.
    """
    # Step 1: Convert FieldOffsets in explicit-layout structs before stripping
    content = convert_explicit_layout_fieldoffsets(content)

    # Step 2: Strip IL2CPP metadata
    content = re.sub(r'^using Il2CppDummyDll;\s*\n', '', content, flags=re.MULTILINE)
    for attr in ['Token', 'Address']:
        content = re.sub(r'^\s*\[Il2CppDummyDll\.' + attr + r'\([^)]*\)\]\s*\n', '', content, flags=re.MULTILINE)
        content = re.sub(r'^\s*\[' + attr + r'\([^)]*\)\]\s*\n', '', content, flags=re.MULTILINE)
    # Strip IL2CPP FieldOffset (with Offset = "0x..." format) but keep real [FieldOffset(N)]
    content = re.sub(r'^\s*\[Il2CppDummyDll\.FieldOffset\([^)]*\)\]\s*\n', '', content, flags=re.MULTILINE)
    content = re.sub(r'^\s*\[FieldOffset\(Offset\s*=\s*"[^"]*"\)\]\s*\n', '', content, flags=re.MULTILINE)
    content = re.sub(r'^\s*\[AttributeAttribute\([^]]*\)\]\s*\n', '', content, flags=re.MULTILINE)
    content = re.sub(r'^\s*\[Attribute\(Name\s*=\s*"[^"]*"[^)]*\)\]\s*\n', '', content, flags=re.MULTILINE)
    content = re.sub(r'^\s*\[Il2CppSetOption\([^)]*\)\]\s*\n', '', content, flags=re.MULTILINE)

    # Step 3: Fix compilation issues
    content = fix_inline_attribute_artifacts(content)
    content = fix_optional_params(content)
    content = fix_circular_const(content)
    content = fix_ambiguous_references(content)

    # Step 4: Fix ilspy rendering of ldnull for value types/generic params
    # ilspy renders ldnull as (T)null or (int)null which is invalid C#
    content = re.sub(r'\(T\)null', 'default(T)', content)
    content = re.sub(r'\(int\)null', '0', content)
    content = re.sub(r'\(float\)null', '0f', content)
    content = re.sub(r'\(double\)null', '0.0', content)
    content = re.sub(r'\(bool\)null', 'false', content)
    content = re.sub(r'\(long\)null', '0L', content)

    # Step 5: Clean up whitespace
    content = re.sub(r'\n{3,}', '\n\n', content)
    return content.lstrip('\n')


def fix_interface_implementations(dst_firstpass, dst_game):
    """Inject the handful of interface members the DummyDll omits outright.

    Interface implementations are normally reconstructed from the IL by StripCG.
    This table is only for members that are genuinely ABSENT from the dump (not
    merely mis-rendered), and it is deliberately tiny: anything added here is a
    known, documented gap rather than a workaround for the decompiler.
    """
    # Only ONE entry remains. Everything else that used to be here -- the
    # IDotGet<T>, IEqualityComparer, IEnumerable, ICollection and IList members --
    # is now reconstructed from the IL by StripCG (MethodImpl/.override records,
    # explicit property definitions, indexer parameters). These entries are
    # genuinely absent from the DummyDll rather than mis-rendered.
    fixes = {}

    # IL2CPP dropped the non-generic IDictionary indexer on this type entirely:
    # the dump has IDictionary.Keys/Values but no IDictionary.Item accessor, and
    # LightWeightDictionary<T1,T2> declares IDictionary. Forward it to the
    # generic indexer that does exist.
    fixes[("EB/LightWeightDictionary.cs", "class LightWeightDictionary<T1, T2>")] = \
        "\t\tobject IDictionary.this[object key] { get { return null; } set { } }\n"

    applied = 0
    for search_dir in [dst_firstpass, dst_game]:
        for (rel_path, class_marker), code in fixes.items():
            target = search_dir / rel_path
            if not target.exists():
                continue
            content = target.read_text()
            # Find the class declaration and insert code before its closing brace
            lines = content.split('\n')
            # Find the line with the class marker
            class_line_idx = None
            for i, line in enumerate(lines):
                if class_marker in line:
                    class_line_idx = i
                    break
            if class_line_idx is None:
                continue
            
            # Find the closing brace of this class by counting braces from class_line_idx
            depth = 0
            close_idx = None
            for i in range(class_line_idx, len(lines)):
                depth += lines[i].count('{') - lines[i].count('}')
                if depth == 0 and i > class_line_idx:
                    close_idx = i
                    break
            
            if close_idx is not None:
                # Insert code before the closing brace
                lines.insert(close_idx, code.rstrip('\n'))
                target.write_text('\n'.join(lines))
                applied += 1
    
    return applied


def _qualify_type(type_str):
    """Fully qualify BCL generic collections so injected code never depends on a
    file's using directives."""
    out = type_str
    for short, full in (
        ('List<', 'System.Collections.Generic.List<'),
        ('Dictionary<', 'System.Collections.Generic.Dictionary<'),
        ('HashSet<', 'System.Collections.Generic.HashSet<'),
        ('Queue<', 'System.Collections.Generic.Queue<'),
        ('Stack<', 'System.Collections.Generic.Stack<'),
    ):
        # Only rewrite bare 'List<' not already preceded by a namespace dot
        out = re.sub(rf'(?<![\w.]){re.escape(short)}', full, out)
    return out


def _split_top_level(s, sep=','):
    """Split on `sep` at bracket depth 0."""
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '<([':
            depth += 1
        elif ch in '>)]':
            depth -= 1
        if ch == sep and depth == 0:
            out.append(cur)
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    return out


def _find_class_body(lines, class_idx):
    """Return (body_lines, close_idx) for the class starting at class_idx."""
    depth = 0
    for i in range(class_idx, len(lines)):
        depth += lines[i].count('{') - lines[i].count('}')
        if depth == 0 and i > class_idx:
            return lines[class_idx:i + 1], i
    return None, None


def _scan_open_generic_serialized_fields(search_dirs):
    """Find every serialized field whose type is an open-generic collection of the
    declaring class's own type parameter.

    This is the pattern that crashes Unity 2020's type-tree builder. Returns
    {source_file: [ (class_name, [type_params], [(field_type, field_name, is_public)]) ]}
    """
    results = {}
    for search_dir in search_dirs:
        if not search_dir.exists():
            continue
        for cs_file in sorted(search_dir.rglob('*.cs')):
            lines = cs_file.read_text(errors='replace').split('\n')
            hits = []
            for i, line in enumerate(lines):
                m = re.search(r'\bclass\s+(\w+)\s*<\s*([^>]+?)\s*>', line)
                if not m:
                    continue
                # Require [Serializable] on the class (or its immediate context)
                ctx = '\n'.join(lines[max(0, i - 3):i + 1])
                if 'Serializable' not in ctx:
                    continue
                cname = m.group(1)
                tparams = [p.strip().split()[-1] for p in _split_top_level(m.group(2))]
                body, _ = _find_class_body(lines, i)
                if body is None:
                    continue
                fields = []
                for k in range(1, len(body) - 1):
                    fl = body[k]
                    if '[' in fl and ']' in fl and ';' not in fl:
                        continue  # attribute-only line
                    if '(' in fl and ')' in fl:
                        continue  # method
                    fm = re.match(
                        r'\s*(?:\[[^\]]*\]\s*)*'
                        r'(public|private|protected|internal)?\s*'
                        r'([\w\.<>\[\],\s]+?)\s+(\w+)\s*(?:=[^;]+)?;', fl)
                    if not fm:
                        continue
                    ftype, fname = fm.group(2).strip(), fm.group(3)
                    is_public = (fm.group(1) == 'public')
                    prev = '\n'.join(body[max(0, k - 2):k])
                    is_serialized = is_public or 'SerializeField' in prev
                    if not is_serialized:
                        continue
                    if 'NonSerialized' in prev:
                        continue
                    if not ('List<' in ftype or '[]' in ftype or 'Dictionary<' in ftype):
                        continue
                    if any(re.search(rf'\b{re.escape(tp)}\b', ftype) for tp in tparams):
                        fields.append((ftype, fname, is_public))
                if fields:
                    hits.append((cname, tparams, fields, i))
            if hits:
                results[cs_file] = hits
    return results


def fix_open_generic_serialized_fields(search_dirs):
    """Neutralize the Unity 2020 Editor type-tree crash ('m_ArrayField != SCRIPTING_NULL'
    followed by SIGSEGV in BuildPlayer).

    Unity 2020's LinearCollectionField cannot inflate an open-generic serialized
    collection such as `List<!0>` when it is reached through a closed subclass
    (e.g. `PropDict : SerializableDictionary<string, PropData>`, or
    `MergeRegionContainer : RegionContainer<MergeRegion>`). Unity 5 handled this
    layout; Unity 2020 does not.

    Fix, applied per generic class found by scanning the generated sources:
      1. Mark the open-generic field `[NonSerialized]` in the generic base.
      2. In every concrete subclass `X : G<Concrete...>`, emit a closed copy of that
         field with the SAME NAME and `[UnityEngine.SerializeField]`, so existing
         prefab data still binds. Re-implement ISerializationCallbackReceiver only
         when the generic base implements it.

    The detector is data-driven, so new patterns are caught without editing this rule.
    Nothing is deleted — PropsController/PropDict/RegionContainer all stay.
    """
    scan = _scan_open_generic_serialized_fields(search_dirs)

    # index: class name -> (file, type params, fields)
    generic_classes = {}
    for cs_file, hits in scan.items():
        for cname, tparams, fields, _ in hits:
            generic_classes[cname] = (cs_file, tparams, fields)

    applied = 0

    # --- Pass 1: mark the open-generic fields [NonSerialized] in each generic class
    for cname, (cs_file, tparams, fields) in generic_classes.items():
        content = cs_file.read_text()
        lines = content.split('\n')
        changed = False
        for ftype, fname, is_public in fields:
            # Find the declaration line and insert [NonSerialized] before it,
            # unless an attribute line already sits there.
            for idx, line in enumerate(lines):
                if re.search(rf'\b{re.escape(fname)}\s*(?:=[^;]+)?;\s*$', line) and \
                   re.search(re.escape(ftype.split('<')[0].strip()) + r'[\w\.<>\[\],\s]*\s+' + re.escape(fname), line):
                    prev = lines[idx - 1] if idx > 0 else ''
                    if 'NonSerialized' in prev:
                        break
                    indent = re.match(r'\s*', line).group(0)
                    if prev.strip().startswith('[') and prev.strip().endswith(']'):
                        # attribute line present: append to it
                        lines[idx - 1] = prev.rstrip() + ' [System.NonSerialized]'
                    else:
                        lines.insert(idx, f'{indent}[System.NonSerialized]')
                    changed = True
                    break
        if changed:
            cs_file.write_text('\n'.join(lines))
            applied += 1
            print(f"    [NonSerialized] on open-generic fields of {cname}<{', '.join(tparams)}>")

    # --- Pass 2: close the fields on each concrete subclass
    for search_dir in search_dirs:
        if not search_dir.exists():
            continue
        for cs_file in sorted(search_dir.rglob('*.cs')):
            lines = cs_file.read_text(errors='replace').split('\n')
            changed = False
            for i, line in enumerate(lines):
                m = re.search(r'\bclass\s+(\w+)\s*:\s*([\w\.]+)\s*<([^>]+)>', line)
                if not m:
                    continue
                derived, base_name, args = m.group(1), m.group(2).split('.')[-1], m.group(3)
                if base_name not in generic_classes:
                    continue
                _, tparams, fields = generic_classes[base_name]
                # Subclass must not introduce its own generics
                if re.search(rf'\bclass\s+{re.escape(derived)}\s*<', line):
                    print(f"    WARNING: {derived} is generic; cannot close "
                          f"{base_name} fields (crash risk remains)")
                    continue
                args_list = [a.strip() for a in _split_top_level(args)]
                if len(args_list) != len(tparams):
                    continue
                subst = dict(zip(tparams, args_list))

                body, close_idx = _find_class_body(lines, i)
                if body is None:
                    continue

                decl = lines[i]
                needs_callback = False
                injected = []
                for ftype, fname, is_public in fields:
                    closed = ftype
                    for tp, concrete in subst.items():
                        closed = re.sub(rf'\b{re.escape(tp)}\b', concrete, closed)
                    if '<' in closed and any(re.search(rf'\b{re.escape(tp)}\b', closed)
                                             for tp in tparams):
                        continue  # could not fully close
                    # `new` is required when the inherited field is visible
                    kw = 'public new' if is_public else 'private'
                    injected.append(
                        f"\t[UnityEngine.SerializeField]\n"
                        f"\t{kw} {_qualify_type(closed)} {fname};")
                    if 'ISerializationCallbackReceiver' in \
                       generic_classes[base_name][0].read_text():
                        needs_callback = True

                if not injected:
                    continue

                if needs_callback and 'ISerializationCallbackReceiver' not in decl:
                    lines[i] = decl.rstrip() + ', UnityEngine.ISerializationCallbackReceiver'
                    injected.append(
                        "\tvoid UnityEngine.ISerializationCallbackReceiver.OnBeforeSerialize() { }\n"
                        "\tvoid UnityEngine.ISerializationCallbackReceiver.OnAfterDeserialize() { }")

                lines.insert(close_idx, '\n'.join(injected))
                changed = True
                print(f"    Closed {base_name} fields on {derived} "
                      f"<{', '.join(args_list)}>")
            if changed:
                cs_file.write_text('\n'.join(lines))
                applied += 1

    return applied


def load_implemented_manifest():
    """Load the set of type names that must NOT be generated as stubs.

    Two sources, both authoritative:
      * tools/stubs92/implemented.txt  -- hand-written replacements
      * tools/port202/ported.txt       -- types whose real bodies come from the
        2.0.2 Mono assemblies via tools/port202/port_202.py

    Both must be respected or the generator emits a stub alongside the real
    implementation and the project fails with CS0101 duplicate definitions.
    """
    implemented = set()
    if PORTED_OUTPUT.exists() and any(PORTED_OUTPUT.glob("*.cs")) and not PORTED_MANIFEST.exists():
        raise SystemExit(
            "Assets/Plugins/Port202 contains ported sources but no manifest at\n"
            f"  {PORTED_MANIFEST.relative_to(REPO_ROOT)}\n"
            "Generating stubs for those types would produce duplicate definitions (CS0101).\n"
            "Run the port first:  python3 tools/port202/port_202.py")
    for manifest, ns_style in ((IMPLEMENTED_MANIFEST, "dotted"),
                               (PORTED_MANIFEST, "bare")):
        if not manifest.exists():
            continue
        for line in manifest.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            # ported.txt holds bare type names (root-level, no namespace) while
            # implemented.txt holds fully-qualified names.
            implemented.add(line if ns_style == "dotted" else line)
    return implemented


# Framework DLLs that the generated stubs reference. These come from the
# Il2CppDumper DummyDll output (build/dumps/v7/DummyDll), NOT from Unity's
# managed folder, because the stub code was decompiled against these.
FRAMEWORK_DLLS = [
    "BouncyCastle.dll",
    "enum2int.dll",
    "Facebook.Unity.Android.dll",
    "Facebook.Unity.dll",
    "Facebook.Unity.Settings.dll",
    "Firebase.App.dll",
    "Firebase.Messaging.dll",
    "Firebase.Platform.dll",
    "Google.Play.AssetDelivery.dll",
    "Google.Play.Common.dll",
    "Google.Play.Core.dll",
    "ICSharpCode.SharpZipLib.dll",
    "Il2CppDummyDll.dll",
    "Kabam.Krash.Native.dll",
    "Kabam.Logger.dll",
    "Mono.Security.dll",
    "NBidi.dll",
]

# Framework DLLs that must NOT be shipped as a binary. These contain serialized
# fields of open generic type on [Serializable] base classes, which makes Unity
# 2020's player-build type-tree pass (ScriptingManager::UpdateScriptHashes ->
# ComputeTypeTreeHashForScriptClass -> LinearCollectionField) die with
# "Assertion failed on expression: 'm_ArrayField != SCRIPTING_NULL'" and SIGSEGV.
# Binary DLLs cannot be fixed by the source-level rules below, so they are
# decompiled to C# and emitted as an assembly-definition-backed folder instead.
# The asmdef must keep the original assembly name, or MonoScript references from
# 9.2 scenes/prefabs (which record "Fabric.Core") stop resolving.
SOURCE_FRAMEWORK_DLLS = {
    "Fabric.Core.dll": {
        "folder": "FabricCore92",
        "assembly": "Fabric.Core",
    },
}


def write_asmdef(dst_dir, assembly_name):
    """Write the assembly definition that preserves the original DLL's assembly
    name, so MonoScript references recorded in 9.2 assets still resolve."""
    dst_dir.mkdir(parents=True, exist_ok=True)
    asmdef = dst_dir / f"{assembly_name}.asmdef"
    asmdef.write_text(json.dumps({
        "name": assembly_name,
        "rootNamespace": "",
        "references": [],
        "includePlatforms": [],
        "excludePlatforms": [],
        "allowUnsafeCode": False,
        "overrideReferences": False,
        "precompiledReferences": [],
        "autoReferenced": True,
        "defineConstraints": [],
        "versionDefines": [],
        "noEngineReferences": False,
    }, indent=4) + "\n")
    return asmdef


def scan_namespace_shadowing(stub_root):
    """Report EB-namespace types whose name collides with a UnityEngine type.

    WHY THIS CHECK EXISTS
    C# resolves an unqualified name by walking outwards through the enclosing
    namespaces BEFORE consulting `using` directives. Hand-written code inside
    namespace EB.Rendering that writes `Time.deltaTime` therefore binds to
    EB.Time, not UnityEngine.Time -- and the generated stub for EB.Time.deltaTime
    is `throw null;`, so it compiles cleanly and fails at runtime.

    That cost a full on-device bisect: the NullReferenceException reported only
    `EBLight.InternalUpdate [0x00000]` in a release build (the throw is in a
    tiny inlined getter) and vanished when the call was wrapped in a try, because
    the catch logged through EB.Debug.LogError -- itself a stub with an empty
    body. A development build with symbols named the real frame.

    Hand-written EB-namespace code must fully qualify any of these names.
    """
    unity_names = {
        "Time", "Debug", "Object", "Random", "Canvas", "Encoding", "Application",
        "Screen", "Input", "Physics", "Resources", "Camera", "Light", "Material",
        "Mesh", "Shader", "Texture", "Texture2D", "Color", "Gradient", "Animation",
        "AnimationCurve", "Coroutines", "AudioSource", "Space", "LayerMask",
    }
    # Namespaces, not just types, can be shadowed: `EB.Math` makes a bare `Math.Min`
    # in EB-namespace code fail to compile (CS0234), and `EB.Core.Random` makes a
    # bare `Random` refer to the wrong thing.
    framework_namespaces = {
        "Math", "Collections", "Diagnostics", "IO", "Text", "Threading", "Linq",
        "Reflection", "Runtime", "Core", "Net", "Security", "Globalization",
    }

    found = []
    for ns_dir in sorted(p for p in stub_root.glob("EB*") if p.is_dir()):
        tail = ns_dir.name.split(".")[-1]
        if tail in framework_namespaces:
            found.append(ns_dir.name + " (namespace -- qualify System.* / UnityEngine.*)")
        for cs in sorted(ns_dir.glob("*.cs")):
            name = cs.stem
            if name in unity_names:
                found.append(ns_dir.name + "." + name)
    return found


def scan_dlls_for_open_generic_fields(dummy_dll_dir, dotnet, ilspycmd, work_dir):
    """Decompile every framework DummyDll to a scratch dir and report any
    [Serializable] generic type holding a serialized collection of its own type
    parameter. Any hit means that DLL would crash the Unity 2020 player build if
    it were shipped as a binary, so it must move to SOURCE_FRAMEWORK_DLLS.

    This runs as a check, not a fix: it exists so the next offender is reported
    by the pipeline instead of costing another on-device build bisect.
    """
    scratch = work_dir / "framework-scan"
    candidates = sorted(p for p in dummy_dll_dir.glob("*.dll")
                        if p.name not in SOURCE_FRAMEWORK_DLLS)
    if not candidates:
        return []

    hits = []
    for dll in candidates:
        out = scratch / dll.stem
        try:
            decompile(dotnet, dll, out, dummy_dll_dir, ilspycmd)
        except (SystemExit, OSError):
            continue
        found = _scan_open_generic_serialized_fields([out])
        for cs_file, classes in found.items():
            for cname, tparams, fields, _idx in classes:
                for ftype, fname, _is_public in fields:
                    hits.append((dll.name, cs_file.name, cname,
                                 ",".join(tparams), ftype, fname))
    return hits


def copy_framework_dlls(dummy_dll_dir, unity_project):
    """Copy the framework DLLs the stubs reference into Assets/Plugins.
    These are git-ignored derived artifacts; this keeps a fresh clone buildable.
    Returns (copied_count, missing_names)."""
    dst_dir = unity_project / "Assets" / "Plugins"
    dst_dir.mkdir(parents=True, exist_ok=True)
    copied = 0
    missing = []
    for dll_name in FRAMEWORK_DLLS:
        src = dummy_dll_dir / dll_name
        if not src.exists():
            missing.append(dll_name)
            continue
        shutil.copy2(src, dst_dir / dll_name)
        copied += 1
    return copied, missing


def copy_stubs(src_dir, dst_dir, implemented=None):
    """Copy cleaned .cs files to the Unity project, skipping project scaffolding
    and types listed in the implemented manifest."""
    if implemented is None:
        implemented = set()
    count = 0
    skipped = 0
    for cs_file in src_dir.rglob('*.cs'):
        if '.csproj' in str(cs_file) or '/Properties/' in str(cs_file):
            continue
        # Check if this file declares an implemented type.
        #
        # Match the FILE STEM, not any `class X` in the file: ilspy writes one
        # top-level type per file, so the stem identifies the type exactly, while a
        # substring search also matches NESTED types. That mattered the moment
        # EB.Debug was implemented -- EB.Sparx/ODRManager.cs declares its own nested
        # `public static class Debug`, so the whole file was skipped and
        # EB.Sparx.Hub then failed to compile against a missing ODRManager.
        #
        # The namespace match is anchored for the same reason: a plain
        # `'namespace EB' in content` also matches `namespace EB.Sparx`.
        content = cs_file.read_text()
        skip = False
        for impl_name in implemented:
            short_name = impl_name.split('.')[-1]
            if cs_file.stem != short_name:
                continue
            ns = '.'.join(impl_name.split('.')[:-1])
            if not ns:
                skip = True
                break
            if re.search(rf'^\s*namespace\s+{re.escape(ns)}\s*$', content, re.M) \
               or re.search(rf'^\s*namespace\s+{re.escape(ns)}\s*;', content, re.M):
                skip = True
                break
        if skip:
            skipped += 1
            continue
        rel = cs_file.relative_to(src_dir)
        target = dst_dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(clean_content(content))
        count += 1
    if skipped:
        print(f"    Skipped {skipped} implemented types")
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dummy-dll-dir", type=Path,
                        default=REPO_ROOT / "build" / "dumps" / "v7" / "DummyDll",
                        help="Directory containing Il2CppDumper v7 DummyDll assemblies")
    parser.add_argument("--ilspycmd", type=Path, default=None,
                        help="Path to ilspycmd.dll")
    parser.add_argument("--unity-project", type=Path,
                        default=DEFAULT_UNITY_PROJECT,
                        help="Unity project root directory")
    parser.add_argument("--dotnet", type=Path, default=None,
                        help="Path to dotnet executable")
    args = parser.parse_args()

    dummy_dll_dir = args.dummy_dll_dir.resolve()
    unity_project = args.unity_project.resolve()
    dotnet = find_dotnet(args.dotnet)
    ilspycmd = find_ilspycmd(args.ilspycmd)

    # Validate inputs
    if not dummy_dll_dir.exists():
        sys.exit(f"ERROR: DummyDll directory not found: {dummy_dll_dir}")
    for dll_name in ["Assembly-CSharp-firstpass.dll", "Assembly-CSharp.dll"]:
        if not (dummy_dll_dir / dll_name).exists():
            sys.exit(f"ERROR: Required DLL not found: {dummy_dll_dir / dll_name}")

    work_dir = REPO_ROOT / "build" / "mono-investigation" / "stubs-92-pipeline"
    stripped_dir = work_dir / "stripped"
    decompiled_firstpass = work_dir / "decompiled" / "firstpass"
    decompiled_game = work_dir / "decompiled" / "game"

    print("=== 9.2 Stub Generation Pipeline ===")
    print(f"  dotnet:     {dotnet}")
    print(f"  ilspycmd:   {ilspycmd}")
    print(f"  DummyDll:   {dummy_dll_dir}")
    print(f"  Unity proj: {unity_project}\n")

    # Clean previous output
    if work_dir.exists():
        shutil.rmtree(work_dir)
    stripped_dir.mkdir(parents=True)
    decompiled_firstpass.mkdir(parents=True)
    decompiled_game.mkdir(parents=True)

    # Step 1: Strip compiler-generated types at IL level
    print("Step 1: Strip compiler-generated types (Mono.Cecil)")
    for dll_name in ["Assembly-CSharp-firstpass.dll", "Assembly-CSharp.dll"]:
        strip_cg(dotnet, dummy_dll_dir / dll_name, stripped_dir / dll_name, dummy_dll_dir)
    for dll_name in SOURCE_FRAMEWORK_DLLS:
        src = dummy_dll_dir / dll_name
        if src.exists():
            strip_cg(dotnet, src, stripped_dir / dll_name, dummy_dll_dir)
        else:
            print(f"  WARNING: {dll_name} not found in DummyDll; skipping")

    # Step 2: Decompile stripped DLLs to C# source
    print("\nStep 2: Decompile to C# (ilspycmd -p -lv CSharp8_0)")
    decompile(dotnet, stripped_dir / "Assembly-CSharp-firstpass.dll",
              decompiled_firstpass, dummy_dll_dir, ilspycmd)
    decompile(dotnet, stripped_dir / "Assembly-CSharp.dll",
              decompiled_game, dummy_dll_dir, ilspycmd)

    # Frameworks converted to source rather than shipped as a binary (see
    # SOURCE_FRAMEWORK_DLLS for why). Each gets its own decompile dir.
    decompiled_frameworks = {}
    for dll_name, cfg in SOURCE_FRAMEWORK_DLLS.items():
        if not (stripped_dir / dll_name).exists():
            continue
        out = work_dir / "decompiled" / cfg["folder"]
        decompile(dotnet, stripped_dir / dll_name, out, dummy_dll_dir, ilspycmd)
        decompiled_frameworks[dll_name] = out

    # Step 3: Copy framework DLLs the stubs reference
    print("\nStep 3: Copy framework DLLs to Unity project")
    dll_count, dll_missing = copy_framework_dlls(dummy_dll_dir, unity_project)
    print(f"  Framework DLLs: {dll_count} copied -> Assets/Plugins")
    if dll_missing:
        print(f"  Missing (not in DummyDll): {', '.join(dll_missing)}")

    # Step 3b: Report any OTHER framework DLL that would crash the same way.
    print("\nStep 3b: Scan framework DLLs for the open-generic serialized-field pattern")
    hits = scan_dlls_for_open_generic_fields(dummy_dll_dir, dotnet, ilspycmd, work_dir)

    shadowed = scan_namespace_shadowing(decompiled_firstpass)
    if shadowed:
        print("  Namespace shadowing: " + ", ".join(shadowed))
        print("    Hand-written code in these namespaces must fully qualify the")
        print("    UnityEngine type (e.g. UnityEngine.Time.deltaTime).")
    if hits:
        print(f"  {len(hits)} hit(s) -- these DLLs must move to SOURCE_FRAMEWORK_DLLS:")
        for dll_name, cs_name, cname, tparams, ftype, fname in hits:
            print(f"    {dll_name}: {cname}<{tparams}> field {ftype} {fname}  ({cs_name})")
    else:
        print("  No other framework DLL has the crash pattern")

    # Step 4: Clean and copy to Unity project
    print("\nStep 4: Clean IL2CPP artifacts and copy to Unity project")
    dst_firstpass = unity_project / "Assets" / "Plugins" / "Firstpass92"
    dst_game = unity_project / "Assets" / "Scripts" / "Assembly-CSharp"

    if dst_firstpass.exists():
        shutil.rmtree(dst_firstpass)
    if dst_game.exists():
        shutil.rmtree(dst_game)

    # Source-converted frameworks: emit .cs plus the asmdef that keeps the
    # original assembly name (MonoScript binding) and drop any stale binary.
    framework_dirs = []
    for dll_name, cfg in SOURCE_FRAMEWORK_DLLS.items():
        src_dir = decompiled_frameworks.get(dll_name)
        if src_dir is None:
            continue
        dst_dir = unity_project / "Assets" / "Plugins" / cfg["folder"]
        if dst_dir.exists():
            shutil.rmtree(dst_dir)
        count = copy_stubs(src_dir, dst_dir)
        asmdef = write_asmdef(dst_dir, cfg["assembly"])
        stale = unity_project / "Assets" / "Plugins" / dll_name
        if stale.exists():
            stale.unlink()
            stale_meta = stale.with_suffix(stale.suffix + ".meta")
            if stale_meta.exists():
                stale_meta.unlink()
        framework_dirs.append(dst_dir)
        print(f"  {dll_name}: {count} files -> {dst_dir.relative_to(unity_project)} "
              f"(asmdef '{cfg['assembly']}')")

    implemented = load_implemented_manifest()
    if implemented:
        print(f"  Exclusion manifest: {len(implemented)} implemented types")

    fp_count = copy_stubs(decompiled_firstpass, dst_firstpass, implemented)
    game_count = copy_stubs(decompiled_game, dst_game, implemented)
    print(f"  Firstpass: {fp_count} files -> {dst_firstpass.relative_to(unity_project)}")
    print(f"  Game: {game_count} files -> {dst_game.relative_to(unity_project)}")

    # Step 4: Supplement the few interface members the DummyDll omits outright.
    # Interface implementations themselves come from StripCG's IL rewrite; this
    # only covers members that are simply absent from the dump.
    fix_count = fix_interface_implementations(dst_firstpass, dst_game)
    if fix_count:
        print(f"  Interface fixes: {fix_count} supplement(s) injected")

    # Step 5: Neutralize the Unity 2020 open-generic serialized-field type-tree crash
    targets = [dst_game, dst_firstpass] + framework_dirs
    sd_count = fix_open_generic_serialized_fields(targets)
    if sd_count:
        print(f"  Open-generic crash fix: {sd_count} file(s)")

    print(f"\n=== Done === ({fp_count + game_count} stub files, "
          f"{len(framework_dirs)} framework DLL(s) converted to source)")


if __name__ == "__main__":
    main()
