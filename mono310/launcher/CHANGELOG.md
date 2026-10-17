# Launcher changelog

## Table of contents

- [0.4.3](#043)
- [0.4.2 (development snapshot)](#042-development-snapshot)
- [0.4.1](#041)
- [0.4.0](#040)
- [0.3.0](#030)
- [0.2.0](#020)
- [0.1.0 (internal, never released)](#010-internal-never-released)

## 0.4.3

**Added**

- ARM64 (`arm64-v8a`) launcher release built with Unity IL2CPP and matching ARM64 native libraries. Verified to start on a
  Samsung Galaxy A52.

**Fixed**

- ARM64 startup left the game on a black screen because `System.Diagnostics.Process.Start` could not start the Legible
  server in the IL2CPP player. Android now starts it with `ProcessBuilder`.

## 0.4.2 (development snapshot)

**Added**

- Initial ARM64 launcher build path using IL2CPP, with matching ARM64 Legible and native stand-in libraries.

**Fixed**

- The black cards with a white glow that floated on the robots (hips, feet, "swords") were the contact-shadow quads: their opaque
  white-on-black mask texture was drawn as a card. They are switched off; the sun light casts real shadows instead.

## 0.4.1

**Fixed**

- Fights in arenas whose donor shaders did not load (Karnak and others) drew magenta, including the enemy robot: materials with an
  empty or unsupported shader now get the stand-in shaders (lights, sky and flares are left to the game).
- The base, story map and menus use a brighter exposure than live fights (the battle state is detected through `BattleArbiter`).

## 0.4.0

**Fixed**

- The game slowed to a crawl when something in the base was selected: the 0.3.0 text-shader fix read `mainTexture` on every
  material every 0.2 s, and each material without a `_MainTex` logged an error (about 950 per second). Now guarded.
- Base buildings and terrain were too dark under the new stand-in shader; the stand-in shader now lights with the scene's own
  sun direction, a wrapped key light, a soft tone curve and a darker underside (more contrast in the base and in fights).
- Selecting a base node left the screen stuck: the edit popup threw in `DefenderSlot.Refresh` (defenders are not authored) and never
  finished opening; the refresh is guarded (patch P48) and the popup opens with its controls.
- Profile picture, BOTS portraits, dialogue and item art stayed unloaded (gears): the content package did not contain the loose
  image files of the donor's portrait/dialogue/item packs; they are now stored in the package (about 64 MB) and listed as raw
  entries in its manifest.
- After the first-run install the app did not come back (it ended on the home screen): the restart now relaunches through an alarm.
- Transparent donor materials (glow cards) are drawn additively by a separate stand-in shader (TFTF/AddFx) instead of opaque.
- Robot and scene shading rebuilt: linear-space PBR-style lighting (sun specular, sky reflection, rim and back light, ACES tone mapping),
  a real shadow-casting sun light, camera-relative key light, full texture quality (the game ran at Unity's Fastest level), and the
  terrain shader's world-projected textures with its colour-layer blend - the story-map ground is textured sand instead of flat grey.
- The file picker keeps the choice on the Java side until the launcher fetches it, takes a persistent permission, and copies files
  from providers whose descriptors cannot be read in place.
- Separate exposure for fights (0.45 / sun 1.5, while a battle is running) and for the base, story map and menus (1.35 / sun 3.0);
  `tftf/tune.txt` ("exposure sun fightExposure fightSun") overrides them on the phone without a rebuild.
- Dev: `tftf/hide.txt` switches renderers off by name part; `tftf/dbg.txt` (1-6) selects shader debug views, `probe_scene.txt` dumps lights, cameras and shaders of the current screen.

## 0.3.0

**Fixed**

- Loading screen showed coloured vertical streaks instead of the planet artwork: `UIStretch` ran before the widget existed
  and scaled the background 770 times too tall (patch P47).
- The loading screen's Autobot/Decepticon logo drew black: its font material sat on a colour shader, which draws an
  alpha-only font texture black; it now gets the text shader.
- In-game splash no longer has the purple tile background (transparent logo).
- Optimus Primal and the other bots started with about 14000 health at 5 stars; the health table is scaled down by about
  half (a 5-star brawler is about 7500), the final boss to 10500.

**Changed**

- The app's package name is now `unicronslair.bigrobot` (it installs next to, and no longer replaces, the original game).
- Robots use a richer stand-in shader (normal, metallic, roughness, ambient occlusion and emissive maps).
- The fast build loop also refreshes the bundled splash/icon images.

## 0.2.0

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
