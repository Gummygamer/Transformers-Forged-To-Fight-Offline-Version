# Meta Quest VR build of the launcher

## Table of contents

- [Status](#status)
- [What it is](#what-it-is)
- [Controls](#controls)
- [Building](#building)
- [How it works](#how-it-works)
- [Open risks](#open-risks)

## Status

**Built, not yet run on a headset.** The `TFTF_VR=1` build compiles, links and signs; its manifest carries the Quest
entries (`com.oculus.intent.category.VR`, `android.hardware.vr.headtracking`, `com.oculus.supportedDevices=quest2`,
`focusaware`, API 29) and it packs `libOVRPlugin.so` and `libOculusXRPlugin.so` **[📄 binary]** (`aapt2 dump` of
`tftf-launcher-quest.apk`, 2026-10-22). Nothing below has been seen working in a headset; every runtime statement is
**[⚠ inferred]** until it is.

## What it is

A third shell variant beside the phone (ARMv7 / ARM64) and Windows builds: the same assetless launcher and game, with
the Oculus XR plugin (1.11.2, bundled with Unity 2020.3.31f1, so no download is needed).

- **Everywhere outside fights** the game renders as on a phone, into a 1920x1080 texture shown on a 3 m screen
  2.5 m in front of you, in a dark room with a floor grid. The headset sees the room in stereo.
- **In fights (arena mode)** the arena itself is drawn in stereo from a seat on the fight camera's line of sight. You can
  lean and look around; the HUD floats in front of the arena on the transparent screen.

## Controls

| Input | Action |
|---|---|
| Trigger (either hand; the last one pulled is the pointer) | the finger: tap, drag and hold on the screen, laser and cursor shown |
| A | light attack (taps the HUD attack zone) |
| Right stick flicked right / left | swipe forward / back across the attack zone (medium attack / dash back) |
| Either grip, held | block (holds the HUD block zone) |
| X | arena mode off / on (off: fights stay on the flat screen) |
| Left stick up / down | arena seat nearer / farther |
| Either stick click | put the screen back in front of you (holding the Oculus button recentres too) |

Specials and every other HUD button are pressed with the laser.

## Building

Same environment as the ARM64 release ([`README.md`](README.md)), plus `TFTF_VR=1`:

```bash
TFTF_SHELL=1 TFTF_VR=1 TFTF_ANDROID_ABI=arm64-v8a mono310/run.sh assemble
TFTF_SHELL=1 TFTF_VR=1 TFTF_ANDROID_ABI=arm64-v8a mono310/run.sh assets   # also build, finalize
```

It uses its own project (`$TFTF_WORK/ShellProjectVR`) and writes `$TFTF_WORK/tftf-launcher-quest.apk`, so it never
touches the phone build. Copying `ShellProject/Library` into the new project first saves the full reimport. Install with
`adb install --no-incremental` on a Quest in developer mode; it appears in the library under Unknown Sources. The package
name is the phone launcher's (`unicronslair.bigrobot`), so the two cannot be installed side by side.

## How it works

| Piece | Where |
|---|---|
| VR packages, `-define:TFTF_VR` (`Assets/csc.rsp`), editor setup copied in | `mono310/scripts/assemble_project.py` |
| XR management + Oculus loader, multi-pass stereo (the game's shaders have no single-pass support), Quest 2, API 29, GLES3 | `mono310/unity/TftfVrBuildSetup.cs`, called from `Recompile310Build.BuildAndroid` |
| Patch P53: the game's `Screen.width/height` → `TftfScreen`, touches → `TftfInput`, `EBImage` backbuffer blits → the VR screen, fight camera and HUD zones reported to `TftfFightState` | `mono310/scripts/compat310.py` (VR only; the phone build is unchanged) |
| Rig, room, laser, retargeting every game camera into the screen texture, arena mode | `shell/Tftf/TftfVr.cs` |
| Launcher and splash screens (IMGUI, which the headset never shows) drawn on a world-space canvas instead | `shell/Tftf/TftfVrPanel.cs` |
| Room shaders | `mono310/unity/shaders/vr/` |

The deferred scene camera writes its own buffers and composites to the backbuffer itself (`EBPostFXManager.PostRender`
→ `EBImage.Blit`/`BlitToNative`, **[📄 binary]** `EB/Rendering/EBPostFXManager.cs:193`), so P53 points that composite at
the screen texture rather than giving the camera a target. In arena mode the fight camera's culling mask moves to the
headset's arena camera and its composite goes to a scratch texture, so the screen keeps only the HUD.

## Open risks

All **[⚠ inferred]**, in the order to check them on a headset:

1. Head tracking: the rig poses both headset cameras from `XRNode.Head` with automatic camera tracking turned off.
2. The HUD's transparency in arena mode depends on no UI camera clearing the screen texture opaque. If the HUD shows
   a black backdrop, the arena is hidden behind it; X turns arena mode off.
3. Arena look: the arena camera is a plain forward camera, so the game's post-FX grade and anything its shaders take
   from the deferred pipeline's buffers are missing. The world scale (game units taken as metres) sets the stereo
   depth and may feel miniature or huge.
4. Comfort: the seat follows the fight camera, eased and snapped on cuts. Fast cinematic cameras in specials may be
   uncomfortable; X turns arena mode off.
5. Frame rate: the game draws its frame at 1920x1080 plus the arena twice. Quest 2 may miss 72 Hz.
6. The Android file picker on Quest: if it cannot reach the APKs, use the `tftf/autopick.txt` test hook
   ([`README.md`](README.md)).
