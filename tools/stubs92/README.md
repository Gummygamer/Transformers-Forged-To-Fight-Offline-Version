# 9.2 Stub Generator

Generates Unity 2020-compatible C# stub classes from the Transformers Forged To Fight 9.2 IL2CPP APK's DummyDll assemblies. These stubs provide the exact class names, namespaces, and serialized field layouts that 9.2 asset bundles expect, so prefabs deserialize without missing-script errors.

**Stubs are the data-contract layer, not an implementation.** Method bodies are empty. Any behavior is hand-written on top of them (see "Replacing a stub" below).

## Inputs

- **Il2CppDumper v7 DummyDll** (`build/dumps/v7/DummyDll/`): All 50 assemblies produced by Il2CppDumper from the 9.2 APK's `global-metadata.dat`. The `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` contain game types; the rest are framework references for resolution.
- **dotnet SDK 8.0+**: For building and running StripCG and ilspycmd.
- **ilspycmd**: Decompiler (found automatically in `build/mono-investigation/tooling/.store/`).

## Outputs

Written into the tracked Unity project `unity/StoryPort/`:

- `Assets/Plugins/Firstpass92/` — Assembly-CSharp-firstpass stubs (Unity compiles `Assets/Plugins/**` into the predefined `Assembly-CSharp-firstpass` assembly)
- `Assets/Scripts/Assembly-CSharp/` — Assembly-CSharp stubs (predefined `Assembly-CSharp`)
- `Assets/Plugins/FabricCore92/` — `Fabric.Core.dll` converted to source (see below), with an asmdef named `Fabric.Core`
- `Assets/Plugins/*.dll` — the framework DLLs the stubs reference, copied from DummyDll

All four are **git-ignored** derived artifacts. The generator recreates them from `build/dumps/v7/DummyDll`, so a fresh clone needs only that dump.

## Pipeline

1. **StripCG** (Mono.Cecil): Removes compiler-generated types (`<Iterator>d__N`, `<>c__DisplayClass`, lambda methods) at the IL level, rewrites every method body to a compilable stub (ctors chain to a base ctor, void methods `ret`, value-returning methods `ldnull; throw`), and rebuilds the interface metadata IL2CPP drops (step 4 below).
2. **ilspycmd** (`-p -lv CSharp8_0 -r`): Decompiles stripped DLLs to per-file C# sources with correct using directives and block namespaces compatible with Unity 2020's C# 8 compiler.
3. **Attribute cleanup**: Strips IL2CPP metadata records (`[Token]`, `[FieldOffset]`, `[Address]`, `[AttributeAttribute]`, `[Il2CppSetOption]`) that are not valid C# attributes, plus fixes for ilspycmd rendering artifacts.
4. **Interface metadata reconstruction** (in StripCG, so it covers every DLL that becomes source — both game assemblies and any converted framework DLL):
   - **Overrides**: DummyDll has no `MethodImpl`/`.override` records, so an explicit interface implementation survives only as a method whose name is the dotted interface member (`Fabric.IEventListener.Process`, sometimes IL2CPP-escaped as `Fabric_002E..._002EProcess`). StripCG finds the interface, matches the member, and adds the override so ilspycmd emits a real explicit implementation.
   - **Explicit properties/indexers**: DummyDll often has the accessor methods but no owning `PropertyDefinition`, and drops indexer parameters outright. StripCG creates the property (`<Iface>.Member`) and restores index parameters from the interface accessor.
   - **Real BCL references**: Unity's `4.7.1-api` reference assemblies are passed to both StripCG's resolver and ilspycmd's `-r`, ahead of DummyDll. The DummyDll's BCL copies have degraded signatures (e.g. `IList.Item` with no index parameter), which would otherwise render explicit indexers as plain properties.
