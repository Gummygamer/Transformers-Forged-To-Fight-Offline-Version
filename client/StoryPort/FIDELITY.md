# StoryPort fidelity workflow

Reference: [Beta gameplay reference video](https://www.youtube.com/watch?v=Zsf63-gR0nA).
Its route and rewards differ from this project's server. The existing server data is authoritative and must not be replaced by
video content: keep its story fight sequence, encounter order, stats, act IDs,
map links, teams, and fight results. The video supplies screen composition, animation, and control cues.

| Video time | Screen to compare | Visible details to retain |
| --- | --- | --- |
| 0:07 | Loading | Full game artwork, logo, loading status |
| 0:30 | Base | Authored rocky base terrain and buildings; seven navigation tabs below the resource bar |
| 1:30 | Bot selection | Large selected bot at left and opponent at right; three tall team portraits at the left edge; names and ratings face each other around VS; each side shows current/max health and attack below; green FIGHT button at lower right |
| 2:48 | Fight | Chicago stage, animated 3D bots, portraits and health at top, touch combat |
| 12:06 | Story missions | Narrow act artwork panels flanking a compact blue chapter and encounter grid |

Review each screen in that order. Identify the local 9.2 prefab, material, or
sprite for every dominant visual element before editing layout. If an element
cannot be matched to a local asset, record that gap explicitly. Keep video
captures, converted assets, and Unity previews outside Git.

Audio: `build/unitypy-env/bin/python tools/storyport/extract_audio.py --project <UnityProject>`
exports the local 9.2 music, UI and per-class combat clips into the project's
Resources (git-ignored). The client plays questboard, pre-fight, fight and
post-fight music plus attack, hit, block, dash and knockout cues.

Fast gates before producing an APK:

1. `python3 -m unittest discover -s tools/storyport -p 'test_prepare_project.py'`
   checks atlas coordinates and fails on missing or invalid UI sprites.
2. Run Unity with `-batchmode -nographics -quit -executeMethod
   StoryPort.Editor.StoryPortEditorChecks.Run` against the prepared local
   project. This checks route parsing, required UI art, bot material values,
   and the Chicago sky and road resources.
3. Run Unity with `-batchmode -force-glcore -quit -executeMethod
   StoryPort.Editor.StoryPortPreviewCapture.CaptureStory`, `CaptureSquad`, or
   `CaptureFight`, setting `STORYPORT_PREVIEW_PNG` to a local output path.
   Compare each 1600×900 render to its video frame. Fix major layout and art
   differences in the Editor before an Android build.
4. Build and install once for the completed screen or combat slice. Check
   navigation, a full fight, and the server's recorded result on device.

Matched to the reference so far (Editor previews, and on the Samsung phone for
the full Act I and Act II route):

- Loading, base (crater camera, buildings on the server's sockets, warm grade),
  story missions, story board, bot selection, fight and result screens.
- Story dialogue from the server's `dialogueTable`: `dialogue` before an
  encounter and `dialoguePE` after the win, with 3D speakers, the silent side
  dimmed, SKIP and tap to continue.
- Combat uses server data only: health/attack from `/bcg/getBaseHeroData`, the
  `attackValues` move table and each blueprint's special ratios `s1..s3` from
  `/bcg/getLoginData`. Specials fire at one to three bars. The enemy attacks in
  one to three hit combos on its own timer; a test fight gave 14 hits landed,
  13 received, chain 5 (reference: 16, 9, 5).
- Music and sound from the local 9.2 audio (see Audio above).
- Mission Complete screen after an act's last encounter (BACK TO MISSIONS, greyed
  REPLAY, PLAY NEXT); the rewards panel stays empty because the server grants none.
- Knockout shot: the camera pushes in on the winner under "<NAME> WINS!".
- Enemy anticipation now uses the 9.2 melee personality's dodge, block,
  sidestep and idle weights. The response plays the converted fight animations;
  a short cooldown lets later combo hits through. Editor checks cover the
  weighted decision, and the converted fight controllers contain its states.
- The seven base navigation icons use the original 9.2 button glyphs from the
  local Tecnica font. `prepare_project.py` copies that font into the ignored
  Unity project; the Editor base preview confirms their placement.
- The 1:30 pre-fight screen uses the selected and opponent bots' server health,
  attack, and derived rating in the center column. The local preview falls back
  to placeholder stats until `/bcg/getBaseHeroData` responds.
- Loading pages (0:07, ~1:36) cycle tips with a gold label ("COMBAT TIP", "HEAVY ATTACKS",
  "FORGE XP"), as the footage does. The heavy tip now matches the local 9.2 text and
  control: a right-side hold past 0.2 seconds fires one heavy attack, which breaks a
  block. A tap remains light; a right swipe strikes medium in reach or advances from
  farther away. Damage and meter come from the server's Heavy and Medium move values.
  Unity 6.6.3f1 Editor checks cover these gestures, canceled holds, block breaks,
  and server move values; the touch controls still need an on-device comparison.
  The footage's Edit Squad screen (~1:04) is not built: the
  port has no locked-slot or squad-tab data, so those details remain a gap. The
  current squad selector supports up to three bots from the eight-bot roster.
- The base uses the local 9.2 Primordial sky panorama behind its 3D crater;
  the Editor preview no longer shows black gaps beyond the terrain.
- The alliance-help beam now uses its converted 9.2 emission gradient,
  blue-violet tint and additive transparent material instead of being hidden.
  The Editor check verifies its renderer, material and texture; a
  graphics-enabled base comparison is still needed to judge exact brightness.
- Mission cards use the local Tecnica font, taller artwork, and larger text,
  compared with the 12:06 frame. Artwork crops UVs at the final card size;
  immediate Editor captures and runtime use the same path without stretching.
  The read-only `/quests/quest-map/<qid>` endpoint supplies enemy portraits
  before starting a quest. All three authored maps were checked; fetching them
  leaves the saved server state unchanged. Act III retains its fourth encounter.
  The exact act-specific mission art is still missing: local loading art is
  cropped around its subjects as a fallback. This remains a visual gap.

Preview with `CaptureStory`, `CaptureSquad`, `CaptureFight`, `CaptureResult`,
`CaptureDialogue`, `CaptureComplete`, `CaptureLoading` and `CaptureBase`. `SP_STAGE`, `SP_CAM`,
`SP_SQUADCAM`, `SP_BASECAM` and `SP_BLDG` retune the stage, cameras and building
layout in the Editor only.

Remaining gaps: the server grants no match rewards, so the result screen has no
rewards row; Marissa has no model or portrait in the local 9.2 data; the base's
alliance beam glow is hidden (it renders opaque); the enemy
defense timing still needs a full device fight comparison; the special-attack
cinematic camera (attacker close-up, swing to target, return) is an approximation.
In Unity 6.6.3f1 Play Mode, both player and enemy level-3 specials moved the camera and restored its
fight position, rotation and field of view exactly. The shot framing still needs frame-by-frame
comparison with the ~2:48 reference footage.
