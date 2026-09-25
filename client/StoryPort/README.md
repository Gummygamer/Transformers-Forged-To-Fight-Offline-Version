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
