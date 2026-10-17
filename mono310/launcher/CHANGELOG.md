# Launcher changelog

## Table of contents

- [0.2.0 (unreleased)](#020-unreleased)
- [0.1.0 (internal, never released)](#010-internal-never-released)

## 0.2.0 (unreleased)

**Added**

- In-game splash after the Kabam screen: the logo, large, with the words "Unicron's Lair" (a tap skips it after a
  moment).
- The in-app game server listens on 127.0.0.1 only (`LEGIBLE_BIND`), so other devices on the network cannot reach it.
- Launcher version 0.2.0 shown on the first-run screen.

**Changed**

- Everything of 0.1.0 below is part of this build; 0.1.0 was an internal milestone and was never released.

## 0.1.0 (internal, never released)

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
- Branding: the app is named "Transformers Forged to Fight Revival", the first-run screen says "revival launcher"
  and shows the logo (a simplified, smiling orange Unicron head with sunglasses, drawn as SVG), which is also the app
  icon.
- The app restarts once after a successful install so the first start is identical to every later start.
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
