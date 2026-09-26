using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StoryPort
{
    public sealed class StoryPortBootstrap : MonoBehaviour
    {
        const string DefaultServer = "http://127.0.0.1:8080";
        const string StorySet = "custom_story_act1";
        static readonly string[] ActQids = { "2.1.1", "2.2.1", "2.3.1" };
        static readonly string[] ActTitles = { "BLUDGEON'S AMBUSH", "RESOURCE SCANNERS", "BLUDGEON'S RECKONING" };
        // Raw quest-detail per act; holds the server's dialogueTable.
        readonly string[] actDetails = new string[3];
        List<DialogueLine> dialogueLines;
        int dialogueIndex;
        Action dialogueDone;
        GameObject dialogueLeft, dialogueRight;
        readonly HashSet<string> dialoguesSeen = new HashSet<string>();
        static readonly string[] ActDescriptions =
        {
            "Optimus Prime confronts Bludgeon in the city streets.",
            "Marissa leads the Autobots to resources that can repair their ship.",
            "Jazz leads a split assault on Grindor and Ironhide before the final duel."
        };
        static readonly string[] ActNodeLabels =
        {
            "Bludgeon's Ambush|Starscream's Betrayal|Ironhide's Challenge",
            "Resource Scanners|Mirage's Ambush|Bumblebee's Challenge",
            "Jazz's Challenge|Grindor's Assault|Ironhide's Stand|Bludgeon's Return"
        };

        readonly string[] rosterKeys =
        {
            "fte_optimus_gs_t3", "bumblebee_gs_kabam", "ironhide_cin_rotf", "jazz_gs_twm05", "bludgeon_gs_rd20"
        };
        readonly string[] rosterNames = { "Optimus Prime", "Bumblebee", "Ironhide", "Jazz", "Bludgeon" };
        readonly List<int> squad = new List<int> { 0, 1 };
        readonly List<GameObject> worldRoots = new List<GameObject>();

        string serverUrl;
        string screen = "title";
        string notice = "";
        string currentQid = ActQids[0];
        string enemyKey = "bludgeon_gs_rd20";
        string playerKey = "fte_optimus_gs_t3";
        string playerName = "Optimus Prime";
        string enemyName = "Bludgeon";
        string[] storyNodes;
        readonly List<StoryMapNode> storyMapNodes = new List<StoryMapNode>();
        readonly Dictionary<Vector2Int, Vector3> storyNodeWorldPositions = new Dictionary<Vector2Int, Vector3>();
        int storyMapDimension;
        int actIndex;
        int mapX;
        int mapY;
        int playerHp = 100;
        int enemyHp = 100;
        // Absolute fight stats from /bcg/getBaseHeroData; the percent fields above
        // are derived from these for the HUD.
        // Preview-only placeholders until the server answers.
        float playerMaxHealth = 3000, playerHealth = 3000, playerAttack = 300;
        float enemyMaxHealth = 3000, enemyHealth = 3000, enemyAttack = 300;
        string lastQuestJson = "";
        int specialMeter;
        float playerMana, enemyMana;
        int hitsLanded, hitsReceived, highestChain;
        int enemySpecialLevel;
        readonly StoryPortCombatRules rules = new StoryPortCombatRules();
        Text playerRatingText, enemyRatingText;
        Image specialHex;
        Image[] specialSegmentFills;
        int selectedBot;
        bool requestBusy;
        bool guarding;
        bool pendingEncounter;
        bool enemyBusy;
        bool playerBusy;
        bool paused;
        bool queuedAttack;
        string queuedAttackState;
        int queuedAttackDamage;
        int queuedAttackCharge;
        bool touchTracking;
        bool squadForStory;
        float nextEnemyTurn;
        float evadeUntil;
        float touchBeganAt;
        int lightCombo;
        int comboHits;
        Vector2 touchStart;
        float lastPlayerHit;
        float lastEnemyDefense;
        Sprite uiButtonSprite;
        Transform headerRoot;
        Transform uiRoot;
        GameObject backgroundArt;
        StoryPortAudio audioPlayer;
        GameObject pauseOverlay;
        GameObject playerActor;
        GameObject enemyActor;
        Animator playerAnimator;
        Animator enemyAnimator;
        Canvas canvas;
        RectTransform content;
        Text statusText;
        Text playerHpText;
        Text enemyHpText;
        Text specialText;
        Text specialButtonLabel;
        Text comboText;
        Image playerHpFill;
        Image enemyHpFill;
        Image specialFill;
        Image[] specialSegments;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartClient()
        {
            if (FindObjectOfType<StoryPortBootstrap>() == null)
                new GameObject("StoryPort").AddComponent<StoryPortBootstrap>();
        }

        void Awake()
        {
            serverUrl = PlayerPrefs.GetString("storyport.server", DefaultServer).TrimEnd('/');
            storyNodes = ActNodeLabels[0].Split('|');
            Application.targetFrameRate = 60;
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            audioPlayer = gameObject.AddComponent<StoryPortAudio>();
            BuildCamera();
            BuildUI();
            Show("title");
            StartCoroutine(LoadCampaignMetadata());
        }

        IEnumerator LoadCampaignMetadata()
        {
            yield return StartCoroutine(Get("/base/active", _ => { }));
            yield return StartCoroutine(Get("/bcg/getLoginData", json => rules.Load(json)));
            for (int i = 0; i < ActQids.Length; i++)
            {
                string response = "";
                yield return StartCoroutine(Get("/quests/quest-detail/" + ActQids[i], json => response = json));
                actDetails[i] = response;
                var title = ExtractJsonString(response, "friendlyName");
                var description = ExtractJsonString(response, "description");
                if (!string.IsNullOrEmpty(title)) ActTitles[i] = title.ToUpperInvariant();
                if (!string.IsNullOrEmpty(description)) ActDescriptions[i] = description;
            }
        }

        void BuildCamera()
        {
            var cameraObject = new GameObject("StoryPort Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.025f, .055f, .09f);
            camera.fieldOfView = 36;
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 4000;
            cameraObject.transform.position = new Vector3(0, 7, -19);
            cameraObject.transform.LookAt(new Vector3(0, 3.2f, 0));
            var light = new GameObject("StoryPort Key Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.65f;
            light.transform.rotation = Quaternion.Euler(42, -28, 0);
            RenderSettings.ambientLight = new Color(.7f, .74f, .8f);
            // Reflect the 9.2 outdoor probe, not Unity's default bright sky,
            // so painted armour keeps its colour instead of reading as chrome.
            var reflection = Resources.Load<Cubemap>("StoryPort/EnvReflection");
            if (reflection != null)
            {
                RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = reflection;
                RenderSettings.reflectionIntensity = 1f;
            }
        }

        // Editor-only iteration hook: SP_* environment variables override defaults.
        static float[] Tune(string name, float[] defaults)
        {
#if UNITY_EDITOR
            var raw = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(raw))
            {
                var parts = raw.Split(',');
                var values = (float[])defaults.Clone();
                for (var i = 0; i < parts.Length && i < values.Length; i++)
                    float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i]);
                return values;
            }
#endif
            return defaults;
        }

        void ResetCamera()
        {
            var camera = Camera.main;
            if (camera == null) return;
            camera.fieldOfView = 36;
            camera.farClipPlane = 4000;
            // Keep the converted Chicago geometry in the background. The
            // characters stand just in front of its near edge so foreground
            // buildings do not cover their legs during a fight.
            var cam = Tune("SP_CAM", new[] { 0f, 2.4f, -16.5f, 0f, 3f, -5f });
            camera.transform.position = new Vector3(cam[0], cam[1], cam[2]);
            camera.transform.LookAt(new Vector3(cam[3], cam[4], cam[5]));
        }

        void FrameWorld(GameObject instance, float padding = 1.18f)
        {
            if (instance == null) return;
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = default(Bounds);
            bool found = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || renderer.name.Equals("Sky", StringComparison.OrdinalIgnoreCase)) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!found || bounds.size.sqrMagnitude < .01f)
            {
                Debug.LogWarning("StoryPort cannot frame " + instance.name + ": renderers=" + renderers.Length +
                    " active=" + Array.FindAll(renderers, r => r != null && r.enabled).Length);
                return;
            }
            var camera = Camera.main;
            if (camera == null) return;
            camera.fieldOfView = 36;
            float aspect = Mathf.Max(.5f, (float)Screen.width / Screen.height);
            float viewHeight = Mathf.Max(bounds.size.y, bounds.size.x / aspect) * padding;
            float distance = viewHeight / (2f * Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad));
            var target = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * .48f, bounds.center.z);
            camera.transform.position = target + new Vector3(0, distance * .32f, -distance);
            camera.transform.LookAt(target);
            camera.farClipPlane = Mathf.Max(4000, distance + bounds.size.magnitude * 2f);
            Debug.Log("StoryPort framed " + instance.name + " bounds=" + bounds + " cameraDistance=" + distance);
        }

        void BuildUI()
        {
            var canvasObject = new GameObject("StoryPort UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = .5f;
            if (FindObjectOfType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            var root = Panel(canvas.transform, "Root", new Color(0, 0, 0, 0), Vector2.zero, Vector2.one);
            uiRoot = root.transform;
            root.GetComponent<Image>().raycastTarget = false;
            Header(root.transform);
            var body = Panel(root.transform, "Body", new Color(0, 0, 0, 0), new Vector2(.025f, .075f), new Vector2(.975f, .88f));
            body.GetComponent<Image>().raycastTarget = false;
            content = body.GetComponent<RectTransform>();
            statusText = Label(root.transform, "Status", "", 17, TextAnchor.MiddleLeft, new Color(.45f, .82f, .9f));
            Anchor(statusText.rectTransform, new Vector2(.035f, .012f), new Vector2(.965f, .062f));
        }

        void Header(Transform parent)
        {
            var bar = Panel(parent, "Top Status Bar", new Color(.018f, .06f, .105f, .99f), new Vector2(0, .88f), Vector2.one);
            headerRoot = bar.transform;
            var stripe = Panel(bar.transform, "Cyan Keyline", new Color(.18f, .72f, .85f, .9f), new Vector2(0, 0), new Vector2(1, .025f));
            var menu = Button(bar.transform, "Menu", () => Show("base"), new Vector2(.008f, .54f), new Vector2(.047f, .95f));
            SetButtonSkin(menu, "button_tab");
            menu.GetComponentInChildren<Text>().text = "☰";
            var commanderPortrait = SpriteImage(bar.transform, "Commander Portrait", "Portraits/portrait_optimus_gs_large",
                new Vector2(.053f, .52f), new Vector2(.083f, .96f), true);
            if (commanderPortrait != null) commanderPortrait.raycastTarget = false;
            LabelAt(bar.transform, "Commander", "COMMANDER  ·  LV 2", 14, TextAnchor.MiddleLeft, new Color(.77f, .87f, .92f), new Vector2(.09f, .62f), new Vector2(.31f, .92f));
            var xpTrack = MakeImage(bar.transform, "Commander XP Track", new Color(.08f, .13f, .18f, .95f), new Vector2(.09f, .53f), new Vector2(.31f, .59f));
            var xpFill = MakeImage(xpTrack.transform, "Commander XP", new Color(.92f, .67f, .12f, 1f), Vector2.zero, new Vector2(.67f, 1f));
            xpFill.raycastTarget = xpTrack.raycastTarget = false;
            HeaderResource(bar.transform, "PvE Energy", "UI/energy_pve", "100/100", .62f, .12f);
            HeaderResource(bar.transform, "Energon", "UI/soft_currency", "7,800", .75f, .09f);
            HeaderResource(bar.transform, "Premium Currency", "UI/hard_currency", "99", .89f, .085f);
            string[] tabs = { "BASE", "BOTS", "INVENTORY", "FIGHT", "ALLIANCE", "CRYSTALS", "STORE" };
            Action[] actions = { () => Show("base"), () => Show("roster"), () => Show("inventory"), () => Show("fightmode"), () => Show("story"), () => Show("roster"), () => Show("roster") };
            var navNormal = Resources.Load<Sprite>("StoryPort/UI/global_nav_button");
            var centerNormal = Resources.Load<Sprite>("StoryPort/UI/global_nav_center");
            float left = .004f;
            float width = .1421f;
            for (int i = 0; i < tabs.Length; i++)
            {
                int index = i;
                float x = left + i * width;
                var button = Button(bar.transform, tabs[i], actions[i], new Vector2(x, .06f), new Vector2(x + width - .006f, .49f));
                var image = button.GetComponent<Image>();
                image.sprite = index == 3 ? (centerNormal != null ? centerNormal : navNormal) : navNormal;
                image.type = Image.Type.Simple;
                image.color = Color.white;
                button.GetComponentInChildren<Text>().fontSize = 14;
                button.GetComponentInChildren<Text>().fontStyle = FontStyle.Bold;
            }
        }

        void HeaderResource(Transform parent, string name, string spriteName, string value, float x, float width)
        {
            var icon = SpriteImage(parent, name + " Icon", spriteName, new Vector2(x, .57f), new Vector2(x + .04f, .94f), true);
            if (icon != null) icon.preserveAspect = true;
            LabelAt(parent, name + " Value", value, 15, TextAnchor.MiddleLeft, new Color(.91f, .94f, .97f),
                new Vector2(x + .04f, .56f), new Vector2(x + width, .94f));
        }

        void UpdateHeaderState(string next)
        {
            if (headerRoot == null) return;
            bool showNav = next == "base" || next == "fightmode" || next == "roster" || next == "inventory";
            headerRoot.GetComponent<RectTransform>().anchorMin = new Vector2(0, showNav ? .88f : .93f);
            int selected = next == "base" ? 0 : next == "roster" || next == "squad" ? 1 : next == "inventory" ? 2 :
                next == "fightmode" || next == "story" || next == "chapter" || next == "map" || next == "fight" || next == "victory" || next == "defeat" ? 3 : -1;
            string[] tabs = { "BASE", "BOTS", "INVENTORY", "FIGHT", "ALLIANCE", "CRYSTALS", "STORE" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var button = headerRoot.Find(tabs[i]);
                if (button == null) continue;
                button.gameObject.SetActive(showNav);
                string resource = i == 3
                    ? (selected == i ? "global_nav_center_active" : "global_nav_center")
                    : (selected == i ? "global_nav_button_active" : "global_nav_button");
                var sprite = Resources.Load<Sprite>("StoryPort/UI/" + resource);
                if (sprite != null) button.GetComponent<Image>().sprite = sprite;
            }
        }

        void Show(string next)
        {
            Time.timeScale = 1f;
            paused = false;
            if (pauseOverlay != null) Destroy(pauseOverlay);
            pauseOverlay = null;
            screen = next;
            notice = "";
            if (backgroundArt != null) Destroy(backgroundArt);
            backgroundArt = null;
            if (headerRoot != null) headerRoot.gameObject.SetActive(next != "title" && next != "loading" &&
                next != "fight" && next != "victory" && next != "defeat" && next != "dialogue" && next != "complete");
            if (content == null) return;
            foreach (Transform child in content) Destroy(child.gameObject);
            if (next != "fight" && next != "victory" && next != "defeat") DestroyWorld();
            if (next == "title") TitleScreen();
            else if (next == "loading") LoadingScreen();
            else if (next == "base") BaseScreen();
            else if (next == "fightmode") FightModeScreen();
            else if (next == "story") StoryScreen();
            else if (next == "chapter") ChapterScreen();
            else if (next == "map") MapScreen();
            else if (next == "squad") SquadScreen();
            else if (next == "fight") FightScreen();
            else if (next == "victory") ResultScreen(true);
            else if (next == "defeat") ResultScreen(false);
            else if (next == "roster") RosterScreen();
            else if (next == "inventory") InventoryScreen();
            else if (next == "dialogue") DialogueScreen();
            else if (next == "complete") MissionCompleteScreen();
            UpdateHeaderState(next);
            PlayScreenMusic(next);
        }

        // Music per screen, from the 9.2 music set.
        void PlayScreenMusic(string next)
        {
            if (audioPlayer == null) return;
            switch (next)
            {
                case "base": audioPlayer.Music("base_ambience"); break;
                case "squad": audioPlayer.Music("music_prefight"); break;
                case "fight": audioPlayer.Music("music_fight_loop"); break;
                case "dialogue": break;
                case "complete": audioPlayer.Music("music_postfight_win", false); break;
                case "victory": audioPlayer.Music("music_postfight_win", false); break;
                case "defeat": audioPlayer.Music("music_postfight_lose", false); break;
                default: audioPlayer.Music("music_questboard_loop"); break;
            }
        }

        void Cue(string clip, float volume = .8f) { if (audioPlayer != null) audioPlayer.Sfx(clip, volume); }

        void Combat(string botKey, string kind, float volume = .9f) { if (audioPlayer != null) audioPlayer.Combat(botKey, kind, volume); }

        static int AttackStep(string state)
        {
            if (state.StartsWith("Special", StringComparison.Ordinal)) return 6;
            if (state.StartsWith("Medium", StringComparison.Ordinal)) return 4;
            int step;
            return int.TryParse(state.Substring(state.Length - 1), out step) ? Mathf.Clamp(step, 1, 3) : 1;
        }

        void TitleScreen()
        {
            Backdrop("UI/title_background");
            var logo = SpriteImage(content, "Title Logo", "UI/tff_logo_en", new Vector2(.28f, .61f), new Vector2(.72f, .91f), false);
            if (logo != null) logo.preserveAspect = true;
            ActionButton("TAP TO START", "", () => { Show("loading"); StartCoroutine(LoadBase()); }, .37f, .24f, .26f, .14f, true);
        }

        IEnumerator LoadBase()
        {
            yield return new WaitForSeconds(.65f);
            Show("base");
        }

        void LoadingScreen()
        {
            // Black loading page with the bot hexagon collage, a tip line and a
            // LOADING… mark at the bottom right, as in the footage.
            var black = Panel(uiRoot != null ? uiRoot : content, "Loading Black", Color.black, Vector2.zero, Vector2.one);
            black.transform.SetAsFirstSibling();
            black.GetComponent<Image>().raycastTarget = false;
            backgroundArt = black;
            var collage = SpriteImage(content, "Loading Collage", "UI/bot_roster", new Vector2(.3f, .3f), new Vector2(.7f, 1.05f), true);
            if (collage != null) collage.raycastTarget = false;
            LabelAt(content, "Loading Tip", "Bots carry their Forge XP forward. The higher their Forge Level, the more Forge XP they provide.", 14,
                TextAnchor.MiddleCenter, new Color(.85f, .88f, .92f), new Vector2(.12f, .2f), new Vector2(.88f, .27f));
            var spinner = SpriteImage(content, "Loading Mark", "UI/icon_loading", new Vector2(.9f, .06f), new Vector2(.965f, .17f), true);
            if (spinner != null) spinner.raycastTarget = false;
            LabelAt(content, "Loading Label", "LOADING...", 11, TextAnchor.MiddleCenter, Color.white, new Vector2(.88f, .02f), new Vector2(.985f, .07f));
            StartCoroutine(AnimateLoading());
        }

        IEnumerator AnimateLoading()
        {
            yield return new WaitForSeconds(1.15f);
            if (screen == "loading") Show("base");
        }

        void BaseScreen()
        {
            var baseWorld = SpawnWorld("Base", "PrimordialBase", Vector3.zero, Vector3.zero, .012f);
            if (baseWorld != null)
                foreach (var renderer in baseWorld.GetComponentsInChildren<Renderer>(true))
                {
                    // The blank terrain plane renders as a white rim and the fringe as icy
                    // blue; the footage's surroundings are dark warm rock.
                    if (renderer.name == "BlankTerrain") renderer.enabled = false;
                    else if (renderer.name.StartsWith("qb_primordial_terrainblend_base_nograss", StringComparison.Ordinal) && renderer.material.HasProperty("_base_col"))
                        renderer.material.SetColor("_base_col", new Color(.6f, .52f, .46f, 1f));
                    else if (renderer.name == "qb_terrain_fringe" && renderer.material.HasProperty("_base_col"))
                        renderer.material.SetColor("_base_col", new Color(.32f, .25f, .2f, 1f));
                }
            FrameWorld(baseWorld, 1.3f);
            if (baseWorld != null) StartCoroutine(LoadBaseBuildings(baseWorld.transform));
        }

        IEnumerator LoadBaseBuildings(Transform baseRoot)
        {
            string response = "";
            yield return StartCoroutine(Get("/base/active", json => response = json));
            if (baseRoot == null || string.IsNullOrEmpty(response)) yield break;
            PlaceBaseBuildings(baseRoot, response);
        }

        void PlaceBaseBuildings(Transform baseRoot, string response)
        {
            var buildings = Regex.Matches(response,
                "\\\"id\\\"\\s*:\\s*\\\"(bldg_[^\\\"]+)\\\"\\s*,\\s*\\\"key\\\"\\s*:\\s*\\\"sock_(\\d+)_(\\d+)\\\"");
            foreach (Match building in buildings)
            {
                string buildingId = building.Groups[1].Value;
                string resource = BuildingResource(buildingId);
                if (string.IsNullOrEmpty(resource)) continue;
                var prefab = Resources.Load<GameObject>("StoryPort/Buildings/" + resource);
                if (prefab == null)
                {
                    Debug.LogWarning("StoryPort missing placed base building: " + resource);
                    continue;
                }

                int x = int.Parse(building.Groups[2].Value);
                int y = int.Parse(building.Groups[3].Value);
                var instance = Instantiate(prefab, baseRoot, false);
                instance.name = buildingId + " at sock_" + x + "_" + y;
                // The server exposes a five by five base socket grid. Its live
                // buildings occupy a compact cross around the command centre.
                // The source prefab and base use the same world scale.
                var layout = Tune("SP_BLDG", new[] { 320f, 1.9f });
                instance.transform.localPosition = new Vector3((x - 2) * layout[0], 0f, (2 - y) * layout[0]);
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one * layout[1];
                // The beam columns are additive glow meshes; opaque they read as white slabs.
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                    if (renderer.name.IndexOf("beam_column", StringComparison.OrdinalIgnoreCase) >= 0) renderer.enabled = false;
            }
            FrameBaseCrater(baseRoot);
        }

        // The footage keeps the camera low inside the crater with the command tower
        // at the centre, rather than showing the whole terrain tile.
        void FrameBaseCrater(Transform baseRoot)
        {
            var camera = Camera.main;
            if (camera == null) return;
            var centre = baseRoot.Find("bldg_battle_centre at sock_2_2");
            if (centre == null) { FrameWorld(baseRoot.gameObject, 1.3f); return; }
            var v = Tune("SP_BASECAM", new[] { 0f, 5.4f, -8.6f, 0f, 1.1f, 0f, 42f });
            var target = centre.position + new Vector3(v[3], v[4], v[5]);
            camera.fieldOfView = v[6];
            // Moody warm grade like the footage: dim ambient, low warm key light.
            var key = FindObjectOfType<Light>();
            if (key != null) { key.color = new Color(1f, .82f, .62f); key.intensity = 1.05f; key.transform.rotation = Quaternion.Euler(38, -35, 0); }
            RenderSettings.ambientLight = new Color(.34f, .33f, .38f);
            camera.backgroundColor = new Color(.1f, .085f, .09f);
            camera.transform.position = centre.position + new Vector3(v[0], v[1], v[2]);
            camera.transform.LookAt(target);
        }

        string BuildingResource(string id)
        {
            switch (id)
            {
                case "bldg_battle_centre": return "battle_centre";
                case "bldg_away_team": return "away_team";
                case "bldg_alliance_help": return "alliance_help";
                case "bldg_crystal_free": return "crystal_free";
                case "bldg_crystal_daily": return "crystal_daily";
                default: return "";
            }
        }

        void FightModeScreen()
        {
            LabelAt(content, "Fight Mode Header", "SELECT A FIGHT MODE", 27, TextAnchor.MiddleCenter, Color.white, new Vector2(.2f, .9f), new Vector2(.8f, .99f));

            string[] names = { "STORY", "RAIDS", "ALLIANCE MISSIONS", "SPECIAL", "DAILY CLASS", "ARENAS" };
            string[] descriptions =
            {
                "ACT I · CHAPTER 1 · MISSION 1",
                "Explore and attack rival bases",
                "Fight alongside your alliance",
                "Limited-time missions",
                "Earn daily class rewards",
                "Battle other commanders"
            };
            string[] artNames =
            {
                "fightstoryimglrg_hd", "fightraidsimg_hd", "fightallianceimg_hd",
                "fighteventimglrg_hd", "fightl_dailymission_hd", "fightversusimg_hd"
            };

            for (int i = 0; i < names.Length; i++)
            {
                int mode = i;
                int column = i % 3;
                int row = i / 3;
                float x = .035f + column * .315f;
                float y = row == 0 ? .475f : .095f;
                var card = Button(content, "Fight Mode " + names[i], () =>
                {
                    if (mode == 0) Show("story");
                    else SetNotice("This mode is not connected to the local server campaign.");
                }, new Vector2(x, y), new Vector2(x + .295f, y + .34f));

                var art = SpriteImage(card.transform, "Mode Artwork", "UI/" + artNames[i], Vector2.zero, Vector2.one, false);
                if (art != null)
                {
                    art.raycastTarget = false;
                    art.transform.SetAsFirstSibling();
                }

                var shade = Panel(card.transform, "Mode Label Shade", new Color(.005f, .015f, .03f, .82f),
                    new Vector2(0f, 0f), new Vector2(1f, .34f));
                shade.GetComponent<Image>().raycastTarget = false;
                shade.transform.SetSiblingIndex(art != null ? 1 : 0);

                var label = card.GetComponentInChildren<Text>();
                label.text = names[i] + "\n<size=12>" + descriptions[i] + "</size>";
                label.fontSize = 17;
                label.alignment = TextAnchor.MiddleLeft;
                Anchor(label.rectTransform, new Vector2(.055f, .035f), new Vector2(.945f, .32f));
                if (art != null) card.targetGraphic = art;
            }
        }

        void StoryScreen()
        {
            TechBackdrop();
            var header = LabelAt(content, "Story Missions Header", "STORY MISSIONS", 25, TextAnchor.MiddleCenter, Color.white,
                new Vector2(.28f, .9f), new Vector2(.72f, 1f));
            header.fontStyle = FontStyle.Bold;
            LabelAt(content, "Story Missions Subtitle", "Gain XP, Energon, and upgrade materials while unraveling the mysteries of New Quintessa!", 13,
                TextAnchor.MiddleCenter, Color.white, new Vector2(.15f, .84f), new Vector2(.85f, .9f));

            // Selected act at the left, the other acts as narrow cards at the right, as in the footage.
            StoryActBanner(actIndex, new Vector2(.004f, .04f), new Vector2(.155f, .8f), true);
            var others = new List<int>();
            for (int i = 0; i < ActQids.Length; i++) if (i != actIndex) others.Add(i);
            for (int i = 0; i < others.Count; i++)
                StoryActBanner(others[i], new Vector2(.7f + i * .152f, .04f), new Vector2(.848f + i * .152f, .8f), false);

            var chapter = Button(content, "Chapter 1", OpenStoryMap, new Vector2(.165f, .42f), new Vector2(.285f, .8f));
            chapter.GetComponent<Image>().sprite = null;
            chapter.GetComponent<Image>().color = new Color(.1f, .48f, .82f, 1f);
            chapter.GetComponentInChildren<Text>().text = "";
            LabelAt(chapter.transform, "Chapter Number", "Chapter 1", 12, TextAnchor.MiddleLeft, new Color(.85f, .95f, 1f),
                new Vector2(.08f, .78f), new Vector2(.95f, .95f)).raycastTarget = false;
            var chapterName = LabelAt(chapter.transform, "Chapter Name", ActTitles[actIndex], 14, TextAnchor.UpperLeft, Color.white,
                new Vector2(.08f, .5f), new Vector2(.95f, .78f));
            chapterName.fontStyle = FontStyle.Bold; chapterName.raycastTarget = false;
            LabelAt(chapter.transform, "Chapter Action", "ENTER STORY BOARD", 10, TextAnchor.MiddleLeft, new Color(.85f, .98f, 1f),
                new Vector2(.08f, .3f), new Vector2(.95f, .48f)).raycastTarget = false;
            var chapterBar = MakeImage(chapter.transform, "Chapter Track", new Color(.03f, .1f, .2f, .9f), new Vector2(.08f, .16f), new Vector2(.92f, .24f));
            chapterBar.raycastTarget = false;
            var bossPreview = Panel(content, "Chapter Boss Backdrop", new Color(.02f, .04f, .08f, .95f), new Vector2(.165f, .04f), new Vector2(.285f, .4f));
            bossPreview.GetComponent<Image>().raycastTarget = false;

            var routeLabels = ActNodeLabels[actIndex].Split('|');
            var routeBosses = new List<string>();
            if (currentQid == ActQids[actIndex] && storyMapNodes.Count > 0)
            {
                var serverLabels = new List<string>();
                foreach (var node in storyMapNodes)
                    if (!string.IsNullOrEmpty(node.boss))
                    {
                        serverLabels.Add(string.IsNullOrEmpty(node.label) ? DisplayName(node.boss) : node.label);
                        routeBosses.Add(node.boss);
                    }
                if (serverLabels.Count > 0) routeLabels = serverLabels.ToArray();
            }
            if (routeBosses.Count > 0)
            {
                var bossArt = SpriteImage(bossPreview.transform, "Chapter Boss", "Portraits/" + PortraitFor(routeBosses[routeBosses.Count - 1]),
                    new Vector2(.05f, .05f), new Vector2(.95f, .95f), true);
                if (bossArt != null) bossArt.raycastTarget = false;
            }
            for (int i = 0; i < routeLabels.Length; i++)
            {
                int column = i % 3, row = i / 3;
                float x = .295f + column * .134f;
                float y = row == 0 ? .42f : .04f;
                var tile = Button(content, "Route Tile " + (i + 1), OpenStoryMap, new Vector2(x, y), new Vector2(x + .126f, y + .38f));
                tile.GetComponent<Image>().sprite = null;
                tile.GetComponent<Image>().color = i == 0 ? new Color(.1f, .48f, .82f, 1f) : new Color(.08f, .3f, .5f, 1f);
                tile.GetComponentInChildren<Text>().text = "";
                if (i < routeBosses.Count)
                {
                    var bossPortrait = SpriteImage(tile.transform, "Encounter Boss", "Portraits/" + PortraitFor(routeBosses[i]), new Vector2(.31f, .44f), new Vector2(.69f, .88f), true);
                    if (bossPortrait != null) bossPortrait.raycastTarget = false;
                }
                var frame = SpriteImage(tile.transform, "Encounter Frame", "UI/frame_selection", new Vector2(.28f, .4f), new Vector2(.72f, .92f), true);
                if (frame != null) { frame.color = new Color(.75f, .8f, .85f, 1f); frame.raycastTarget = false; }
                var icon = SpriteImage(tile.transform, "Encounter Icon", "UI/boss_icon", new Vector2(.14f, .38f), new Vector2(.3f, .52f), true);
                if (icon != null) icon.raycastTarget = false;
                var label = LabelAt(tile.transform, "Encounter Name", (i + 1) + ". " + routeLabels[i], 12, TextAnchor.UpperLeft, new Color(.85f, .95f, 1f),
                    new Vector2(.1f, .12f), new Vector2(.95f, .38f));
                label.raycastTarget = false;
                if (i == 0)
                {
                    var bookmark = SpriteImage(tile.transform, "Current Marker", "UI/story_bookmark", new Vector2(-.04f, .8f), new Vector2(.2f, 1.06f), true);
                    if (bookmark != null) bookmark.raycastTarget = false;
                }
                var track = MakeImage(tile.transform, "Encounter Track", new Color(.03f, .1f, .2f, .9f), new Vector2(.1f, .04f), new Vector2(.9f, .09f));
                track.raycastTarget = false;
            }
            // Empty encounter slots continue the grid downwards.
            for (int i = routeLabels.Length; i < 6; i++)
            {
                int column = i % 3, row = i / 3;
                float x = .295f + column * .134f;
                float y = row == 0 ? .42f : .04f;
                var empty = Panel(content, "Empty Slot " + (i + 1), new Color(.02f, .04f, .08f, .9f), new Vector2(x, y), new Vector2(x + .126f, y + .38f));
                empty.GetComponent<Image>().raycastTarget = false;
            }
            var back = Button(content, "Back to Fight Modes", () => Show("fightmode"),
                new Vector2(.004f, .9f), new Vector2(.06f, .995f));
            SetButtonSkin(back, "button_tab");
            back.GetComponentInChildren<Text>().text = "‹";
        }

        void ChapterScreen()
        {
            StoryScreen();
        }

        void StoryActBanner(int index, Vector2 min, Vector2 max, bool selected)
        {
            var banner = Button(content, "Act Banner " + (index + 1), () => { actIndex = index; Show("story"); }, min, max);
            banner.GetComponent<Image>().color = new Color(.02f, .05f, .09f, 1f);
            string[] artworkNames = { "UI/planet_landscape", "UI/starscream_fight", "UI/fightstoryimglrg_hd" };
            var artwork = SpriteImage(banner.transform, "9.2 Story Art", artworkNames[index], Vector2.zero, Vector2.one, false);
            if (artwork != null) { artwork.raycastTarget = false; artwork.transform.SetAsFirstSibling(); }
            var shade = Panel(banner.transform, "Banner Shade", new Color(.008f, .018f, .038f, .3f),
                new Vector2(0, .25f), new Vector2(1, 1f));
            shade.GetComponent<Image>().raycastTarget = false;
            LabelAt(banner.transform, "Act Number", "ACT " + Roman(index + 1), 11, TextAnchor.MiddleLeft, Color.white,
                new Vector2(.08f, .88f), new Vector2(.93f, .97f)).raycastTarget = false;
            LabelAt(banner.transform, "Act Description", ActDescriptions[index], 10, TextAnchor.UpperLeft,
                new Color(.9f, .93f, .96f), new Vector2(.08f, .5f), new Vector2(.93f, .88f)).raycastTarget = false;
            var track = MakeImage(banner.transform, "Act Track", new Color(.3f, .75f, .32f, 1f), new Vector2(.06f, .155f), new Vector2(.94f, .185f));
            track.raycastTarget = false;
            var play = MakeImage(banner.transform, "Act Play", new Color(.09f, .5f, .2f, 1f), new Vector2(.06f, .03f), new Vector2(.94f, .14f));
            play.raycastTarget = false;
            LabelAt(play.transform, "Act Play Label", selected ? "ENTER" : "SELECT", 13, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.one).raycastTarget = false;
            banner.GetComponentInChildren<Text>().text = "";
        }

        void MapScreen()
        {
            var board = BuildStoryBoard();
            FrameStoryBoard(board);
            DrawHexMapField();
            SectionTitle("ACT " + Roman(actIndex + 1) + "  ·  " + ActTitles[actIndex], "Select an encounter to continue the campaign");
            DrawBoardNodes();
            ActionButton("SQUAD", "Select the team for the next fight", () => { squadForStory = true; Show("squad"); }, .05f, .08f, .2f, .16f, false);
            var back = Button(content, "Back to Story", () => Show("story"), new Vector2(.004f, .935f), new Vector2(.06f, .995f));
            SetButtonSkin(back, "button_tab");
            back.GetComponentInChildren<Text>().text = "‹";
        }

        void DrawHexMapField()
        {
            var backdrop = Panel(content, "Story Board Dimming", new Color(.008f, .018f, .043f, .08f), Vector2.zero, Vector2.one);
            backdrop.transform.SetAsFirstSibling();
        }

        GameObject BuildStoryBoard()
        {
            storyNodeWorldPositions.Clear();
            var board = new GameObject("StoryPort World · 9.2 Story Terrain Board");
            worldRoots.Add(board);
            var terrainPrefab = Resources.Load<GameObject>("StoryPort/StoryBoard/TerrainHex");
            var terrainPrefab02 = Resources.Load<GameObject>("StoryPort/StoryBoard/TerrainHex02") ?? terrainPrefab;
            var terrainPrefab03 = Resources.Load<GameObject>("StoryPort/StoryBoard/TerrainHex03") ?? terrainPrefab;
            var terrainRunPrefab = Resources.Load<GameObject>("StoryPort/StoryBoard/TerrainRun");
            var terrainWingPrefab = Resources.Load<GameObject>("StoryPort/StoryBoard/TerrainWing");
            var routeNodePrefab = Resources.Load<GameObject>("StoryPort/StoryBoard/QuestHexTile");
            if ((terrainRunPrefab == null && terrainPrefab == null) || routeNodePrefab == null)
            {
                Debug.LogError("StoryPort missing extracted 9.2 terrain or quest-node art");
                SetNotice("Converted 9.2 story-board terrain is missing");
                return board;
            }

            // The 9.2 one-cell pieces are 20m terrain modules. Their intended
            // board-scale ground is already authored as a continuous 3x9
            // primordial landmass, so use that asset as the story map instead
            // of stamping dozens of overlapping rubble modules.
            GameObject terrainRun = null;
            float stitchedGroundY = -.35f;
            // The converted 3x9 landmass is a real 180m board strip. Keep it
            // for maps large enough to use that footprint; small server maps
            // should use the original 1x1 modules at their actual tile cells.
            if (terrainRunPrefab != null && storyMapDimension >= 9)
            {
                terrainRun = Instantiate(terrainRunPrefab, Vector3.zero, Quaternion.Euler(0f, 90f, 0f), board.transform);
                terrainRun.name = "9.2 Primordial 3x9 Story Landmass";
                if (terrainWingPrefab != null)
                {
                    foreach (var offset in new[] { -60f, 60f })
                    {
                        var wing = Instantiate(terrainWingPrefab, Vector3.zero, Quaternion.Euler(0f, 90f, 0f), board.transform);
                        wing.name = "9.2 Primordial 3x9 Story Terrain Wing " + (offset < 0f ? "Left" : "Right");
                        wing.transform.localPosition = new Vector3(offset, 0f, 0f);
                        // The 3x9_02 edge cliffs are encounter-stage scenery,
                        // not walkable board surface. Keep its converted ground
                        // mesh and texture as the side terrain, while the central
                        // 3x9_01 piece supplies the route's smaller rock detail.
                        foreach (var renderer in wing.GetComponentsInChildren<Renderer>(true))
                            if (renderer.name != "BlankTerrain") renderer.enabled = false;
                    }
                }
                Bounds groundBounds = new Bounds(board.transform.position, Vector3.zero);
                bool foundGround = false;
                foreach (var collider in board.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var filter in board.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null || filter.name != "BlankTerrain") continue;
                    var meshCollider = filter.GetComponent<MeshCollider>();
                    if (meshCollider == null) meshCollider = filter.gameObject.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = filter.sharedMesh;
                    meshCollider.enabled = true;
                }
                foreach (var renderer in board.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled || renderer.name != "BlankTerrain") continue;
                    if (!foundGround) { groundBounds = renderer.bounds; foundGround = true; }
                    else groundBounds.Encapsulate(renderer.bounds);
                }
                if (foundGround)
                {
                    board.transform.position -= new Vector3(groundBounds.center.x, 0f, groundBounds.center.z);
                    stitchedGroundY = groundBounds.min.y - .25f;
                }
                Physics.SyncTransforms();
                Debug.Log("StoryPort placed the converted 9.2 3x9 primordial landmass and terrain wings as the story-board terrain");
            }

            // Keep the server grid's 20m coordinates intact so routes preserve
            // authored branches instead of stretching to fit the landmass.
            int dimension = Mathf.Max(1, storyMapDimension);
            if (storyMapDimension >= 9)
                BuildPrimordialGroundGrid(board, dimension, stitchedGroundY);

            // A route usually visits only a few cells, but the original board
            // has authored terrain across the whole playable grid. Stamp the
            // converted 9.2 1x1 modules into every cell and let their terrain
            // meshes form the surface instead of stretching one flat texture
            // over the empty cells.
            if (storyMapDimension < 9 && terrainPrefab != null)
            {
                for (int tileY = 0; tileY < dimension; tileY++)
                {
                    for (int tileX = 0; tileX < dimension; tileX++)
                    {
                        int variant = (tileX * 7 + tileY * 3) % 3;
                        var selectedTerrain = variant == 1 ? terrainPrefab02 : variant == 2 ? terrainPrefab03 : terrainPrefab;
                        var localTileCenter = StoryMapTileCenter(tileX, tileY, dimension);
                        var tilePosition = board.transform.TransformPoint(new Vector3(localTileCenter.x, stitchedGroundY, localTileCenter.z));
                        var terrain = Instantiate(selectedTerrain, tilePosition, Quaternion.identity, board.transform);
                        terrain.name = "9.2 Primordial 1x1 Story Tile " + tileX + "-" + tileY;
                        foreach (var collider in terrain.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                        foreach (var light in terrain.GetComponentsInChildren<Light>(true)) light.enabled = false;
                        foreach (var filter in terrain.GetComponentsInChildren<MeshFilter>(true))
                        {
                            if (filter.sharedMesh == null || filter.name != "BlankTerrain") continue;
                            var meshCollider = filter.GetComponent<MeshCollider>();
                            if (meshCollider == null) meshCollider = filter.gameObject.AddComponent<MeshCollider>();
                            meshCollider.sharedMesh = filter.sharedMesh;
                            meshCollider.enabled = true;
                        }
                    }
                }
            }

            foreach (var node in storyMapNodes)
            {
                Vector3 localTileCenter = StoryMapTileCenter(node.x, node.y, dimension);
                Vector3 position = board.transform.TransformPoint(localTileCenter);
                RaycastHit terrainHit;
                if (Physics.Raycast(position + Vector3.up * 1000f, Vector3.down, out terrainHit, 2000f))
                    position = terrainHit.point + Vector3.up * .65f;
                else
                    position = board.transform.TransformPoint(new Vector3(localTileCenter.x, stitchedGroundY + .65f, localTileCenter.z));
                storyNodeWorldPositions[new Vector2Int(node.x, node.y)] = position;
                var routeMarker = Instantiate(routeNodePrefab, position, Quaternion.identity, board.transform);
                routeMarker.name = "9.2 Server Quest Node " + node.x + "-" + node.y;
                foreach (var collider in routeMarker.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var light in routeMarker.GetComponentsInChildren<Light>(true)) light.enabled = false;
                Color marker = node.isFinal ? new Color(.96f, .52f, .46f) :
                    node.boss.Length > 0 ? new Color(.64f, .92f, .94f) : Color.white;
                foreach (var renderer in routeMarker.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled) continue;
                    var material = renderer.material;
                    if (material.HasProperty("_base_col")) material.SetColor("_base_col", marker);
                    else if (material.HasProperty("_Color")) material.SetColor("_Color", marker);
                }
            }
            BuildStoryRouteRibbons(board);
            Debug.Log("StoryPort built the 9.2 Primordial terrain board with " + storyMapNodes.Count + " server-authored route nodes for act " + (actIndex + 1));
            return board;
        }

        void FrameStoryBoard(GameObject board)
        {
            if (board == null) return;
            Bounds bounds = new Bounds(board.transform.position, Vector3.zero);
            bool found = false;
            foreach (var renderer in board.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            var camera = Camera.main;
            if (!found || camera == null) return;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.018f, .025f, .052f);
            camera.fieldOfView = 36f;
            float aspect = Mathf.Max(.5f, (float)Screen.width / Screen.height);
            const float elevation = 55f * Mathf.Deg2Rad;
            float projectedDepth = bounds.size.z * Mathf.Sin(elevation) + bounds.size.y * Mathf.Cos(elevation);
            float viewHeight = Mathf.Max(projectedDepth, bounds.size.x / aspect) * 1.2f;
            float distance = viewHeight / (2f * Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad));
            var target = new Vector3(bounds.center.x, bounds.center.y, bounds.center.z);
            camera.transform.position = target + new Vector3(0f, Mathf.Sin(elevation), -Mathf.Cos(elevation)) * distance;
            camera.transform.LookAt(target);
            camera.farClipPlane = Mathf.Max(4000f, distance + bounds.size.magnitude * 2f);
        }

        Vector2 StoryBoardContentPoint(Vector3 world)
        {
            var camera = Camera.main;
            if (camera == null) return new Vector2(.5f, .5f);
            var viewport = camera.WorldToViewportPoint(world);
            return new Vector2((viewport.x - .025f) / .95f, (viewport.y - .075f) / .805f);
        }

        static Vector3 StoryMapTileCenter(int x, int y, int dimension)
        {
            const float tileSize = 20f;
            float mapOrigin = -dimension * tileSize * .5f;
            return new Vector3(mapOrigin + x * tileSize + tileSize * .5f, 0f,
                mapOrigin + (dimension - 1 - y) * tileSize + tileSize * .5f);
        }

        void BuildPrimordialGroundGrid(GameObject board, int dimension, float groundY)
        {
            var material = Resources.Load<Material>("StoryPort/StoryBoard/PrimordialGround");
            if (material == null)
            {
                Debug.LogWarning("StoryPort missing converted primordial ground material");
                return;
            }
            const float tileSize = 20f;
            int side = dimension + 1;
            var vertices = new Vector3[side * side];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[dimension * dimension * 6];
            float halfMap = dimension * tileSize * .5f;
            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    int index = z * side + x;
                    // The server grid uses the game's 20m tile spacing,
                    // centered around the origin.
                    vertices[index] = new Vector3(-halfMap + x * tileSize, groundY, -halfMap + z * tileSize);
                    uv[index] = new Vector2(x / (float)dimension, z / (float)dimension);
                }
            }
            int triangle = 0;
            for (int z = 0; z < dimension; z++)
            {
                for (int x = 0; x < dimension; x++)
                {
                    int lowerLeft = z * side + x;
                    int upperLeft = lowerLeft + side;
                    int lowerRight = lowerLeft + 1;
                    int upperRight = upperLeft + 1;
                    triangles[triangle++] = lowerLeft;
                    triangles[triangle++] = upperLeft;
                    triangles[triangle++] = upperRight;
                    triangles[triangle++] = lowerLeft;
                    triangles[triangle++] = upperRight;
                    triangles[triangle++] = lowerRight;
                }
            }
            var mesh = new Mesh { name = "StoryPort 20m stitched terrain grid" };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var surface = new GameObject("Primordial 20m terrain tiles", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            surface.transform.SetParent(board.transform, false);
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            surface.GetComponent<MeshRenderer>().sharedMaterial = material;
            surface.GetComponent<MeshCollider>().sharedMesh = mesh;
        }

        void BuildStoryRouteRibbons(GameObject board)
        {
            var routeShader = Shader.Find("Unlit/Color");
            if (routeShader == null) routeShader = Shader.Find("Sprites/Default");
            if (routeShader == null) return;
            var material = new Material(routeShader);
            material.color = new Color(.25f, .76f, .92f, .9f);
            var drawn = new HashSet<string>();
            foreach (var node in storyMapNodes)
            {
                var from = new Vector2Int(node.x, node.y);
                Vector3 fromWorld;
                if (!storyNodeWorldPositions.TryGetValue(from, out fromWorld)) continue;
                foreach (var link in node.links)
                {
                    var to = new Vector2Int(link.x, link.y);
                    Vector3 toWorld;
                    if (!storyNodeWorldPositions.TryGetValue(to, out toWorld)) continue;
                    string key = from.x < to.x || (from.x == to.x && from.y < to.y)
                        ? from + ":" + to : to + ":" + from;
                    if (!drawn.Add(key)) continue;
                    Vector3 a = board.transform.InverseTransformPoint(fromWorld);
                    Vector3 b = board.transform.InverseTransformPoint(toWorld);
                    Vector3 direction = (b - a).normalized;
                    Vector3 side = Vector3.Cross(direction, Vector3.up).normalized * .75f;
                    a.y += .18f;
                    b.y += .18f;
                    var mesh = new Mesh { name = "Story path " + key };
                    mesh.vertices = new[] { a - side, a + side, b + side, b - side };
                    mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    mesh.RecalculateNormals();
                    var segment = new GameObject("3D Story Route " + key, typeof(MeshFilter), typeof(MeshRenderer));
                    segment.transform.SetParent(board.transform, false);
                    segment.GetComponent<MeshFilter>().sharedMesh = mesh;
                    segment.GetComponent<MeshRenderer>().sharedMaterial = material;
                }
            }
        }

        void DrawBoardNodes()
        {
            if (storyMapNodes.Count == 0) return;
            var positions = new Dictionary<Vector2Int, Vector2>();
            foreach (var node in storyMapNodes)
            {
                var key = new Vector2Int(node.x, node.y);
                Vector3 world;
                if (!storyNodeWorldPositions.TryGetValue(key, out world)) continue;
                positions[key] = StoryBoardContentPoint(world);
            }

            foreach (var node in storyMapNodes)
            {
                var coord = new Vector2Int(node.x, node.y);
                if (!positions.ContainsKey(coord)) continue;
                Vector2 point = positions[coord];
                bool encounter = !string.IsNullOrEmpty(node.boss);
                bool available = encounter && IsNextEncounter(node.x, node.y);
                float size = encounter ? .09f : .07f;
                var panel = Panel(content, "Server Quest Node " + node.x + "-" + node.y, Color.clear,
                    new Vector2(point.x - size * .5f, point.y - size * .63f),
                    new Vector2(point.x + size * .5f, point.y + size * .63f));
                if (encounter)
                {
                    var portrait = SpriteImage(panel.transform, "Server Boss Portrait", "Portraits/" + PortraitFor(node.boss), new Vector2(.08f, .16f), new Vector2(.92f, .94f), true);
                    if (portrait != null) { portrait.preserveAspect = true; portrait.raycastTarget = false; }
                }
                var frame = SpriteImage(panel.transform, encounter ? "Quest Boss Card Frame" : "Quest Route Cell", encounter ? "UI/bosscard_frame0" : "UI/hexagon_progress", Vector2.zero, Vector2.one, true);
                if (frame != null)
                {
                    frame.raycastTarget = false;
                    frame.color = available ? new Color(.54f, .94f, 1f, 1f) : new Color(.62f, .68f, .75f, .8f);
                }
                string label = string.IsNullOrEmpty(node.label) ? (encounter ? DisplayName(node.boss) : "ROUTE") : node.label;
                var nodeLabel = LabelAt(panel.transform, "Server Node Label", label.ToUpperInvariant(), encounter ? 12 : 10,
                    TextAnchor.MiddleCenter, Color.white, new Vector2(-.48f, -.36f), new Vector2(1.48f, .16f));
                nodeLabel.raycastTarget = false;
                var hit = panel.GetComponent<Image>();
                hit.color = Color.clear;
                if (encounter)
                {
                    var button = panel.AddComponent<Button>();
                    button.targetGraphic = hit;
                    button.transition = Selectable.Transition.None;
                    button.interactable = available;
                    int nodeX = node.x, nodeY = node.y;
                    button.onClick.AddListener(() => MoveToEncounter(nodeX, nodeY));
                }
            }
        }

        bool IsNextEncounter(int targetX, int targetY)
        {
            if (pendingEncounter && targetX == mapX && targetY == mapY) return true;
            var current = FindStoryMapNode(mapX, mapY);
            if (current == null) return false;
            foreach (var link in current.links)
                if (link.x == targetX && link.y == targetY) return true;
            return false;
        }

        StoryMapNode FindStoryMapNode(int x, int y)
        {
            return storyMapNodes.Find(node => node.x == x && node.y == y);
        }

        bool CurrentNodeIsFinal()
        {
            var node = FindStoryMapNode(mapX, mapY);
            return node != null && node.isFinal;
        }

        void MoveToEncounter(int targetX, int targetY)
        {
            if (!IsNextEncounter(targetX, targetY)) return;
            MoveStory(targetX - mapX, targetY - mapY);
        }

        void SquadScreen()
        {
            var camera = Camera.main;
            if (camera != null)
            {
                var cam = Tune("SP_SQUADCAM", new[] { 0f, 2.7f, -11.5f, 0f, 2.4f, 0f });
                camera.fieldOfView = 36;
                camera.transform.position = new Vector3(cam[0], cam[1], cam[2]);
                camera.transform.LookAt(new Vector3(cam[3], cam[4], cam[5]));
            }
            var selected = Mathf.Clamp(selectedBot, 0, rosterKeys.Length - 1);
            var player = SpawnBot(rosterKeys[selected], new Vector3(squadForStory ? -3.1f : 0f, 0, 0), 150f, .88f);
            if (player != null)
            {
                worldRoots.Add(player);
                PlayState(FindFightAnimator(player), "Idle");
            }
            if (squadForStory)
            {
                var opponent = SpawnBot(enemyKey, new Vector3(3.5f, 0, 0), 215f, .88f);
                if (opponent != null)
                {
                    worldRoots.Add(opponent);
                    PlayState(FindFightAnimator(opponent), "Idle");
                }
            }

            TechBackdrop();
            var title = LabelAt(content, "Bot Selection Title", squadForStory ? "SELECT YOUR BOT" : "BOT ROSTER", 26,
                TextAnchor.MiddleCenter, Color.white, new Vector2(.34f, .88f), new Vector2(.66f, .98f));
            title.fontStyle = FontStyle.Bold;
            // Team column down the left edge; tapping a portrait focuses that bot.
            for (int i = 0; i < rosterKeys.Length; i++)
            {
                int index = i;
                float y = .745f - i * .152f;
                var tile = Button(content, "Choose " + rosterNames[i], () => FocusBot(index),
                    new Vector2(.004f, y), new Vector2(.084f, y + .135f));
                SetButtonSkin(tile, i == selected ? "frame_selection" : "frame_button");
                tile.GetComponentInChildren<Text>().text = "";
                var portrait = SpriteImage(tile.transform, "Bot Portrait", "Portraits/" + PortraitFor(rosterKeys[i]),
                    new Vector2(.13f, .09f), new Vector2(.87f, .91f), true);
                if (portrait != null) portrait.raycastTarget = false;
                var teamBar = MakeImage(tile.transform, "Team Marker", squad.Contains(i) ? new Color(.2f, .7f, .91f) : new Color(.15f, .2f, .26f),
                    new Vector2(.1f, -.06f), new Vector2(.9f, .02f));
                teamBar.raycastTarget = false;
            }
            LabelAt(content, "Team Count", "TEAM  " + squad.Count + " / 3", 11, TextAnchor.MiddleLeft,
                new Color(.51f, .86f, .95f), new Vector2(.004f, .885f), new Vector2(.09f, .93f));

            // Names sit under each bot, health as a thin blue track.
            LabelAt(content, "Selected Bot Name", rosterNames[selected].ToUpperInvariant(), 20, TextAnchor.MiddleLeft,
                Color.white, new Vector2(.14f, .8f), new Vector2(.4f, .87f)).fontStyle = FontStyle.Bold;
            HealthBar(content, "Selected Bot Health", new Vector2(.14f, .78f), new Vector2(.4f, .795f), 1f,
                new Color(.2f, .7f, .91f));
            var teamButton = Button(content, "Team Selection", () => ToggleBot(selected),
                new Vector2(.42f, .04f), new Vector2(.58f, .11f));
            SetButtonSkin(teamButton, squad.Contains(selected) ? "button_tab_active" : "button_tab");
            teamButton.GetComponentInChildren<Text>().text = squad.Contains(selected) ? "－ REMOVE" : "＋ ADD";

            if (squadForStory)
            {
                var vs = LabelAt(content, "Versus", "VS", 34, TextAnchor.MiddleCenter, Color.white,
                    new Vector2(.44f, .47f), new Vector2(.56f, .58f));
                vs.fontStyle = FontStyle.BoldAndItalic;
                var match = MakeImage(content, "Matchup Bar", new Color(.4f, .8f, .3f), new Vector2(.46f, .455f), new Vector2(.54f, .47f));
                var middle = MakeImage(match.transform, "Matchup Warning", new Color(.95f, .75f, .15f), new Vector2(.35f, 0), new Vector2(.68f, 1));
                var hard = MakeImage(match.transform, "Matchup Danger", new Color(.9f, .25f, .2f), new Vector2(.68f, 0), new Vector2(1, 1));
                match.raycastTarget = middle.raycastTarget = hard.raycastTarget = false;
                LabelAt(content, "Opponent Name", enemyName.ToUpperInvariant(), 20, TextAnchor.MiddleRight,
                    Color.white, new Vector2(.66f, .8f), new Vector2(.92f, .87f)).fontStyle = FontStyle.Bold;
                HealthBar(content, "Opponent Health", new Vector2(.66f, .78f), new Vector2(.92f, .795f), 1f,
                    new Color(.2f, .7f, .91f));

                var repair = Button(content, "Repair", null, new Vector2(.06f, .02f), new Vector2(.2f, .095f));
                repair.GetComponentInChildren<Text>().text = "REPAIR";
                repair.interactable = false;
                var fight = Button(content, "Fight", SaveSquadAndBegin,
                    new Vector2(.82f, .02f), new Vector2(.995f, .105f));
                SetButtonSkin(fight, "button_main_glowing");
                fight.GetComponent<Image>().color = new Color(.22f, .83f, .28f, 1f);
                fight.GetComponentInChildren<Text>().text = !squad.Contains(selected) ? "ADD BOT FIRST" : pendingEncounter ? "FIGHT!" : "SAVE TEAM";
                var back = Button(content, "Back to Board", () => Show("map"),
                    new Vector2(.004f, .92f), new Vector2(.06f, .995f));
                SetButtonSkin(back, "button_tab");
                back.GetComponentInChildren<Text>().text = "‹";
            }
            else
            {
                ActionButton("SAVE ROSTER", "", SaveRoster, .8f, .02f, .19f, .1f, true);
                ActionButton("BACK TO BASE", "", () => Show("base"), .09f, .885f, .16f, .075f, false);
            }
        }

        // Dark blue-black technical panel backdrop used behind bot selection.
        void TechBackdrop()
        {
            const int w = 160, h = 90;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var dx = (x - w * .5f) / (w * .5f);
                    var dy = (y - h * .5f) / (h * .5f);
                    var vignette = Mathf.Clamp01(1f - (dx * dx * .5f + dy * dy * .7f));
                    var band = Mathf.Abs(Mathf.Repeat((x + y * 1.4f) / 22f, 1f) - .5f) < .03f ? .035f : 0f;
                    var shade = .02f + vignette * .085f + band;
                    texture.SetPixel(x, y, new Color(shade * .55f, shade * .8f, shade * 1.35f, 1f));
                }
            texture.Apply();
            // A camera-attached quad keeps the 3D bots visible in front of it; a UI
            // image would cover them.
            var camera = Camera.main;
            if (camera == null) return;
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Tech Backdrop";
            quad.transform.SetParent(camera.transform, false);
            // Size the quad to overfill the view at any aspect ratio (phones are ~2.2:1).
            float viewHeight = 2f * 60f * Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad) * 1.1f;
            float viewWidth = viewHeight * Mathf.Max(2.4f, Mathf.Max(camera.aspect, (float)Screen.width / Mathf.Max(1, Screen.height)));
            quad.transform.localPosition = new Vector3(0, 0, 60f);
            quad.transform.localScale = new Vector3(viewWidth, viewHeight, 1f);
            var collider = quad.GetComponent<Collider>();
            if (collider != null) { if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider); }
            var material = new Material(Shader.Find("Unlit/Texture"));
            texture.wrapMode = TextureWrapMode.Clamp;
            material.mainTexture = texture;
            quad.GetComponent<Renderer>().sharedMaterial = material;
            worldRoots.Add(quad);
        }

        void FightScreen()
        {
            if (playerActor == null) SpawnFightWorld();
            guarding = false;
            enemyBusy = false;
            playerBusy = false;
            queuedAttack = false;
            lightCombo = 0;
            comboHits = 0;
            lastPlayerHit = 0;
            lastEnemyDefense = -10f;
            nextEnemyTurn = Time.time + 2.8f;
            if (audioPlayer != null) { audioPlayer.Preload(playerKey); audioPlayer.Preload(enemyKey); }
            hitsLanded = hitsReceived = highestChain = 0;
            Cue("fight_start", .7f);
            var hud = Panel(content, "Fight HUD", new Color(0, 0, 0, 0), Vector2.zero, Vector2.one);
            hud.GetComponent<Image>().raycastTarget = false;
            // The fight HUD spans the full screen, as in the beta footage: hex portraits
            // in the top corners, slanted health bars, a hit counter on the left, two
            // hex touch zones at the bottom corners and a segmented special meter.
            var full = hud.GetComponent<RectTransform>();
            full.anchorMin = new Vector2(-.0263f, -.0932f); full.anchorMax = new Vector2(1.0263f, 1.1491f);
            full.offsetMin = full.offsetMax = Vector2.zero;
            FighterHud(hud.transform, "Player", playerKey, playerName, false, out playerHpText, out playerHpFill);
            FighterHud(hud.transform, "Enemy", enemyKey, enemyName, true, out enemyHpText, out enemyHpFill);
            // Ratings under the portraits use the server formula (health + attack) / 20.
            playerRatingText = LabelAt(hud.transform, "Player Rating", StoryPortCombatRules.Rating(playerMaxHealth, playerAttack).ToString(), 14,
                TextAnchor.MiddleCenter, Color.white, new Vector2(.004f, .79f), new Vector2(.08f, .84f));
            enemyRatingText = LabelAt(hud.transform, "Enemy Rating", StoryPortCombatRules.Rating(enemyMaxHealth, enemyAttack).ToString(), 14,
                TextAnchor.MiddleCenter, Color.white, new Vector2(.918f, .79f), new Vector2(.996f, .84f));
            playerRatingText.fontStyle = enemyRatingText.fontStyle = FontStyle.Bold;
            var pause = Button(hud.transform, "PAUSE", TogglePause, new Vector2(.47f, .915f), new Vector2(.53f, .99f));
            SetButtonSkin(pause, "button_tab");
            pause.GetComponentInChildren<Text>().text = "Ⅱ";
            pause.GetComponentInChildren<Text>().fontSize = 24;
            comboText = LabelAt(hud.transform, "Combo", "", 30, TextAnchor.MiddleLeft, Color.white, new Vector2(.012f, .5f), new Vector2(.2f, .64f));
            comboText.fontStyle = FontStyle.BoldAndItalic;
            comboText.raycastTarget = false;
            // Bottom corners as in the footage: the left hex is the special button and
            // glows green once a bar is charged; the right hex marks the attack side,
            // with the three special bars beside it.
            var specialButton = Button(hud.transform, "SPECIAL", SpecialAttack, new Vector2(.03f, .035f), new Vector2(.115f, .175f));
            specialButton.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            specialButtonLabel = specialButton.GetComponentInChildren<Text>();
            specialButtonLabel.text = "";
            specialHex = SpriteImage(specialButton.transform, "Special Hex", "UI/hexagon_progress", Vector2.zero, Vector2.one, true);
            if (specialHex != null) specialHex.raycastTarget = false;
            var attackZone = SpriteImage(hud.transform, "Attack Zone", "UI/hexagon_border", new Vector2(.885f, .035f), new Vector2(.97f, .175f), true);
            if (attackZone != null) { attackZone.color = new Color(.75f, .85f, .95f, .45f); attackZone.raycastTarget = false; }
            specialSegments = new Image[3];
            specialSegmentFills = new Image[3];
            for (var i = 0; i < 3; i++)
            {
                float x = .68f + i * .066f;
                var slot = MakeImage(hud.transform, "Special Bar " + (i + 1), new Color(.12f, .13f, .15f, .8f), new Vector2(x, .05f), new Vector2(x + .06f, .085f));
                slot.raycastTarget = false;
                var fill = MakeImage(slot.transform, "Special Bar Fill", new Color(1f, .86f, .1f, 1f), Vector2.zero, Vector2.one);
                fill.sprite = SlantSprite(false, false);
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Horizontal;
                fill.fillAmount = 0f;
                fill.raycastTarget = false;
                specialSegments[i] = slot;
                specialSegmentFills[i] = fill;
            }
            StartCoroutine(FightIntro(hud.transform));
            UpdateFightHud();
        }

        // Big "FIGHT!" call at the start of each fight; the enemy waits for it.
        IEnumerator FightIntro(Transform hud)
        {
            nextEnemyTurn = Time.time + 3.4f;
            var label = LabelAt(hud, "Fight Call", "FIGHT!", 64, TextAnchor.MiddleCenter, Color.white, new Vector2(.3f, .42f), new Vector2(.7f, .62f));
            label.fontStyle = FontStyle.BoldAndItalic;
            label.raycastTarget = false;
            label.gameObject.AddComponent<Outline>().effectColor = new Color(.05f, .25f, .45f, .9f);
            float t = 0f;
            while (t < 1.3f && label != null)
            {
                t += Time.deltaTime;
                label.transform.localScale = Vector3.one * Mathf.Lerp(1.6f, 1f, Mathf.Clamp01(t * 5f));
                label.color = new Color(1f, 1f, 1f, Mathf.Clamp01((1.3f - t) * 3f));
                yield return null;
            }
            if (label != null) Destroy(label.gameObject);
        }

        // One fighter's HUD cluster: hex portrait, name and a slanted health bar.
        void FighterHud(Transform hud, string prefix, string key, string displayName, bool mirrored, out Text percent, out Image fill)
        {
            float px0 = mirrored ? .925f : .012f;
            var portrait = SpriteImage(hud, prefix + " Portrait", "Portraits/" + PortraitFor(key), new Vector2(px0, .84f), new Vector2(px0 + .063f, .99f), true);
            if (portrait != null) { portrait.preserveAspect = true; portrait.raycastTarget = false; }
            var frame = SpriteImage(hud, prefix + " Portrait Frame", "UI/frame_hud_portrait", new Vector2(px0, .84f), new Vector2(px0 + .063f, .99f), true);
            if (frame != null) { frame.preserveAspect = true; frame.raycastTarget = false; }
            float x0 = mirrored ? .6f : .085f, x1 = mirrored ? .915f : .4f;
            var name = LabelAt(hud, prefix + " Name", displayName, 15, mirrored ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, Color.white, new Vector2(x0, .945f), new Vector2(x1, .99f));
            name.fontStyle = FontStyle.Bold;
            var rim = MakeImage(hud, prefix + " Health Frame", Color.white, new Vector2(x0, .885f), new Vector2(x1, .945f));
            rim.sprite = SlantSprite(true, mirrored); rim.color = new Color(.82f, .85f, .88f, 1f); rim.raycastTarget = false;
            var back = MakeImage(rim.transform, prefix + " Health Back", Color.white, new Vector2(.012f, .12f), new Vector2(.988f, .88f));
            back.sprite = SlantSprite(false, mirrored); back.color = new Color(.02f, .06f, .1f, .95f); back.raycastTarget = false;
            fill = MakeImage(back.transform, prefix + " Health Fill", new Color(.27f, .66f, .9f, 1f), Vector2.zero, Vector2.one);
            fill.sprite = SlantSprite(false, mirrored);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = mirrored ? 1 : 0;
            fill.raycastTarget = false;
            percent = LabelAt(rim.transform, prefix + " Health", "100%", 13, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.one);
            percent.fontStyle = FontStyle.Bold;
            percent.raycastTarget = false;
        }

        Sprite slantOutline, slantFillLeft, slantFillRight, slantOutlineRight;

        // Parallelogram bar shape (the HUD bars lean like the footage's), generated at
        // runtime so no game texture is required.
        Sprite SlantSprite(bool outline, bool mirrored)
        {
            var cached = outline ? (mirrored ? slantOutlineRight : slantOutline) : (mirrored ? slantFillRight : slantFillLeft);
            if (cached != null) return cached;
            const int w = 256, h = 32, lean = 10;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (var y = 0; y < h; y++)
            {
                var shift = (float)y / h * lean;
                if (mirrored) shift = lean - shift;
                for (var x = 0; x < w; x++)
                {
                    var inside = x >= shift && x <= w - lean + shift;
                    texture.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            }
            texture.Apply();
            var sprite = Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(.5f, .5f), 100f);
            if (outline) { if (mirrored) slantOutlineRight = sprite; else slantOutline = sprite; }
            else { if (mirrored) slantFillRight = sprite; else slantFillLeft = sprite; }
            return sprite;
        }

        void SpawnFightWorld()
        {
            ResetCamera();
            CreateChicagoSky();
            // Scale, offset and heading place the fighters on the ruined street by
            // the stage's main fight area, facing the brick towers as in the footage.
            var tune = Tune("SP_STAGE", new[] { .6f, -33.6f, 0f, 40f, 180f });
            var stage = SpawnWorld("Chicago Fight Stage", "ChicagoFightStage", Vector3.zero, new Vector3(0f, tune[4], 0f), tune[0]);
            if (stage != null)
            {
                stage.transform.localPosition += new Vector3(tune[1], tune[2], tune[3]);
                ApplyStoryPortMaterials(stage);
                foreach (var child in stage.GetComponentsInChildren<Transform>(true))
                    if (child.name == "Main Stage") child.gameObject.SetActive(false);
                var roadMaterial = Resources.Load<Material>("StoryPort/ChicagoRoad");
                foreach (var renderer in stage.GetComponentsInChildren<Renderer>(true))
                {
                    if (roadMaterial != null && renderer.name.IndexOf("trrn_road", StringComparison.OrdinalIgnoreCase) >= 0)
                        renderer.sharedMaterial = roadMaterial;
                }
            }
            playerActor = SpawnBot(playerKey, new Vector3(-2.55f, 0, -5.5f), 90f, .72f);
            enemyActor = SpawnBot(enemyKey, new Vector3(2.55f, 0, -5.5f), 270f, .72f);
            if (playerActor != null) worldRoots.Add(playerActor);
            if (enemyActor != null) worldRoots.Add(enemyActor);
            playerAnimator = FindFightAnimator(playerActor);
            enemyAnimator = FindFightAnimator(enemyActor);
            PlayState(playerAnimator, "Idle");
            PlayState(enemyAnimator, "Idle");
            nextEnemyTurn = Time.time + 2.8f;
        }

        void CreateChicagoSky()
        {
            var skyMaterial = Resources.Load<Material>("StoryPort/ChicagoDaySky");
            var camera = Camera.main;
            if (skyMaterial == null || camera == null) return;
            var sky = GameObject.CreatePrimitive(PrimitiveType.Quad);
            sky.name = "9.2 Chicago Day Sky";
            sky.transform.SetParent(camera.transform, false);
            // The texture's upper half runs from the sun-lit horizon (bottom) to the
            // zenith, so pin its bottom edge to the camera's horizon line.
            camera.backgroundColor = new Color(.62f, .5f, .32f); // dusk haze below the sky texture
            var key = FindObjectOfType<Light>();
            if (key != null) { key.color = new Color(1f, .86f, .68f); key.intensity = 1.25f; }
            RenderSettings.ambientLight = new Color(.5f, .5f, .55f);
            var pitch = camera.transform.eulerAngles.x;
            if (pitch > 180f) pitch -= 360f;
            const float distance = 100f, height = 46f;
            var horizon = distance * Mathf.Tan(pitch * Mathf.Deg2Rad);
            sky.transform.localPosition = new Vector3(0f, horizon + height * .5f - 1f, distance);
            sky.transform.localRotation = Quaternion.identity;
            sky.transform.localScale = new Vector3(150f, height, 1f);
            sky.GetComponent<Renderer>().sharedMaterial = skyMaterial;
            var collider = sky.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) Destroy(collider);
                else DestroyImmediate(collider);
            }
            worldRoots.Add(sky);
        }

        void ApplyStoryPortMaterials(GameObject root)
        {
            var shader = Shader.Find("StoryPort/EBPBR");
            if (shader == null)
            {
                Debug.LogWarning("StoryPort could not find its mobile PBR shader for the Chicago stage");
                return;
            }

            int converted = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.materials)
                {
                    if (material == null || !material.HasProperty("_base_tex")) continue;
                    material.shader = shader;
                    if (material.HasProperty("_use_pbr_composite"))
                        material.SetFloat("_use_pbr_composite", material.GetTexture("_pbr_composite_tex") != null ? 1 : 0);
                    if (material.HasProperty("_use_metallic_tex"))
                        material.SetFloat("_use_metallic_tex", material.GetTexture("_metallic_tex") != null ? 1 : 0);
                    if (material.HasProperty("_use_roughness_tex"))
                        material.SetFloat("_use_roughness_tex", material.GetTexture("_roughness_tex") != null ? 1 : 0);
                    converted++;
                }
            }
            Debug.Log("StoryPort applied its mobile PBR shader to " + converted + " Chicago stage materials");
        }

        GameObject SpawnBot(string key, Vector3 position, float yaw, float scale)
        {
            var prefabName = key == "fte_optimus_gs_t3" ? "optimusprime_gs_v" : key;
            var prefab = Resources.Load<GameObject>("StoryPort/Bots/" + prefabName);
            if (prefab == null && key.Contains("stars")) prefab = Resources.Load<GameObject>("StoryPort/Bots/starscream_gs");
            if (prefab == null && key.Contains("ironhide")) prefab = Resources.Load<GameObject>("StoryPort/Bots/ironhide_cin_rotf");
            if (prefab == null && key.Contains("megatron")) prefab = Resources.Load<GameObject>("StoryPort/Bots/optimusprime_cin_tf");
            if (prefab == null)
            {
                SetNotice("Missing local character art: " + key);
                return null;
            }
            var actor = Instantiate(prefab, position, Quaternion.Euler(0, yaw, 0));
            actor.name = key;
            actor.transform.localScale *= scale;
            var renderers = actor.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                Bounds visualBounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) visualBounds.Encapsulate(renderers[i].bounds);
                if (visualBounds.size.y > .01f)
                    actor.transform.localScale *= Mathf.Clamp(4.2f / visualBounds.size.y, .02f, 1.2f);
                visualBounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) visualBounds.Encapsulate(renderers[i].bounds);
                actor.transform.position += Vector3.up * -visualBounds.min.y;
            }
            var animators = actor.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators) animator.applyRootMotion = false;
            return actor;
        }

        Animator FindFightAnimator(GameObject actor)
        {
            if (actor == null) return null;
            var animators = actor.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
                if (animator.runtimeAnimatorController != null && animator.runtimeAnimatorController.name.IndexOf("char_fight", StringComparison.OrdinalIgnoreCase) >= 0)
                    return animator;
            return animators.Length > 0 ? animators[0] : null;
        }

        void PlayerAttack(string state, int damage, int charge)
        {
            if (requestBusy || screen != "fight" || enemyHp <= 0) return;
            if (enemyBusy || playerBusy)
            {
                queuedAttack = true;
                queuedAttackState = state;
                queuedAttackDamage = damage;
                queuedAttackCharge = charge;
                return;
            }
            StartCoroutine(PlayerAttackImpact(state, damage, charge));
        }

        void RunQueuedAttack()
        {
            if (!queuedAttack || screen != "fight" || enemyHp <= 0 || enemyBusy || playerBusy) return;
            string state = queuedAttackState;
            int damage = queuedAttackDamage;
            int charge = queuedAttackCharge;
            queuedAttack = false;
            PlayerAttack(state, damage, charge);
        }

        IEnumerator PlayerAttackImpact(string state, int damage, int charge)
        {
            playerBusy = true;
            guarding = false;
            Vector3 playerHome = playerActor != null ? playerActor.transform.position : Vector3.zero;
            Vector3 enemyHome = enemyActor != null ? enemyActor.transform.position : Vector3.zero;
            // The original AI anticipates an incoming attack and weights dodge,
            // block, sidestep and idle. Give each reaction a short cooldown so a
            // combo can break through, as it does in the reference fight.
            var defense = Time.time - lastEnemyDefense >= 1f
                ? StoryPortEnemyDefense.Choose(UnityEngine.Random.value)
                : StoryPortEnemyDefense.Action.Idle;
            if (defense != StoryPortEnemyDefense.Action.Idle) lastEnemyDefense = Time.time;
            if (defense == StoryPortEnemyDefense.Action.Block)
            {
                PlayState(enemyAnimator, "Block");
                SetBoolIfPresent(enemyAnimator, "Blocking", true);
            }
            else if (defense == StoryPortEnemyDefense.Action.Dodge || defense == StoryPortEnemyDefense.Action.Sidestep)
            {
                PlayState(enemyAnimator, defense == StoryPortEnemyDefense.Action.Dodge ? "Dodge" : "Dash");
                Combat(enemyKey, defense == StoryPortEnemyDefense.Action.Dodge ? "dodge" : "dash", .7f);
                if (enemyActor != null) StartCoroutine(MoveActor(enemyActor, enemyHome + enemyActor.transform.right * (defense == StoryPortEnemyDefense.Action.Dodge ? .7f : -.5f), .2f));
            }
            PlayState(playerAnimator, state);
            int step = AttackStep(state);
            Combat(playerKey, "attack_" + step);
            if (playerActor != null && enemyActor != null)
            {
                Vector3 towardEnemy = (enemyActor.transform.position - playerActor.transform.position).normalized;
                StartCoroutine(MoveActor(playerActor, playerHome + towardEnemy * .65f, .16f));
            }
            yield return new WaitForSeconds(state == "SpecialAttack03" ? 1f : state.StartsWith("Special", StringComparison.Ordinal) ? .58f : state.StartsWith("Medium", StringComparison.Ordinal) ? .43f : .28f);
            if (enemyHp <= 0 || screen != "fight") { playerBusy = false; yield break; }
            var move = rules.MoveFor(state);
            bool specialMove = state.StartsWith("Special", StringComparison.Ordinal);
            float share = specialMove ? rules.SpecialRatio(playerKey, AttackStepLevel(state)) : move.Share;
            bool crit = !specialMove && UnityEngine.Random.value < move.CritChance;
            float dealt = playerAttack * share * (crit ? move.CritDamage : 1f) * UnityEngine.Random.Range(.95f, 1.05f);
            bool dodged = defense == StoryPortEnemyDefense.Action.Dodge || defense == StoryPortEnemyDefense.Action.Sidestep;
            bool blocked = defense == StoryPortEnemyDefense.Action.Block;
            if (dodged) dealt = 0f;
            else if (blocked) dealt *= .1f;
            if (blocked) { PlayState(enemyAnimator, "BlockReact"); Combat(enemyKey, "block_react"); }
            else if (!dodged && enemyAnimator != null) PlayState(enemyAnimator, specialMove ? state + "HitReaction" : "HitReactionLightLeftHigh");
            enemyHealth = Mathf.Max(0f, enemyHealth - dealt);
            SyncHealthPercent();
            if (dodged) SetNotice("DODGED");
            else
            {
                ShowDamageNumber(Mathf.RoundToInt(dealt), enemyActor, blocked ? new Color(.7f, .85f, 1f) : crit ? new Color(1f, .8f, .2f) : new Color(1f, .97f, .9f));
                Combat(playerKey, "attack_hit_" + step);
                if (!blocked) Combat(enemyKey, step >= 6 ? "hit_react_heavy" : step >= 4 ? "hit_react_medium" : "hit_react_light", .6f);
            }
            if (enemyHp == 0) Combat(enemyKey, "knockout");
            if (!specialMove && !dodged)
            {
                // Attacker gains the move's special energy, the defender half of it.
                playerMana = Mathf.Min(3 * StoryPortCombatRules.ManaPerBar, playerMana + move.Mana);
                enemyMana = Mathf.Min(3 * StoryPortCombatRules.ManaPerBar, enemyMana + move.Mana * .5f);
            }
            if (Time.time - lastPlayerHit > 1.4f) comboHits = 0;
            if (!dodged) { comboHits++; hitsLanded++; }
            else comboHits = 0;
            highestChain = Mathf.Max(highestChain, comboHits);
            lastPlayerHit = Time.time;
            if (comboText != null) comboText.text = comboHits > 0 ? comboHits + " HITS!\n<size=16>GOOD!</size>" : "";
            if (!dodged && !blocked && playerActor != null && enemyActor != null)
            {
                var direction = (enemyActor.transform.position - playerActor.transform.position).normalized;
                enemyActor.transform.position += direction * .22f;
                CameraShake(.04f);
            }
            UpdateFightHud();
            if (playerActor != null) yield return StartCoroutine(MoveActor(playerActor, playerHome, .16f));
            if (enemyActor != null && dodged) yield return StartCoroutine(MoveActor(enemyActor, enemyHome, .16f));
            if (blocked) SetBoolIfPresent(enemyAnimator, "Blocking", false);
            playerBusy = false;
            if (enemyHp == 0) { StartCoroutine(ResolveWinAfterImpact()); yield break; }
            // A landed hit only staggers the enemy briefly; it keeps its own attack rhythm.
            nextEnemyTurn = Mathf.Max(nextEnemyTurn, Time.time + .35f);
            RunQueuedAttack();
        }

        IEnumerator ResolveWinAfterImpact()
        {
            PlayState(enemyAnimator, "KnockoutLight");
            yield return new WaitForSeconds(.9f);
            yield return StartCoroutine(WinnerShot(playerActor, playerName));
            StartCoroutine(ResolveWin());
        }

        // Knockout shot from the reference: the camera closes in on the winner
        // under a "<NAME> WINS!" call before the result screen.
        IEnumerator WinnerShot(GameObject winner, string winnerName)
        {
            var camera = Camera.main;
            if (camera == null || winner == null) yield break;
            Cue("enemy_killed", .7f);
            var hud = content != null ? content.Find("Fight HUD") : null;
            if (hud != null) hud.gameObject.SetActive(false);
            var label = LabelAt(content, "Winner Call", winnerName.ToUpperInvariant() + " WINS!", 40, TextAnchor.MiddleCenter, Color.white, new Vector2(.2f, .8f), new Vector2(.8f, .95f));
            label.fontStyle = FontStyle.BoldAndItalic;
            label.raycastTarget = false;
            label.gameObject.AddComponent<Outline>().effectColor = new Color(.05f, .25f, .45f, .9f);
            var start = camera.transform.position;
            var startRotation = camera.transform.rotation;
            var focus = winner.transform.position + Vector3.up * 3.2f;
            var toCamera = (start - winner.transform.position);
            toCamera.y = 0f;
            var end = focus + toCamera.normalized * 5.5f + Vector3.up * .3f;
            float t = 0f;
            while (t < 2.2f)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / .8f));
                // Slow orbit after the push-in.
                var orbit = Quaternion.AngleAxis(Mathf.Max(0f, t - .8f) * 8f, Vector3.up);
                camera.transform.position = Vector3.Lerp(start, focus + orbit * (end - focus), k);
                camera.transform.rotation = Quaternion.Slerp(startRotation, Quaternion.LookRotation(focus - camera.transform.position), k);
                yield return null;
            }
            if (label != null) Destroy(label.gameObject);
        }

        void Dash()
        {
            Dash(true);
        }

        void Dash(bool towardEnemy)
        {
            if (screen != "fight" || playerActor == null || enemyActor == null) return;
            guarding = false;
            PlayState(playerAnimator, "Dash");
            Combat(playerKey, towardEnemy ? "dash" : "dodge", .7f);
            var direction = (enemyActor.transform.position - playerActor.transform.position).normalized;
            playerActor.transform.position += direction * (towardEnemy ? .95f : -.7f);
            evadeUntil = Time.time + (towardEnemy ? .38f : .72f);
            nextEnemyTurn = Time.time + .8f;
            UpdateFightHud();
        }

        IEnumerator MoveActor(GameObject actor, Vector3 target, float duration)
        {
            if (actor == null) yield break;
            Vector3 start = actor.transform.position;
            float elapsed = 0f;
            while (elapsed < duration && actor != null)
            {
                elapsed += Time.deltaTime;
                float amount = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                actor.transform.position = Vector3.Lerp(start, target, amount);
                yield return null;
            }
            if (actor != null) actor.transform.position = target;
        }

        void ToggleBlock()
        {
            if (screen != "fight") return;
            guarding = !guarding;
            if (playerAnimator != null)
            {
                if (playerAnimator.HasState(0, Animator.StringToHash("Block"))) PlayState(playerAnimator, guarding ? "Block" : "Idle");
                SetBoolIfPresent(playerAnimator, "Blocking", guarding);
            }
            UpdateFightHud();
        }

        void TogglePause()
        {
            if (screen != "fight" || paused) return;
            paused = true;
            Time.timeScale = 0f;
            pauseOverlay = Panel(content, "Pause Overlay", new Color(.005f, .015f, .03f, .86f), new Vector2(.29f, .25f), new Vector2(.71f, .76f));
            LabelAt(pauseOverlay.transform, "Pause Title", "PAUSED", 28, TextAnchor.MiddleCenter, Color.white, new Vector2(.08f, .62f), new Vector2(.92f, .9f));
            var resume = Button(pauseOverlay.transform, "RESUME", ResumeFight, new Vector2(.18f, .12f), new Vector2(.82f, .42f));
            SetButtonSkin(resume, "button_main_glowing");
        }

        void ResumeFight()
        {
            if (pauseOverlay != null) Destroy(pauseOverlay);
            pauseOverlay = null;
            paused = false;
            Time.timeScale = 1f;
        }

        void RetryFight()
        {
            Time.timeScale = 1f;
            paused = false;
            guarding = false;
            enemyBusy = false;
            playerBusy = false;
            queuedAttack = false;
            lightCombo = 0;
            comboHits = 0;
            playerMana = enemyMana = 0;
            playerHealth = playerMaxHealth;
            enemyHealth = enemyMaxHealth;
            SyncHealthPercent();
            nextEnemyTurn = Time.time + 2.8f;
            DestroyWorld();
            Show("fight");
        }

        void SpecialAttack()
        {
            int level = Mathf.FloorToInt(playerMana / StoryPortCombatRules.ManaPerBar);
            if (level < 1 || playerBusy || enemyHp <= 0)
            {
                if (level < 1) SetNotice("Land hits to charge a special bar");
                return;
            }
            level = Mathf.Min(3, level);
            playerMana -= level * StoryPortCombatRules.ManaPerBar;
            PlayerAttack("SpecialAttack0" + level, 0, 8);
            UpdateFightHud();
        }

        void EnemyTurn()
        {
            if (screen != "fight" || enemyBusy || enemyHp <= 0 || playerHp <= 0) return;
            StartCoroutine(EnemyAttack());
        }

        // Enemy turn: a special when a bar is charged, otherwise a combo of one to
        // three light hits, then a short breather. The reference fights show the
        // AI landing about one hit for every two the player lands.
        IEnumerator EnemyAttack()
        {
            // Claim the turn so further player taps queue, then let the current swing land.
            enemyBusy = true;
            while (playerBusy && screen == "fight") yield return null;
            if (screen != "fight" || playerHp <= 0 || enemyHp <= 0) { enemyBusy = false; yield break; }
            int enemyBars = Mathf.FloorToInt(enemyMana / StoryPortCombatRules.ManaPerBar);
            bool special = enemyBars >= 1 && (enemyBars >= 3 || UnityEngine.Random.value < .3f);
            enemySpecialLevel = special ? Mathf.Min(3, enemyBars) : 0;
            if (special) enemyMana -= enemySpecialLevel * StoryPortCombatRules.ManaPerBar;
            int hits = special ? 1 : UnityEngine.Random.Range(1, 4);
            Vector3 enemyHome = enemyActor != null ? enemyActor.transform.position : Vector3.zero;
            if (enemyActor != null && playerActor != null)
            {
                Vector3 towardPlayer = (playerActor.transform.position - enemyActor.transform.position).normalized;
                StartCoroutine(MoveActor(enemyActor, enemyHome + towardPlayer * .7f, .2f));
            }
            SetNotice(special ? "ENEMY SPECIAL · BLOCK OR DODGE" : "");
            for (int hit = 1; hit <= hits && playerHp > 0 && enemyHp > 0 && screen == "fight"; hit++)
            {
                PlayState(enemyAnimator, special ? "SpecialAttack0" + enemySpecialLevel : "LightAttack0" + hit);
                int enemyStep = special ? 6 : hit;
                Combat(enemyKey, "attack_" + enemyStep);
                // The first swing telegraphs; follow-ups come faster.
                yield return new WaitForSeconds(special ? 1f : hit == 1 ? .5f : .32f);
                if (playerHp <= 0 || enemyHp <= 0) break;
                var enemyMove = rules.MoveFor("LightAttack0" + hit);
                float raw = enemyAttack * (special ? rules.SpecialRatio(enemyKey, enemySpecialLevel) : enemyMove.Share) * UnityEngine.Random.Range(.95f, 1.05f);
                bool evaded = Time.time < evadeUntil;
                float taken = evaded ? 0f : guarding ? raw * .1f : raw;
                int damage = Mathf.RoundToInt(taken);
                playerHealth = Mathf.Max(0f, playerHealth - taken);
                SyncHealthPercent();
                if (damage > 0) ShowDamageNumber(damage, playerActor, guarding ? new Color(.7f, .85f, 1f) : new Color(1f, .45f, .4f));
                if (guarding) Combat(playerKey, "block_react");
                else if (damage > 0)
                {
                    hitsReceived++;
                    Combat(enemyKey, "attack_hit_" + enemyStep);
                    Combat(playerKey, special ? "hit_react_heavy" : "hit_react_light", .6f);
                    comboHits = 0;
                    if (comboText != null) comboText.text = "";
                }
                if (!special && taken > 0f)
                {
                    enemyMana = Mathf.Min(3 * StoryPortCombatRules.ManaPerBar, enemyMana + enemyMove.Mana);
                    playerMana = Mathf.Min(3 * StoryPortCombatRules.ManaPerBar, playerMana + enemyMove.Mana * .5f);
                }
                PlayState(playerAnimator, guarding ? "BlockReact" : evaded ? "Dash" : special ? "SpecialAttack0" + enemySpecialLevel + "HitReaction" : "HitReactionLightRightHigh");
                CameraShake(guarding || evaded ? .05f : .12f);
                if (evaded) SetNotice("DODGED");
                UpdateFightHud();
                if (playerHp == 0)
                {
                    PlayState(playerAnimator, "KnockoutLight");
                    Combat(playerKey, "knockout");
                    yield return new WaitForSeconds(.8f);
                    yield return StartCoroutine(WinnerShot(enemyActor, enemyName));
                    Show("defeat");
                }
                if (evaded) break;
            }
            enemyBusy = false;
            if (enemyActor != null) StartCoroutine(MoveActor(enemyActor, enemyHome, .18f));
            // Keep a held block; only drop a block the player has already released.
            if (guarding && !touchTracking)
            {
                guarding = false;
                PlayState(playerAnimator, "Idle");
            }
            nextEnemyTurn = Time.time + UnityEngine.Random.Range(1.4f, 2.6f);
            RunQueuedAttack();
        }

        void Update()
        {
            if (screen == "fight" && !paused) HandleFightTouch();
            // The enemy's turn comes on its own timer, not only when the player is idle.
            if (screen == "fight" && !paused && !requestBusy && !enemyBusy && Time.time >= nextEnemyTurn && playerHp > 0 && enemyHp > 0)
                EnemyTurn();
            if (statusText != null)
            {
                statusText.text = requestBusy ? "SYNCING…" : notice;
                statusText.gameObject.SetActive(requestBusy || !string.IsNullOrEmpty(notice));
            }
        }

        void UpdateFightHud()
        {
            if (playerHpText != null) playerHpText.text = playerHp + "%";
            if (enemyHpText != null) enemyHpText.text = enemyHp + "%";
            if (playerHpFill != null) playerHpFill.fillAmount = playerHp / 100f;
            if (enemyHpFill != null) enemyHpFill.fillAmount = enemyHp / 100f;
            if (specialFill != null) specialFill.fillAmount = specialMeter / 3f;
            if (specialText != null) specialText.text = "SPECIAL  " + specialMeter + " / 3";
            specialMeter = Mathf.FloorToInt(playerMana / StoryPortCombatRules.ManaPerBar);
            if (specialSegmentFills != null)
                for (var i = 0; i < specialSegmentFills.Length; i++)
                    if (specialSegmentFills[i] != null)
                        specialSegmentFills[i].fillAmount = Mathf.Clamp01(playerMana / StoryPortCombatRules.ManaPerBar - i);
            if (specialHex != null)
                specialHex.color = specialMeter >= 1 ? new Color(.3f, 1f, .3f, .95f) : new Color(.75f, .85f, .95f, .45f);
            if (specialButtonLabel != null) specialButtonLabel.text = "";
        }

        void MoveStory(int dx, int dy)
        {
            if (requestBusy) return;
            var path = "/quests/quest-movedir/" + currentQid + "-0/" + dx + "/" + dy;
            StartCoroutine(Post(path, "{}", response =>
            {
                lastQuestJson = response;
                if (!ReadCurrentPosition(response))
                {
                    mapX += dx;
                    mapY += dy;
                }
                var serverEnemy = ExtractJsonString(response, "currentBattleId");
                if (string.IsNullOrEmpty(serverEnemy)) serverEnemy = ExtractBattleKey(response);
                if (!string.IsNullOrEmpty(serverEnemy))
                {
                    pendingEncounter = true;
                    enemyKey = serverEnemy;
                    enemyName = DisplayName(enemyKey);
                    playerHp = 100;
                    enemyHp = 100;
                    playerMana = enemyMana = 0;
                    playerKey = rosterKeys[squad[0]];
                    playerName = rosterNames[squad[0]];
                    squadForStory = true;
                    Cue("node_land_on_fight");
                    var landed = FindStoryMapNode(mapX, mapY);
                    PlayDialogue(landed != null ? landed.dialogue : "", () => Show("squad"));
                }
                else
                {
                    pendingEncounter = false;
                    SetNotice("NO ENCOUNTER AT THIS ROUTE NODE");
                    Show("map");
                }
            }));
        }

        bool ReadCurrentPosition(string json)
        {
            var match = Regex.Match(json, "\\\"currentPos\\\"\\s*:\\s*\\{\\s*\\\"x\\\"\\s*:\\s*(-?\\d+)\\s*,\\s*\\\"y\\\"\\s*:\\s*(-?\\d+)");
            if (!match.Success) return false;
            mapX = int.Parse(match.Groups[1].Value);
            mapY = int.Parse(match.Groups[2].Value);
            return true;
        }

        void ReadStoryMap(string response)
        {
            var route = StoryRouteData.Parse(response, currentQid);
            storyMapNodes.Clear();
            storyMapNodes.AddRange(route.nodes);
            storyMapDimension = route.dimension;
            if (!route.hasMap)
                Debug.LogWarning("StoryPort server response did not include map data for " + currentQid);
            Debug.Log("StoryPort loaded " + storyMapNodes.Count + " walkable server map nodes for " + currentQid + " (dimension " + storyMapDimension + ")");
        }

        IEnumerator LoadFight()
        {
            int playerRank, playerLevel, enemyRank, enemyLevel;
            ReadRankLevel(lastQuestJson, playerKey, out playerRank, out playerLevel);
            ReadRankLevel(lastQuestJson, enemyKey, out enemyRank, out enemyLevel);
            string body = "{\"heroes\":[{\"bid\":\"" + playerKey + "\",\"rank\":" + playerRank + ",\"level\":" + playerLevel +
                "},{\"bid\":\"" + enemyKey + "\",\"rank\":" + enemyRank + ",\"level\":" + enemyLevel + "}]}";
            string response = "";
            yield return StartCoroutine(Post("/bcg/getBaseHeroData", body, json => response = json));
            playerMaxHealth = ReadStat(response, playerKey, "max_hp", 3000f);
            playerAttack = ReadStat(response, playerKey, "attack", 300f);
            enemyMaxHealth = ReadStat(response, enemyKey, "max_hp", 3000f);
            enemyAttack = ReadStat(response, enemyKey, "attack", 300f);
            playerHealth = playerMaxHealth;
            enemyHealth = enemyMaxHealth;
            SyncHealthPercent();
            yield return new WaitForSeconds(.3f);
            if (screen == "loading") Show("fight");
        }

        static float ReadStat(string json, string bid, string stat, float fallback)
        {
            // Hero entries hold nested objects ("pvpb": {}), so scan from this hero's
            // "bid" to the next hero's instead of matching one brace-free block.
            var start = Regex.Match(json ?? "", "\"bid\"\\s*:\\s*\"" + Regex.Escape(bid) + "\"");
            if (!start.Success) return fallback;
            var next = json.IndexOf("\"bid\"", start.Index + start.Length, StringComparison.Ordinal);
            var body = json.Substring(start.Index, (next < 0 ? json.Length : next) - start.Index);
            var value = Regex.Match(body, "\"" + stat + "\"\\s*:\\s*(-?[0-9.]+)");
            float parsed;
            return value.Success && float.TryParse(value.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed) && parsed > 0 ? parsed : fallback;
        }

        // Rank and level of a hero or boss as the quest data lists them (default 1/1).
        static void ReadRankLevel(string json, string key, out int rank, out int level)
        {
            rank = level = 1;
            foreach (Match block in Regex.Matches(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{[^{}]*\\}"))
            {
                var r = Regex.Match(block.Value, "\"rank\"\\s*:\\s*(\\d+)");
                var l = Regex.Match(block.Value, "\"level\"\\s*:\\s*(\\d+)");
                if (!r.Success) continue;
                rank = int.Parse(r.Groups[1].Value);
                if (l.Success) level = int.Parse(l.Groups[1].Value);
                return;
            }
        }

        // Floating damage number above a fighter, as in the reference footage.
        void ShowDamageNumber(int amount, GameObject target, Color color)
        {
            var camera = Camera.main;
            if (camera == null || target == null || uiRoot == null) return;
            var label = Label(uiRoot, "Damage Number", amount.ToString(), 30, TextAnchor.MiddleCenter, color);
            label.fontStyle = FontStyle.Bold;
            label.raycastTarget = false;
            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, .8f);
            StartCoroutine(FloatDamageNumber(label, target.transform.position + Vector3.up * 3.6f));
        }

        IEnumerator FloatDamageNumber(Text label, Vector3 world)
        {
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.sizeDelta = new Vector2(160, 50);
            float t = 0f;
            var jitter = new Vector2(UnityEngine.Random.Range(-30f, 30f), 0);
            while (t < .9f && label != null)
            {
                t += Time.deltaTime;
                var camera = Camera.main;
                if (camera == null) break;
                var screenPoint = (Vector2)camera.WorldToScreenPoint(world);
                var scale = canvas != null ? canvas.scaleFactor : 1f;
                rect.anchoredPosition = screenPoint / scale + jitter + Vector2.up * (t * 70f);
                label.color = new Color(label.color.r, label.color.g, label.color.b, Mathf.Clamp01(1.6f - t * 1.8f));
                yield return null;
            }
            if (label != null) Destroy(label.gameObject);
        }

        void SyncHealthPercent()
        {
            playerHp = playerHealth <= 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(playerHealth / playerMaxHealth * 100f), 1, 100);
            enemyHp = enemyHealth <= 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(enemyHealth / enemyMaxHealth * 100f), 1, 100);
        }

        static int AttackStepLevel(string state)
        {
            int level;
            return int.TryParse(state.Substring(state.Length - 1), out level) ? Mathf.Clamp(level, 1, 3) : 1;
        }

        void SaveSquadAndBegin()
        {
            if (squad.Count == 0) { SetNotice("Select at least one bot"); return; }
            if (!squad.Contains(selectedBot)) { SetNotice("Add the selected bot to the team first"); return; }
            squad.Remove(selectedBot);
            squad.Insert(0, selectedBot);
            var heroes = new List<string>();
            for (int i = 0; i < squad.Count; i++) heroes.Add("\"" + rosterKeys[squad[i]] + "\"");
            string saved = "{\"heroes\":[" + string.Join(",", heroes) + "],\"api\":6,\"nonce\":\"storyport\",\"teamID\":\"0\"}";
            StartCoroutine(Post("/bcg/setSavedTeam", saved, _ =>
            {
                playerKey = rosterKeys[squad[0]];
                playerName = rosterNames[squad[0]];
                if (pendingEncounter)
                {
                    playerHp = enemyHp = 100;
                    playerMana = enemyMana = 0;
                    Show("loading");
                    StartCoroutine(LoadFight());
                }
                else Show("map");
            }));
        }

        void OpenStoryMap()
        {
            currentQid = ActQids[actIndex];
            storyNodes = ActNodeLabels[actIndex].Split('|');
            string body = "{\"setId\":\"" + StorySet + "\"}";
            pendingEncounter = false;
            Show("loading");
            StartCoroutine(Post("/quests/quest-begin/" + currentQid, body, response =>
            {
                ReadCurrentPosition(response);
                ReadStoryMap(response);
                StartCoroutine(ProbeStoryPosition());
            }));
        }

        IEnumerator ProbeStoryPosition()
        {
            string response = "";
            yield return StartCoroutine(Post("/quests/quest-movedir/" + currentQid + "-0/0/0", "{}", json => response = json));
            lastQuestJson = response;
            ReadCurrentPosition(response);
            var serverEnemy = ExtractJsonString(response, "currentBattleId");
            if (string.IsNullOrEmpty(serverEnemy)) serverEnemy = ExtractBattleKey(response);
            pendingEncounter = !string.IsNullOrEmpty(serverEnemy);
            if (pendingEncounter)
            {
                enemyKey = serverEnemy;
                enemyName = DisplayName(enemyKey);
            }
            if (screen == "loading") Show("map");
        }

        IEnumerator ResolveWin()
        {
            string body = "{\"qid\":\"" + currentQid + "\",\"results\":{\"result\":\"WON\"}}";
            yield return StartCoroutine(Post("/matches/resolve-match/quests_fight", body, _ => pendingEncounter = false));
            Show("victory");
            SetNotice("VICTORY SAVED TO THE STORY ROUTE");
        }

        // Result screen after the reference: title, the fight's stats and the
        // mission's explored share from the server route, tap anywhere to continue.
        void ResultScreen(bool won)
        {
            Cue(won ? "fight_won" : "fight_lost", .8f);
            var shade = Panel(content, "Result Shade", new Color(.01f, .03f, .06f, .78f), new Vector2(-.03f, -.1f), new Vector2(1.03f, 1.15f));
            shade.GetComponent<Image>().raycastTarget = false;
            var title = LabelAt(content, "Result", won ? "VICTORY" : "DEFEAT", 54, TextAnchor.MiddleCenter, won ? new Color(.86f, .95f, 1f) : new Color(1f, .5f, .45f), new Vector2(.3f, .86f), new Vector2(.7f, 1.02f));
            title.fontStyle = FontStyle.Bold;
            title.gameObject.AddComponent<Outline>().effectColor = won ? new Color(.1f, .55f, .95f, .9f) : new Color(.6f, .1f, .1f, .9f);
            var stats = Panel(content, "Result Stats", new Color(.08f, .1f, .13f, .92f), new Vector2(.2f, .34f), new Vector2(.8f, .78f));
            stats.GetComponent<Image>().raycastTarget = false;
            LabelAt(stats.transform, "Stats Title", "STATS", 18, TextAnchor.MiddleCenter, new Color(.8f, .85f, .9f), new Vector2(0, .8f), new Vector2(1, .98f));
            Panel(stats.transform, "Stats Rule", new Color(.35f, .4f, .45f, 1f), new Vector2(.03f, .78f), new Vector2(.97f, .785f)).GetComponent<Image>().raycastTarget = false;
            string[] names = { "SUCCESSFUL HITS", "HITS RECEIVED", "HIGHEST CHAIN" };
            int[] values = { hitsLanded, hitsReceived, highestChain };
            for (int i = 0; i < 3; i++)
            {
                float y = .56f - i * .22f;
                LabelAt(stats.transform, names[i], names[i], 16, TextAnchor.MiddleLeft, new Color(.78f, .82f, .86f), new Vector2(.12f, y), new Vector2(.7f, y + .18f));
                LabelAt(stats.transform, names[i] + " Value", values[i].ToString(), 16, TextAnchor.MiddleRight, Color.white, new Vector2(.7f, y), new Vector2(.88f, y + .18f));
            }
            var mission = Panel(content, "Result Mission", new Color(.08f, .1f, .13f, .92f), new Vector2(.2f, .2f), new Vector2(.8f, .3f));
            mission.GetComponent<Image>().raycastTarget = false;
            LabelAt(mission.transform, "Mission Label", "ACT " + Roman(actIndex + 1), 15, TextAnchor.MiddleLeft, Color.white, new Vector2(.05f, 0), new Vector2(.2f, 1));
            var track = MakeImage(mission.transform, "Mission Track", new Color(.2f, .2f, .22f, 1f), new Vector2(.2f, .3f), new Vector2(.72f, .7f));
            track.raycastTarget = false;
            float explored = ExploredShare();
            var fill = MakeImage(track.transform, "Mission Fill", new Color(.9f, .64f, .12f, 1f), Vector2.zero, new Vector2(explored, 1f));
            fill.raycastTarget = false;
            LabelAt(mission.transform, "Mission Explored", "Explored " + Mathf.RoundToInt(explored * 100f) + "%", 15, TextAnchor.MiddleRight, Color.white, new Vector2(.72f, 0), new Vector2(.96f, 1));
            LabelAt(content, "Result Continue", "TAP ANYWHERE TO CONTINUE", 14, TextAnchor.MiddleCenter, new Color(.75f, .8f, .85f), new Vector2(.3f, .02f), new Vector2(.7f, .1f));
            var tap = Button(content, "Result Tap", won ? (Action)ContinueRoute : RetryFight, new Vector2(-.03f, -.1f), new Vector2(1.03f, 1.15f));
            tap.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            tap.GetComponentInChildren<Text>().text = "";
            tap.transform.SetAsLastSibling();
        }

        // Share of the act's encounters the server reports cleared (or reached).
        float ExploredShare()
        {
            int total = 0, reached = 0;
            foreach (var node in storyMapNodes)
            {
                if (string.IsNullOrEmpty(node.boss)) continue;
                total++;
                if (node.x <= mapX) reached++;
            }
            return total == 0 ? 1f : Mathf.Clamp01((float)reached / total);
        }

        void ContinueRoute()
        {
            var node = FindStoryMapNode(mapX, mapY);
            var after = node != null ? node.dialogueAfter : "";
            if (!string.IsNullOrEmpty(after) && !dialoguesSeen.Contains(currentQid + "/" + after))
            {
                PlayDialogue(after, ContinueRoute);
                return;
            }
            if (!CurrentNodeIsFinal())
            {
                Show("map");
                return;
            }
            Show("complete");
        }

        // Mission Complete screen after an act's final encounter, as in the
        // reference. The server grants no rewards, so the panel stays empty.
        void MissionCompleteScreen()
        {
            Cue("complete_celebration", .8f);
            TechBackdrop();
            var title = LabelAt(content, "Mission Complete", "MISSION COMPLETE!", 44, TextAnchor.MiddleCenter, Color.white, new Vector2(.2f, .83f), new Vector2(.8f, .98f));
            title.fontStyle = FontStyle.Bold;
            title.gameObject.AddComponent<Outline>().effectColor = new Color(.1f, .5f, .9f, .9f);
            LabelAt(content, "Mission Name", ActTitles[actIndex], 20, TextAnchor.MiddleCenter, Color.white, new Vector2(.2f, .76f), new Vector2(.8f, .84f)).fontStyle = FontStyle.Bold;
            var rewards = Panel(content, "Rewards Panel", new Color(.08f, .09f, .11f, .95f), new Vector2(.18f, .34f), new Vector2(.82f, .76f));
            rewards.GetComponent<Image>().raycastTarget = false;
            LabelAt(rewards.transform, "Rewards Title", "REWARDS", 16, TextAnchor.MiddleCenter, new Color(.8f, .85f, .9f), new Vector2(0, .82f), new Vector2(1, .98f));
            Panel(rewards.transform, "Rewards Rule", new Color(.3f, .34f, .38f, 1f), new Vector2(.03f, .8f), new Vector2(.97f, .805f)).GetComponent<Image>().raycastTarget = false;
            var bar = Panel(content, "Mission Progress", new Color(.08f, .09f, .11f, .95f), new Vector2(.18f, .25f), new Vector2(.82f, .33f));
            bar.GetComponent<Image>().raycastTarget = false;
            LabelAt(bar.transform, "Mission Number", "MISSION " + Roman(actIndex + 1), 14, TextAnchor.MiddleLeft, new Color(.8f, .85f, .9f), new Vector2(.04f, 0), new Vector2(.28f, 1));
            var track = MakeImage(bar.transform, "Mission Track", new Color(.2f, .2f, .22f, 1f), new Vector2(.3f, .3f), new Vector2(.72f, .7f));
            track.raycastTarget = false;
            float explored = ExploredShare();
            MakeImage(track.transform, "Mission Fill", new Color(.9f, .64f, .12f, 1f), Vector2.zero, new Vector2(explored, 1f)).raycastTarget = false;
            LabelAt(bar.transform, "Mission Explored", Mathf.RoundToInt(explored * 100f) + "% EXPLORED", 14, TextAnchor.MiddleRight, new Color(.75f, .8f, .85f), new Vector2(.72f, 0), new Vector2(.96f, 1));

            var back = Button(content, "Back to Missions", () => Show("story"), new Vector2(.18f, .1f), new Vector2(.4f, .19f));
            SetButtonSkin(back, "button_main_glowing");
            back.GetComponent<Image>().color = new Color(.16f, .55f, .25f, 1f);
            back.GetComponentInChildren<Text>().text = "BACK TO MISSIONS";
            // Replaying would rewrite the server's progress, so it stays greyed out.
            var replay = Button(content, "Replay", null, new Vector2(.42f, .1f), new Vector2(.58f, .19f));
            replay.interactable = false;
            replay.GetComponent<Image>().color = new Color(.45f, .47f, .5f, .8f);
            replay.GetComponentInChildren<Text>().text = "REPLAY";
            bool hasNext = actIndex < 2;
            var next = Button(content, "Play Next", () => { actIndex++; Show("chapter"); }, new Vector2(.6f, .1f), new Vector2(.82f, .19f));
            SetButtonSkin(next, "button_main_glowing");
            next.GetComponent<Image>().color = new Color(.16f, .55f, .25f, 1f);
            next.GetComponentInChildren<Text>().text = "PLAY NEXT";
            next.interactable = hasNext;
        }

        // Server-authored story dialogue: 3D speakers left and right over a dimmed
        // backdrop, one line at a time, as in the reference footage.
        void PlayDialogue(string setId, Action done)
        {
            var lines = StoryRouteData.ReadDialogue(actDetails[actIndex], setId);
            var seenKey = currentQid + "/" + setId;
            if (lines.Count == 0 || dialoguesSeen.Contains(seenKey)) { done(); return; }
            dialoguesSeen.Add(seenKey);
            dialogueLines = lines;
            dialogueIndex = 0;
            dialogueDone = done;
            Show("dialogue");
        }

        void DialogueScreen()
        {
            TechBackdrop();
            var camera = Camera.main;
            if (camera != null)
            {
                camera.fieldOfView = 36;
                // Close framing: speakers fill the side thirds and are cut off at the band.
                camera.transform.position = new Vector3(0, 3.1f, -7.2f);
                camera.transform.LookAt(new Vector3(0, 2.9f, 0));
            }
            string left = "", right = "";
            foreach (var line in dialogueLines)
            {
                if (line.side == "right") { if (right == "") right = line.character; }
                else if (left == "") left = line.character;
            }
            dialogueLeft = SpawnSpeaker(left, -3.9f, 150f);
            dialogueRight = SpawnSpeaker(right, 3.9f, 210f);
            var band = Panel(content, "Dialogue Band", new Color(0, 0, 0, .72f), new Vector2(-.03f, -.1f), new Vector2(1.03f, .2f));
            band.GetComponent<Image>().raycastTarget = false;
            var speaker = LabelAt(content, "Dialogue Speaker", "", 16, TextAnchor.MiddleLeft, new Color(.55f, .8f, .95f), new Vector2(.01f, .135f), new Vector2(.6f, .185f));
            speaker.fontStyle = FontStyle.Bold;
            var text = LabelAt(content, "Dialogue Line", "", 22, TextAnchor.UpperLeft, new Color(.92f, .33f, .27f), new Vector2(.01f, -.02f), new Vector2(.99f, .135f));
            LabelAt(content, "Dialogue Continue", "TAP ANYWHERE TO CONTINUE  >", 11, TextAnchor.MiddleCenter, new Color(.7f, .75f, .8f), new Vector2(.3f, -.09f), new Vector2(.7f, -.04f));
            var advance = Button(content, "Dialogue Advance", () => AdvanceDialogue(speaker, text), new Vector2(-.03f, -.1f), new Vector2(1.03f, 1.1f));
            advance.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            advance.GetComponentInChildren<Text>().text = "";
            advance.transform.SetAsFirstSibling();
            var skip = Button(content, "Dialogue Skip", FinishDialogue, new Vector2(.9f, 1.02f), new Vector2(.99f, 1.09f));
            SetButtonSkin(skip, "button_tab");
            skip.GetComponentInChildren<Text>().text = "SKIP";
            ShowDialogueLine(speaker, text);
        }

        GameObject SpawnSpeaker(string character, float x, float yaw)
        {
            if (string.IsNullOrEmpty(character) || character.Contains("marissa")) return null;
            var actor = SpawnBot(character == "optimusprime_cin_tf" ? "optimusprime_cin_tf" : character, new Vector3(x, 0, 0), yaw, .9f);
            if (actor == null) return null;
            worldRoots.Add(actor);
            PlayState(FindFightAnimator(actor), "Idle");
            return actor;
        }

        void AdvanceDialogue(Text speaker, Text text)
        {
            dialogueIndex++;
            if (dialogueIndex >= dialogueLines.Count) { FinishDialogue(); return; }
            ShowDialogueLine(speaker, text);
        }

        void ShowDialogueLine(Text speaker, Text text)
        {
            var line = dialogueLines[dialogueIndex];
            speaker.text = SpeakerName(line.character).ToUpperInvariant();
            text.text = line.line;
            Cue(line.inShadow ? "text_in_corrupt" : "text_in", .6f);
            // The speaking side is lit; the other side (and a shadowed speaker) is dark.
            bool rightSpeaks = line.side == "right";
            ShadeSpeaker(dialogueLeft, rightSpeaks || (!rightSpeaks && line.inShadow));
            ShadeSpeaker(dialogueRight, !rightSpeaks || (rightSpeaks && line.inShadow));
        }

        readonly Dictionary<Renderer, Material[]> speakerMaterials = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Renderer, Material[]> speakerShadowMaterials = new Dictionary<Renderer, Material[]>();

        // Swap to darkened copies of the speaker's materials while it is silent.
        void ShadeSpeaker(GameObject actor, bool dark)
        {
            if (actor == null) return;
            foreach (var renderer in actor.GetComponentsInChildren<Renderer>(true))
            {
                if (!speakerMaterials.ContainsKey(renderer))
                {
                    var original = renderer.sharedMaterials;
                    var shadow = new Material[original.Length];
                    for (var i = 0; i < original.Length; i++)
                    {
                        if (original[i] == null) continue;
                        shadow[i] = new Material(original[i]);
                        if (shadow[i].HasProperty("_base_col")) shadow[i].SetColor("_base_col", new Color(.04f, .05f, .07f, 1f));
                        if (shadow[i].HasProperty("_metallic_range")) shadow[i].SetFloat("_metallic_range", 0f);
                        if (shadow[i].HasProperty("_roughness_range")) shadow[i].SetFloat("_roughness_range", 1f);
                        if (shadow[i].HasProperty("_use_pbr_composite")) shadow[i].SetFloat("_use_pbr_composite", 0f);
                        if (shadow[i].HasProperty("_use_roughness_tex")) shadow[i].SetFloat("_use_roughness_tex", 0f);
                    }
                    speakerMaterials[renderer] = original;
                    speakerShadowMaterials[renderer] = shadow;
                }
                renderer.sharedMaterials = dark ? speakerShadowMaterials[renderer] : speakerMaterials[renderer];
            }
        }

        string SpeakerName(string character)
        {
            if (string.IsNullOrEmpty(character)) return "";
            if (character.Contains("marissa")) return "Marissa Faireborn";
            return DisplayName(character);
        }

        void FinishDialogue()
        {
            var done = dialogueDone;
            dialogueDone = null;
            dialogueLeft = dialogueRight = null;
            speakerMaterials.Clear();
            speakerShadowMaterials.Clear();
            if (done != null) done();
            else Show("map");
        }

        void RosterScreen()
        {
            squadForStory = false;
            SquadScreen();
        }

        void InventoryScreen()
        {
            var backdrop = SpriteImage(content, "Roster Loading Art", "UI/bot_roster", Vector2.zero, Vector2.one, false);
            if (backdrop != null) backdrop.color = new Color(1, 1, 1, .55f);
            SectionTitle("INVENTORY", "Your collected bots and gear are ready for deployment.");
            ActionButton("VIEW BOT ROSTER", "Choose a team", () => Show("roster"), .33f, .24f, .34f, .18f, true);
        }

        void ToggleBot(int index)
        {
            if (squad.Contains(index))
            {
                if (squad.Count == 1) { SetNotice("Keep at least one bot in the team"); return; }
                squad.Remove(index);
                selectedBot = squad[0];
            }
            else
            {
                if (squad.Count >= 3) { SetNotice("Team already has three bots"); return; }
                squad.Add(index);
                selectedBot = index;
            }
            Show(squadForStory ? "squad" : "roster");
        }

        void FocusBot(int index)
        {
            selectedBot = index;
            Show(squadForStory ? "squad" : "roster");
        }

        void SaveRoster()
        {
            if (squad.Count == 0) { SetNotice("Select at least one bot"); return; }
            var heroes = new List<string>();
            for (int i = 0; i < squad.Count; i++) heroes.Add("\"" + rosterKeys[squad[i]] + "\"");
            string saved = "{\"heroes\":[" + string.Join(",", heroes) + "],\"api\":6,\"nonce\":\"storyport\",\"teamID\":\"0\"}";
            StartCoroutine(Post("/bcg/setSavedTeam", saved, _ => { squadForStory = false; Show("base"); }));
        }

        GameObject SpawnWorld(string objectName, string resourceName, Vector3 position, Vector3 rotation, float scale)
        {
            var prefab = Resources.Load<GameObject>("StoryPort/" + resourceName);
            if (prefab == null)
            {
                Debug.LogError("StoryPort missing Resources prefab: StoryPort/" + resourceName);
                SetNotice("Converted 9.2 environment is missing: " + resourceName);
                return null;
            }
            var root = new GameObject("StoryPort World · " + objectName);
            worldRoots.Add(root);
            var instance = Instantiate(prefab, position, Quaternion.Euler(rotation), root.transform);
            instance.name = objectName;
            instance.transform.localScale *= scale;
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
                if (renderer.name.Equals("Sky", StringComparison.OrdinalIgnoreCase)) renderer.enabled = false;
            Debug.Log("StoryPort loaded " + resourceName + ": renderers=" + renderers.Length +
                " scale=" + instance.transform.localScale);
            return instance;
        }

        void DestroyWorld()
        {
            foreach (var root in worldRoots) if (root != null) Destroy(root);
            worldRoots.Clear();
            playerActor = null;
            enemyActor = null;
            playerAnimator = null;
            enemyAnimator = null;
        }

        IEnumerator Get(string path, Action<string> done) { return Request("GET", path, "", done); }
        IEnumerator Post(string path, string body, Action<string> done) { return Request("POST", path, body, done); }

        IEnumerator Request(string method, string path, string body, Action<string> done)
        {
            requestBusy = true;
            using (var request = new UnityWebRequest(serverUrl + path, method))
            {
                if (method == "POST")
                {
                    request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Accept", "application/json");
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    done?.Invoke(request.downloadHandler.text);
                    notice = "";
                }
                else SetNotice("CONNECTION ERROR · " + request.error);
            }
            requestBusy = false;
        }

        string ExtractJsonString(string json, string key)
        {
            return StoryRouteData.ReadString(json, key);
        }

        string ExtractBattleKey(string json)
        {
            var match = Regex.Match(json, "\\\"battleEnemy\\\"\\s*:\\s*\\{[^}]*\\\"key\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        string DisplayName(string key)
        {
            if (key.IndexOf("bludgeon", StringComparison.OrdinalIgnoreCase) >= 0) return "Bludgeon";
            if (key.IndexOf("optimus", StringComparison.OrdinalIgnoreCase) >= 0) return "Optimus Prime";
            if (key.IndexOf("stars", StringComparison.OrdinalIgnoreCase) >= 0) return "Starscream";
            if (key.IndexOf("ironhide", StringComparison.OrdinalIgnoreCase) >= 0) return "Ironhide";
            if (key.IndexOf("kickback", StringComparison.OrdinalIgnoreCase) >= 0) return "Kickback";
            if (key.IndexOf("mirage", StringComparison.OrdinalIgnoreCase) >= 0) return "Mirage";
            if (key.IndexOf("bumblebee", StringComparison.OrdinalIgnoreCase) >= 0) return "Bumblebee";
            if (key.IndexOf("grindor", StringComparison.OrdinalIgnoreCase) >= 0) return "Grindor";
            if (key.IndexOf("jazz", StringComparison.OrdinalIgnoreCase) >= 0) return "Jazz";
            return key.Replace('_', ' ').ToUpperInvariant();
        }

        string EncounterKeyForIndex(int index)
        {
            if (actIndex == 0) return index == 0 ? "bludgeon_gs_rd20" : index == 1 ? "fte_stars_gs_t3" : "ironhide_cin_rotf";
            if (actIndex == 1) return index == 0 ? "kickback_gs_kabam" : index == 1 ? "mirage_gs_deluxe2016" : "bumblebee_gs_kabam";
            return index == 0 ? "jazz_gs_twm05" : index == 1 ? "grindor_cin_rotf" : index == 2 ? "ironhide_cin_rotf" : "bludgeon_gs_rd20";
        }

        string PortraitFor(string key)
        {
            if (key.IndexOf("bludgeon", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_bludge_gs_large";
            if (key.IndexOf("stars", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_stars_gs_large";
            if (key.IndexOf("ironhide", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_ironh_c_rotf_large";
            if (key.IndexOf("kickback", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_kickb_gs_large";
            if (key.IndexOf("mirage", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_mirag_gs_large";
            if (key.IndexOf("bumblebee_gs", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_bumbl_gs_large";
            if (key.IndexOf("bumblebee", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_bumbl_c_large";
            if (key.IndexOf("grindor", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_grind_c_rotf_large";
            if (key.IndexOf("jazz", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_jazz_gs_large";
            if (key.IndexOf("optimus_cin", StringComparison.OrdinalIgnoreCase) >= 0) return "portrait_optimus_c_tf_large";
            return "portrait_optimus_gs_large";
        }

        void PlayState(Animator animator, string state)
        {
            if (animator == null) return;
            int hash = Animator.StringToHash(state);
            if (animator.HasState(0, hash)) animator.CrossFadeInFixedTime(hash, .06f, 0, 0);
        }

        void SetBoolIfPresent(Animator animator, string parameter, bool value)
        {
            if (animator == null) return;
            foreach (var item in animator.parameters)
                if (item.name == parameter && item.type == AnimatorControllerParameterType.Bool) { animator.SetBool(parameter, value); return; }
        }

        void CameraShake(float amount)
        {
            var camera = Camera.main;
            if (camera != null) StartCoroutine(Shake(camera.transform, amount));
        }

        IEnumerator Shake(Transform camera, float amount)
        {
            Vector3 home = camera.position;
            float end = Time.time + .13f;
            while (Time.time < end)
            {
                camera.position = home + UnityEngine.Random.insideUnitSphere * amount;
                yield return null;
            }
            camera.position = home;
        }

        void Backdrop(string resource, float alpha = 1)
        {
            var image = SpriteImage(uiRoot != null ? uiRoot : content, "Artwork Background", resource, Vector2.zero, Vector2.one, false);
            if (image != null) image.transform.SetAsFirstSibling();
            if (image != null) backgroundArt = image.gameObject;
            if (image != null) { image.color = new Color(1, 1, 1, alpha); image.raycastTarget = false; }
        }

        void DrawPathSegment(Vector2 a, Vector2 b, Color color)
        {
            float width = content.rect.width;
            float height = content.rect.height;
            var direction = (b - a).normalized;
            float inset = Mathf.Min(.065f, Vector2.Distance(a, b) * .3f);
            a += direction * inset;
            b -= direction * inset;
            var delta = Vector2.Scale(b - a, new Vector2(width, height));
            var segment = Panel(content, "Story Route", color, Vector2.zero, Vector2.zero);
            var rect = segment.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.sizeDelta = new Vector2(delta.magnitude, 7f);
            rect.anchoredPosition = Vector2.Scale((a + b) * .5f, new Vector2(width, height));
            rect.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        void HandleFightTouch()
        {
            if (Input.touchCount == 0) return;
            var touch = Input.GetTouch(0);
            var point = new Vector2(touch.position.x / Screen.width, touch.position.y / Screen.height);
            if (touch.phase == TouchPhase.Began)
            {
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touch.fingerId))
                {
                    touchTracking = false;
                    return;
                }
                if (point.y > .72f || point.y < .18f) return;
                touchTracking = true;
                touchStart = point;
                touchBeganAt = Time.time;
                if (point.x < .3f)
                {
                    guarding = true;
                    PlayState(playerAnimator, "Block");
                    UpdateFightHud();
                }
            }
            else if (touch.phase == TouchPhase.Ended && touchTracking)
            {
                touchTracking = false;
                var delta = point - touchStart;
                if (touchStart.x < .3f)
                {
                    guarding = false;
                    if (Mathf.Abs(delta.x) > .09f || Mathf.Abs(delta.y) > .09f) Dash(delta.x >= 0f);
                    else PlayState(playerAnimator, "Idle");
                    UpdateFightHud();
                    return;
                }
                if (touchStart.x > .38f)
                {
                    if (Mathf.Abs(delta.x) > .12f)
                        Dash(delta.x >= 0f);
                    else if (Time.time - touchBeganAt >= .34f)
                        PlayerAttack("MediumAttack01", 27, 10);
                    else
                    {
                        lightCombo = (lightCombo % 3) + 1;
                        PlayerAttack("LightAttack0" + lightCombo, 16 + (lightCombo == 3 ? 8 : 0), 7);
                    }
                }
            }
        }

        Image SpriteImage(Transform parent, string name, string resource, Vector2 min, Vector2 max, bool fit)
        {
            var sprite = Resources.Load<Sprite>("StoryPort/" + resource);
            if (sprite == null && resource.StartsWith("Portraits/", StringComparison.Ordinal))
                sprite = Resources.Load<Sprite>("StoryPort/Portraits/portrait_optimus_gs_large");
            if (sprite == null) return null;
            var image = MakeImage(parent, name, new Color(1, 1, 1, 1), min, max);
            image.sprite = sprite;
            image.preserveAspect = fit;
            return image;
        }

        void SectionTitle(string title, string detail)
        {
            LabelAt(content, "Screen Title", title, 31, TextAnchor.MiddleLeft, Color.white, new Vector2(.035f, .76f), new Vector2(.75f, .94f));
            LabelAt(content, "Screen Detail", detail, 18, TextAnchor.MiddleLeft, new Color(.55f, .75f, .82f), new Vector2(.04f, .7f), new Vector2(.86f, .79f));
            Panel(content, "Title Rule", new Color(.16f, .61f, .72f, .72f), new Vector2(.04f, .685f), new Vector2(.24f, .692f));
        }

        void TextAt(string title, string detail, float x, float y, float w, float h, int size, TextAnchor align)
        {
            var titleText = LabelAt(content, title, title, size, align, Color.white, new Vector2(x, y), new Vector2(x + w, y + h));
            titleText.fontStyle = FontStyle.Bold;
            if (!string.IsNullOrEmpty(detail))
                LabelAt(content, title + " Detail", detail, 20, align, new Color(.68f, .8f, .85f), new Vector2(x, y - .09f), new Vector2(x + w, y));
        }

        void ActionButton(string title, string detail, Action action, float x, float y, float w, float h, bool primary)
        {
            var button = Button(content, title, action, new Vector2(x, y), new Vector2(x + w, y + h));
            SetButtonSkin(button, primary ? "button_main_glowing" : "button_tab");
            var text = button.GetComponentInChildren<Text>();
            text.text = title + (string.IsNullOrEmpty(detail) ? "" : "\n<size=15>" + detail + "</size>");
            text.fontSize = 20;
            text.alignment = TextAnchor.MiddleCenter;
        }

        GameObject Panel(Transform parent, string name, Color color, Vector2 min, Vector2 max)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = color;
            return go;
        }

        Image MakeImage(Transform parent, string name, Color color, Vector2 min, Vector2 max)
        {
            return Panel(parent, name, color, min, max).GetComponent<Image>();
        }

        Button Button(Transform parent, string title, Action action, Vector2 min, Vector2 max)
        {
            var go = Panel(parent, title, new Color(.08f, .29f, .36f, .98f), min, max);
            var image = go.GetComponent<Image>();
            if (uiButtonSprite == null)
            {
                var texture = Resources.Load<Texture2D>("StoryPort/UI/background_button");
                if (texture != null)
                    uiButtonSprite = Sprite.Create(texture, new Rect(8, 80, 240, 95), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(30, 20, 30, 20));
            }
            var buttonSprite = uiButtonSprite;
            if (buttonSprite != null)
            {
                image.sprite = buttonSprite;
                image.type = Image.Type.Sliced;
                image.color = Color.white;
            }
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors; colors.highlightedColor = new Color(.2f, .55f, .6f); colors.pressedColor = new Color(.96f, .59f, .2f); button.colors = colors;
            button.onClick.AddListener(() => { Cue("finger_tap", .55f); action?.Invoke(); });
            var label = Label(go.transform, "Label", title, 18, TextAnchor.MiddleCenter, Color.white);
            Anchor(label.rectTransform, Vector2.zero, Vector2.one);
            return button;
        }

        void SetButtonLook(Button button, Color color)
        {
            if (button == null) return;
            var image = button.GetComponent<Image>();
            image.color = color;
        }

        void SetButtonSkin(Button button, string resource)
        {
            if (button == null) return;
            var image = button.GetComponent<Image>();
            var sprite = Resources.Load<Sprite>("StoryPort/UI/" + resource);
            if (sprite == null) return;
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            // The extracted main-button sprite is an uncolored white atlas
            // mask. Apply the original UI's cool cyan tint so white labels stay
            // legible over its body while preserving the sprite's glow alpha.
            var primaryTint = resource == "button_main_glowing"
                ? new Color(.12f, .65f, .78f, 1f)
                : Color.white;
            image.color = primaryTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = resource == "button_main_glowing" ? new Color(.78f, .95f, 1f, 1f) : Color.white;
            colors.pressedColor = resource == "button_main_glowing" ? new Color(1f, .72f, .35f, 1f) : Color.white;
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(1f, 1f, 1f, .45f);
            button.colors = colors;
        }

        Text LabelAt(Transform parent, string name, string value, int size, TextAnchor align, Color color, Vector2 min, Vector2 max)
        {
            var text = Label(parent, name, value, size, align, color);
            Anchor(text.rectTransform, min, max);
            return text;
        }

        Text Label(Transform parent, string name, string value, int size, TextAnchor align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = align;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            return text;
        }

        Image HealthBar(Transform parent, string name, Vector2 min, Vector2 max, float value, Color fill)
        {
            var background = MakeImage(parent, name + " Background", Color.white, min, max);
            background.sprite = Resources.Load<Sprite>("StoryPort/UI/progress_bar_fill_gray");
            var image = MakeImage(background.transform, name + " Fill", fill, Vector2.zero, Vector2.one);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillAmount = value;
            return image;
        }

        void Anchor(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        }

        void SetNotice(string value) { notice = value; }
        string Roman(int value) { return value == 1 ? "I" : value == 2 ? "II" : "III"; }
    }
}
