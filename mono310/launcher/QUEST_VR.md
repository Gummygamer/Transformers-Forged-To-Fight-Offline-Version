# Meta Quest VR build of the launcher

## Table of contents

- [Status](#status)
- [What it is](#what-it-is)
- [Controls](#controls)
- [Building](#building)
- [How it works](#how-it-works)
- [Open risks](#open-risks)

## Status

**Run on a Quest 2 (2026-10-22):** the stereo arena renders in fights (see [Open risks](#open-risks), item 2). The
rest of this section predates that run. **Built.** The `TFTF_VR=1` build compiles, links and signs; its manifest carries the Quest
entries (`com.oculus.intent.category.VR`, `android.hardware.vr.headtracking`, `com.oculus.supportedDevices=quest2`,
`focusaware`, API 29) and it packs `libOVRPlugin.so` and `libOculusXRPlugin.so` **[📄 binary]** (`aapt2 dump` of
`tftf-launcher-quest.apk`, 2026-10-22). Nothing below has been seen working in a headset; every runtime statement is
**[⚠ inferred]** until it is.

## What it is

A third shell variant beside the phone (ARMv7 / ARM64) and Windows builds: the same assetless launcher and game, with
the Oculus XR plugin (1.11.2, bundled with Unity 2020.3.31f1, so no download is needed).

- **Everywhere outside fights and the home base** the game renders as on a phone, into a 1920x1080 texture shown on a 3 m screen
  2.5 m in front of you, in a dark room with a floor grid. The headset sees the room in stereo.
- **In fights, first-person (default)** you are the fighter: the view sits in its head, facing the opponent, the world
  scaled so its eye height is yours. Its body stays in view (the head is shrunk so the eye sees out) and its arms follow
  your controllers or tracked hands (two-bone IK, `shell/Tftf/TftfVrBody.cs`). You fight with motion controls (below).
  Cinematic specials and the fight's intro and outro switch to the arena seat for their duration.
- **In fights (arena mode)** the arena itself is drawn in stereo from a seat on the fight camera's line of sight. You can
  lean and look around; the HUD floats in front of the arena on the transparent screen.
- **On the home base (base mode)** the base is drawn in stereo the same way, from a seat on the base camera's line of
  sight, with a level horizon and at a tenth of its size (head moves and eye separation scaled by 10), so it lies below
  the HUD screen as a model town with depth. Point the laser at the base itself to tap a building or drag to pan.

## Controls

| Input | Action |
|---|---|
| Trigger (either hand; the last one pulled is the pointer) | the finger: tap, drag and hold on the screen (or, in base mode, on the base), laser and cursor shown |
| A | light attack (taps the HUD attack zone) |
| Right stick flicked right / left | swipe forward / back across the attack zone (medium attack / dash back) |
| Either grip, held | block (holds the HUD block zone) |
| X | fights: first-person → arena → flat (flat also turns base mode off) |
| Y | motion controls off / on |
| B | special attack (the highest the meter allows) |
| Left stick up / down | arena / base seat nearer / farther |
| Either stick click | put the screen back in front of you (holding the Oculus button recentres too) |

Specials and every other HUD button are pressed with the laser. Put the controllers down and the tracked hands take
over: a pinch is the trigger, the laser comes from the hand's pointer pose.

### Motion controls (fights)

`shell/TftfMain/TftfVrCombat.cs` plays gestures on the fighter's `PlayerInput` (as the test bot does, so online Arena
fights capture them too). Directions are relative to where your head faces.

| Gesture | Action |
|---|---|
| Fast punch forward, one hand (a tracked hand must be a fist) | light attack |
| Punch with both hands at once, or a quick step in | dash (medium attack) |
| Both hands raised above your head, then brought down | heavy attack |
| Both forearms up in front of your face, held | block |
| Quick lean back | dodge back |
| Quick lean left / right | sidestep |
| Both fists held together in front of your chest (0.35 s) | special |

Hand markers show where the arms are not driven (arena view): blue idle, green guard, yellow raised, orange on a landed
hit, red when hit. Thresholds are tunable without a rebuild: `tftf/vrcombat.txt`, lines `<name> <value>` (`eye`,
`jab`, `jabreach`, `both`, `fist`, `lean`, `leandist`, `guardy`, `guardz`, `guardgap`, `raise`; defaults in
`TftfVrCombat.Tune`), read at the start of each fight.

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
| First-person seat, fighter's head shrunk, arm IK; tracked hands (OVRPlugin `ovrp_GetHandState`, read raw) | `shell/Tftf/TftfVr.cs`, `TftfVrBody.cs`, `TftfVrHands.cs` |
| Gestures → `PlayerInput`, fighters reported for the seat, haptics | `shell/TftfMain/TftfVrCombat.cs` |
| Hand tracking in the manifest (`HAND_TRACKING` permission, optional `oculus.software.handtracking`) | `TftfVrHandTrackingManifest` in `mono310/unity/TftfVrBuildSetup.cs` |
| Launcher and splash screens (IMGUI, which the headset never shows) drawn on a world-space canvas instead | `shell/Tftf/TftfVrPanel.cs` |
| Room shaders | `mono310/unity/shaders/vr/` |

The deferred scene camera writes its own buffers and composites to the backbuffer itself (`EBPostFXManager.PostRender`
→ `EBImage.Blit`/`BlitToNative`, **[📄 binary]** `EB/Rendering/EBPostFXManager.cs:193`), so P53 points that composite at
the screen texture rather than giving the camera a target. In arena mode the fight camera's culling mask moves to the
headset's arena camera and its composite goes to a scratch texture, so the screen keeps only the HUD.

## Open risks

All **[⚠ inferred]**, in the order to check them on a headset:

1. Head tracking: the rig poses both headset cameras from `XRNode.Head` with automatic camera tracking turned off.
2. The HUD's transparency in arena mode depends on no game camera painting the screen texture. In arena mode every
   game camera drawing into it clears depth only and loses the arena's layers (the cutscene camera stays enabled
   through fights); both are restored when arena mode ends. X turns arena mode off.

**Fixed on a Quest 2 (2026-10-22):** fights were black but for the HUD, the sun flare and a few effects. A new camera's
rendering path resolves to VertexLit from the player settings, which skips every ForwardBase pass of the game's
shaders; the arena eye now takes the fight camera's rendering path (Forward). The headset's view cannot be captured
over adb: pushing an empty `tftf/vrshot.txt` during a fight writes `tftf/vrshot-seat.png` (the arena eye without
stereo) and `tftf/vrshot-screen.png` (the HUD).
**Fixed (2026-10-22):** magenta upside-down silhouettes over Karnak fights (both modes). Every camera without a target was
pointed at the screen texture, including the game's planar reflection camera, which is disabled, rendered by hand and
targets its own buffers through `SetTargetBuffers` (so `targetTexture` reads null). Disabled cameras and the game's light
cameras are now left alone.
3. Arena look: the arena camera is a plain forward camera, so the game's post-FX grade and anything its shaders take
   from the deferred pipeline's buffers are missing. The world scale (game units taken as metres) sets the stereo
   depth and may feel miniature or huge.
4. Comfort: the seat follows the fight camera, eased and snapped on cuts. Fast cinematic cameras in specials may be
   uncomfortable; X turns arena mode off.
5. Frame rate: the game draws its frame at 1920x1080 plus the arena twice. Quest 2 may miss 72 Hz.
6. The Android file picker on Quest: if it cannot reach the APKs, use the `tftf/autopick.txt` test hook
   ([`README.md`](README.md)).
7. Base mode (0.10, never run on a headset): the base camera is reported by `BaseCameraController.UpdateCamera`
   (P53). The scaled eye separation is set with `Camera.SetStereoViewMatrix` from the headset's eye positions; if the
   Oculus plugin does not apply hand-set stereo matrices, the base still shows, only flatter. Laser taps on the base
   are turned into the base camera's pixel of the touched collider (or ground point), so they assume the game's own
   raycast hits the same object from the base camera.
8. First-person and motion controls (never run on a headset): the arm bones are found by the humanoid map or by name
   (the log line `[TFTF] VR body of …` names them; `-` means not found, and that arm stays animated with a marker
   instead). The IK runs after the game's animation, in `Application.onBeforeRender`; if the game reads hand bone
   positions for hits before the next animation step, the moved arms could change what hits. The eye height comes
   from the head bone (or the collider height); `eye` in `tftf/vrcombat.txt` scales the fallback. The hand-state layout
   is OVRPlugin's `HandStateInternal` as of 2020-21; tracked-hand poses are assumed to share the controllers'
   tracking space. The wrist does not follow the controller's rotation, and fingers are not driven.
