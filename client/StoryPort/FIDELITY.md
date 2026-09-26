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

Matched to the reference so far (Editor previews): fight (street-level Chicago
stage at real scale, dusk sky pinned to the horizon, slanted health bars, hex
touch zones, segmented special meter), bot selection (dark panel, team column,
VS with matchup bar), story missions (act cards, chapter panel, encounter grid)
and the hex-collage loading page. Preview with `CaptureFight`, `CaptureSquad`,
`CaptureStory`, `CaptureLoading`; `SP_STAGE`, `SP_CAM` and `SP_SQUADCAM`
environment variables retune the stage and cameras in the Editor only.

Remaining gaps: base building placement and camera; bot material color and
reflection; story board terrain framing; Chicago skyline density and rubble;
per-bot rating and class icons under the fight bars (no server field mapped
yet); explored percentages on the story cards; on-device verification of the
new fight camera and HUD.
