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
mkdir -p /tmp/tftf-il2cpp
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
extracted assemblies have not been modified since export, invokes Roslyn against the recovered
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

### First compilation milestone

The APK framework DLLs are incomplete as compiler references: for example, its
`DllImportAttribute` lacks the constructor required to compile native imports.
Use locally installed, unstripped framework references for compilation. The audit accepts
repeatable `--reference-dir` arguments; a matching filename overrides the APK reference,
and the report records its path and SHA-256. These are compiler inputs only and must not
be packaged into the game as runtime replacements.

The tested framework inputs are Microsoft's
[net20](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net20/1.0.3)
and [net35](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net35/1.0.3)
reference packages, version 1.0.3. Download the `.nupkg` archives from NuGet and extract
locally under `build/tooling/net20` and `build/tooling/net35`. A fresh clone must supply
these tools and references; they are not tracked.

```bash
python3 tools/decompilation.py compile-audit build/decompilation/mono-XXXX \
  --dotnet /path/to/dotnet \
  --csc /path/to/sdk/8.0.422/Roslyn/bincore/csc.dll \
  --reference-dir build/tooling/net20/build/.NETFramework/v2.0 \
  --reference-dir build/tooling/net35/build/.NETFramework/v3.5 \
  --assembly Assembly-CSharp-firstpass --assembly Assembly-CSharp \
  --assembly Fabric.Core --assembly NBidi --repair-accessors --repair-contracts
```

Each audit compiles an isolated source snapshot and records input and compiled source
hashes. Compiler output uses deterministic mode and maps temporary paths to a stable
prefix. The audit compiles C# only; it does not reconstruct embedded-resource packaging,
signing, or the Unity project. `--repair-accessors` rewrites only simple explicit-interface
getter methods into property getters. `--repair-contracts` applies narrow IL-backed repairs
to decompiler declarations in the isolated snapshot: stripped Unity attribute setters,
metadata-only optional parameters, namespace collisions, recovered string-switch bodies, and
the `Activator.CreateInstance` generic call. Every repair is listed in the report; the
original export is never modified. Selected projects are topologically ordered from their
ILSpy project references, and a successfully compiled dependency is used as the reference
for later selected projects. The report records those reference paths and compiled hashes.

On the local 2.0.2 APK with SHA-256
`61c1860df9d5bb64ab28934b0fd6954c71c5410887f66260917351820b08aca4`,
`Fabric.Core`, `NBidi`, `Assembly-CSharp-firstpass`, and `Assembly-CSharp` compile
successfully with Roslyn 8.0.422 when the isolated contract repairs are enabled. The
eleven `CS0165` diagnostics for string-switch locals were resolved from the original
managed assembly's IL; the report records each recovered branch map and repair. No rebuilt
DLL has been installed or runtime-verified. This Mono milestone does not reconstruct the
9.2 IL2CPP client.

The next audit widened the selected set to include all Facebook.Unity assemblies,
`ICSharpCode.SharpZipLib`, and `crypto`. Facebook.Unity now also compiles: its two
setter-only `MethodCall<T>` properties refer to private backing fields that are present in
the original IL but omitted from the ILSpy C# declaration. The audit restores those fields
only in the isolated snapshot and records both repairs. The remaining blockers are confined
to the two third-party libraries: `crypto` has three decompiled derived constructors whose
base calls omit required parameters, and SharpZipLib has three `ICryptoTransform`
implementations missing the `CanReuseTransform` property required by the .NET 8 contract.
Those are now the next evidence-recovery targets; no values have been guessed and the
original export remains unchanged. The expanded report is under the ignored
`build/decompilation/mono-2fviys29/compile-*/report.json` output.

A follow-up audit of `UnityEngine`, `UnityEngine.Networking`, and `UnityEngine.UI` compiled
`UnityEngine.UI` after removing six invalid `virtual` modifiers from explicit UI interface
implementations in the isolated snapshot. The original IL confirms that `Hash128` and
`NetworkSceneId` define equality only; no inequality operators were added, so
`UnityEngine.Networking` remains blocked by the modern C# requirement for a matching `!=`.
`UnityEngine` remains blocked by the same equality-only `Hash128` declaration and by
`UnityLogWriter` inheriting the abstract `TextWriter.Encoding` member that is absent from
the original IL; neither missing contract has been invented.

