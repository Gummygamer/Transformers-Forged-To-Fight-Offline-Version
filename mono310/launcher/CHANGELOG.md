# Launcher changelog

## Table of contents

- [0.1.0 (unreleased)](#010-unreleased)

## 0.1.0 (unreleased)

First version of the assetless launcher: the recompiled 3.1.0 game with no game assets, which prepares them from the
player's own 3.1.0 and 9.2.0 APKs on the phone. Not released: see [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md).

**Added**

- First-run screen with a file picker for the two APKs, immediate checks of what was picked, a free-space check,
  install progress and an install log.
- On-phone converter for the 3.1.0 player data and builder for the 9.2.0 content package; the finished package is
  placed in the game's cache so the first play needs no unpacking.
- Offline game server inside the app (Legible interpreter and the authored server scripts).
- Authored manifest, Java stand-ins and native stand-ins; nothing of the original app is copied into the APK.
  SharpZipLib and BouncyCastle come from NuGet.
- Fighters from the 9.2.0 packs land hits (their move files come from the 9.2.0 APK).

**Fixed compared with the first launcher builds**

- Title screen: logo no longer drawn opaque, button labels present, side bars black.
- Base screen: `/base/active` answers in 0.13 s instead of 65 s on a phone.
- Health bars show remaining health and recent damage.
- First start after installing no longer fails on a missing base file.

**Known limits**

- 32-bit (`armeabi-v7a`) only; tested on one device.
- Purchases, push notifications, analytics and Play Games sign-in are stubs.
- Black terrain hexes on the quest board, missing small portraits and one arena background, as in the
  non-launcher build.
