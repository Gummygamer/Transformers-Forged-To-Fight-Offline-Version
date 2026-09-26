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
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var client = new GameObject("StoryPort Preview").AddComponent<StoryPortBootstrap>();
            Invoke(client, "BuildCamera");
            Invoke(client, "BuildUI");
            Invoke(client, "StoryScreen");
            Invoke(client, "UpdateHeaderState", "story");

            var camera = Camera.main;
            var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
            if (camera == null || canvas == null) throw new Exception("Story preview did not create its camera and canvas");
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 10f;
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
            if (string.IsNullOrEmpty(output)) output = Path.GetFullPath("StoryPort-story-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllBytes(output, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(target);
            Debug.Log("StoryPort story preview captured: " + output);
        }

        static void Invoke(StoryPortBootstrap client, string method, params object[] arguments)
        {
            var target = typeof(StoryPortBootstrap).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null) throw new MissingMethodException("StoryPortBootstrap", method);
            target.Invoke(client, arguments);
        }
    }
}
#endif
