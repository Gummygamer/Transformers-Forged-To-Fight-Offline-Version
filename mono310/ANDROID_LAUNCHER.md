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

Ship one APK that contains **no game bytes**: the Unity 2020 Mono player, newly authored code and
shaders, and the offline server. The user supplies the original 3.1.0 and 9.2.0 APKs (copyright
compliance, as in the console recompilation projects) and the launcher reads or converts what it
needs on the phone, so users without a PC can play.

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
- The game code (`Assembly-CSharp`, `-firstpass`) was written for Unity 5.3 APIs. The compat
  repairs in `scripts/compat310.py` are mostly decompiler fixes, which a start from the original
  IL would not need, plus a smaller set of 2020 API and behaviour fixes. **[⚠ inferred]**

## Proposed architecture

An empty-shell player plus an on-device **ingest** step.

1. **Shell APK (distributed).** Unity 2020 Mono player, authored shaders, `Hud_Health` and the
   other replacement shaders, the offline server, and an ingest UI. No game data or game code.
2. **Ingest, once, on the phone.** The user picks the 3.1.0 and 9.2.0 APKs (Storage Access
   Framework). The launcher then:
   1. extracts the 3.1.0 bundle folders and lays out the 9.2.0 bundles plus `toc` (port of
      `build_odr_wad.py`, plain file copying);
   2. converts the player data into type-tree bundles: add type trees (engine classes from a
      published class database, `MonoBehaviour`s from the script fields in the user's own
      `Assembly-CSharp`), swap shader references for the authored shaders, wrap as `UnityFS`;
   3. patches the user's own `Assembly-CSharp*.dll` at IL level with authored rules and keeps
      the result in app storage;
   4. extracts the move data the 9.2.0 fighters need (already scripted in
      `scripts/extract_donor_moves.py`).
3. **Run.** The shell `Assembly.Load`s the patched DLLs before loading any prefab bundle, then
   starts the boot scene from the converted bundle. All outputs stay in app-private storage and
   are never uploaded.

Because everything game-derived is produced on the user's device from the user's files, the
distributable stays free of game bytes, as in the PC pipeline.

## Milestones

| | milestone | proves |
|---|---|---|
| M0 | PC-side release kit: one `package` command plus the launcher that installs the result and hosts the server | a distributable that needs no `adb reverse` or host server |
| M1 | Spike: a 2020 Mono shell `Assembly.Load`s an IL-patched original `Assembly-CSharp` and instantiates one converted NGUI prefab | the code and data halves of the design work together |
| M2 | Converter for the player data in a PC tool (UnityPy or .NET) validated against the current Editor build | conversion correctness |
| M3 | Port of M2 to C# running inside the shell on the phone | no PC needed |
| M4 | On-device IL patch rules, then ingest UI and signing/installation flow | the end-user experience |

## Risks and open questions

- **Runtime `Assembly.Load` with prefab bundles.** Mono builds are commonly extended this way,
  but it is untested here. **[⚠ inferred]** M1 decides it.
- **Mono 2.0 era IL on a 2020 runtime.** The original references `mscorlib 2.0.5.0`; the IL
  patch rules for removed or changed Unity APIs are not yet enumerated. **[⚠ inferred]**
- **Type-tree generation for `MonoBehaviour`s** must follow Unity 5.3 serialization rules
  (public and `[SerializeField]` fields, nested `[Serializable]` types, arrays and lists).
- **Shader and material fidelity** stays at the current placeholder level.
- **Distributing the Unity runtime** inside the shell must satisfy the Unity license terms.
- **Android-only ingest cost:** the WAD is about 400 MB and the conversion allocates a lot of
  memory; both need measuring on a mid-range phone.
- **Not feasible:** running the existing PC tools on the phone. AssetRipper, ILSpy and the Unity
  Editor cannot run on Android, and the Editor has no ARM Linux build.
