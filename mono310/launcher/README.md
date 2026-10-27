# Assetless launcher

## Table of contents

- [What this is](#what-this-is)
- [For players](#for-players)
- [For builders](#for-builders)
- [Layout](#layout)
- [Limits](#limits)
- [Release](#release)

## What this is

A build of the recompiled 3.1.0 game (see [`../README.md`](../README.md)) that contains **no game
assets**. On first start it asks you for your own 3.1.0 and 9.2.0 APKs, converts what it needs on the
phone and then starts the game with its own offline server inside the app. Design, measurements and
pitfalls: [`../ANDROID_LAUNCHER.md`](../ANDROID_LAUNCHER.md). Third-party parts:
[`../THIRD_PARTY.md`](../THIRD_PARTY.md). **Status: works on one device.**

## For players

1. Have both game APKs on the phone (3.1.0 build 822754 and 9.2.0). The launcher cannot download them.
2. Install the launcher APK and open it.
3. Tap "Select the 3.1.0 APK" and choose that file, then "Select the 9.2.0 APK" and choose that file.
4. Tap "Install and play" and wait (under a minute). Nothing is uploaded anywhere. When it is done the app closes and
   starts itself again once, so that the game starts exactly as it will on every later launch.
5. The first time you press play, the game unpacks about 450 MB of content from inside the app. The
   title screen stays up for a few minutes. Later starts skip all of this.

Everything is stored in the app's private folder (`Android/data/unicronslair.bigrobot/files/tftf`); uninstalling the app
removes it. If the install fails, the reason is shown on screen and written to `install.log` in that folder.

## For builders

Same environment as [`../README.md`](../README.md), plus:

| Variable | What |
|---|---|
| `TFTF_SHELL=1` | build the shell instead of the full recompile |
| `TFTF_LEGIBLE_ARMV7` | release build of the Legible interpreter for `armv7-linux-androideabi` (`cargo build --release --no-default-features --target armv7-linux-androideabi`). It must support the `LEGIBLE_BIND` setting (the HTTP listener binds the address in that variable instead of all interfaces); the app sets it to `127.0.0.1`, so the game server is not reachable from other devices on the network. Older Legible builds ignore it and listen on every interface |
| `TFTF_ANDROID_ABI=arm64-v8a` | build the ARM64 shell release (IL2CPP, Android 5.0/API 21 minimum); requires Unity's IL2CPP Android module and the ARM64 Legible library below. Omit it for the ARMv7/Mono build |
| `TFTF_LEGIBLE_ARM64` | ARM64 `liblegible.so`, built from Legible for `aarch64-linux-android`, with `LEGIBLE_BIND` support; required when `TFTF_ANDROID_ABI=arm64-v8a` |
| `TFTF_TREES_BIN` | type-tree database for the on-phone converter. Make it once with the full project: `mono310/run.sh assemble && mono310/run.sh treedb` (needs `pip install UnityPy`, Docker and the Unity 2020 install); it prints the path of `trees.bin` |

```bash
TFTF_SHELL=1 mono310/run.sh assemble && mono310/run.sh assets && mono310/run.sh build && mono310/run.sh finalize
```

The result is `tftf-launcher-armv7.apk` by default, or `tftf-launcher-arm64.apk` when `TFTF_ANDROID_ABI=arm64-v8a`.
For script-only changes, `run.sh fast --install`
is much quicker (it patches the managed code into the last finalized APK). The converter and the ODR
package builder can also be run on a PC: `launcher/shell/ConvertTool/selftest.sh` compiles them with Unity's mcs, converts the
3.1.0 data, builds the content package from the 9.2.0 APK and runs the package self-test (about 25 s, needs the same
variables plus `UNITY_2020_ROOT`). `launcher/shell/ConvertTool/compile_check.sh` compiles all phone-side C# against the
Unity 2020 engine modules in a few seconds (run it before every player build). `launcher/tools/phone_firstrun_test.sh` runs the whole first-run flow on a
connected phone (wipe, install, pick the APKs, install, boot) and writes a report and screenshots; it needs the two
game APKs in the phone's Downloads folder and uses the phone while it runs. It pre-selects the APKs through a test hook of the
launcher (`tftf/autopick.txt` in the app's folder, two lines with readable APK paths) instead of driving the system file
picker. `launcher/tools/capture_boot.sh` saves a screenshot series of the boot sequence (Kabam logo, splash, title).

To build the ARM64 release, build Legible for `aarch64-linux-android`, then run the same steps with
`TFTF_ANDROID_ABI=arm64-v8a` and `TFTF_LEGIBLE_ARM64=/path/to/liblegible.so`. It uses IL2CPP and requires
a full player build when changing the ABI. For managed-code-only edits, `run.sh fast` compiles and swaps the
managed DLLs into the existing APK. Android starts the Legible server through `TftfProcess.java` because the
IL2CPP player's `System.Diagnostics.Process.Start` cannot launch it.

Online Arena fights between two players (relay server, lockstep netcode, the in-game panel) are described in
[`ONLINE_PVP.md`](ONLINE_PVP.md).

A Meta Quest VR variant (`TFTF_VR=1`: the game on a big screen in a VR room, fights in stereo) is described in
[`QUEST_VR.md`](QUEST_VR.md). It is built but not yet run on a headset.

## Layout

| path | content |
|---|---|
| `shell/Tftf/` | the code that runs on the phone: first-run screen, installer, converter, ODR builder, servers, online Arena connection |
| `shell/TftfMain/` | phone code that uses the game's types (compiled into Assembly-CSharp): the online Arena glue |
| `shell/android/` | authored manifest, Java and native stand-ins for the original app's plugin classes, and the launcher icon |
| `shell/android/icon/` | `revival-icon.svg` (the logo: a simplified, smiling orange Unicron head with sunglasses, newly drawn) and `make_icon.py`, which renders it with `cairosvg` (`pip install cairosvg`) into the committed `icon-512.png`; the build script sets it as the app icon through the player settings, so building the APK does not need the renderer |
| `shell/ConvertTool/` | PC harness for the converter and the ODR builder |
| `convert/` | Python converter prototype and the type-tree exporter |
| `spike/`, `tools/` | Editor checks and the API-gap analyzer used while designing this |

## Limits

- Version 0.5.2 is an ARM64 (`arm64-v8a`) release tested to launch on a Samsung Galaxy A52. The complete first-run
  flow has not yet been validated on a 64-bit-only phone.
- Purchases, push notifications, analytics and Play Games sign-in are stubs.
- The first-play content unpack is slow because the game extracts the package itself.

## Release

The checklist for a 0.2 release, including the compliance decision that blocks publishing, is in
[`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md); what changed is in [`CHANGELOG.md`](CHANGELOG.md).
