# Decompilation workspace

This branch is the dedicated decompilation track. It builds a reproducible source
browsing workspace from an operator supplied APK; it does not add the APK, managed
assemblies, native libraries, or generated source to Git. Those artifacts remain under
ignored `build/` directories and can be regenerated from the same input.

There are two client generations in this repository:

| input | runtime | recovery path |
| --- | --- | --- |
| `com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk` | Mono | `tools/decompilation.py export` |
| `Transformers 9.2 offline.apk` | IL2CPP | `tools/recover_9_2.py`, `tools/index_il2cpp_dump.py`, and the Ghidra workflows |

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

The script rejects an IL2CPP APK with a clear message. For the 9.2 native path, use the
repeatable recovery command below. The lower-level Il2CppDumper recipe remains useful when
only a type/offset dump is needed:

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

## Recreate the 9.2 managed and Unity project outputs

Supply the APK you are entitled to use and local copies of the pinned Cpp2IL build and
AssetRipper GUI Free. The recovery script installs nothing and stores all generated
assemblies, extracted files, project assets, logs, and its provenance manifest in the
chosen output directory. The default is ignored `build/recovery-9.2/`; set it to a
directory on external storage when space is limited. The script keeps the untouched
AssetRipper export and creates a second staged copy, so budget several gigabytes for both.

The pinned upstream Cpp2IL build needs a small generic IL-generation correction for this
input. The source patch and MIT notice are included in `tools/patches/`; build it from an
operator-local Cpp2IL clone at the exact commit using .NET 10:

```bash
mkdir -p build/tooling
git clone https://github.com/SamboyCoding/Cpp2IL.git build/tooling/cpp2il-source
git -C build/tooling/cpp2il-source checkout b5ad444b82267cb1e4b88b8b373c008105bdea52
python3 tools/build_patched_cpp2il.py build/tooling/cpp2il-source \
  --dotnet /path/to/dotnet10/dotnet \
  --dotnet-root /path/to/dotnet10 \
  --output build/tooling/cpp2il-patched
```

The builder creates a detached worktree, verifies the pinned commit and patch hash, applies
the MIT-licensed patch, builds the CLI, and records its provenance in `manifest.json`. It
does not alter the original clone. Use its printed executable path as `--cpp2il` below and
set `DOTNET_ROOT` to the same .NET 10 installation when running the apphost. The recovery
script checks the full pinned commit id in the tool's version output, extracts the matching
ARM64 library and metadata from the APK, and passes both explicitly with Unity version
`2020.3.31f1` to Cpp2IL.

```bash
python3 tools/recover_9_2.py "/path/to/Transformers 9.2 offline.apk" \
  --cpp2il /path/to/Cpp2IL \
  --assetripper /path/to/AssetRipper.GUI.Free \
  --output build/recovery-9.2
```

Or set `CPP2IL_BIN` and `ASSETRIPPER_BIN` in the environment and omit those options. The
previously pinned APK hash is now treated as a known offline-patched input and is rejected.
The script also rejects a bundled `libdothook.so` or a `libil2cpp.so` dependency marker for it.
No pristine Kabam 9.2.0 hash is verified in this workspace yet. For a replacement APK, first
verify its package and version with REA and its release signer with `apksigner`; then explicitly
pass `--allow-unverified-apk`. The script records the supplied APK hash and its source checks. It
extracts the matching arm64 IL2CPP library and global metadata, runs Cpp2IL's
`dll_il_recovery` output, asks AssetRipper to export the Unity project, checks that the
export has `Assets/` and `ProjectSettings/ProjectVersion.txt`, and records tool/input
hashes in `recovery-manifest.json`. It checks the Cpp2IL commit and AssetRipper version
against the recovered versions used here, and refuses a non-empty output directory so existing
local work is not overwritten. The pinned versions are Cpp2IL
`2022.1.0-development.1743+b5ad444` and AssetRipper GUI Free
`2.0.0+1ac666f47d8e9dedf96afb0b914c70d7656151ea`.
If AssetRipper wraps its export in `ExportedProject/`, the recovery script follows that
directory as the Unity project root.

