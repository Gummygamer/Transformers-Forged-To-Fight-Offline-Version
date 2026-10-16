# mono310 as an assetless Android launcher

## Table of contents

- [Scope and status](#scope-and-status)
- [Goal](#goal)
- [What the build is made of](#what-the-build-is-made-of)
- [What can be read from the user's APKs unchanged](#what-can-be-read-from-the-users-apks-unchanged)
- [What needs conversion](#what-needs-conversion)
- [Proposed architecture](#proposed-architecture)
- [Results so far](#results-so-far)
- [Milestones](#milestones)
- [Risks and open questions](#risks-and-open-questions)
- [Shell contents](#shell-contents)
- [Pitfalls found while building the shell](#pitfalls-found-while-building-the-shell)

## Scope and status

**Status 2026-10-16: the launcher works end to end on one device, and is not releasable.** A shell APK
(recompiled scripts, no game assets) starts a first-run screen; the user picks their own 3.1.0 and 9.2.0
APKs; the app converts and unpacks everything into its private storage in about 43 s and boots to the
title screen, the home base, the quest board and a fight with hits and health bars. **[👁 live]**
(Samsung A52, Android 14, armeabi-v7a build, Unity 2020.3.31f1; 3.1.0 build 822754.)

What does **not** work or is not done (read before relying on this):

- **The shell APK still carries game-derived code** (compiled recompiled scripts) and, until the build in
  `COMPLIANCE.md` ("Assetless launcher") is reviewed, **must not be published**.
- Only `armeabi-v7a` (32-bit) was built and tested, on one device. A 64-bit build is a separate port (see
  [`launcher/README.md`](launcher/README.md#limits)).
- Original-app Java code, loose assets and native libraries are no longer copied into the shell, and the
  three third-party managed libraries come from NuGet instead (see [`THIRD_PARTY.md`](THIRD_PARTY.md)).
  Features that depended on them (purchases, push, analytics, Play Games) are stubbed or absent.
- Known visual issues that exist in the non-launcher build as well: black terrain hexes on the quest board,
  missing small portraits, Ironhide's arena background.
- First play downloads and unpacks the 450 MB content package through the in-app server (about 4 min).
- Marks follow [`DOC_STYLE.md`](../DOC_STYLE.md). Sections below "Goal" up to "Proposed architecture" are the
  original 2026-10-16 morning measurements and design; they stay as written.

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

Superseded: the level scene, shader fixing and the on-device port were done later the same day; see
[Shell contents](#shell-contents) and [Pitfalls found while building the shell](#pitfalls-found-while-building-the-shell).

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

## Shell contents

Built with `TFTF_SHELL=1 mono310/run.sh assemble && run.sh assets && run.sh build && run.sh finalize`
(environment variables in `mono310/README.md`; extra for the shell: `TFTF_LEGIBLE_ARMV7` = Legible built for
`armv7-linux-androideabi`, `TFTF_TREES_BIN` = the type-tree database from
`launcher/convert/export_tree_db.py`). **[📄 binary]** (build scripts)

| piece | where | what it does |
|---|---|---|
| Recompiled game scripts | `Assembly-CSharp*.dll` | the game itself |
| Bootstrap and first-run screen | `launcher/shell/Tftf/TftfBootstrap.cs`, `TftfLauncher.cs` | picks APKs (Storage Access Framework through `Activity.java`), shows progress, then boots the converted data |
| Restart | `Activity.java` `tftfRestart`, `TftfBootstrap.cs` | after a successful install the app starts itself in a fresh process, so no start-up code of the game can see a half-installed state **[👁 live]** |
| Installer | `Tftf/TftfInstaller.cs` | copies the loose `assets/` from the 3.1.0 APK, converts `assets/bin/Data` (`Tftf/Convert`), copies the 9.2.0 move bundle, builds the ODR package (`Tftf/Odr`) |
| Offline server | `Tftf/TftfServer.cs`, `Server/*.lbl`, Legible as `liblegible.so` | the Sparx backend, started inside the app on 127.0.0.1:8080 |
| Content server | `Tftf/TftfStaticServer.cs` | serves files of the `odr/` folder on 127.0.0.1:8090 with Range support (only needed if the game ever has to fetch the package again; the installer pre-places it) |
| Java stand-ins | `launcher/shell/android/` | newly authored classes for the plugin calls the game makes (device info, downloads, notifications, deep links, purchases) |
| Native stand-ins | `launcher/shell/android/native/stubs.c`, `build.sh` | do-nothing `libandroid-signal.so`, `libapkprotect.so`, `libenetlib.so`, built with the NDK at assemble time; the game imports them with `DllImport` |
| Manifest | `launcher/shell/android/AndroidManifest.xml` | authored; no Facebook, Adjust, push or billing components |

Storage layout under `persistentDataPath/tftf`: `base/assets` (loose assets), `resources.bundle` (converted
player data), `moves92.bundle`, `odr/` (manifest and `wad/toc.zip`),
`Server/` (authored server files), `ready.txt` (installer version). **[👁 live]**

Measured on the A52: copy 1 s, convert 8 s, build the ODR package 34 s. **[👁 live]** The installer writes the
finished `toc.wad` straight to `asset_cache/odr/<SHA-1 of "/odr/wad/toc.zip/toc.wad">.wad`, the name the game's
`DownloadExtractor` would give its own unpacked copy (the manifest has `check_crc: false`), so the first play
needs no download and no unpacking. **[📄 binary]** (`DownloadExtractor.GetFilePath`, `Cache.GetCacheFilename`)
**[👁 live]** (file present and used, no `toc.zip` request in the log)

## Pitfalls found while building the shell

Symptom first.

- *Teal rectangle behind the logo, button labels missing.* `UITexture.mShader` pointed at a shader the
  converter had dropped, so the logo drew with the opaque default. Keep a named stub `Shader` object in the
  converted bundle so references survive; the game resolves the real shader by name. **[👁 live]**
- *Base screen shows "Loading" forever, `Read Timed out` on `/base/active`.* The server built that response by
  parsing and re-encoding a 98 KB file; the interpreter needs 2.9 s on a PC and 65 s on the phone, so the game
  retried until the single-threaded server was saturated. Serve the file as it is (0.13 s). **[👁 live]**
- *Loading toast forever on the base screen, `start-tutorial ShieldTutorial` repeats in the server log.* The
  server blocks that tutorial by default; set `TFTF_TUTORIAL_BLOCKLIST` to empty (the shell host does). **[👁 live]**
- *Title art missing, game asks for the ODR manifest and gets `connection refused`.* The in-app server was not
  listening yet; the host now waits for the port before booting. **[👁 live]**
- *`TypeLoadException ... Mono.Security.Interface.MonoTlsProvider` when starting the content server.*
  `HttpListener` needs a newer Mono.Security than the one in the game; use plain sockets. **[👁 live]**
- *Attacks of 9.2 fighters never land.* Their move files are not in the 3.1.0 `moves` bundle. Load them from
  the 9.2.0 `moves.assetbundle` and unload that bundle right after reading its text assets, otherwise the
  3.1.0 `moves` bundle refuses to load ("another AssetBundle with the same files is already loaded"). **[👁 live]**
- *Health bars drawn as plain transparency.* The material asks for the shader name `EBG/UI/HudHealth`; the
  authored replacement must be installed under that name. **[👁 live]**
- *`TypeLoadException ... Could not load file or assembly 'ICSharpCode.SharpZipLib'` after the libraries were
  copied to app storage and loaded with `Assembly.Load` / `LoadFrom` / an `AssemblyResolve` handler.* The
  runtime resolves an assembly's references when that assembly loads, before any managed code can run, and
  remembers a miss. Third-party libraries must be inside the APK; use the open-source builds. **[👁 live]**
- *Unity build fails with "Found plugins with same names".* Two Java plugin files may not share a file name;
  give package-private classes unique file names. **[👁 live]**
- *Black screen after the first-run install, `NoSuchMethodError: getInstance ... (Ljava/lang/Object;)` in the log.*
  `CallStatic<T>("name", null)` passes no argument in Unity 5 but a single null argument in Unity 2020, so a stand-in
  needs an overload taking one `Object`. Check every `CallStatic(..., null)` in the decompiled game when writing a
  Java stand-in. **[👁 live]**
- *`DllNotFoundException: android-signal` on every log line, game never finishes booting.* The crash reporter's
  native library was no longer shipped; the do-nothing stand-ins under `shell/android/native` fix it. **[👁 live]**
- *Native crash (`SIGSEGV`, `SEGV_ACCERR`) a few seconds after the boot scene, inside `CultureInfo.GetCultureInfo` /
  `stpcpy`; the fault address always ends in the same three hex digits.* A native stand-in returned a pointer to a
  constant string. Mono frees the strings that `DllImport`ed functions return, so the constant ended up on the
  allocator's free list and a later allocation was handed read-only memory. Return `strdup("")`. **[👁 live]**
  (after the change the same flow boots and the log shows no crash)
- *Quick native iteration.* `launcher/tools/replace_apk_libs.py IN.apk OUT.apk DIR` swaps the `.so` files into a
  built APK and re-signs it (one minute instead of a full build). **[👁 live]**
- *Only on the very first start after installing: `Fatal Filesystem Error! Missing: packs.txt`, title never appears;
  a second start works.* The game's `EB.FileSystem.Manager` (and its `FileDriver`) is built by a static constructor
  at application start, before the first-run screen has copied the base files. A search path added only when the
  folder already exists is missing on a first run: register it unconditionally and let each lookup check the file.
  Any other start-up code that captures "does this folder exist" has the same problem. **[👁 live]** (wipe-and-install run showed the error;
  after the fix the same run boots into the game)