5. **Open-generic serialized-field fix**: Unity 2020's player build dies in `LinearCollectionField` (`m_ArrayField != SCRIPTING_NULL` → SIGSEGV) on any serialized collection field whose element type is a generic parameter of its declaring type (`[Serializable] class D<T> { [SerializeField] List<T> keys; }`). The rule marks such fields `[NonSerialized]` on the generic base and injects closed `[SerializeField]` copies into each concrete subclass.
6. **Framework DLL scan**: every DummyDll that is *not* converted to source is decompiled to a scratch directory and checked for the pattern in step 5. Any hit is reported, because shipping such a DLL as a binary reintroduces the crash. This check is why `Fabric.Core.dll` is converted to source.
7. **Namespace-shadowing scan**: reports EB types and sub-namespaces whose names collide with `UnityEngine`/`System` names, because unqualified use of those names inside `EB.*` code compiles cleanly and fails at runtime. See [Namespace shadowing](#namespace-shadowing--the-trap-that-compiles-cleanly-and-fails-at-runtime).

## Framework DLLs that must be source, not binary

`SOURCE_FRAMEWORK_DLLS` in the generator lists them. A binary DLL cannot be fixed by any source-level rule, so such a DLL is decompiled to C# and emitted into a folder with an **assembly definition named after the original DLL** — that keeps the assembly name identical, so MonoScript references recorded in 9.2 scenes and prefabs still resolve.

Currently one entry: `Fabric.Core.dll` (assembly `Fabric.Core`). Its `SerializableDictionary<TKey,TValue>`, `FastList<Key,Data>` and their concrete subclasses are exactly the step-5 pattern, and it was the cause of the `BuildPlayer` SIGSEGV.

**Do not fix such a DLL by substituting an older copy.** A 2.0.2-era DLL has 2.0.2 field layouts and 9.2 prefabs would silently fail to bind to it.

## Usage

```bash
python3 tools/stubs92/gen_92_stubs.py
```

Paths default to this repository's layout. Override with `--dummy-dll-dir`, `--ilspycmd`, `--unity-project`, or `--dotnet`.

## Project layout rules

`unity/StoryPort/` is tracked; the generated parts inside it are not.

| Path | Assembly | Tracked | Purpose |
|------|----------|---------|---------|
| `Assets/Game/` | Assembly-CSharp | yes | Hand-written logic for **Assembly-CSharp** types, plus new helpers |
| `Assets/Plugins/Game/` | Assembly-CSharp-firstpass | yes | Hand-written **replacements** for firstpass types |
| `Assets/Editor/` | Editor | yes | Build tooling |
| `Assets/Plugins/Firstpass92/` | Assembly-CSharp-firstpass | no | Generated stubs |
| `Assets/Scripts/Assembly-CSharp/` | Assembly-CSharp | no | Generated stubs |
| `Assets/Plugins/FabricCore92/` | `Fabric.Core` (asmdef) | no | `Fabric.Core.dll` as source |
| `Assets/Plugins/*.dll` | — | no | Framework DLLs from DummyDll |
| `Assets/StreamingAssets/tftf-9.2` | — | no | Symlink to bundles in `build/` |

**Assembly placement matters.** All 9.2 code and prefabs bind to the `Assembly-CSharp-firstpass` copy of a firstpass type. A same-named class in `Assets/Game/` lands in `Assembly-CSharp` and would silently never be used — and firstpass cannot even see `Assembly-CSharp`. So:

- Replacing an **Assembly-CSharp** type (e.g. `Setup`) → implement in `Assets/Game/`.
- Replacing an **Assembly-CSharp-firstpass** type (e.g. `WindowManager`, `Hub`, `GameboardManager`) → implement in `Assets/Plugins/Game/`.
- New helper classes → `Assets/Game/`, **with names that don't shadow any 9.2 type**.

## Replacing a stub

`implemented.txt` lists fully-qualified type names that have hand-written implementations. The generator skips those types so regeneration never clobbers them.

To implement a class:

1. Add its full name to `implemented.txt` (e.g. `EB.Sparx.LoginManager`).
2. Create the hand-written file in the correct assembly folder (see table above).
3. Keep the **exact 9.2 class name, namespace, and serialized field names/types**. Prefabs bind by name — renaming a field silently drops its data.
4. Re-run the generator; the stub for that type is skipped.

`implemented.txt` currently lists the EB rendering types plus `EB.Time` and `EB.Debug` (see "Namespace shadowing" below). Other behavior so far lives in new helper classes (`RevivalClient`, `RevivalLogin`, `StoryPortWindows`), which don't shadow 9.2 types.

## Namespace shadowing — the trap that compiles cleanly and fails at runtime

C# resolves an unqualified name by walking **outwards through the enclosing
namespaces** before it consults `using` directives. So inside `namespace EB.Rendering`
a bare `Time.deltaTime` binds to `EB.Time`, *not* `UnityEngine.Time` — and the
generated stub for `EB.Time.deltaTime` has a body of `throw null;`.

This is worse than a compile error, because it compiles perfectly:

- `EBLight.LateUpdate` threw a `NullReferenceException` once per run.
- A **release** build reported only `EBLight.InternalUpdate [0x00000]` — no file,
  no line, no callee, because the throw was inside a tiny inlined getter.
- Wrapping the call in `try`/`catch` made the exception *disappear*, because the
  catch logged through `EB.Debug.LogError` — another empty stub. The fault was
  being caught and reported into a void.

A development build with symbols named the real frame in one run.

**Rules**

1. Hand-written code in any `EB.*` namespace must fully qualify a name whenever EB
   reuses it: `UnityEngine.Time.deltaTime`, `System.Math.Min`, etc.
2. `gen_92_stubs.py` now prints a **Namespace shadowing** report on every run. If
   your new file is in one of those namespaces, qualify accordingly.
3. Prefer implementing the EB utility rather than dodging it — `EB.Time` and
   `EB.Debug` are now real implementations, so `Time.deltaTime` and `Debug.Log`
   work for every EB class, including ones written later.

## Debugging a runtime fault on device

Release IL offsets are not enough to locate a fault. Build a development player
with symbols (opt-in, so normal builds are untouched):

```bash
export TFTF_DEV_BUILD=1
export TFTF_ANDROID_OUTPUT="$PWD/build/mono-investigation/storyport-dev.apk"
build/mono-investigation/unity-2020.3.31f1/Editor/Unity -batchmode -nographics -quit \
  -projectPath "$PWD/unity/StoryPort" \
  -executeMethod StoryPortProjectSetup.BuildAndroid \
  -logFile "$PWD/build/mono-investigation/android-dev.log"
```

`TFTF_DEV_BUILD=1` adds `BuildOptions.Development | BuildOptions.AllowDebugging`
and sets full stack traces for logs, exceptions and errors. The logcat stack then
carries `EBLight.cs:<line>` for the throwing frame **and its callees**, which is
what turned the EBLight fault from hours of guessing into a single run. It also
revealed the `EB.Time` frame that the release build had inlined away.

## Implemented EB rendering slice

The first vertical slice of real game behaviour replaces the lighting stubs. All
of these are in `Assets/Plugins/Game/` (firstpass) except `RenderSettings`, which
is an **Assembly-CSharp** type and therefore lives in `Assets/Game/`.

| Type | Why it had to be real |
|------|-----------------------|
| `EBShaderGlobalProperty` | `Shader.PropertyToID` + the `SetGlobal*` calls |
| `EBRenderSettingsBase` | exposure/fog/ambient maths; publishes the globals |
| `RenderSettings` | the component the scene attaches — its stub **overrode `ApplyWhenActivated` with an empty body**, so the base-class work never ran |
| `EBLight` | publishes `_EBDirectionalLightDirection` / `_EBDirectionalLightLuminanceIntensity` |
| `EBReflectionProbe` | publishes `_PMREM`/`_IEM` and the SH coefficients |
| `EB.Time`, `EB.Debug` | EB utilities that shadow their UnityEngine equivalents |

**The EB PBR shaders do not read Unity lights.** They read *global* shader
properties, so with the globals unset every surface renders black no matter how
the scene is lit. Two symptoms followed from that and are worth remembering:

- A missing exposure global makes the whole model black, because the shader
  multiplies its final colour by exposure.
- An unbound `_ShadowMap` makes every surface fully shadowed; the render proof
  binds a white 1x1 texture with a zero exponential factor to mean "fully lit"
  while no shadow pass runs.

The reference is the 2.0.2 `Assembly-CSharp-firstpass` source, decompiled with
`ilspycmd -t "EB.Rendering.EBLight"`; the 2.0.2 `EB.Rendering.RenderSettings`
lives in `Assembly-CSharp.dll`, not firstpass.

## Known DummyDll gaps

The generator's `fix_interface_implementations` table is down to a single entry, and it is not a workaround for the decompiler — it covers a member that is genuinely missing from the dump. If you find yourself adding entries, first check whether StripCG can reconstruct the metadata instead (see pipeline step 4); that is how the previous 17 entries were retired.

- `EB.LightWeightDictionary<T1,T2>`: IL2CPP dropped the non-generic `IDictionary` indexer entirely — the dump has `IDictionary.Keys`/`Values` but no `IDictionary.Item` accessor. The entry forwards `IDictionary.this[object]` to the generic indexer that does exist.

`copy_stubs` matches `implemented.txt` entries by **file stem + anchored namespace**, not by substring. ilspy writes one top-level type per file, so the stem identifies the type exactly. A substring search also matches *nested* types and *longer* namespaces: implementing `EB.Debug` skipped `EB.Sparx/ODRManager.cs` (which declares its own nested `class Debug`), and a plain `'namespace EB' in content` check also matches `namespace EB.Sparx`. Both are fixed; keep the anchored form.

## Git policy

Never commit generated stubs, framework DLLs, or asset bundles. `unity/StoryPort/.gitignore` covers them. What belongs in version control: the generator (`gen_92_stubs.py`, `StripCG.cs`, `StripCG.csproj`, `implemented.txt`, this README), the hand-written game code, ProjectSettings, and Packages.