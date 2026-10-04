#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryPort.Editor
{
    public static class StoryPortPreviewCapture
    {
        // Renders the actual screen construction at a fixed resolution. The
        // resulting screenshot stays local; no converted game art enters Git.
        public static void CaptureStory()
        {
            Capture("story");
        }

        public static void CaptureSquad()
        {
            Capture("squad");
        }

        public static void CaptureLoading()
        {
            Capture("loading");
        }

        public static void InspectBase()
        {
            var prefab = Resources.Load<GameObject>("StoryPort/PrimordialBase");
            var root = UnityEngine.Object.Instantiate(prefab);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = "";
                foreach (var m in r.sharedMaterials) mats += (m != null ? m.name + "[" + (m.shader != null ? m.shader.name : "-") + "]" : "null") + ",";
                Debug.Log("StoryPort base renderer " + r.name + " mats=" + mats + " b=" + r.bounds.center + r.bounds.size);
            }
        }

        public static void InspectBuilding()
        {
            var bld = Environment.GetEnvironmentVariable("SP_BLD") ?? "Buildings/battle_centre";
            var prefab = Resources.Load<GameObject>("StoryPort/" + (bld.Contains("/") ? bld : "Buildings/" + bld));
            var root = UnityEngine.Object.Instantiate(prefab);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = "";
                foreach (var m in r.sharedMaterials) mats += (m != null ? m.name + "[" + (m.shader != null ? m.shader.name : "-") + "]" : "null") + ",";
                if (true)
                    foreach (var m in r.sharedMaterials)
                    {
                        var sh = m.shader;
                        for (var i = 0; i < sh.GetPropertyCount(); i++)
                        {
                            var n = sh.GetPropertyName(i);
                            var t = sh.GetPropertyType(i);
                            string v = t == UnityEngine.Rendering.ShaderPropertyType.Texture ? (m.GetTexture(n) != null ? m.GetTexture(n).name : "null")
                                : t == UnityEngine.Rendering.ShaderPropertyType.Color ? m.GetColor(n).ToString()
                                : t == UnityEngine.Rendering.ShaderPropertyType.Vector ? m.GetVector(n).ToString() : m.GetFloat(n).ToString();
                            Debug.Log("StoryPort building prop " + r.name + " " + n + "=" + v);
                            if (t == UnityEngine.Rendering.ShaderPropertyType.Texture && m.GetTexture(n) != null)
                                Debug.Log("StoryPort building texpath " + n + "=" + UnityEditor.AssetDatabase.GetAssetPath(m.GetTexture(n)));
                        }
                    }
                Debug.Log("StoryPort building renderer " + r.name + " active=" + r.gameObject.activeInHierarchy + " enabled=" + r.enabled + " mats=" + mats + " b=" + r.bounds.center + r.bounds.size);
            }
        }

        public static void CaptureBase()
        {
            Capture("base");
        }

        public static void CaptureDialogue()
        {
            Capture("dialogue");
        }

        public static void CaptureResult()
        {
            Capture("victory");
        }

        public static void CaptureComplete()
        {
            Capture("complete");
        }

        public static void CaptureFight()
        {
            Capture("fight");
        }

        public static void InspectFightStage()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = Resources.Load<GameObject>("StoryPort/ChicagoFightStage");
            if (prefab == null) throw new Exception("Missing local Chicago stage prefab");
            var root = UnityEngine.Object.Instantiate(prefab);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                Debug.Log("StoryPort stage renderer " + r.transform.parent.name + "/" + r.name + " mesh=" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.name + " v=" + mf.sharedMesh.vertexCount : "-") +
                    " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "-") + " active=" + r.gameObject.activeInHierarchy + " b=" + r.bounds.center + r.bounds.size);
            }
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name != "Main Stage" && child.name != "GroundPlane" &&
                    !child.name.Contains("road") && !child.name.Contains("rubble")) continue;
                var renderers = child.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) continue;
                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                Debug.Log("StoryPort stage piece " + child.name + " position=" + child.position +
                    " bounds=" + bounds + " renderers=" + renderers.Length);
            }
        }

        static void Capture(string screen)
        {
            // -nographics runs on the Null device and renders flat gray; a
            // capture from it says nothing about the real scene.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new Exception("Preview capture needs a real graphics device; this editor is running on the Null device (-nographics)");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var client = new GameObject("StoryPort Preview").AddComponent<StoryPortBootstrap>();
            Invoke(client, "BuildCamera");
            Invoke(client, "BuildUI");
            if (screen == "story")
            {
                var questFile = Environment.GetEnvironmentVariable("STORYPORT_PREVIEW_QUEST");
                if (!string.IsNullOrEmpty(questFile))
                {
                    int act;
                    int.TryParse(Environment.GetEnvironmentVariable("STORYPORT_PREVIEW_ACT"), out act);
                    act = Mathf.Clamp(act, 0, 2);
                    var qids = (string[])typeof(StoryPortBootstrap).GetField("ActQids", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    var routes = (StoryRouteData[])typeof(StoryPortBootstrap).GetField("actRoutes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                    routes[act] = StoryRouteData.Parse(File.ReadAllText(questFile), qids[act]);
                    if (!routes[act].hasMap) throw new Exception("Story preview needs the server's quest-map response");
                    Set(client, "actIndex", act);
                }
                Invoke(client, "StoryScreen");
            }
            else if (screen == "squad")
            {
                Set(client, "squadForStory", true);
                Set(client, "pendingEncounter", true);
                Invoke(client, "SquadScreen");
            }
            else if (screen == "fight")
            {
                Invoke(client, "FightScreen");
                // SP_FIGHTSTATE=<enemyHp>,<playerHp>,<playerMana>,<enemyMana> previews a mid-fight HUD.
                var state = Environment.GetEnvironmentVariable("SP_FIGHTSTATE");
                if (!string.IsNullOrEmpty(state))
                {
                    var values = state.Split(',');
                    var invariant = System.Globalization.CultureInfo.InvariantCulture;
                    Set(client, "enemyHp", int.Parse(values[0], invariant));
                    Set(client, "playerHp", int.Parse(values[1], invariant));
                    Set(client, "playerMana", float.Parse(values[2], invariant));
                    Set(client, "enemyMana", float.Parse(values[3], invariant));
                    Set(client, "enemyTrail", int.Parse(values[0], invariant) / 100f + .12f);
                    Invoke(client, "UpdateFightHud");
                    Invoke(client, "UpdateHealthTrails");
                    var call = GameObject.Find("Fight Call");
                    if (call != null) UnityEngine.Object.DestroyImmediate(call);
                }
            }
            else if (screen == "victory")
            {
                Invoke(client, "FightScreen");
                Set(client, "hitsLanded", 16);
                Set(client, "hitsReceived", 9);
                Set(client, "highestChain", 5);
                foreach (Transform child in client.transform) { }
                var content = (RectTransform)typeof(StoryPortBootstrap).GetField("content", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                foreach (Transform child in content) UnityEngine.Object.DestroyImmediate(child.gameObject);
                Invoke(client, "ResultScreen", true);
            }
            else if (screen == "loading") Invoke(client, "LoadingScreen");
            else if (screen == "complete") Invoke(client, "MissionCompleteScreen");
            else if (screen == "dialogue")
            {
                var detail = "{\"dialogueTable\":{\"intro\":[" +
                    "{\"inShadow\":false,\"character\":\"optimusprime_cin_tf\",\"side\":\"left\",\"line\":\"Bludgeon?! So you are the one behind this ambush...\"}," +
                    "{\"inShadow\":false,\"character\":\"bludgeon_gs_rd20\",\"side\":\"right\",\"line\":\"There is no escape, Optimus.\"}]}}";
                Set(client, "dialogueLines", StoryRouteData.ReadDialogue(detail, "intro"));
                Invoke(client, "DialogueScreen");
            }
            else if (screen == "base")
            {
                // Same sockets the server's /base/active placement list returns.
                Invoke(client, "BaseScreen");
                var baseRoot = GameObject.Find("StoryPort World · Base");
                var response = "";
                var only = Environment.GetEnvironmentVariable("SP_ONLY");
                foreach (var socket in new[] { "bldg_battle_centre:2_2", "bldg_away_team:2_1", "bldg_alliance_help:2_3", "bldg_crystal_free:1_2", "bldg_crystal_daily:3_2" })
                {
                    var parts = socket.Split(':');
                    if (!string.IsNullOrEmpty(only) && !only.Contains(parts[0])) continue;
                    response += "{\"id\":\"" + parts[0] + "\",\"key\":\"sock_" + parts[1] + "\"}";
                }
                Invoke(client, "PlaceBaseBuildings", baseRoot.transform.GetChild(0), response);
                var hide = Environment.GetEnvironmentVariable("SP_HIDE");
                if (!string.IsNullOrEmpty(hide))
                    foreach (var r in baseRoot.GetComponentsInChildren<Renderer>(true))
                        foreach (var h in hide.Split(','))
                            if (r.name.Contains(h)) r.enabled = false;
            }
            else throw new ArgumentOutOfRangeException("screen", screen, "No preview renderer for this screen");
            Invoke(client, "UpdateHeaderState", screen);

            var camera = Camera.main;
            var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
            if (camera == null || canvas == null) throw new Exception("Story preview did not create its camera and canvas");
            var statusBar = canvas.transform.Find("Root/Top Status Bar");
            if (statusBar != null) statusBar.gameObject.SetActive(screen != "title" && screen != "loading" && screen != "fight" && screen != "dialogue" && screen != "victory" && screen != "complete");
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = .3f;
            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            target.Create();
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(1600, 900, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            camera.targetTexture = null;
            var output = Environment.GetEnvironmentVariable("STORYPORT_PREVIEW_PNG");
            if (string.IsNullOrEmpty(output)) output = Path.GetFullPath("StoryPort-" + screen + "-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllBytes(output, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(target);
            Debug.Log("StoryPort " + screen + " preview captured: " + output);
        }

        static void Invoke(StoryPortBootstrap client, string method, params object[] arguments)
        {
            var target = typeof(StoryPortBootstrap).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null) throw new MissingMethodException("StoryPortBootstrap", method);
            target.Invoke(client, arguments);
        }

        static void Set(StoryPortBootstrap client, string field, object value)
        {
            var target = typeof(StoryPortBootstrap).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null) throw new MissingFieldException("StoryPortBootstrap", field);
            target.SetValue(client, value);
        }
    }
}
#endif
