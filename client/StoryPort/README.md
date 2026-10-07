# StoryPort client

StoryPort is a clean Unity client for the local offline game server. It is a new
client implementation, not a port of the game's executable code. Runtime code is
versioned here; the Unity project, editor cache, and all converted game content
belong under the ignored `build/` directory and are generated from a locally
owned 9.2.0 install.

The first vertical slice covers the base hub, the three-act story selection,
story map navigation, squad selection, and a complete playable first fight. The
client uses `/base/active`, `/quests/quest-detail`, `/quests/quest-begin`,
`/quests/quest-movedir`, `/bcg/setSavedTeam`, and `/matches/resolve-match` from
the existing external server. Combat controls and hit resolution run locally.

`Editor/StoryPortAssetSetup.cs` scans locally converted assets and creates
Resources aliases for scene prefabs, terrain meshes/materials, and the requested
bot models. Converted assets and aliases are not checked into Git.

Story fights read `mapOverride` and `todIndex` from the current server
`battleEnemy` (every Story quest picks a deterministic arena per encounter; see
`BOARD_AUTHORING.md`). `tools/storyport/prepare_project.py` links, for Chicago,
Hong Kong, Karnak, Mine, and Rust, the `<level>_merged` geometry prefab as
`Resources/StoryPort/Arenas/<level>_stage` and each time of day's sky texture as
`<level>_sky_<0-2>`; the `<level>_timeofday_*` prefabs hold only a sky dome and sun,
so they are not used as stages. `StoryPortAssetSetup` wraps the sky textures in
unlit materials, and `ArenaPlacement` in `StoryPortBootstrap.cs` seats the fighters
on each arena floor. Levels missing from the main converted project are read from
`build/story-arena-conversion-<level>/ExportedProject` (one AssetRipper export per
`assetpack/<level>/<level>_merged.assetbundle`; override the folder with
`--arena-conversions`). Editor checks require all five stages and 15 skies and the
server-arena parser to pass. A missing runtime stage falls back to the converted
Chicago street. `STORYPORT_PREVIEW_ARENA=<level>:<tod>` with
`StoryPortPreviewCapture.CaptureFight` renders one arena on a graphics-enabled editor.

## Story dialogue language

Story dialogue follows the player's saved language (`PlayerPrefs` key
`storyport.language`; first run uses the device language, else English). The title
screen and the dialogue screen each have a language button; switching on the
dialogue screen redraws the current line without changing its position.

The server still sends English in each line's `line` string. A `line` object of
`{"en": ..., "<locale>": ...}` is also accepted. Other languages come from UTF-8
catalogs in `Assets/Resources/StoryPort/Localization/dialogue_<locale>.txt`, one
`KEY<TAB>text` per line (`#` comments; `\n`, `\t`, `\\` escapes). Keys are
`ID_STORY_<SET ID UPPERCASED>_<NNN>` with a 1-based line number, for example
`ID_STORY_CUSTOM_OPENING_INTRO_001`. Screen chrome uses `ID_STORY_UI_TAP_CONTINUE`
and `ID_STORY_UI_SKIP`. Supported locales: `en zh-CN zh-TW pt fr it de es ru ko ja
tr ar id th no nl`. A line resolves as the server's locale entry, then the selected
catalog, then server English, then the English catalog; empty entries and unknown
locales count as missing. `tools/storyport/prepare_project.py` copies the catalogs
into the prepared Unity project.