The `export-il` command now snapshots every managed DLL, verifies its exported SHA-256
before and after disassembly, and writes one original-IL file plus a provenance report for
each assembly. It performs no source repairs and does not alter the managed inputs:

```bash
DOTNET_ROOT=/home/darabat/.dotnet \
  python3 tools/decompilation.py export-il build/decompilation/mono-XXXX \
  --ilspy build/tooling/ilspycmd
```

On this APK, all 17 managed assemblies exported successfully with ILSpy 9.1.0.7988.
This is metadata and instruction coverage; it does not imply C# compilation, packaging,
runtime verification, or Unity/IL2CPP equivalence.

### Metadata-backed declaration audit

`tools/mono_metadata` is a small .NET 8 helper that reads ECMA-335 metadata through
`System.Reflection.Metadata`; it never loads the inspected DLL. It records assembly
identity, references, type inheritance, fields, methods, properties, events, attributes,
generic constraints, marshalling/default metadata, resources, and layout facts. Method
bodies, resource contents, Unity asset type trees, native binding resolution, and runtime
behavior are explicitly outside its evidence.

Build it locally, then compare the recovered C# declaration inventory with the original
metadata:

```bash
/home/darabat/.dotnet/dotnet restore tools/mono_metadata/MonoMetadata.csproj --ignore-failed-sources
/home/darabat/.dotnet/dotnet build tools/mono_metadata/MonoMetadata.csproj --no-restore
python3 tools/decompilation.py metadata-audit build/decompilation/mono-XXXX \
  --dotnet /home/darabat/.dotnet/dotnet \
  --metadata-tool tools/mono_metadata/bin/Debug/net8.0/MonoMetadata.dll \
  --assembly Assembly-CSharp --assembly Assembly-CSharp-firstpass
```

The ignored report keeps exact PE metadata separate from the decompiler observation. Its
`metadata_contracts` section preserves the helper's per-type fields, methods, properties,
events, attributes, generic constraints, marshalling/default metadata, method implementations,
and raw flags. `metadata_api_surface` adds a readable view of visibility, inheritance,
overload signatures, assembly references, resources, and serialization-relevant field order
and offsets while retaining those raw flags. The `decompiler` section remains a shallow
name inventory from ILSpy C# output, and `comparison` is still only a name-based triage
against that observation; neither section claims behavioral equivalence or Unity runtime
compatibility. The managed-input SHA-256 is checked against `manifest.json` before each
report is produced.

To compare an isolated compiler output with its original APK assembly, use the
metadata-only diff. It reads both PE files through the helper, records both hashes, and
does not load or install either assembly:

```bash
python3 tools/decompilation.py metadata-compare build/decompilation/mono-XXXX \
  --dotnet /home/darabat/.dotnet/dotnet \
  --metadata-tool tools/mono_metadata/bin/Debug/net8.0/MonoMetadata.dll \
  --compiled-dir build/decompilation/mono-XXXX/compile-XXXX/bin \
  --assembly Assembly-CSharp-firstpass --assembly Assembly-CSharp
```

The diff reports assembly identity, references, resources, type inheritance and flags,
attributes, overload signatures, fields, methods, properties, events, and field layout
changes. On the current isolated audit outputs, all four compiled identities match their
original identities, but the game roots are not metadata-equivalent: compiler-generated
closure types use different names, the .NET 2.0/3.5 reference inputs emit different
framework versions/public-key tokens than the APK's `2.0.5.0` framework references, and
the compiled firstpass output carries an additional `Assembly-CSharp` reference. These
are compatibility work items, not evidence that the DLLs can replace the APK originals.
The comparison report now retains those exact differences while classifying
compiler-generated type churn, framework-reference identity drift, and public versus
non-public type/member changes separately. Properties and events are included in the
visibility classification through their accessor methods; property and event flag bits do
not carry accessibility. An unresolved accessor is kept in an `unknown` bucket rather
than guessed public or non-public. This lets reviewers prioritize externally visible API
shape without treating non-public or compiler-generated changes as harmless, and without
discarding the exact metadata evidence.

For the playable-client boundary, `replacement-plan` computes the transitive closure of
explicit replacement roots using both PE assembly references and ILSpy project references:

