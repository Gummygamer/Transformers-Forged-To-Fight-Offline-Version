# mono310 as an assetless Android launcher

## Table of contents

- [Scope and status](#scope-and-status)
- [Goal](#goal)
- [What the build is made of](#what-the-build-is-made-of)
- [What can be read from the user's APKs unchanged](#what-can-be-read-from-the-users-apks-unchanged)
- [What needs conversion](#what-needs-conversion)
- [Proposed architecture](#proposed-architecture)
- [Milestones](#milestones)
- [Risks and open questions](#risks-and-open-questions)

## Scope and status

**Nothing in this document is implemented.** It records measurements taken on 2026-10-16 and a
proposed design. The working path today is the PC pipeline in [`README.md`](README.md); an
Android-only path does not exist yet. Marks follow [`DOC_STYLE.md`](../DOC_STYLE.md).

Environment: operator 3.1.0 APK `com.kabam.bigrobot` build 822754 (Unity 5.3.5p8, Mono,
`armeabi-v7a`); operator 9.2.0 APK (all 102 bundles report engine `2020.3.31f1`); recompile built
with Unity 2020.3.31f1.

## Goal

Ship one APK that is the recompiled game **without its assets**, so users without a PC can play.
The user supplies the original 3.1.0 and 9.2.0 APKs, as in the console recompilation projects,
and the launcher reads or converts the assets it needs on the phone.

## What the build is made of

Measured by diffing the original 3.1.0 APK against the recompiled one. **[📄 binary]**

| part | original 3.1.0 | recompiled | how it is produced today |
|---|---|---|---|
| `assets/characters`, `assets/ui`, `assets/karnak`, `assets/frontendfx` | 18.9 / 6.2 / 31.0 / 0.2 MB | identical sizes | copied unchanged |
| `assets/bin/Data` (player data) | 67.4 MB, 1610 files | 82.8 MB, 2550 files | AssetRipper + Unity Editor build |
| `Managed/Assembly-CSharp*.dll` | 5.1 + 3.1 MB | 4.8 + 2.9 MB | ILSpy, text repairs, Unity compile |
| `lib/armeabi-v7a` | Unity 5.3 runtime | Unity 2020 runtime | Unity Editor build |
| 9.2.0 content | n/a | WAD of repacked bundles | `scripts/build_odr_wad.py` (pure repacking) |

## What can be read from the user's APKs unchanged

- **The four 3.1.0 asset-bundle folders.** They are 5.3.5 `UnityFS` bundles with type trees and
  load in the Unity 2020 player: the recompiled APK carries them byte-identical and the fight
  reads `move_sword_*` text assets out of `characters/moves.assetbundle`. **[👁 live]**
- **Every 9.2.0 bundle.** They already target 2020.3.31f1, so a 2020 player loads them as they
  are. The WAD step only re-lays them out and writes a `toc`. **[📄 binary]** (engine header of
  all 102 bundles)

## What needs conversion

The original player data cannot be loaded by a 2020 player:

- All 1583 serialized files checked have **no type tree**, so a newer runtime cannot upgrade
  them. **[📄 binary]** (UnityPy `enable_type_tree` false)
- It holds 204 compiled shaders in the 5.3 format, which a 2020 runtime cannot use; the build
  replaces them with newly authored shaders by name. **[📄 binary]**
- Its content is small and regular: one boot scene, about 1018 prefabs (mostly NGUI
  `MonoBehaviour`s, 28,864 of them), 137 config `TextAsset`s (6.8 MB), 41 textures, 17 materials,
  2 fonts, 26 `.asset`. **[📄 binary]** (ripped project, UnityPy object census)
- **The code needs very little API repair.** `launcher/tools/ApiGap.cs` checks every external
  type, method and field reference of the original IL against the Unity 2020 Android Mono
  assemblies. `Assembly-CSharp` (11,189 member references) has **0 unresolved members and 2
  unresolved types**; `Assembly-CSharp-firstpass` (7,413) has **11 unresolved members and 6
  unresolved types**. The gaps are the removed legacy networking types (`RPC`, `RPCMode`,
  `NetworkStateSynchronization`), `UnityEngine.Profiler` (now `UnityEngine.Profiling`),
  `Application.bundleIdentifier`, `RenderTexture.generateMips`, `Texture2D.EncodeToPNG`,
  `DownloadHandlerScript` (namespace moved) and one `SharpZipLib` overload. **[📄 binary]**
  Most of `scripts/compat310.py` therefore repairs decompiler output rather than API drift and
  would not be needed when patching the original IL. Behavioural fixes (for example `EnumMap`'s
  comparer, the fight fixes) still need IL-level patches. **[⚠ inferred]**

## Proposed architecture

The shipped APK is the recompiled game itself, **without its assets**. The user's APKs supply the
assets at run time.

1. **Shell APK (distributed).** The Unity 2020.3 Mono player build we already produce, containing:
   the recompiled game scripts, the authored replacement shaders, the offline server and a small
   authored bootstrap scene. Its `Resources`, scenes, textures, audio and bundles are empty.
2. **First launch, on the phone.** The user picks the original 3.1.0 and 9.2.0 APKs (Storage
   Access Framework). The launcher then:
   1. extracts the four 3.1.0 bundle folders and lays out the 9.2.0 bundles plus `toc` (port of
      `build_odr_wad.py`; plain file copying, no conversion);
   2. converts the 3.1.0 player data into type-tree bundles that a 2020 player can load (see
      below);
   3. extracts the 9.2.0 move data the donor fighters need (`scripts/extract_donor_moves.py`
      logic);
   4. stores everything in app-private storage and never uploads it.
3. **Run.** The bootstrap scene loads the converted bundles and starts the original boot scene.
   The recompiled scripts resolve the `MonoBehaviour`s in those bundles by class name.

**The one piece that does not exist yet is the converter in step 2.2.** Recipe: read the 5.3.5
serialized files (no type trees), attach type trees (engine classes harvested from the user's own
3.1.0 bundles, which carry them; `MonoBehaviour` layouts from the shell's own compiled script
classes), swap shader references for the authored shaders, and write `UnityFS` bundles. The 2020
runtime then upgrades them on load, exactly as it already does for the 3.1.0 bundles.

### Rejected alternative: patch the original IL

Instead of shipping recompiled scripts, an on-device Cecil patcher could repair the user's own
`Assembly-CSharp` (the measured API gap above is small). This keeps game code out of the
distributable, but it is a patcher rather than the recompiled game, and the functional fixes in
`scripts/compat310.py` would all need re-expressing as IL patches. Not pursued; the measurements
are kept because they are true and cheap to reuse.

### Compliance note

Shipping the compiled recompiled scripts differs from the current rule in `COMPLIANCE.md` ("no
compiled game assemblies"). That section must be updated before a release if this design is
adopted.

## Results so far

PC prototype in `launcher/convert/`, checked in the Unity 2020.3.31f1 Editor against the recompiled scripts
(2026-10-16). **[👁 live]**

- **Type trees.** UnityPy's class database matches every genuine 5.3.5 tree in the user's bundles (29 of 29
  classes) and parses all 40,000+ non-script objects of the original player data byte-exactly. Script layouts
  built by the Editor from the recompiled scripts parse all 28,849 `MonoBehaviour`s whose script exists, once
  the `Keyframe` and `Gradient` structs are restored to their 5.3.5 layout.
- **Bundle.** `convert_player_data.py` writes one multi-file bundle (54.7 MB, uncompressed) from the whole
  original `Data` folder. Unity loads it: 1,017 prefabs (47,830 components, 34 missing-script components in 27
  prefabs), 138 text assets, 35 textures, 16 materials, 6 cubemaps, 3 fonts, 2 animator controllers and a
  `ScriptableObject`. The 197 shaders are deliberately dropped (old compiled format); materials carry their shader
  name as a `TFTF_SHADER=` keyword for the loader to resolve.
- **Pitfalls that cost time** (symptom first):
  - *`LoadAsset` returns null for assets in a second node of the same bundle*: external paths must be
    `archive:/<main CAB name>/<node>` in lower case, not `archive:/<node>/<node>`.
  - *"The file ... is corrupted ... Position out of bounds" when loading a `Texture2D`*: the `TypelessData`
    node of the type tree must have `m_TypeFlags = 1` like `Array` nodes.
  - *Scripts that fail to parse only when they contain a curve or gradient*: 5.3.5 `Keyframe` has no weight
    fields and `Gradient` stores packed 32-bit colours and no `m_Mode`.

Not yet done: the level scene (`level0`), builtin-asset references, material shader fixing, rendering
correctness, the on-device port and everything after M2.

## Milestones

| | milestone | proves |
|---|---|---|
| M1 | Converter spike on the PC: wrap one 5.3.5 prefab as a type-tree bundle and load it in a 2020 player | the conversion idea works at all |
| M2 | Converter for the whole player data on the PC, checked against the current Editor build | conversion correctness |
| M3 | Shell project: strip the build to scripts plus bootstrap scene; loader that mounts the converted bundles | the game runs from converted assets |
| M4 | Port the converter to C# inside the shell; ingest UI (pick APKs, progress, errors) | no PC needed |
| M5 | Offline server inside the shell, signing and packaging, version 0.1 release | a distributable launcher |

## Risks and open questions

- **Type trees for `MonoBehaviour`s** must match what the compiled scripts serialize; the
  recompile keeps field layouts, so this should hold. **[⚠ inferred]**
- **Shader and material fidelity** stays at the current placeholder level.
- **Distributing the Unity runtime** inside the shell must satisfy the Unity license terms.
- **Ingest cost:** the WAD is about 400 MB and conversion allocates a lot of memory; both need
  measuring on a mid-range phone.
- **Not feasible:** running the existing PC tools on the phone. AssetRipper, ILSpy and the Unity
  Editor cannot run on Android, and the Editor has no ARM Linux build.
