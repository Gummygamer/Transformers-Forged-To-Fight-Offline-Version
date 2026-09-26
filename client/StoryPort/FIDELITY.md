# StoryPort fidelity workflow

Reference: [Beta gameplay reference video](https://www.youtube.com/watch?v=Zsf63-gR0nA).
Its route and rewards differ from this project's server. The existing server data is authoritative and must not be replaced by
video content: keep its story fight sequence, encounter order, stats, act IDs,
map links, teams, and fight results. The video supplies screen composition, animation, and control cues.

| Video time | Screen to compare | Visible details to retain |
| --- | --- | --- |
| 0:07 | Loading | Full game artwork, logo, loading status |
| 0:30 | Base | Authored rocky base terrain and buildings; seven navigation tabs below the resource bar |
| 1:30 | Bot selection | One large selected bot at left, one large opponent at right, vertical team portraits, health and rating in the centre, Fight action |
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

Preview with `CaptureStory`, `CaptureSquad`, `CaptureFight`, `CaptureResult`,
`CaptureDialogue`, `CaptureComplete`, `CaptureLoading` and `CaptureBase`. `SP_STAGE`, `SP_CAM`,
`SP_SQUADCAM`, `SP_BASECAM` and `SP_BLDG` retune the stage, cameras and building
layout in the Editor only.

Remaining gaps: the server grants no match rewards, so the result screen has no
rewards row; Marissa has no model or portrait in the local 9.2 data; the base
still shows black beyond parts of the crater terrain, and its alliance beam
glow is hidden (it renders opaque); the enemy
defense timing still needs a full device fight comparison; no special-attack
cinematic camera yet.