Cpp2IL reconstructs managed assemblies from IL2CPP metadata and native code without an AI
agent. Those assemblies can be inspected or decompiled to C# locally with ILSpy. AssetRipper
recovers Unity assets and project structure, but its generated C# script files are stubs;
the recovered assemblies are separate inputs that need to be staged as plugin assemblies.
No reverse-engineering tool currently turns this output into the original authoring sources
or guarantees a runnable rebuilt 9.2 client. The exact Unity Editor used for this package is
2020.3.31f1 with Android support.

The recovery script prepares `unity-rebuild/`: it preserves the AssetRipper export, moves
generated script stubs and their `.meta` files to `RecoveredScripts/`, and stages the 19
recovered game/plugin assemblies listed in `assembly-set.txt` under `Assets/Plugins/`. This
is the assembly set used by the successful Unity import in this investigation. Build that
staged project with the included editor helper:

```bash
python3 tools/build_recovered_9_2.py build/recovery-9.2/unity-rebuild \
  --unity /path/to/2020.3.31f1/Editor/Unity \
  --output-apk build/recovery-9.2/rebuilt-9.2-arm64.apk
```

If a newer editor has already upgraded that local staged project, pass
`--allow-upgraded-project`; this only bypasses the original export-version check. Unity's
Mono.Cecil version is selected from the editor passed to `--unity`, and the builder locates
the adjacent legacy-runtime library directory when the editor distribution provides one.
ARM64 IL2CPP remains the default.
The repair script reads `System.String`'s length-field name from the selected editor's local
`mscorlib.dll` and retargets recovered field references when that private name changed between
Unity releases, preserving the original `ldfld`/`stfld` operations. It likewise recognizes
Unity 6's renamed private fields in `Dictionary<TKey,TValue>`, `List<T>`, and `Hashtable`, plus
`UnhandledExceptionEventArgs._Exception`, after confirming replacement fields exist in that
editor's core library.

Before building, recreate the serialized script bindings using the untouched AssetRipper
export and recovered DLLs:

```bash
python3 tools/generate_9_2_script_bindings.py \
  build/recovery-9.2/unity-project/ExportedProject \
  build/recovery-9.2/cpp2il \
  build/recovery-9.2/unity-rebuild/Assets \
  --unity /path/to/2020.3.31f1/Editor/Unity
```

The generator reads script GUIDs from serialized scenes, prefabs, and assets, then creates
small `.cs` binders that inherit from the corresponding recovered assembly types. It keeps
the original `.cs.meta` GUIDs so serialized components bind to code in the recovered DLLs.
It uses Unity's bundled Mono compiler and Mono.Cecil; no system compiler installation is
needed. The last verified export produced 776 main and 108 firstpass binders, plus two
AssetRipper stub fallbacks. Four additional script GUIDs occur only in debug prefabs and have
no source `.meta`; the manifest records four empty placeholder MonoBehaviours for those
references. These placeholders preserve project importability, not their missing behavior.
The generator writes its counts and unresolved GUIDs to
`recovered-script-bindings-manifest.json` beside the staged project. All generated binders
and assemblies stay in the local build directory.

On Ubuntu 26.04, the Unity 2020 Editor also needs compatible legacy runtime libraries. Keep
the extracted Ubuntu 24.04 `libxml2`, ICU 74, and OpenSSL 1.1 libraries under external
storage and expose them only to the build process. Unity's bundled Roslyn compiler needs
invariant globalization with this local library set:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
LD_LIBRARY_PATH=/path/to/Unity/legacy-runtime/usr/lib/x86_64-linux-gnu \
  python3 tools/build_recovered_9_2.py build/recovery-9.2/unity-rebuild \
  --unity /path/to/2020.3.31f1/Editor/Unity \
  --output-apk build/recovery-9.2/rebuilt-9.2-arm64.apk
