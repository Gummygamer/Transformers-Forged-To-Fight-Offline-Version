# Launcher 0.1 release checklist

## Table of contents

- [Blocking decision](#blocking-decision)
- [Build](#build)
- [Device test matrix](#device-test-matrix)
- [Known gaps accepted for 0.1](#known-gaps-accepted-for-01)

## Blocking decision

Do **not** publish the APK until the maintainer has decided that shipping the compiled recompiled game scripts is
acceptable; see [`../../COMPLIANCE.md`](../../COMPLIANCE.md) ("Assetless launcher"). Everything below can be prepared
without publishing.

## Build

- [ ] Clean work directory, then `TFTF_SHELL=1 mono310/run.sh assemble && run.sh assets && run.sh build && run.sh finalize`
      (see [`README.md`](README.md)); no step may fail and the log must show the third-party libraries coming from NuGet.
- [ ] Sign with a release key: `TFTF_KEYSTORE=<your keystore>` (the build otherwise uses the Android debug key, which
      must never be used for a published file). Keep the key out of the repository.
- [ ] `unzip -l` the APK: no `assets/` folder of the original game, no `classes2.dex`, only `classes.dex`, the Unity
      player libraries, `liblegible.so` and the three stand-in libraries.
- [ ] Record the SHA-256 of the APK next to the commit it was built from.
- [ ] `mono310/launcher/shell/ConvertTool/selftest.sh` and the server tests (`legible run Server/test_fakeserver.lbl`)
      pass.

## Device test matrix

Each row: fresh install, wipe the app's storage, pick both APKs, install, play. `tools/phone_firstrun_test.sh` does
the first rows automatically and leaves the report and screenshots to attach to the record.

| check | pass when |
|---|---|
| first-run screen | both pick buttons show the chosen file names; install progress moves; no overlapping text |
| install time and space | finishes without error; about 1.2 GB free is enough |
| wrong file | picking the wrong APK shows a clear message and the buttons come back |
| title and login | title art and labels correct; PLAY NOW logs in; base screen loads |
| story fight | START opens a fight; hits land; both health bars move |
| restart | closing and reopening skips the first-run screen and reaches the title in about a minute |
| uninstall | removes the app's folder; nothing is left on shared storage |

Record device, Android version and result for each row. So far: Samsung A52, Android 14, armeabi-v7a, partial.

## Known gaps accepted for 0.1

- 32-bit only ([`README.md#limits`](README.md#limits)).
- Purchases, push, analytics and Play Games are stubs.
- Visual issues shared with the non-launcher build: black terrain hexes, missing small portraits, one arena background.
- The first play after install unpacks nothing, but the very first boot of the game scripts takes about a minute.
