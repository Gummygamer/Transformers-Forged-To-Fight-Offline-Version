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
            "optimusprime_cin_tf", "bumblebee_gs_kabam", "ironhide_cin_rotf", "jazz_gs_twm05", "bludgeon_gs_rd20"
        };
        readonly string[] rosterNames = { "Optimus Prime", "Bumblebee", "Ironhide", "Jazz", "Bludgeon" };
        readonly List<int> squad = new List<int> { 0, 1 };
        readonly List<GameObject> worldRoots = new List<GameObject>();

        string serverUrl;
        string screen = "title";
        string notice = "";
        string currentQid = ActQids[0];
        string enemyKey = "bludgeon_gs_rd20";
        string playerKey = "optimusprime_cin_tf";
        string playerName = "Optimus Prime";
        string enemyName = "Bludgeon";
        string[] storyNodes;
        int actIndex;
        int mapX;
        int mapY;
        int playerHp = 100;
        int enemyHp = 100;
        int specialMeter;
        int enemySpecialMeter;
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
        Sprite uiButtonSprite;
        Transform headerRoot;
        Transform uiRoot;
        GameObject backgroundArt;
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
        Text enemySpecialText;
        Text specialButtonLabel;
        Text comboText;
        Image playerHpFill;
        Image enemyHpFill;
        Image specialFill;
        Image enemySpecialFill;

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
            BuildCamera();
            BuildUI();
            Show("title");
            StartCoroutine(LoadCampaignMetadata());
        }

        IEnumerator LoadCampaignMetadata()
        {
            yield return StartCoroutine(Get("/base/active", _ => { }));
            for (int i = 0; i < ActQids.Length; i++)
            {
                string response = "";
                yield return StartCoroutine(Get("/quests/quest-detail/" + ActQids[i], json => response = json));
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
            camera.transform.position = new Vector3(0, 5.1f, -17.5f);
            camera.transform.LookAt(new Vector3(0, 2.2f, -5f));
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
            var logo = SpriteImage(bar.transform, "Logo", "UI/tff_logo_en", new Vector2(.012f, .22f), new Vector2(.12f, .82f), true);
            if (logo != null) logo.preserveAspect = true;
            LabelAt(bar.transform, "Commander", "COMMANDER  ·  LV 2", 16, TextAnchor.MiddleLeft, new Color(.77f, .87f, .92f), new Vector2(.13f, .48f), new Vector2(.27f, .88f));
            LabelAt(bar.transform, "Resources", "⚡ 100/100      ◈ 7,800      ✦ 99", 18, TextAnchor.MiddleRight, new Color(.89f, .91f, .94f), new Vector2(.7f, .48f), new Vector2(.985f, .88f));
            string[] tabs = { "BASE", "BOTS", "INVENTORY", "FIGHT", "ALLIANCE", "CRYSTALS", "STORE" };
            Action[] actions = { () => Show("base"), () => Show("roster"), () => Show("inventory"), () => Show("story"), () => Show("story"), () => Show("roster"), () => Show("roster") };
            var navNormal = Resources.Load<Sprite>("StoryPort/UI/global_nav_button");
            var navActive = Resources.Load<Sprite>("StoryPort/UI/global_nav_button_active");
            float left = .12f;
            float width = .083f;
            for (int i = 0; i < tabs.Length; i++)
            {
                int index = i;
                float x = left + i * width;
                var button = Button(bar.transform, tabs[i], actions[i], new Vector2(x, .06f), new Vector2(x + width - .006f, .49f));
                var image = button.GetComponent<Image>();
                image.sprite = index == 0 || index == 3 ? (navActive != null ? navActive : navNormal) : navNormal;
                image.type = Image.Type.Simple;
                image.color = Color.white;
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
            if (headerRoot != null) headerRoot.gameObject.SetActive(next != "title" && next != "loading");
            if (content == null) return;
            foreach (Transform child in content) Destroy(child.gameObject);
            if (next != "fight" && next != "victory" && next != "defeat") DestroyWorld();
            if (next == "title") TitleScreen();
            else if (next == "loading") LoadingScreen();
            else if (next == "base") BaseScreen();
            else if (next == "story") StoryScreen();
            else if (next == "chapter") ChapterScreen();
            else if (next == "map") MapScreen();
            else if (next == "squad") SquadScreen();
            else if (next == "fight") FightScreen();
            else if (next == "victory") ResultScreen(true);
            else if (next == "defeat") ResultScreen(false);
            else if (next == "roster") RosterScreen();
            else if (next == "inventory") InventoryScreen();
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
            Backdrop("UI/planet_landscape");
            var logo = SpriteImage(content, "Loading Logo", "UI/tff_logo_en", new Vector2(.34f, .66f), new Vector2(.66f, .89f), false);
            if (logo != null) logo.preserveAspect = true;
            TextAt("INITIALIZING TELETRAN", "Loading base and story data…", .27f, .35f, .46f, .14f, 23, TextAnchor.MiddleCenter);
            Panel(content, "Loading Track", new Color(.04f, .15f, .2f, .9f), new Vector2(.32f, .28f), new Vector2(.68f, .3f));
            Panel(content, "Loading Fill", new Color(.15f, .77f, .89f, .95f), new Vector2(.32f, .28f), new Vector2(.56f, .3f));
            StartCoroutine(AnimateLoading());
        }

        IEnumerator AnimateLoading()
        {
            yield return new WaitForSeconds(1.15f);
            if (screen == "loading") Show("base");
        }

        void BaseScreen()
        {
            FrameWorld(SpawnWorld("Base", "PrimordialBase", Vector3.zero, Vector3.zero, .012f));
            SectionTitle("COMMAND CENTER", "Your base is operational. Select a mission and deploy.");
            ActionButton("STORY MISSIONS", "Follow the three-act campaign", () => Show("story"), .68f, .56f, .27f, .19f, true);
            ActionButton("BOT ROSTER", "View and select your squad", () => Show("roster"), .68f, .32f, .27f, .18f, false);
            LabelAt(content, "Welcome", "WELCOME, COMMANDER", 19, TextAnchor.MiddleLeft, new Color(.83f, .9f, .92f), new Vector2(.065f, .18f), new Vector2(.48f, .25f));
        }

        void StoryScreen()
        {
            LabelAt(content, "Story Missions Header", "STORY MISSIONS", 27, TextAnchor.MiddleCenter, Color.white, new Vector2(.2f, .89f), new Vector2(.8f, .98f));
            LabelAt(content, "Story Missions Subtitle", "Gain XP, Energon, and upgrade materials as you uncover the mysteries of New Quintessa.", 13, TextAnchor.MiddleCenter, new Color(.71f, .81f, .86f), new Vector2(.12f, .83f), new Vector2(.88f, .9f));
            string[] artNames = { "UI/fightstoryimglrg_hd", "UI/fightstoryimglrg_sd", "UI/fightstoryimgsml_hd" };
            for (int i = 0; i < 3; i++)
            {
                int act = i;
                float x = .035f + i * .315f;
                var card = Panel(content, "Story Act Card " + (i + 1), new Color(.012f, .028f, .052f, .96f), new Vector2(x, .1f), new Vector2(x + .29f, .79f));
                var art = SpriteImage(card.transform, "Act Art", artNames[i], new Vector2(.035f, .39f), new Vector2(.965f, .97f), false);
                if (art != null) art.preserveAspect = false;
                Panel(card.transform, "Art Shade", new Color(.01f, .025f, .045f, .78f), new Vector2(.02f, .02f), new Vector2(.98f, .43f));
                LabelAt(card.transform, "Act Label", "ACT " + Roman(i + 1), 13, TextAnchor.MiddleLeft, new Color(.28f, .8f, .91f), new Vector2(.08f, .33f), new Vector2(.9f, .42f));
                LabelAt(card.transform, "Act Title", ActTitles[i], 17, TextAnchor.MiddleLeft, Color.white, new Vector2(.08f, .23f), new Vector2(.92f, .35f));
                LabelAt(card.transform, "Act Description", ActDescriptions[i], 11, TextAnchor.UpperLeft, new Color(.7f, .8f, .85f), new Vector2(.08f, .1f), new Vector2(.92f, .24f));
                var select = Button(card.transform, "SELECT ACT", () => { actIndex = act; Show("chapter"); }, new Vector2(.52f, .025f), new Vector2(.93f, .105f));
                SetButtonSkin(select, i == 0 ? "button_main_glowing" : "button_tab");
                var selectLabel = select.GetComponentInChildren<Text>();
                selectLabel.text = "ACT " + Roman(i + 1);
                selectLabel.fontSize = 12;
            }
        }

        void ChapterScreen()
        {
            var panel = Panel(content, "Chapter Panel", new Color(.025f, .08f, .125f, .96f), new Vector2(.12f, .1f), new Vector2(.88f, .9f));
            SpriteImage(panel.transform, "Chapter Art", "UI/fightstoryimglrg_hd", new Vector2(.03f, .05f), new Vector2(.48f, .95f), false);
            LabelAt(panel.transform, "Act", "ACT " + Roman(actIndex + 1), 18, TextAnchor.MiddleLeft, new Color(.3f, .76f, .85f), new Vector2(.54f, .75f), new Vector2(.94f, .9f));
            LabelAt(panel.transform, "Title", ActTitles[actIndex], 32, TextAnchor.MiddleLeft, Color.white, new Vector2(.54f, .59f), new Vector2(.94f, .76f));
            LabelAt(panel.transform, "Description", ActDescriptions[actIndex], 21, TextAnchor.UpperLeft, new Color(.69f, .8f, .85f), new Vector2(.54f, .4f), new Vector2(.94f, .58f));
            ActionButton("CHAPTER 1", "Resume this act's mission", () => OpenStoryMap(), .55f, .19f, .37f, .15f, true);
            ActionButton("BACK", "Story missions", () => Show("story"), .55f, .04f, .18f, .12f, false);
        }

        void MapScreen()
        {
            FrameWorld(BuildStoryBoard(), 1.25f);
            var camera = Camera.main;
            if (camera != null)
            {
                float mapSize = (actIndex == 2 ? 5f : 4f) * 20f;
                float distance = mapSize * 1.1f;
                var target = new Vector3(0, .4f, 0);
                camera.transform.position = target + new Vector3(0, distance * .84f, -distance * .54f);
                camera.transform.LookAt(target);
            }
            SectionTitle("ACT " + Roman(actIndex + 1) + "  ·  " + ActTitles[actIndex], "Select an encounter to continue the campaign");
            DrawBoardNodes();
            ActionButton("SQUAD", "Select the team for the next fight", () => { squadForStory = true; Show("squad"); }, .05f, .08f, .2f, .16f, false);
        }

        GameObject BuildStoryBoard()
        {
            var board = new GameObject("StoryPort World · Primordial Story Board");
            worldRoots.Add(board);
            BuildPrimordialGroundGrid(board, actIndex == 2 ? 5 : 4);
            var pieces = actIndex == 2
                ? new[] { "landmass_3x3", "landmass_2x2", "landmass_3x3_alt", "landmass_3x5", "landmass_4x4" }
                : new[] { "landmass_3x3", "landmass_1x1", "landmass_3x3_alt", "landmass_2x2", "landmass_3x5" };
            var x = actIndex == 2
                ? new[] { -30f, -15f, 0f, 0f, 15f }
                : new[] { -30f, -15f, 0f, 15f, 30f };
            var z = actIndex == 2
                ? new[] { 0f, 0f, 10f, -10f, 0f }
                : new[] { -1f, 2f, -2f, 2f, 0f };

            for (int i = 0; i < pieces.Length; i++)
            {
                var prefab = Resources.Load<GameObject>("StoryPort/StoryBoard/" + pieces[i]);
                if (prefab == null)
                {
                    Debug.LogError("StoryPort missing converted 9.2 board module: " + pieces[i]);
                    SetNotice("Converted primordial board modules are missing");
                    continue;
                }
                var tile = Instantiate(prefab, new Vector3(x[i], 0, z[i]), Quaternion.Euler(0, (i % 2) * 180f, 0), board.transform);
                tile.name = "Primordial Route Terrain " + (i + 1);
                var renderers = tile.GetComponentsInChildren<Renderer>(true);
                Bounds bounds = default(Bounds);
                bool found = false;
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled) continue;
                    if (!found) { bounds = renderer.bounds; found = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (found)
                {
                    float footprint = Mathf.Max(bounds.size.x, bounds.size.z);
                    if (footprint > .01f)
                        tile.transform.localScale *= Mathf.Clamp(15f / footprint, .08f, 20f);
                }
            }
            Debug.Log("StoryPort built a route from converted Primordial landmass modules for act " + (actIndex + 1));
            return board;
        }

        void BuildPrimordialGroundGrid(GameObject board, int dimension)
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
                    // The backend's 4x4 and 5x5 maps use the game's 20 m
                    // tile spacing, centered around the origin.
                    vertices[index] = new Vector3(-halfMap + x * tileSize, -.35f, -halfMap + z * tileSize);
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
            var surface = new GameObject("Primordial 20m terrain tiles", typeof(MeshFilter), typeof(MeshRenderer));
            surface.transform.SetParent(board.transform, false);
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            surface.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        void DrawBoardNodes()
        {
            var labels = ActNodeLabels[actIndex].Split('|');
            float[][] points = actIndex == 2
                ? new[] { new[] { .14f, .48f }, new[] { .34f, .48f }, new[] { .54f, .66f }, new[] { .54f, .3f }, new[] { .78f, .48f } }
                : new[] { new[] { .12f, .48f }, new[] { .32f, .48f }, new[] { .52f, .48f }, new[] { .72f, .48f }, new[] { .9f, .48f } };
            int count = Math.Min(labels.Length, actIndex == 2 ? 4 : labels.Length);
            if (actIndex == 2)
            {
                DrawPathSegment(new Vector2(.14f, .48f), new Vector2(.34f, .48f), new Color(.19f, .69f, .84f, .9f));
                DrawPathSegment(new Vector2(.34f, .48f), new Vector2(.54f, .66f), new Color(.19f, .69f, .84f, .9f));
                DrawPathSegment(new Vector2(.34f, .48f), new Vector2(.54f, .3f), new Color(.19f, .69f, .84f, .9f));
                DrawPathSegment(new Vector2(.54f, .66f), new Vector2(.78f, .48f), new Color(.19f, .69f, .84f, .9f));
                DrawPathSegment(new Vector2(.54f, .3f), new Vector2(.78f, .48f), new Color(.19f, .69f, .84f, .9f));
            }
            else
            {
                var last = new Vector2(.12f, .48f);
                for (int i = 0; i < count; i++)
                {
                    var next = new Vector2(points[i + 1][0], points[i + 1][1]);
                    DrawPathSegment(last, next, new Color(.19f, .69f, .84f, .9f));
                    last = next;
                }
            }
            for (int i = 0; i < count; i++)
            {
                int index = i;
                int p = actIndex == 2 && i == 2 ? 3 : i + 1;
                float x = points[p][0], y = points[p][1];
                int targetX = actIndex == 2 ? (i == 0 ? 1 : i == 3 ? 3 : 2) : i + 1;
                int targetY = actIndex == 2 ? (i == 0 ? 2 : i == 1 ? 1 : i == 2 ? 3 : 2) : mapY;
                bool available = IsNextEncounter(targetX, targetY);
                var panel = Panel(content, "Path Node " + i, Color.clear, new Vector2(x - .07f, y - .115f), new Vector2(x + .07f, y + .115f));
                var portrait = SpriteImage(panel.transform, "Opponent Portrait", "Portraits/" + PortraitFor(EncounterKeyForIndex(i)), new Vector2(.08f, .16f), new Vector2(.92f, .94f), true);
                if (portrait != null) portrait.preserveAspect = true;
                var frame = SpriteImage(panel.transform, "Quest Card Frame", "UI/bosscard_frame0", Vector2.zero, Vector2.one, true);
                if (frame != null) frame.raycastTarget = false;
                if (portrait != null) portrait.raycastTarget = false;
                var nodeLabel = LabelAt(panel.transform, "Node Label", labels[i].ToUpperInvariant(), 13, TextAnchor.MiddleCenter, Color.white, new Vector2(-.55f, -.35f), new Vector2(1.55f, .2f));
                nodeLabel.raycastTarget = false;
                if (available && frame != null) frame.color = new Color(.54f, .94f, 1f, 1f);
                else if (frame != null) frame.color = new Color(.62f, .68f, .75f, .8f);
                var hit = panel.GetComponent<Image>();
                // The game node is a floating portrait medallion over the
                // board; retain a transparent hit target instead of a panel.
                hit.color = Color.clear;
                var button = panel.AddComponent<Button>();
                button.targetGraphic = hit;
                button.transition = Selectable.Transition.None;
                button.interactable = available;
                int nodeX = targetX, nodeY = targetY;
                button.onClick.AddListener(() => MoveToEncounter(nodeX, nodeY));
            }
        }

        bool IsNextEncounter(int targetX, int targetY)
        {
            if (pendingEncounter && targetX == mapX && targetY == mapY) return true;
            if (actIndex != 2) return targetX == mapX + 1 && targetY == mapY;
            if (mapX == 0) return targetX == 1 && targetY == 2;
            if (mapX == 1) return targetX == 2 && (targetY == 1 || targetY == 3);
            if (mapX == 2) return targetX == 3 && targetY == 2;
            return false;
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
                camera.transform.position = new Vector3(0, 5.1f, -12.5f);
                camera.transform.LookAt(new Vector3(0, 2.1f, 0));
            }
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Roster Display Platform";
            platform.transform.position = new Vector3(0, -.12f, 0);
            platform.transform.localScale = new Vector3(13f, .24f, 3.5f);
            var platformRenderer = platform.GetComponent<Renderer>();
            var platformMaterial = new Material(Shader.Find("Standard"));
            platformMaterial.color = new Color(.075f, .16f, .21f);
            platformRenderer.sharedMaterial = platformMaterial;
            worldRoots.Add(platform);
            for (int i = 0; i < rosterKeys.Length; i++)
            {
                var actor = SpawnBot(rosterKeys[i], new Vector3((i - 2) * 2.45f, 0, 0), 180f, .68f);
                if (actor == null) continue;
                worldRoots.Add(actor);
                PlayState(FindFightAnimator(actor), "Idle");
            }
            SectionTitle("SELECT YOUR BOTS", "Choose up to three bots for this encounter.");
            for (int i = 0; i < rosterKeys.Length; i++)
            {
                int index = i;
                float x = .035f + i * .187f;
                var tile = Button(content, rosterNames[i], () => ToggleBot(index), new Vector2(x, .17f), new Vector2(x + .175f, .34f));
                SetButtonSkin(tile, squad.Contains(i) ? "button_tab_active" : "button_tab");
                var label = tile.GetComponentInChildren<Text>();
                label.text = rosterNames[i] + (squad.Contains(i) ? "\nSELECTED" : "\nTAP TO ADD");
                label.fontSize = 14;
                label.alignment = TextAnchor.MiddleCenter;
            }
            if (squadForStory)
            {
                ActionButton("DEPLOY SQUAD", "Save team and open this encounter", () => SaveSquadAndBegin(), .65f, .025f, .3f, .12f, true);
                ActionButton("BACK TO BOARD", "Return to the story route", () => Show("map"), .33f, .025f, .3f, .12f, false);
            }
            else
            {
                ActionButton("SAVE ROSTER", "Save your selected team", () => SaveRoster(), .65f, .025f, .3f, .12f, true);
                ActionButton("BACK TO BASE", "Return to command center", () => Show("base"), .33f, .025f, .3f, .12f, false);
            }
        }

        void FightScreen()
        {
            if (playerActor == null) SpawnFightWorld();
            guarding = false;
            enemyBusy = false;
            playerBusy = false;
            queuedAttack = false;
            enemySpecialMeter = 0;
            lightCombo = 0;
            comboHits = 0;
            lastPlayerHit = 0;
            nextEnemyTurn = Time.time + 2.8f;
            var hud = Panel(content, "Fight HUD", new Color(0, 0, 0, 0), Vector2.zero, Vector2.one);
            hud.GetComponent<Image>().raycastTarget = false;
            var playerPortrait = SpriteImage(hud.transform, "Player Portrait", "Portraits/" + PortraitFor(playerKey), new Vector2(.012f, .81f), new Vector2(.075f, .99f), true);
            if (playerPortrait != null) playerPortrait.preserveAspect = true;
            var enemyPortrait = SpriteImage(hud.transform, "Enemy Portrait", "Portraits/" + PortraitFor(enemyKey), new Vector2(.925f, .81f), new Vector2(.988f, .99f), true);
            if (enemyPortrait != null) enemyPortrait.preserveAspect = true;
            var playerPortraitFrame = SpriteImage(hud.transform, "Player Portrait Frame", "UI/frame_hud_portrait", new Vector2(.012f, .81f), new Vector2(.075f, .99f), true);
            if (playerPortraitFrame != null) { playerPortraitFrame.preserveAspect = true; playerPortraitFrame.raycastTarget = false; }
            var enemyPortraitFrame = SpriteImage(hud.transform, "Enemy Portrait Frame", "UI/frame_hud_portrait", new Vector2(.925f, .81f), new Vector2(.988f, .99f), true);
            if (enemyPortraitFrame != null) { enemyPortraitFrame.preserveAspect = true; enemyPortraitFrame.raycastTarget = false; }
            LabelAt(hud.transform, "Player Name", playerName, 16, TextAnchor.MiddleLeft, Color.white, new Vector2(.08f, .91f), new Vector2(.34f, .99f));
            LabelAt(hud.transform, "Enemy Name", enemyName, 16, TextAnchor.MiddleRight, Color.white, new Vector2(.66f, .91f), new Vector2(.92f, .99f));
            playerHpText = LabelAt(hud.transform, "Player Health", playerHp + "%", 11, TextAnchor.MiddleLeft, Color.white, new Vector2(.08f, .81f), new Vector2(.16f, .89f));
            enemyHpText = LabelAt(hud.transform, "Enemy Health", enemyHp + "%", 11, TextAnchor.MiddleRight, Color.white, new Vector2(.84f, .81f), new Vector2(.92f, .89f));
            playerHpFill = HealthBar(hud.transform, "Player Health Bar", new Vector2(.16f, .825f), new Vector2(.4f, .89f), playerHp / 100f, new Color(.15f, .78f, .52f));
            enemyHpFill = HealthBar(hud.transform, "Enemy Health Bar", new Vector2(.6f, .825f), new Vector2(.84f, .89f), enemyHp / 100f, new Color(.9f, .29f, .23f));
            enemySpecialFill = HealthBar(hud.transform, "Enemy Special Meter", new Vector2(.6f, .785f), new Vector2(.84f, .8f), enemySpecialMeter / 3f, new Color(1f, .48f, .13f));
            enemySpecialText = LabelAt(hud.transform, "Enemy Special Charges", "SPECIAL  " + enemySpecialMeter + " / 3", 10, TextAnchor.MiddleRight, new Color(1f, .77f, .53f), new Vector2(.6f, .755f), new Vector2(.84f, .785f));
            var pause = Button(hud.transform, "PAUSE", TogglePause, new Vector2(.482f, .9f), new Vector2(.518f, .99f));
            SetButtonSkin(pause, "button_tab");
            pause.GetComponentInChildren<Text>().text = "Ⅱ";
            pause.GetComponentInChildren<Text>().fontSize = 24;
            comboText = LabelAt(hud.transform, "Combo", "", 24, TextAnchor.MiddleLeft, Color.white, new Vector2(.015f, .49f), new Vector2(.17f, .64f));

            var dodge = Button(content, "BLOCK", ToggleBlock, new Vector2(.025f, .025f), new Vector2(.16f, .21f));
            SetButtonSkin(dodge, "button_main_glowing");
            var attack = Button(content, "ATTACK", () =>
            {
                lightCombo = (lightCombo % 3) + 1;
                PlayerAttack("LightAttack0" + lightCombo, 16 + (lightCombo == 3 ? 8 : 0), 7);
            }, new Vector2(.84f, .025f), new Vector2(.975f, .21f));
            SetButtonSkin(attack, "button_main_glowing");
            var specialButton = Button(content, "SPECIAL", SpecialAttack, new Vector2(.425f, .025f), new Vector2(.575f, .17f));
            SetButtonSkin(specialButton, "button_tab_active");
            specialButtonLabel = specialButton.GetComponentInChildren<Text>();
            specialButtonLabel.text = "SPECIAL";
            specialButtonLabel.fontSize = 16;
            specialFill = HealthBar(content, "Special Meter", new Vector2(.425f, .18f), new Vector2(.575f, .195f), specialMeter / 3f, new Color(.28f, .72f, 1f));
            specialText = LabelAt(content, "Special Charges", "SPECIAL  " + specialMeter + " / 3", 12, TextAnchor.MiddleCenter, new Color(.72f, .89f, .98f), new Vector2(.425f, .17f), new Vector2(.575f, .22f));
            UpdateFightHud();
        }

        void SpawnFightWorld()
        {
            ResetCamera();
            var stage = SpawnWorld("Chicago Fight Stage", "ChicagoFightStage", Vector3.zero, Vector3.zero, .01f);
            if (stage != null) stage.transform.localPosition += new Vector3(-2.84f, 0, 0);
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

        GameObject SpawnBot(string key, Vector3 position, float yaw, float scale)
        {
            var prefab = Resources.Load<GameObject>("StoryPort/Bots/" + key);
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
            PlayState(playerAnimator, state);
            yield return new WaitForSeconds(state == "SpecialAttack03" ? 1f : state.StartsWith("Special", StringComparison.Ordinal) ? .58f : state.StartsWith("Medium", StringComparison.Ordinal) ? .43f : .28f);
            if (enemyHp <= 0 || screen != "fight") { playerBusy = false; yield break; }
            if (enemyAnimator != null) PlayState(enemyAnimator, state == "SpecialAttack03" ? "SpecialAttack03HitReaction" : "HitReactionLightLeftHigh");
            enemyHp = Mathf.Max(0, enemyHp - damage);
            specialMeter = Mathf.Min(3, specialMeter + 1);
            if (Time.time - lastPlayerHit > 1.4f) comboHits = 0;
            comboHits++;
            lastPlayerHit = Time.time;
            if (comboText != null) comboText.text = comboHits + " HITS!\n<size=16>GOOD!</size>";
            if (playerActor != null && enemyActor != null)
            {
                var direction = (enemyActor.transform.position - playerActor.transform.position).normalized;
                playerActor.transform.position += direction * .22f;
                CameraShake(.04f);
            }
            UpdateFightHud();
            playerBusy = false;
            if (enemyHp == 0) { StartCoroutine(ResolveWinAfterImpact()); yield break; }
            nextEnemyTurn = Time.time + Mathf.Max(.8f, 2.4f - charge * .04f);
            RunQueuedAttack();
        }

        IEnumerator ResolveWinAfterImpact()
        {
            yield return new WaitForSeconds(.85f);
            if (enemyActor != null) enemyActor.SetActive(false);
            StartCoroutine(ResolveWin());
        }

        void Dash()
        {
            if (screen != "fight" || playerActor == null) return;
            guarding = false;
            PlayState(playerAnimator, "Dash");
            if (enemyActor != null)
            {
                var towardEnemy = (enemyActor.transform.position - playerActor.transform.position).normalized;
                playerActor.transform.position += towardEnemy * .95f;
            }
            specialMeter = Mathf.Min(3, specialMeter + 1);
            evadeUntil = Time.time + .65f;
            nextEnemyTurn = Time.time + .8f;
            UpdateFightHud();
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
            specialMeter = 0;
            enemySpecialMeter = 0;
            playerHp = 100;
            enemyHp = 100;
            nextEnemyTurn = Time.time + 2.8f;
            DestroyWorld();
            Show("fight");
        }

        void SpecialAttack()
        {
            if (specialMeter < 3)
            {
                SetNotice("Build 3 special charges with attacks and dodges");
                return;
            }
            specialMeter = 0;
            PlayerAttack("SpecialAttack03", 45, 8);
            specialMeter = 0;
            UpdateFightHud();
        }

        void EnemyTurn()
        {
            if (screen != "fight" || enemyBusy || enemyHp <= 0 || playerHp <= 0) return;
            StartCoroutine(EnemyAttack());
        }

        IEnumerator EnemyAttack()
        {
            enemyBusy = true;
            bool special = enemySpecialMeter >= 3;
            PlayState(enemyAnimator, special ? "SpecialAttack03" : "LightAttack01");
            SetNotice(special ? "ENEMY SPECIAL · BLOCK OR DODGE" : "INCOMING ATTACK · BLOCK OR DODGE");
            yield return new WaitForSeconds(special ? 1f : .58f);
            if (playerHp > 0 && enemyHp > 0)
            {
                int damage = Time.time < evadeUntil ? 0 : guarding ? (special ? 7 : 1) : (special ? 24 : 4);
                playerHp = Mathf.Max(0, playerHp - damage);
                comboHits = 0;
                if (comboText != null) comboText.text = "";
                if (special) enemySpecialMeter = 0;
                else enemySpecialMeter = Mathf.Min(3, enemySpecialMeter + 1);
                specialMeter = guarding ? Mathf.Min(3, specialMeter + 1) : specialMeter;
                PlayState(playerAnimator, guarding ? "BlockReact" : damage == 0 ? "Dash" : special ? "SpecialAttack03HitReaction" : "HitReactionLightRightHigh");
                CameraShake(guarding || damage == 0 ? .05f : .12f);
                if (damage == 0) SetNotice("DODGED");
                UpdateFightHud();
                if (playerHp == 0)
                {
                    PlayState(playerAnimator, "KnockoutLight");
                    yield return new WaitForSeconds(.8f);
                    Show("defeat");
                }
            }
            enemyBusy = false;
            if (guarding)
            {
                guarding = false;
                PlayState(playerAnimator, "Idle");
            }
            nextEnemyTurn = Time.time + 4.2f;
            RunQueuedAttack();
        }

        void Update()
        {
            if (screen == "fight" && !paused) HandleFightTouch();
            if (screen == "fight" && !paused && !requestBusy && !enemyBusy && !playerBusy && Time.time >= nextEnemyTurn && playerHp > 0 && enemyHp > 0)
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
            if (enemySpecialFill != null) enemySpecialFill.fillAmount = enemySpecialMeter / 3f;
            if (enemySpecialText != null) enemySpecialText.text = "SPECIAL  " + enemySpecialMeter + " / 3";
            if (specialButtonLabel != null) specialButtonLabel.text = specialMeter >= 3 ? "SPECIAL 3" : "SPECIAL";
        }

        void MoveStory(int dx, int dy)
        {
            if (requestBusy) return;
            var path = "/quests/quest-movedir/" + currentQid + "-0/" + dx + "/" + dy;
            StartCoroutine(Post(path, "{}", response =>
            {
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
                    specialMeter = 0;
                    playerKey = rosterKeys[squad[0]];
                    playerName = rosterNames[squad[0]];
                    Show("loading");
                    StartCoroutine(LoadFight());
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

        IEnumerator LoadFight()
        {
            yield return new WaitForSeconds(.75f);
            if (screen == "loading") Show("fight");
        }

        void SaveSquadAndBegin()
        {
            if (squad.Count == 0) { SetNotice("Select at least one bot"); return; }
            var heroes = new List<string>();
            for (int i = 0; i < squad.Count; i++) heroes.Add("\"" + rosterKeys[squad[i]] + "\"");
            string saved = "{\"heroes\":[" + string.Join(",", heroes) + "],\"api\":6,\"nonce\":\"storyport\",\"teamID\":\"0\"}";
            StartCoroutine(Post("/bcg/setSavedTeam", saved, _ => BeginStory()));
        }

        void BeginStory()
        {
            currentQid = ActQids[actIndex];
            mapX = 0;
            mapY = actIndex == 2 ? 2 : 1;
            pendingEncounter = false;
            storyNodes = ActNodeLabels[actIndex].Split('|');
            var parts = new List<string> { "\"setId\":\"" + StorySet + "\"" };
            for (int i = 0; i < squad.Count; i++) parts.Add("\"tm" + i + "\":\"" + rosterKeys[squad[i]] + "\"");
            StartCoroutine(Post("/quests/quest-begin/" + currentQid, "{" + string.Join(",", parts) + "}", response =>
            {
                ReadCurrentPosition(response);
                Show("loading");
                StartCoroutine(LoadBoardAndMove());
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
                StartCoroutine(ProbeStoryPosition());
            }));
        }

        IEnumerator ProbeStoryPosition()
        {
            string response = "";
            yield return StartCoroutine(Post("/quests/quest-movedir/" + currentQid + "-0/0/0", "{}", json => response = json));
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

        IEnumerator LoadBoardAndMove()
        {
            yield return new WaitForSeconds(.7f);
            if (screen != "loading") yield break;
            yield return StartCoroutine(ProbeStoryPosition());
        }

        IEnumerator ResolveWin()
        {
            string body = "{\"qid\":\"" + currentQid + "\",\"results\":{\"result\":\"WON\"}}";
            yield return StartCoroutine(Post("/matches/resolve-match/quests_fight", body, _ => pendingEncounter = false));
            Show("victory");
            SetNotice("VICTORY SAVED TO THE STORY ROUTE");
        }

        void ResultScreen(bool won)
        {
            var panel = Panel(content, "Match Result", new Color(.02f, .065f, .105f, .92f), new Vector2(.28f, .29f), new Vector2(.72f, .83f));
            LabelAt(panel.transform, "Result", won ? "VICTORY" : "DEFEAT", 42, TextAnchor.MiddleCenter, won ? new Color(.45f, .94f, .78f) : new Color(1f, .48f, .42f), new Vector2(.08f, .57f), new Vector2(.92f, .93f));
            LabelAt(panel.transform, "Opponent", playerName + "   VS   " + enemyName, 20, TextAnchor.MiddleCenter, Color.white, new Vector2(.08f, .37f), new Vector2(.92f, .6f));
            LabelAt(panel.transform, "Final Health", "" + playerHp + "%   ·   " + enemyHp + "%", 18, TextAnchor.MiddleCenter, new Color(.69f, .84f, .88f), new Vector2(.08f, .22f), new Vector2(.92f, .39f));
            if (won) ActionButton("CONTINUE STORY", "Use the next node from the server route", () => ContinueRoute(), .39f, .1f, .22f, .15f, true);
            else ActionButton("RETRY ENCOUNTER", "Reset this battle", RetryFight, .39f, .1f, .22f, .15f, true);
        }

        void ContinueRoute()
        {
            if (actIndex < 2 && mapX >= 3)
            {
                actIndex++;
                Show("chapter");
                return;
            }
            if (actIndex == 2 && mapX >= 3)
            {
                SetNotice("ALL THREE ACTS COMPLETE");
                Show("story");
                return;
            }
            Show("map");
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
            if (squad.Contains(index)) { if (squad.Count > 1) squad.Remove(index); }
            else if (squad.Count < 3) squad.Add(index);
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
            var match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        string ExtractBattleKey(string json)
        {
            var match = Regex.Match(json, "\\\"battleEnemy\\\"\\s*:\\s*\\{[^}]*\\\"key\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        string DisplayName(string key)
        {
            if (key.IndexOf("bludgeon", StringComparison.OrdinalIgnoreCase) >= 0) return "Bludgeon";
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
            segment.transform.SetAsFirstSibling();
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
                    if (Mathf.Abs(delta.x) > .09f || Mathf.Abs(delta.y) > .09f) Dash();
                    else PlayState(playerAnimator, "Idle");
                    UpdateFightHud();
                    return;
                }
                if (touchStart.x > .38f)
                {
                    if (Time.time - touchBeganAt >= .34f)
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
            button.onClick.AddListener(() => action?.Invoke());
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
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
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
