#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace StoryPort.Editor
{
    // Run with -batchmode -quit -executeMethod StoryPort.Editor.StoryPortEditorChecks.Run.
    // This runs against the local asset catalog without producing an APK.
    public static class StoryPortEditorChecks
    {
        public static void Run()
        {
            CheckStoryRoute();
            AssetDatabase.Refresh();
            StoryPortAssetSetup.ImportLocalUiArt(false);
            foreach (var name in new[]
            {
                "button_main_glowing", "global_nav_button", "teletraan_bg",
                "hero_tile_background", "boss_icon", "frame_selection", "icon_lock"
            })
                if (Resources.Load<Sprite>("StoryPort/UI/" + name) == null)
                    throw new Exception("Missing imported 9.2 UI sprite: " + name);
            Debug.Log("StoryPort Editor checks passed: server route parser and 9.2 UI sprites");
        }

        static void CheckStoryRoute()
        {
            const string response = "{\"result\":{\"2.1.1\":{\"map\":{\"gridDimension\":3,\"grid\":[" +
                "[{\"walkable\":true,\"hidden\":false,\"lab\":\"Start\",\"boss\":\"bludgeon\",\"links\":[{\"x\":1,\"y\":0}]}," +
                "{\"walkable\":true,\"hidden\":true,\"lab\":\"Hidden\"}]," +
                "[{\"walkable\":true,\"hidden\":false,\"lab\":\"Final\",\"boss\":\"ironhide\",\"final\":true,\"links\":[]}],[]]}}}}";
            var route = StoryRouteData.Parse(response, "2.1.1");
            if (!route.hasMap || route.dimension != 3 || route.nodes.Count != 2)
                throw new Exception("Story route lost its dimension or visible nodes");
            if (route.nodes[0].x != 0 || route.nodes[0].y != 0 || route.nodes[0].boss != "bludgeon" ||
                route.nodes[0].links.Count != 1 || route.nodes[0].links[0] != new Vector2Int(1, 0))
                throw new Exception("Story route changed server coordinates or links");
            if (route.nodes[1].x != 1 || route.nodes[1].y != 0 || !route.nodes[1].isFinal)
                throw new Exception("Story route lost its final encounter");
            var absent = StoryRouteData.Parse(response, "2.3.1");
            if (absent.hasMap || absent.nodes.Count != 0)
                throw new Exception("An unrelated quest was parsed as the active act");
        }
    }
}
#endif
