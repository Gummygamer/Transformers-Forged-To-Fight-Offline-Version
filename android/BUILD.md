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
        ZipWriter.kt           ZIP writer with Android APK alignment rules
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

# Generate hook libraries and the port-specific bundled payload for a debug build.
# Release builds run this step automatically through the prepareReleaseAssets task.
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

## PvP host app

`:pvphost` is a separate Android application (`com.gummygamer.tftfpvphost`) for
hosting Arena/PvP sessions and pairing players on a trusted LAN or tunnel. It
serves plain HTTP on all interfaces, stores sessions/rosters/matches in its
private app storage, and keeps serving while the foreground service is running.
The patcher and host install side by side.

Prepare the shared payload, then build and test the host:

```bash
./tools/prepare-assets.sh 8080
./gradlew :pvphost:assembleDebug :pvphost:testDebugUnitTest
./gradlew :pvphost:assembleRelease
```

The preparation script copies `tftf_payload.bin` into the host's ignored
`pvphost/src/main/assets/` directory. Do not add that generated file, native
`.bin` files, APKs, or signing keys to Git. Release uses R8; archive
`pvphost/build/outputs/mapping/release/mapping.txt` with any published release.
Without a valid machine-local `keystore.properties` configuration, the release
APK is intentionally unsigned and can still be built for local testing.

On the phone, open `TFTF PvP Host`, join Wi-Fi or a configured tunnel, choose a
port (8080 by default), and tap Start. Give each player the displayed address
and build their client with exactly:

```text
--scheme http --server-host <IP> --server-port 8080
```

The app also shows a complete `legible run Server/build_phone_apk.lbl` command
and the equivalent patcher-app Separate server fields. Allow TCP 8080 (or the
selected port) through the host firewall. The host has no endpoint
authentication, so anyone who can reach the port can register as a peer; use a
trusted network. The CDN rewrite switch should stay enabled when clients need
host-local CDN URLs.

Known limitations: HTTPS/8443 is not implemented, so clients must explicitly
use HTTP and port 8080 (or the chosen host port). Story board play is not served
by this app; use the bundled in-apk server or PC server for Story. The bundled
in-apk server remains loopback-only at `127.0.0.1:8080`; do not run it on the
same device and port as the host app. If the port is occupied, choose another
port in the host UI. The payload's internal port is informational here, so a
port mismatch is logged rather than preventing startup.

## Release signing

Distribution builds are signed with a long-lived, machine-local identity. Nothing
about it is committed.

| Item | Location | Mode |
|---|---|---|
| Keystore | `~/.android-keys/tftf-apk-patcher-release.pkcs12` | `600` (parent `700`) |
| Credentials | `android/keystore.properties` | `600`, gitignored |

`keystore.properties` keys: `storeFile`, `storeType`, `storePassword`, `keyAlias`,
`keyPassword`. `app/build.gradle` reads it defensively: if the file is absent the
release variant is simply left **unsigned**, so a fresh clone still configures and
builds without a key.

Signing config is v1 off, **v2 + v3 on**. v3 lets Android 9+ rotate the key later
without breaking upgrades for existing installs.

Create an identity (once, per maintainer):

```bash
keytool -genkeypair -keystore ~/.android-keys/tftf-apk-patcher-release.pkcs12 \
  -storetype PKCS12 -storepass "$PW" -keyalg RSA -keysize 2048 -validity 10950 \
  -alias tftfapkpatcher \
  -dname "CN=TFTF APK Patcher, OU=Offline Mod Tooling, O=Gummygamer, L=Internet, C=US"
```

> **Back the keystore up off-machine.** If it is lost, no future build installs as
> an update: every user must uninstall first, which also discards the on-device
> signing identity the patcher generated for their patched games.

Build and verify a distributable APK:

```bash
./gradlew :app:assembleRelease
$ANDROID_HOME/build-tools/35.0.0/apksigner verify --print-certs --verbose \
  app/build/outputs/apk/release/app-release.apk
$ANDROID_HOME/build-tools/35.0.0/zipalign -c -P 16 -v 4 \
  app/build/outputs/apk/release/app-release.apk
```

`:app:assembleRelease` always invokes `tools/prepare-assets.sh 8080` with forced asset
regeneration before packaging.
This is required because hooks and the bundled payload are generated inputs ignored by
Git; an automated release worker must never reuse files left by another checkout or
source revision. A release build therefore needs the Legible interpreter and Android
NDK even when those tools were not needed by a debug-only build. Archive the generated
`mapping.txt` with the exact APK published by the release job.

Release uses R8 (`minifyEnabled` + `shrinkResources`). **Archive
`app/build/outputs/mapping/release/mapping.txt` with every published build** or
crash traces cannot be deobfuscated. `proguard-rules.pro` keeps the manifest
components, the view-binding classes, and all of `org.bouncycastle.**`; the app
instantiates its own `BouncyCastleProvider`, so letting R8 strip or rename those
classes breaks on-device keystore generation and v2 signing of patched output.

`:app:connectedDebugAndroidTest` installs the **debug** variant, so it fails with
`INSTALL_FAILED_UPDATE_INCOMPATIBLE` whenever a release-signed build is already on
the device. `adb uninstall com.gummygamer.apkpatcher` first. The symptom is a
report claiming failing tests while the XML reads `tests="0"`; the real cause is
in `app/build/outputs/androidTest-results/connected/debug/*/test-result.textproto`.

## Locally generated assets

Run `./tools/prepare-assets.sh <port>` from `android/` before a bundled build.
It rebuilds stale native hooks from the current sources when an Android NDK is
available, then generates the current payload from the server sources. Native
binaries, game APKs, and private keys must not be committed:

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

The Android patcher performs the complete patch-and-install workflow on-device:

| Operation | Android behavior |
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

Legacy desktop steps replaced:
- **NDK hook rebuild** → prebuilt `.bin` assets in APK
- **zipalign** → in-process alignment during ZIP write (4-byte for `resources.arsc`,
  16 KiB for uncompressed native libraries, and preserved compression elsewhere)
- **apksigner** → APK Signature Scheme v2 in pure Kotlin/Java

After changing `tools/nativehook/hook.c`, `hook_arm32.c`, or
`inapk_server.c`, a release build regenerates both hooks automatically. For a
debug build or an asset-only check, run `tools/prepare-assets.sh`; it rebuilds
both hooks with the matching Android NDK before copying them into the patcher
assets. The hook logs its runtime page size under the `TFTFHOOK`
tag; capture that line with the game's first-start log when diagnosing a startup
exit. The native patcher computes the page range for `mprotect()` at runtime, so
the same asset can be tested on 4 KiB and 16 KiB-page devices.

## Engine API

```kotlin
suspend fun patch(
    request: PatchRequest,
    onStep: (StepProgress) -> Unit,
    onLog: (LogLine) -> Unit
): PatchOutcome
```

States are `idle → running → succeeded/failed/cancelled`.
Single-run lock enforced; cancellation checked between steps. Untouched ZIP
entries are copied in 64 KiB chunks; only patch targets, `resources.arsc`, and
native libraries are inflated, and signing uses private temporary files.

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
