# TFTF APK Patcher — Android Build Guide

## Project layout

```
android/
  settings.gradle          Gradle project settings
  build.gradle             Root build (plugin versions)
  gradle.properties        JVM/Gradle properties
  gradlew                  Wrapper launch script
  gradle/wrapper/
    gradle-wrapper.properties  (downloads Gradle 8.11.1)
  app/
    build.gradle           App module (minSdk 26, targetSdk 35, JDK 17)
    proguard-rules.pro
    src/main/
      AndroidManifest.xml
      java/com/gummygamer/apkpatcher/
        PatchRequest.kt        Data model + validation
        ZipReader.kt           Streaming ZIP parser (EOCD → central dir → local headers)
        ZipWriter.kt           ZIP writer with .so alignment (4-byte boundaries)
        MetadataPatch.kt       global-metadata.dat literal-table patcher
        SparxPatch.kt          res/raw/sparxmanifest endpoint replacer
        EndpointConfigPatch.kt assets/bin/Data/e1917… patch (fixed-size)
        Il2cppPatch.kt         16-site byte patches + DT_NEEDED injection (arm64/armv7)
        ApksigSigner.kt        APK Signature Scheme v2 (file-backed)
        KeystoreManager.kt     Portable PKCS12 generation + PKCS12/JKS import
        PatcherEngine.kt       Pipeline orchestrator (7 steps: read→validate→hook→il2cpp→build→sign→write)
        MainActivity.kt        SAF-based patcher form and progress UI
      res/values/
        themes.xml / colors.xml
      assets/                  (generated locally; see below)
    src/test/java/com/gummygamer/apkpatcher/
      PatcherEngineTest.kt    JVM unit tests for core logic
```

## Build prerequisites

- **JDK 17+** — JAVA_HOME must point to a JDK 17 installation
- **Android SDK** — ANDROID_HOME with:
  - `platforms/android-35/` (install via `sdkmanager "platforms;android-35"`)
  - `build-tools/35.0.0/`
- **Gradle 8.11+** — the wrapper downloads it automatically; set GRADLE_HOME
  for an existing installation
- **Network** — first build downloads Gradle distribution + dependencies

## Quick build

```bash
cd android

# Set environment
export JAVA_HOME=$HOME/jdk17
export ANDROID_HOME=$HOME/Android/Sdk
export PATH=$JAVA_HOME/bin:$PATH

# Generate hook libraries and the port-specific bundled payload.
./tools/prepare-assets.sh 8080

# Build debug APK
./gradlew :app:assembleDebug

# Run unit tests
./gradlew :app:testDebugUnitTest

# Run provider checks on a connected Android device
./gradlew :app:connectedDebugAndroidTest
```

Output at `app/build/outputs/apk/debug/app-debug.apk`.

The device test exercises RSA certificate generation, X.509 parsing, PKCS12
serialization, and keystore reload using Android's actual security providers.

## Locally generated assets

Run `./tools/prepare-assets.sh <port>` from `android/` before a bundled build.
It creates ignored assets from repository sources; native binaries, game APKs,
and private keys must not be committed:

| File | Source | Notes |
|------|--------|-------|
| `libdothook-arm64.bin` | `tools/nativehook/libdothook.so` | arm64 hook; copy and rename |
| `libdothook-armv7.bin` | `tools/nativehook/libdothook-armeabi-v7a.so` | armv7 hook; copy and rename |
| `tftf_payload.bin` | `Server/export_payload.lbl` output | Offline payload blob (~5 MB), bound to the selected port |
| `patched_libil2cpp-arm64.so` | `patches/patch_il2cpp.lbl` output | Optional; app auto-patches without it |
| `patched_libil2cpp-armv7.so` | `patches/patch_il2cpp.lbl` output | Optional; armv7 needs --needed inplace |

If assets are missing or generated for another port, the patcher stops before
writing an APK with an actionable error. Generated `.bin` files are git-ignored and
must be regenerated locally.

## Supported operations

All web GUI patcher operations run fully on-device:

| Web GUI option | Android equivalent |
|---|---|
| Source APK picker | SAF OpenDocument `.apk` |
| ABI: arm64-v8a / armeabi-v7a | Radio group, same coercion |
| Keep other ABI | Checkbox |
| Server mode: bundled / separate | Radio |
| Bundled → 127.0.0.1:8080 http | Fixed (enforced) |
| Separate → host/port/scheme | Text fields |
| Patched libil2cpp | SAF pick or auto-patch |
| Auto-patch il2cpp | On by default; engine patches 16 sites + DT_NEEDED |
| Keystore + passwords | Generated PKCS12 or SAF-imported PKCS12/JKS |
| Command preview | Step list preview |
| Install (adb) | PackageInstaller session handoff |

The patch result is first written atomically into persistent app-private storage at
`files/patched_apks/` (rather than the reclaimable cache). The result card identifies the
saved artifact and provides **Save / Share** and **Install patched APK** actions. Save copies
the complete signed APK to the SAF destination selected by the user, so choose Downloads (or
another visible folder) when you need to find it outside the patcher. A provider that cannot
open the destination is reported as an export failure.

Install checks Android's per-app "install unknown apps" permission and opens its settings page
when needed. Returning to the patcher resumes the same APK. PackageInstaller confirmation is
shown when Android requires user approval; the receiver handles pending confirmation and final
success, cancellation, signature conflict, policy, storage, and invalid-APK statuses and sends
the result back to the activity. The same saved artifact can be installed again without
rebuilding.

Desktop-only steps replaced:
- **NDK hook rebuild** → prebuilt `.bin` assets in APK
- **zipalign** → in-process alignment during ZIP write (4-byte for .so entries)
- **apksigner** → APK Signature Scheme v2 in pure Kotlin/Java

## Engine API

```kotlin
suspend fun patch(
    request: PatchRequest,
    onStep: (StepProgress) -> Unit,
    onLog: (LogLine) -> Unit
): PatchOutcome
```

States mirror the web runner: `idle → running → succeeded/failed/cancelled`.
Single-run lock enforced; cancellation checked between steps. Untouched ZIP
entries are copied in 64 KiB chunks; only patch targets and native libraries are
inflated, and signing uses private temporary files.

## Known limitations

1. **Bundled port**: The payload is port-specific; rerun `prepare-assets.sh`
   when changing the bundled server port.
2. **Only Transformers 9.2.0** is tested; other versions may have different
   il2cpp offsets and will fail validation.
3. **No v1 (JAR) signature**: Only APK Signature Scheme v2 is applied.
   Android 7.0+ (API 24+) supports v2; the app's minSdk is 26.
4. **Signing identity**: The default RSA/PKCS12 identity is generated once in
   app-private no-backup storage and reused. An older `patcher-signing.jks` is
   imported and migrated when present so upgrades retain the same certificate.
   It is not the game's original key, so replacing an original installation may
   require uninstalling it first.
5. **Build environment**: Requires a writable Android SDK directory for Gradle
   build cache and platform installation.