```

The compatibility libraries are local tool dependencies and are not added to this
repository.

`--backend Mono` selects Unity's Mono player backend for a diagnostic build; IL2CPP is the
default and matches the original scripting backend. Set `--architecture ARMv7` for the Mono
diagnostic path in this Unity version; ARM64 remains the default for IL2CPP.

The helper sets package id `com.kabam.bigrobot`, version `9.2.0`, version code `9200`,
ARM64, and IL2CPP, then builds the enabled scenes (or `Assets/Scenes/1_boot.unity` if none
are enabled). Managed stripping is disabled so Unity keeps the recovered assemblies and
generated script bindings. It creates or reuses a local keystore beside the APK output and signs with it,
so the result cannot update an installation signed by the original publisher. It points
Unity to the SDK, NDK, and OpenJDK installed with that Editor. The staged project imported
successfully. An initial IL2CPP build stopped in UnityLinker with a `NullReferenceException`
and produced no APK. The recovered assemblies had lost the PE `IMAGE_FILE_DLL` characteristic
when the temporary import sanitizer rewrote them. The build helper now restores that flag on
managed plugin images before launching Unity. The import repair also restores missing native
P/Invoke declarations for the packaged Google Play Games, Firebase, Krash, ENet, bug-report,
and APK-signature wrappers; those declaration-only mappings use linkage names observed in the
local 9.2 import/native-library inputs.

With the DLL flags restored, UnityLinker completed and IL2CPP reached its `WarmNamingComponent`
pass, then stopped with a `NullReferenceException` in
`TypeReferenceEqualityComparer.GetHashCodeFor`. Source-stage diagnostics traced the bad generic
operands to Cpp2IL's metadata resolver: for metadata v27+, it tried every immediate and absolute
load address as a possible metadata-usage slot. In `InterpolatedDelayParameter::.ctor(System.Single)`,
the immediate `0xCCCD` decoded by coincidence as `T`, owned by the unrelated
`BlockingUiAction+<>c__DisplayClass3_0` generic type. The native `MOVZ`/`MOVK` sequence instead
forms `0x3D4CCCCD`, the bit pattern for `0.05f`. `MidiSequencer.Process` also received an open
`T` from `System.Activator.CreateInstance<T>` in a double addition; that generic parameter was
owned by an unrelated method, not by `MidiSequencer.Process` or its declaring type.

The reproducible source patch now limits candidate metadata slots to pointer-aligned addresses
and accepts an open generic parameter only when its owner is in the current method/type
instantiation. The patched builder applies cleanly to the pinned Cpp2IL commit, and a fresh run
against the exact APK exits successfully, emits 49 assemblies, and reports all 64,447 methods
decompiled. This is stronger IL-generation evidence, but does not prove semantic equivalence or
a complete Unity rebuild. A later Unity 2020 ARM64 IL2CPP diagnostic build was run in a
plugins-only blank project after the metadata-resolver patch. With the recovered
`Assembly-CSharp.dll` unchanged, IL2CPP still stopped in `WarmNamingComponent`.

Inspecting Unity 2020.3.31f1's bundled `Unity.Cecil.Awesome.dll` showed that the naming
comparer hashes generic-parameter owners. A Cecil scan of the recovered plugin set found 11
ownerless `!0` generic-parameter references in `Assembly-CSharp.dll`, including local types
and `castclass` operands in five recovered methods. In a disposable diagnostic copy only,
replacing those 11 references with `System.Object` let IL2CPP pass `WarmNamingComponent` and
reach method source generation. That substitution is not a semantic repair and was not kept
in the project. Unity's bundled `Mono.Security.dll` has the same strong-name identity as the
recovered dependency and contains its failing `PKCS12.GetExistingParameters(Boolean&)` method.
Replacing it in a disposable build copy passed that method conversion. The import-repair tool
now applies this substitution only after verifying both the full assembly identity and that
method signature; this is a framework dependency replacement, not a Mono player build.

With both diagnostic substitutions, IL2CPP reported 15 method-conversion errors plus a fatal
method-conversion exception across `Assembly-CSharp`, `Assembly-CSharp-firstpass`, Fabric,
Facebook, SharpZipLib, and NBidi. The main errors include invalid evaluation-stack types and
by-reference operands in recovered IL. A Cecil resolution check against Unity 2020's managed
assemblies resolved `System.TimeSpan`, `UnityEngine.Vector2`, and `UnityEngine.Vector3` to their
expected definitions, so these errors are in method-body stack analysis rather than missing
Mono/player or Unity type assemblies. The failures include comparisons whose recovered operands
have incompatible value types and instructions whose recovered method references lost by-reference
wrappers. This confirms the naming crash is triggered by malformed recovered generic references,
while also showing that repairing that crash alone is insufficient for an ARM64 APK. The original
recovered `Assembly-CSharp.dll` and `Mono.Security.dll` were restored after the diagnostic run; no
APK was produced.

An external-only compatibility trial compared the operator-supplied 2.0.2 managed libraries with
the recovered 9.2 assemblies. Five exact-signature methods in `Fabric.Core` and `NBidi` were copied
into disposable 9.2 assembly copies, leaving their 9.2 types and remaining methods intact. Unity
2020 then crashed with `SIGSEGV` during managed-resource preparation, before it invoked IL2CPP.
That first run did not establish whether the copied bodies are compatible or reduce the IL2CPP
errors. In a follow-up plugins-only build, a disposable copy of `Assembly-CSharp.dll` also received
the 11-reference generic workaround described above. Unity then passed `WarmNamingComponent` and
entered IL2CPP method conversion, but reported 17 build errors, including invalid stack types for
recovered value types and invalid casts involving by-reference operands. The failing methods span
`Assembly-CSharp-firstpass`, Fabric, `Mono.Security`, Facebook, and SharpZipLib. Because this run
combined the method-body copies with the generic workaround and did not use a matching control,
it cannot attribute any change in the error set to the copied bodies. No APK was emitted. The
isolated `Assembly-CSharp.dll` was restored; no diagnostic DLL or copied method body was added to
the repository.

The 2.0.2 trial is guidance only and is not a source of method bodies for the 9.2.0 rebuild. A later isolated body-copy experiment in `Assembly-CSharp.dll` reached UnityLinker but failed because the copied method referenced a compiler-generated lambda cache field absent from the 9.2 type. The experiment was discarded, and the active isolation project was restored to its 9.2 Cpp2IL assemblies before continuing. Native 9.2 inspection also showed that the 11 ownerless generic references above are symptoms of incorrect virtual-call recovery and register-type propagation, not missing concrete generic arguments: for example, apparent `List<T>` calls resolve to `BCGHeroDetailsBase.GetHashKey`, `OldObjectPoolItem.IsObject`, `StashInventoryItemDisplay.Init`, `EB.Sparx.EndPoint.Service`, and `CategoryTabData.get_tabId` in the 9.2 native binary. Substituting concrete generic types would encode the wrong behavior. The next repair must correct 9.2 call recovery or reconstruct the affected methods from 9.2 evidence.

The Unity 2020 ARM64 IL2CPP build is the target workflow. The optional Mono/ARMv7 invocation
above records an earlier packaging diagnostic only; it is not a required build step or the
target runtime.

The Mono ARMv7 diagnostic build produced a signed 36 MB APK. `aapt dump badging` and
`apksigner verify --print-certs` confirmed package id `com.kabam.bigrobot`, version `9.2.0`,
version code `9200`, and a valid local signature. That build logged 30,708 unresolved script
references and 1,263 invalid-IL exceptions from recovered assemblies. It establishes that
the AssetRipper assets and recovered DLLs can be packaged by Unity, not that the client is
playable or fully restored. A faithful ARM64 IL2CPP rebuild still needs valid recovered IL,
script-to-scene mappings, and successful UnityLinker and IL2CPP conversion passes. The original shipped APK's
signing identity and runtime integrations are not reproduced.

### Alternative Cpp2IL analysis experiment

The official Cpp2IL `new-analysis` branch was also built locally and run against the
same matched 9.2 ARM64 library and metadata under its .NET 6 runtime. With its experimental
IL-to-assembly option, it mapped 89,278 method definitions and reported analysis for
33,516 methods, with 31,984 successful (95%). It wrote 50 assemblies, including a 9.9 MB
`Assembly-CSharp.dll`; this is a useful second reconstruction to compare against the
pinned build, but it is not yet a rebuild input. ILSpy 9.1 emitted 2,191 C# files (about
15.9 MB) before stopping on an invalid method body in
`BT.NodeTypeMetadata.CanAddMoreChildren`. The sampled `QuestFlow` state-machine output
also contains incomplete control flow and placeholder exceptions. The percentage is an
analysis success metric, not a measure of recovered behavior or source completeness.
The pinned patched generator now reproduces a 49-assembly set (58,055,680 bytes) from the
exact 9.2 input and reports all 64,447 methods emitted. This establishes repeatable
assembly generation, not semantic correctness: generated DLL hashes vary between runs,
and invalid or incomplete methods can still prevent a faithful client rebuild. The
original and rebuilt output remain local.

The script and this recipe contain no APK, assemblies, native library, game assets, or
decompiled source. Keep generated material local under ignored `build/` or another
operator-controlled storage location; do not commit or redistribute those outputs. The
source-only compliance boundary is described in [`COMPLIANCE.md`](COMPLIANCE.md).

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

The comparison also emits `loader_risk_differences` for assembly identity and metadata
profile changes, reference and resource changes, inheritance/interface changes,
serialization-relevant field/layout changes, and changed native imports. On the current
two-root isolated outputs, identity, metadata profile, resources, inheritance/interfaces,
and changed native imports are unchanged; both outputs still have three changed framework
references, and `Assembly-CSharp-firstpass` adds a reference to `Assembly-CSharp`. The
serialization-layout bucket contains 345 firstpass types and 502 game-root types because
it reports exact type/field differences for review. These categories are loader-risk
signals, not a Mono acceptance verdict.

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

To pin a candidate to a successful isolated compile audit and assess it alongside the
preserved original DLLs, pass that audit's report:

```bash
python3 tools/decompilation.py replacement-plan build/decompilation/mono-XXXX \
  --dotnet /home/darabat/.dotnet/dotnet \
  --metadata-tool tools/mono_metadata/bin/Debug/net8.0/MonoMetadata.dll \
  --root Assembly-CSharp --root Assembly-CSharp-firstpass \
  --compile-report build/decompilation/mono-XXXX/compile-XXXX/report.json
```

The schema-2 report records the exact replacement and preservation file set, the compile
report hash, output hashes, per-root metadata diffs, and an exact AssemblyRef-to-supplied
AssemblyDef identity inventory. It compares that inventory with the original all-DLL
baseline, so compiler-reference drift and newly introduced edges are visible without
copying a DLL. On the current `mono-2fviys29` audit, the proposed set is exactly the two
game roots; all other managed files are preserved. The candidate has 24 exact internal
identity matches and 28 mismatches, including six newly exposed framework-reference
mismatches and one added `Assembly-CSharp-firstpass` → `Assembly-CSharp` edge. The report
therefore remains `review-required`, with `packaging_authorized=false` and
`runtime_verified=false`. These are metadata and loader-risk signals, not proof that Mono
will reject or accept the files.

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
