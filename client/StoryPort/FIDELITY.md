# StoryPort fidelity workflow

Reference: [PrimeDev beta gameplay](https://www.youtube.com/watch?v=Zsf63-gR0nA).
Its route and rewards differ from this project's server. The server remains the
source for the three act IDs, encounter order, map links, teams, and fight
results. The video supplies screen composition, animation, and control cues.

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
   project. This checks route parsing and required imported art.
3. Run Unity with `-batchmode -force-glcore -quit -executeMethod
   StoryPort.Editor.StoryPortPreviewCapture.CaptureStory`, setting
   `STORYPORT_PREVIEW_PNG` to a local output path. Compare the resulting
   1600×900 screen to the video frame at 12:06. Fix major layout and art
   differences in the Editor before an Android build.
4. Build and install once for the completed screen or combat slice. Check
   navigation, a full fight, and the server's recorded result on device.

Next visual gaps: base building placement and camera; squad composition and
materials; story board terrain framing; combat motion and HUD. The Editor
preview should cover these screens as their code is separated from the main
bootstrap file.
