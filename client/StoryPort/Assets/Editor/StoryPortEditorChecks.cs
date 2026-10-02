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
            CheckStoryLocalization();
            CheckEnemyDefense();
            CheckFightGestures();
            CheckHeavyMove();
            AssetDatabase.Refresh();
            CheckNavigationFont();
            StoryPortAssetSetup.ImportLocalUiArt(false);
            foreach (var name in new[]
            {
                "button_main_glowing", "global_nav_button", "teletraan_bg",
                "hero_tile_background", "boss_icon", "frame_selection", "icon_lock"
            })
                if (Resources.Load<Sprite>("StoryPort/UI/" + name) == null)
                    throw new Exception("Missing imported 9.2 UI sprite: " + name);
            CheckBotMaterial();
            CheckFightAnimations();
            CheckAudio();
            var sky = Resources.Load<Material>("StoryPort/ChicagoDaySky");
            if (sky == null || sky.mainTexture == null)
                throw new Exception("Missing converted 9.2 Chicago daylight sky");
            if (Resources.Load<Texture2D>("StoryPort/BaseSky") == null)
                throw new Exception("Missing converted 9.2 Primordial base sky");
            CheckAllianceBeam();
            var road = Resources.Load<Material>("StoryPort/ChicagoRoad");
            if (road == null || road.mainTexture == null || road.shader == null || road.shader.name != "StoryPort/ChicagoRoad")
                throw new Exception("Missing converted 9.2 Chicago asphalt material");
            Debug.Log("StoryPort Editor checks passed: server route and combat values, fight gestures, 9.2 art/audio, and Chicago environment");
        }

        static void CheckFightGestures()
        {
            var input = new StoryPortFightGesture();
            var right = new Vector2(.7f, .4f);
            if (input.Begin(right, 0f) != StoryPortFightGesture.Action.None ||
                input.Move(right, .19f) != StoryPortFightGesture.Action.None ||
                input.Move(right, .21f) != StoryPortFightGesture.Action.Heavy ||
                input.Move(right, .4f) != StoryPortFightGesture.Action.None ||
                input.End(right, .5f) != StoryPortFightGesture.Action.None)
                throw new Exception("A held attack did not fire heavy exactly once");
            input.Begin(right, 1f);
            if (input.End(right, 1.12f) != StoryPortFightGesture.Action.Light)
                throw new Exception("A short tap did not fire light");
            input.Begin(right, 2f);
            if (input.Move(new Vector2(.82f, .4f), 2.25f) != StoryPortFightGesture.Action.None ||
                input.End(new Vector2(.82f, .4f), 2.3f) != StoryPortFightGesture.Action.RightSwipe)
                throw new Exception("A right swipe was misread as a heavy hold");
            input.Begin(right, 3f);
            if (input.End(new Vector2(.57f, .4f), 3.12f) != StoryPortFightGesture.Action.LeftSwipe)
                throw new Exception("A left swipe did not dodge");
            if (input.Begin(new Vector2(.2f, .4f), 4f) != StoryPortFightGesture.Action.Block ||
                input.Cancel() != StoryPortFightGesture.Action.ReleaseBlock || input.Guarding || input.Tracking ||
                input.End(new Vector2(.2f, .4f), 4.5f) != StoryPortFightGesture.Action.None)
                throw new Exception("A canceled block left the guard latched");
            input.Begin(right, 5f);
            if (input.Cancel() != StoryPortFightGesture.Action.None ||
                input.Move(right, 5.5f) != StoryPortFightGesture.Action.None ||
                input.End(right, 5.6f) != StoryPortFightGesture.Action.None)
                throw new Exception("A canceled hold fired an attack");
        }

        static void CheckHeavyMove()
        {
            var rules = new StoryPortCombatRules();
            rules.Load("{\"attackValues\":{\"Heavy\":{\"a\":1.37,\"m\":143,\"c\":0.13,\"d\":1.9}}}");
            var heavy = rules.MoveFor("HeavyAttack");
            if (Mathf.Abs(heavy.Share - 1.37f) > .001f || Mathf.Abs(heavy.Mana - 143f) > .001f ||
                Mathf.Abs(heavy.CritChance - .13f) > .001f || Mathf.Abs(heavy.CritDamage - 1.9f) > .001f)
                throw new Exception("Heavy attack lost server-authored move values");
            if (StoryPortCombatRules.DefenseMultiplier("HeavyAttack", StoryPortEnemyDefense.Action.Block) != 1f ||
                StoryPortCombatRules.DefenseMultiplier("MediumAttack01", StoryPortEnemyDefense.Action.Block) != .1f ||
                StoryPortCombatRules.DefenseMultiplier("HeavyAttack", StoryPortEnemyDefense.Action.Dodge) != 0f)
                throw new Exception("Heavy block break or dodge response changed");
        }

        static void CheckFightAnimations()
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Art92/AnimatorController/animator_char_fight.controller");
            if (controller == null) throw new Exception("Missing converted 9.2 fight controller");
            bool heavy = false, reaction = false;
            foreach (var clip in controller.animationClips)
            {
                heavy |= clip.name == "char_attackHeavy";
                reaction |= clip.name == "char_hitReaction_heavy_front";
            }
            if (!heavy || !reaction)
                throw new Exception("Converted fight controller lacks heavy attack or block break reaction");
        }

        static void CheckEnemyDefense()
        {
            if (StoryPortEnemyDefense.Choose(0f) != StoryPortEnemyDefense.Action.Dodge ||
                StoryPortEnemyDefense.Choose(.3f) != StoryPortEnemyDefense.Action.Block ||
                StoryPortEnemyDefense.Choose(.7f) != StoryPortEnemyDefense.Action.Sidestep ||
                StoryPortEnemyDefense.Choose(.95f) != StoryPortEnemyDefense.Action.Idle)
                throw new Exception("Enemy anticipation weights changed unexpectedly");
        }

        static void CheckNavigationFont()
        {
            var font = Resources.Load<Font>("StoryPort/Fonts/tecnica_nav");
            if (font == null)
                throw new Exception("Missing converted 9.2 Tecnica navigation icon font");
            font.RequestCharactersInTexture("\uE201\uE202\uE203\uE204\uE205\uE206\uE207");
            foreach (var glyph in "\uE201\uE202\uE203\uE204\uE205\uE206\uE207")
                if (!font.HasCharacter(glyph))
                    throw new Exception("Converted navigation font lacks icon " + ((int)glyph).ToString("X4"));
        }

        static void CheckAudio()
        {
            foreach (var name in new[] { "music_fight_loop", "music_questboard_loop", "music_prefight", "fight_won", "fight_lost", "finger_tap" })
                if (Resources.Load<AudioClip>("StoryPort/Audio/UI/" + name) == null)
                    throw new Exception("Missing extracted 9.2 audio clip " + name + "; run tools/storyport/extract_audio.py");
            foreach (var key in new[] { "fte_optimus_gs_t3", "bludgeon_gs_rd20", "ironhide_cin_rotf", "kickback_gs_kabam", "bumblebee_gs_kabam" })
            {
                var set = StoryPortAudio.SoundSetFor(key);
                var clips = Resources.LoadAll<AudioClip>("StoryPort/Audio/Char/" + set);
                bool attack = false, hit = false, react = false;
                foreach (var clip in clips)
                {
                    attack |= clip.name.StartsWith(set + "_attack_1", StringComparison.Ordinal);
                    hit |= clip.name.StartsWith(set + "_attack_hit_", StringComparison.Ordinal);
                    react |= clip.name.StartsWith(set + "_hit_react_light", StringComparison.Ordinal);
                }
                if (!attack || !hit || !react)
                    throw new Exception("Combat sound set " + set + " for " + key + " is incomplete");
            }
        }

        static void CheckBotMaterial()
        {
            var bot = Resources.Load<GameObject>("StoryPort/Bots/optimusprime_cin_tf");
            if (bot == null) throw new Exception("Missing converted 9.2 Optimus prefab");
            var foundPackedSurface = false;
            foreach (var renderer in bot.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null || material.shader.name != "StoryPort/EBPBR")
                        throw new Exception("A converted bot renderer has no StoryPort shader");
                    if (material.GetTexture("_pbr_composite_tex") == null) continue;
                    var scale = material.GetTextureScale("_base_tex");
                    var offset = material.GetTextureOffset("_base_tex");
                    var transform = material.GetVector("_base_uv_transform");
                    if (Mathf.Abs(transform.x - scale.x) > .001f || Mathf.Abs(transform.y - scale.y) > .001f ||
                        Mathf.Abs(transform.z - offset.x) > .001f || Mathf.Abs(transform.w - offset.y) > .001f)
                        throw new Exception("Converted bot lost its base atlas transform");
                    if (material.GetFloat("_roughness_range") > .5f) foundPackedSurface = true;
                }
            if (!foundPackedSurface)
                throw new Exception("Converted 9.2 bot roughness was reset to zero");
        }

        static void CheckAllianceBeam()
        {
            var building = Resources.Load<GameObject>("StoryPort/Buildings/alliance_help");
            if (building == null) throw new Exception("Missing converted 9.2 alliance help building");
            int beamRenderers = 0;
            bool convertedBeamFound = false;
            foreach (var renderer in building.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.name.IndexOf("beam_column", StringComparison.OrdinalIgnoreCase) < 0) continue;
                beamRenderers++;
                if (!renderer.enabled) throw new Exception("Alliance beam renderer was disabled");
                bool rendererHasConvertedBeam = false;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null || material.shader.name != "StoryPort/AllianceBeam") continue;
                    if (material.GetTexture("_EmissionTex") == null ||
                        material.GetTexture("_EmissionTex").name != "fx_t_gradient_l2r")
                        throw new Exception("Alliance beam lost its converted 9.2 emission gradient");
                    if (material.renderQueue != 3100 || material.GetFloat("_EmissionBoost") <= 0f ||
                        material.GetFloat("_EmissionBoost") > 4f)
                        throw new Exception("Alliance beam blend queue or bounded emission strength is invalid");
                    Color tint = material.GetColor("_TintColor");
                    if (tint.a <= 0f || tint.b <= tint.r)
                        throw new Exception("Alliance beam lost its authored blue-violet tint");
                    rendererHasConvertedBeam = true;
                    convertedBeamFound = true;
                    break;
                }
                if (!rendererHasConvertedBeam)
                    throw new Exception("Alliance beam renderer has no transparent additive material");
            }
            if (beamRenderers == 0) throw new Exception("Converted alliance help prefab has no beam_column renderer");
            if (!convertedBeamFound) throw new Exception("Converted alliance help prefab has no transparent beam material");
        }

        static void CheckStoryLocalization()
        {
            const string detail = "{\"dialogueTable\":{\"intro\":[" +
                "{\"inShadow\":false,\"character\":\"a\",\"side\":\"left\",\"line\":\"Hello, {0}!\"}," +
                "{\"inShadow\":true,\"character\":\"b\",\"side\":\"right\",\"line\":\"Second\"}," +
                "{\"inShadow\":false,\"character\":\"a\",\"side\":\"left\",\"line\":{\"en\":\"Third\",\"fr\":\"Troisi\\u00e8me\",\"de\":\"\"}}," +
                "{\"inShadow\":false,\"character\":\"b\",\"side\":\"right\",\"line\":\"\"}]}}";
            string saved = null;
            var catalogs = new System.Collections.Generic.Dictionary<string, string>
            {
                { "de", "# comment\nID_STORY_INTRO_001\tHallo, {0}!\\nZeile\nID_STORY_INTRO_002\t\nID_STORY_INTRO_001\tduplicate\r\n" },
                { "fr", "ID_STORY_INTRO_003\tCatalogue français\n" },
                { "en", "ID_STORY_INTRO_002\tEnglish catalog\nID_STORY_INTRO_004\tEnglish catalog fallback\n" },
                { "ja", "ID_STORY_INTRO_001\t\u3053\u3093\u306b\u3061\u306f\nID_STORY_UI_SKIP\t\u30b9\u30ad\u30c3\u30d7\n" }
            };
            StoryLocalization.ConfigureForTests(
                locale => { string text; return catalogs.TryGetValue(locale, out text) ? text : null; },
                () => saved ?? "", value => saved = value);
            try
            {
                var lines = StoryRouteData.ReadDialogue(detail, "intro");
                if (lines.Count != 4 || lines[0].character != "a" || lines[1].side != "right" || !lines[1].inShadow)
                    throw new Exception("Localized dialogue lost order, speaker, or side");
                if (lines[0].key != "ID_STORY_INTRO_001" || lines[2].key != "ID_STORY_INTRO_003")
                    throw new Exception("Dialogue keys are not set id plus position");
                if (StoryLocalization.Locale != "en" || lines[0].Text != "Hello, {0}!")
                    throw new Exception("English dialogue changed");
                StoryLocalization.SetLocale("de");
                if (saved != "de" || lines[0].Text != "Hallo, {0}!\nZeile")
                    throw new Exception("German switch did not apply to a line read before it");
                if (lines[1].Text != "Second") throw new Exception("Empty translation did not fall back to English");
                if (lines[2].Text != "Third") throw new Exception("Empty locale-map entry did not fall back to English");
                if (lines[3].Text != "English catalog fallback") throw new Exception("English catalog did not provide the final fallback");
                StoryLocalization.SetLocale("fr");
                if (lines[2].Text != "Troisi\u00e8me" || lines[0].Text != "Hello, {0}!")
                    throw new Exception("Server locale map or catalog fallback resolved wrongly");
                StoryLocalization.SetLocale("ja");
                if (lines[0].Text != "\u3053\u3093\u306b\u3061\u306f" || StoryLocalization.Ui("ID_STORY_UI_SKIP", "SKIP") != "\u30b9\u30ad\u30c3\u30d7")
                    throw new Exception("Japanese dialogue or chrome did not resolve");
                StoryLocalization.SetLocale("xx");
                if (StoryLocalization.Locale != "en" || saved != "en" || lines[0].Text != "Hello, {0}!")
                    throw new Exception("Unknown locale did not fall back to English");
                if (StoryLocalization.Ui("ID_STORY_UI_SKIP", "SKIP") != "SKIP")
                    throw new Exception("Missing chrome key returned a raw key or blank");
                // A new session reads the saved choice, ignoring unknown saved values.
                saved = "pt_BR"; StoryLocalization.ResetCache();
                if (StoryLocalization.Locale != "pt") throw new Exception("Saved language was not restored");
                saved = "zz"; StoryLocalization.ResetCache();
                if (StoryLocalization.Normalize(saved) != null || StoryLocalization.Normalize(StoryLocalization.Locale) == null)
                    throw new Exception("Unknown saved language was accepted instead of using the device fallback");
                if (StoryLocalization.Locales.Length != StoryLocalization.NativeNames.Length)
                    throw new Exception("Language list and native names differ");
            }
            finally { StoryLocalization.ConfigureForTests(null, null, null); }
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