```bash
python3 tools/decompilation.py replacement-plan build/decompilation/mono-XXXX \
  --dotnet /home/darabat/.dotnet/dotnet \
  --metadata-tool tools/mono_metadata/bin/Debug/net8.0/MonoMetadata.dll \
  --root Assembly-CSharp --root Assembly-CSharp-firstpass
```

The report labels roots, dependencies in their managed closure, and assemblies outside the
closure. It preserves assembly identities and input hashes, but does not decide that every
dependency should be rebuilt: existing framework, Unity, and plugin DLLs may be retained
only after identity, API, resources, native bindings, Android packaging, and runtime load
order are separately verified. This is the replacement boundary evidence for the 2.0.2
client, not a packaging or playability result.

The metadata report also establishes the current compiler/runtime profile facts for this
APK. `mscorlib.dll`, `System.dll`, and `System.Core.dll` identify as version `2.0.5.0`,
carry metadata version `v2.0.50727`, and are `I386`/`ILOnly` PE images. `UnityEngine.dll`,
`Assembly-CSharp-firstpass.dll`, and `Assembly-CSharp.dll` use the same metadata version
and have assembly identity version `0.0.0.0`. The Roslyn audit therefore uses .NET 2.0/3.5
reference assemblies as source-compatibility inputs; it does not establish that a newly
compiled DLL will load under the APK's embedded Mono runtime. A packaging experiment must
preserve these identities and verify load behavior on the original APK before any runtime
claim is made.

The audit's compiler profile is also explicit: Roslyn `csc.dll` from the local .NET SDK
8.0.422, `/langversion:12`, deterministic output, and the unstripped Microsoft .NET
Framework 2.0/3.5 reference assemblies under `build/tooling/`. Those inputs are a modern
source-checking profile. The recovered APK does not establish which historical C# compiler
produced its assemblies, and the audit does not replace or reconstruct Unity's embedded
Mono compiler/runtime, Unity native bindings, or editor build pipeline. Only the managed
PE metadata profile is established by this workspace.

Two closure members currently demonstrate why “dependency” does not mean “rebuild”: the
original `crypto` IL has no instance constructor metadata on `DsaPrivateKeyParameters`,
`RsaPrivateCrtKeyParameters`, or `BerOutputStream`, so Roslyn's synthesized constructor
cannot satisfy their parameterized bases without inventing a new API. The metadata helper
confirms the exact declarations: `BerOutputStream` has no methods; `DsaPrivateKeyParameters`
has equality/hash methods but no constructor; and `RsaPrivateCrtKeyParameters` has
accessors plus equality/hash methods but no constructor. The original SharpZipLib transform
types expose `CanTransformMultipleBlocks` but no `CanReuseTransform` member in IL, while
the modern `ICryptoTransform` reference requires it. The three affected transforms still
have their input/output block properties, transform methods, and `Dispose`. The Unity
blockers are similarly exact: `Hash128` and `NetworkSceneId` expose `op_Equality` but no
`op_Inequality`, and `UnityLogWriter` has no `Encoding` property despite inheriting
`TextWriter`. These are retained original binaries or separate runtime-profile work items,
not contracts to guess into the reconstructed source.

The static 2.0.2 Mono endpoint and payload inventory is maintained separately in
[`MONO_SERVER_CONTRACTS.md`](MONO_SERVER_CONTRACTS.md). It records paths, verbs,
API versions, request keys, and response containers recovered from the managed
client. It is not network capture or runtime verification, and it must not be
substituted with the 9.2 IL2CPP server path.
The static comparison against the offline server is recorded in
[`MONO_SERVER_COMPARISON.md`](MONO_SERVER_COMPARISON.md); it identifies route,
request-field, version-validation, and response-container gaps without changing
the server or claiming end-to-end compatibility.

Next milestones: compile additional game assemblies against rebuilt dependencies, compare
assembly APIs and serialized field layouts, then test a locally packaged Mono APK against
the offline server. The 2.0.2 protocol and assets
must be verified independently before claiming parity with patched 9.2.

Run synthetic tooling tests with `python3 -m unittest discover -s tools -p test_decompilation.py`.

## Scope and provenance

Only local, operator-supplied inputs are used. APKs, extracted DLLs, generated C# and
screenshots are ignored by Git. Do not place any generated output under `media/`, and do
not commit game assets or binaries. The repository's native IL2CPP documentation remains
the authoritative path for the 9.2 client; this branch adds the complete Mono analysis
workspace alongside it.
