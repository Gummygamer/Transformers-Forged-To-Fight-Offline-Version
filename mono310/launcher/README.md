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
[`../THIRD_PARTY.md`](../THIRD_PARTY.md). **Status: works on one device; not releasable until the open
question in `../../COMPLIANCE.md` ("Assetless launcher") is settled.**

## For players

1. Have both game APKs on the phone (3.1.0 build 822754 and 9.2.0). The launcher cannot download them.
2. Install the launcher APK and open it.
3. Tap "Select the 3.1.0 APK" and choose that file, then "Select the 9.2.0 APK" and choose that file.
4. Tap "Install and play" and wait (about one minute). Nothing is uploaded anywhere.
5. The first time you press play, the game unpacks about 450 MB of content from inside the app. The
   title screen stays up for a few minutes. Later starts skip all of this.

Everything is stored in the app's private folder (`Android/data/com.kabam.bigrobot/files/tftf`); uninstalling the app
removes it. If the install fails, the reason is shown on screen and written to `install.log` in that folder.

## For builders

Same environment as [`../README.md`](../README.md), plus:

| Variable | What |
|---|---|
| `TFTF_SHELL=1` | build the shell instead of the full recompile |
| `TFTF_LEGIBLE_ARMV7` | release build of the Legible interpreter for `armv7-linux-androideabi` (`cargo build --release --no-default-features --target armv7-linux-androideabi`) |
| `TFTF_TREES_BIN` | type-tree database, written by `convert/export_tree_db.py` from a local Editor build |

```bash
TFTF_SHELL=1 mono310/run.sh assemble && mono310/run.sh assets && mono310/run.sh build && mono310/run.sh finalize
```

The result is `tftf-launcher-armv7.apk` in `TFTF_WORK`. For script-only changes, `run.sh fast --install`
is much quicker (it patches the managed code into the last finalized APK). The converter and the ODR
package builder can also be run on a PC: `launcher/shell/ConvertTool/selftest.sh` compiles them with Unity's mcs, converts the
3.1.0 data, builds the content package from the 9.2.0 APK and runs the package self-test (about 25 s, needs the same
variables plus `UNITY_2020_ROOT`). `launcher/shell/ConvertTool/compile_check.sh` compiles all phone-side C# against the
Unity 2020 engine modules in a few seconds (run it before every player build).

## Layout

| path | content |
|---|---|
| `shell/Tftf/` | the code that runs on the phone: first-run screen, installer, converter, ODR builder, servers |
| `shell/android/` | authored manifest and Java stand-ins for the original app's plugin classes |
| `shell/ConvertTool/` | PC harness for the converter and the ODR builder |
| `convert/` | Python converter prototype and the type-tree exporter |
| `spike/`, `tools/` | Editor checks and the API-gap analyzer used while designing this |

## Limits

- `armeabi-v7a` (32-bit) only, one test device. Phones that no longer run 32-bit apps cannot use this build. A 64-bit
  build would need Unity's IL2CPP backend (as far as the Unity 2020 Android documentation says, the Mono backend
  has no ARM64) plus 64-bit builds of the Legible interpreter and the native stand-ins **[⚠ inferred]**, which is a
  separate port of the whole recompile.
- Purchases, push notifications, analytics and Play Games sign-in are stubs.
- The first-play content unpack is slow because the game extracts the package itself.

## Release

The checklist for a 0.1 release, including the compliance decision that blocks publishing, is in
[`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md); what changed is in [`CHANGELOG.md`](CHANGELOG.md).
