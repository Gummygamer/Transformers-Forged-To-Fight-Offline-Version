# Launcher 0.2 release checklist

## Table of contents

- [Blocking decision](#blocking-decision)
- [Build](#build)
- [Device test matrix](#device-test-matrix)
  - [Results so far](#results-so-far)
- [Known gaps accepted for 0.2](#known-gaps-accepted-for-02)

## Blocking decision

Do **not** publish the APK until the maintainer has decided that shipping the compiled recompiled game scripts is
acceptable; see [`../../COMPLIANCE.md`](../../COMPLIANCE.md) ("Assetless launcher"). Everything below can be prepared
without publishing.

## Build

- [ ] Clean work directory, then `TFTF_SHELL=1 mono310/run.sh assemble && run.sh assets && run.sh build && run.sh finalize`
      (see [`README.md`](README.md)); no step may fail and the log must show the third-party libraries coming from NuGet.
- [x] Sign with a release key: `TFTF_KEYSTORE=<your keystore>` and `TFTF_KEYSTORE_PASS=<its password>` (the build otherwise
      uses the Android debug key, which must never be used for a published file). Keep the key out of the repository.
      0.3.0 was signed with `~/.android/tftf-release.keystore` (alias `tftf-release`, RSA 4096, certificate SHA-256
      `79cbe365...f84e`); back that file and its password up: an APK signed with another key cannot update an installed one.
      A release-signed APK cannot be installed over a debug-signed one: uninstall the old one first.
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
| local-only server | with the game running, `adb shell cat /proc/net/tcp` shows the game server (port 8080, hex `1F90`) and the content server (8090, hex `1F9A`) listening on `0100007F` (127.0.0.1), not `00000000` |
| uninstall | removes the app's folder; nothing is left on shared storage |

Record device, Android version and result for each row.

### Results so far

Samsung A52 (SM-A525M), Android 14, armeabi-v7a, 2026-10-16; build with the app name "Transformers Forged to Fight
Revival". **[👁 live]**

| check | result |
|---|---|
| first-run screen | pass: logo, title and version line shown; buttons clean; progress bar moves |
| install time and space | pass: 13-43 s (the faster figure with a warm file cache); no space check failure seen |
| wrong file | not tested on a device (code and compile check only) |
| title and login | pass (earlier builds; the from-scratch run reaches the board directly after the restart) |
| story fight | pass on an earlier build: hits land, both health bars move; not repeated on the latest build |
| restart | pass: the app starts itself once after the install and boots into the game |
| uninstall | not tested |

Automated run (`tools/phone_firstrun_test.sh`, autopick): wipe, install, auto-select, install, restart, boot to the
story board: pass, no native crash, no Java errors.

## Known gaps accepted for 0.2

- 32-bit only ([`README.md#limits`](README.md#limits)).
- Purchases, push, analytics and Play Games are stubs.
- Visual issues shared with the non-launcher build: black terrain hexes, missing small portraits, one arena background.
- The first play after install unpacks nothing, but the very first boot of the game scripts takes about a minute.
