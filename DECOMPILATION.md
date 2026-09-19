# Decompilation workspace

This branch is the dedicated decompilation track. It builds a reproducible source
browsing workspace from an operator supplied APK; it does not add the APK, managed
assemblies, native libraries, or generated source to Git. Those artifacts remain under
ignored `build/` directories and can be regenerated from the same input.

There are two client generations in this repository:

| input | runtime | recovery path |
| --- | --- | --- |
| `com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk` | Mono | `tools/decompilation.py export` |
| `Transformers 9.2 offline.apk` | IL2CPP | `Il2CppDumper`, `tools/index_il2cpp_dump.py`, and the Ghidra workflows |

The Mono path is a complete managed assembly export. It extracts every DLL below
`assets/bin/Data/Managed/`, records SHA-256 hashes and the decompiler version, writes one
ILSpy project per assembly, and creates `Recovered.sln`. It reports each assembly's source
file count and export status in `manifest.json`. The output is reference source for study;
the original Unity editor, platform SDKs, generated project settings, and proprietary
runtime integrations are not present, so a successful export does not imply a rebuildable
game client.

## Recreate the managed workspace

Install .NET 8 and ILSpy command-line (`ilspycmd`). The repository contains a local copy of
the ILSpy app host under `build/tooling/`, but its .NET runtime is intentionally not tracked.
Set `DOTNET_ROOT` when using a non-system SDK:

```bash
DOTNET_ROOT=/home/darabat/.dotnet \
  python3 tools/decompilation.py export /path/to/mono.apk \
  --ilspy /path/to/ilspycmd
```

The command prints the generated workspace path. It is safe to run repeatedly; each run
gets a fresh `build/decompilation/mono-*` directory. Inspect `manifest.json` first, then
open `Recovered.sln` or the individual projects in an IDE. The managed DLLs in the same
workspace are the exact references used by the generated projects.

The script rejects an IL2CPP APK with a clear message. Use the native path for 9.2:

```bash
unzip -p "Transformers 9.2 offline.apk" \
  lib/arm64-v8a/libil2cpp.so > /tmp/tftf-il2cpp/libil2cpp.so
unzip -p "Transformers 9.2 offline.apk" \
  assets/bin/Data/Managed/Metadata/global-metadata.dat > /tmp/tftf-il2cpp/global-metadata.dat
Il2CppDumper /tmp/tftf-il2cpp/libil2cpp.so /tmp/tftf-il2cpp/global-metadata.dat /tmp/tftf-il2cpp-out
python3 tools/index_il2cpp_dump.py /tmp/tftf-il2cpp-out/dump.cs \
  --script-json /tmp/tftf-il2cpp-out/script.json \
  --out build/analysis/9.2-index.json
```

## Compilation status

Managed recovery and compilation are separate claims. `compile-audit` checks that the
export has not been modified since it was produced, invokes Roslyn against the recovered
game sources, and writes a machine-readable report. It is deliberately an audit rather
than a promise that Unity can be rebuilt:

```bash
DOTNET_ROOT=/home/darabat/.dotnet \
  python3 tools/decompilation.py compile-audit build/decompilation/mono-XXXX \
  --dotnet /home/darabat/.dotnet/dotnet \
  --csc /home/darabat/.dotnet/sdk/8.0.422/Roslyn/bincore/csc.dll
```

The current APK's recovered game assemblies export cleanly. The audit records compiler
errors in `compile-*/report.json`; the known blockers are Unity/Mono framework contracts,
platform bindings, and decompiler output that depends on the original Unity build setup.
Treat those errors as the prioritized porting list, not as missing decompilation coverage.

## Scope and provenance

Only local, operator-supplied inputs are used. APKs, extracted DLLs, generated C# and
screenshots are ignored by Git. Do not place any generated output under `media/`, and do
not commit game assets or binaries. The repository's native IL2CPP documentation remains
the authoritative path for the 9.2 client; this branch adds the complete Mono analysis
workspace alongside it.
