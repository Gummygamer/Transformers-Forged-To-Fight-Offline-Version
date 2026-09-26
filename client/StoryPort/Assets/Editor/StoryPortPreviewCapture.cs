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
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var client = new GameObject("StoryPort Preview").AddComponent<StoryPortBootstrap>();
            Invoke(client, "BuildCamera");
            Invoke(client, "BuildUI");
            if (screen == "story") Invoke(client, "StoryScreen");
            else if (screen == "squad")
            {
                Set(client, "squadForStory", true);
                Set(client, "pendingEncounter", true);
                Invoke(client, "SquadScreen");
            }
            else if (screen == "fight") Invoke(client, "FightScreen");
            else if (screen == "loading") Invoke(client, "LoadingScreen");
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
            if (statusBar != null) statusBar.gameObject.SetActive(screen != "fight");
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
