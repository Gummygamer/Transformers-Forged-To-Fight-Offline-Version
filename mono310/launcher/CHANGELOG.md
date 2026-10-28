# Launcher changelog

## Table of contents

- [Unreleased](#unreleased)
- [0.10](#010)
- [0.9](#09)
- [0.8](#08)
- [0.7.3](#073)
- [0.7.2](#072)
- [0.7.0](#070)
- [0.6.1](#061)
- [0.6.0](#060)
- [0.5.2](#052)
- [0.5.0](#050)
- [0.4.3](#043)
- [0.4.2 (development snapshot)](#042-development-snapshot)
- [0.4.1](#041)
- [0.4.0](#040)
- [0.3.0](#030)
- [0.2.0](#020)
- [0.1.0 (internal, never released)](#010-internal-never-released)

## Unreleased

**Added**

- Quest VR build: first-person fights with motion controls and hand tracking. The view sits in your fighter's head,
  facing the opponent; the fighter's body stays in view and its arms follow your controllers or tracked hands (two-bone
  IK). Punch for a light attack, punch with both hands or step in to dash, raise both hands and bring them down for a
  heavy, hold a guard in front of your face to block, lean back to dodge, lean sideways to sidestep, hold both fists
  together (or B) for a special. Controllers buzz on hits. X now cycles first-person, arena and flat. Hand tracking
  reads OVRPlugin directly; the manifest declares it as optional. Not yet tested on a headset.

**Fixed**

- Quest VR first-person: the fighter's own collar and shoulders no longer block the view. The eye sat at the head bone,
  inside the neck; it is now at the front of the face, and geometry within 15 cm of the eye is clipped (hands, chest
  and feet stay visible). With controllers, the fighter's wrists now turn with them and its fists, not its wrists, sit
  where you hold the controllers. All tunable in `tftf/vrcombat.txt` (`eyefwd`, `eyeup`, `near`, `wrist`,
  `wristpitch`).
- Quest VR: Y no longer turns motion controls off. Pressed in the menus it switched gestures and the first-person arms
  off, unseen, for every fight after.

## 0.10

**Added**

- Quest VR build: the home base is shown in full VR (base mode), like fights: the base camera's view in stereo from a
  seat on its line of sight, horizon level, at a tenth of its size so it reads as a model town with depth, the HUD on the
  transparent screen in front. Pointing the laser at the base (off the screen) taps and drags that spot. X switches
  both base and arena mode back to the theater screen. Not yet tested on a headset.
- Quest VR build: the online PVP button and panel are on the Arena screen too, and say "headset" instead of "phone"
  ("HOST ON THIS HEADSET").

## 0.9

**Added**

- Player levels: every finished fight earns experience (120 for a win, 40 for a loss, story and Arena), and the level
  shown in the top bar follows it (level 2 at 100 XP, 3 at 300, 4 at 600, up to 60). Saved by the in-app server.
- The Arena (online PVP) is unlocked from level 0.

**Changed**

- Bots, the board and fights keep the 9.2.0 donor PBR shaders (metallic reflections, roughness, normal maps) instead of
  the flat stand-in. Scenes keep their own exposure and tone map, and the launcher's bloom is off by default
  (`tftf/tune.txt` with `bloom 1.1` turns it back on), so the story squad screen and fights are no longer washed out.
- The online PVP button and panel on the Arena screen use the game's own buttons and fonts; the button moved to the
  bottom right, so it no longer covers the arena's ENTER and VIEW DETAILS buttons.

**Fixed**

- BOTS roster portraits show again (a loading-screen `UIStretch` patch from the same change was dropped).
- The hip contact shadow (a dark card between the legs) is hidden without errors every frame.

- Tutorial and mode locks: `ShieldTutorial` is marked completed in the login responses, and `fightLanding_story`,
  `story` and `raids` unlock at player level 0 alongside daily and special missions.
- The server tag in the top-left corner is hidden by the client, so a stale or older server copy on the phone can no
  longer bring back the red "prod" label.

## 0.8

**Added**

- Online Arena fights against another player: an "Online PVP" button on the Arena screen chooses a relay server
  (`Server/pvp_relay.py`) and room code and finds an opponent; fights run in input lockstep with the host deciding
  health and knock-outs. Not yet tested between two phones. See [`ONLINE_PVP.md`](ONLINE_PVP.md).
- "Host on this phone" in the online panel runs the relay inside the app, so other phones can connect to this one
  without a computer.

**Changed**

- Version bump; includes the latest server and SP3 cutscene fixes.

## 0.7.3

**Changed**

- Quest enemies spawn at level 36 (final bosses at 61) instead of 1, stats grow 2.5% per level, and enemy AI profiles are
  harder.

**Fixed**

- The red "prod" server tag no longer shows in the top-left corner of the screen.
- Android shell (`libandroid-signal.so`): fix SP3 character model transformation in cutscenes (e.g. Megatron tank mode,
  weapon parts, and redeco models). Native Matinee tracks expect PascalCase entity paths (e.g.
  `Megatron_GS_Leader2015|Actor0_locator`), but spawned bot root GameObjects use lowercase names
  (`megatron_gs_leader2015`), causing Unity's case-sensitive `Transform.Find` to fail during
  `MatineeActorTrack.FixupEntity`. `libandroid-signal.so` now dynamically intercepts `FixupEntity`, captures the actor
  GameObject from the bound locator hierarchy, and synchronizes the actor name (`MATINEE_ALIAS`).

## 0.7.2

**Fixed**

- Quest VR: magenta upside-down silhouettes over fights in arenas with floor reflections (Karnak). The VR build pointed the
  game's planar reflection camera, which draws into its own buffers, at the VR screen; disabled cameras and the game's light
  cameras are now left alone.

## 0.7.0

**Added**

- Meta Quest VR build (`TFTF_VR=1`, [`QUEST_VR.md`](QUEST_VR.md)): the game on a large screen in a VR room with a
  controller laser as the finger, and fights drawn in stereo with the HUD in front. Built, not yet tested on a headset.

**Fixed**

- Fight arena rendering: baked arena lightmap and fight colour grade, bloom no longer blows the sky out to white,
  magenta FX materials fixed.
- Text shaders: authored text shaders are preferred over the stub, `ShaderFallback` no longer catches "Texture"
  shaders, and the Alpha8 glyph trick is kept out of the image shaders.

## 0.6.1

**Fixed**

- Special Mode opens from level 0 (the `fightLanding_special` and `fightLanding_daily` level locks are tuned to 0), and the
  Karma Six special mission is unlocked from the start.
- Starscream's Device and Motormaster's Reckoning are unlocked in the story.
- The base board no longer hangs on loading when a building model is missing, and the commander portrait shows in the top bar.
- NGUI dynamic-font and soft-clipped text render correctly.

## 0.6.0

First Windows release: the same assetless launcher as a Windows 10/11 x64 program (Mono, Unity 2020.3.31f1), built natively
with `mono310/winbuild.ps1`. Unzip `TFTF-Revival`, run `TFTF.exe`, pick your own 3.1.0 and 9.2.0 APKs; the game server
(`legible.exe`) runs next to it. The Android builds are unchanged apart from the shared fixes below.

**Added (Windows)**

- The installer restamps every bundle it copies from the APKs for the PC player and transcodes their ETC1 textures to
  DXT1 in place (same block size); the PC player refuses Android bundles and cannot sample ETC1.
- Android-only code (JNI glue, Play Games, signal and APK-protection libraries) is stubbed after the build
  (`launcher/tools/jnistub.cs`); permission requests answer "granted".
- Mouse input on the story map, base and in fights: the left button is one finger (click to tap or attack, drag to pan
  or swipe, hold to block).

**Fixed**

- Bundle paths inside the content package and its cache file name use the separators the game expects on Windows.
- The Windows server is built from legible-lang's development branch: older interpreters evaluate both sides of
  `and`/`or` and crashed on every login.

**Known gaps (Windows)**

- No mouse-wheel zoom on the map (pinch only); two-finger fight inputs need a touch screen.
- Three ETC2 textures are not transcoded.
- The game server listens on all network interfaces (this interpreter build has no `LEGIBLE_BIND`); the game itself
  only connects to 127.0.0.1.

## 0.5.2

The 0.5.0 code again, with two fixes. An unreleased 0.5.1 had changed the bundled server profile and the content package, which altered the story data, the starting squad and the base data; those changes are not part of this release.

**Fixed**

- The release code builds against the current ripped game sources (a callback signature in the asset-bundle loading patch).
- Robust fallback loading of `trees.bin` directly from the APK zip or app storage when the streaming-assets request fails on Android, so "Install and play" does not stay disabled.
- Guard the boot-screen capture against runtime capture exceptions, which could hang the transition while the home screen loads.
- Keep launcher warning messages when selecting files and add the specific I/O error to APK validation failures.

## 0.5.0

**Fixed**

- The Karnak arena's sky was magenta: its donor sky shader does not compile on the phone's GPU. The sky dome now uses its own texture
  on an unlit background shader (`TFTF/SkyLite`); when the broken material exposes no texture it is found by name.
- Fight lighting is closer to the original: distance haze and ambient/reflection colour taken from each arena's sky texture, a warm
  sun, stronger shadows (higher shadow resolution), slight saturation and contrast; the base and menus keep the calmer look.
- Robust fallback loading of `trees.bin` directly from APK zip or app storage when streamingAssets `UnityWebRequest` fails on Android, preventing the "Install and play" button from remaining permanently disabled.
- Guard `UIScreenBackground` screen capture against runtime capture exceptions, preventing transition hangs during home-screen loading.
- Retain launcher warning messages when selecting files and append specific I/O exception messages to APK validation failures for clear diagnostics.

**Added**

- Live lighting tuning on the phone through `tftf/tune.txt` ("name value" lines: exposure, sun, ambient, rim, sat, contrast, fog;
  prefix `f` for fights, e.g. `fexposure 0.5`).
- Story-map landmass scenery is placed at the outer edge of the padded board so it does not cover story nodes.
- Quest fights report wins and losses to the client; a final-boss win completes the quest and opens its result flow.
- `tftf/fullmana.txt` enables a development switch that keeps the player's special meter full.

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
