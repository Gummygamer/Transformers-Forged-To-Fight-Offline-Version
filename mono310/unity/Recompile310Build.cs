using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class Recompile310Build
{
    // Shader.Find-only shaders are stripped from players; list them in GraphicsSettings
    // "Always Included Shaders" (Assets/Shader310 holds replacements named like the originals).
    static void IncludeShaders()
    {
        var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Resources/Shader310" });
        var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(gs);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        list.ClearArray();
        foreach (var g in guids)
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(g));
            if (sh == null) continue;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = sh;
        }
        // Do not add Shader.Find("GUI/Text Shader") here: it is a DontSave object from "unity default
        // resources" and makes the player build fail writing unity_builtin_extra.
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("3.1.0 always-included shaders: " + list.arraySize);
    }

    static string CreateBootstrapScene()
    {
        const string path = "Assets/Scenes/Bootstrap.unity";
        System.IO.Directory.CreateDirectory("Assets/Scenes");
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        var go = new GameObject("TftfBootstrap");
        go.AddComponent<TftfBootstrap>();
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
        return path;
    }

    public static void BuildAndroid()
    {
        if (System.Environment.GetEnvironmentVariable("TFTF_310_NO_INCLUDE") != "1") IncludeShaders();
        // Batch builds run with an isolated HOME, so select this editor's
        // bundled Android toolchain instead of relying on user EditorPrefs.
        string androidPlayer = System.IO.Path.Combine(
            EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer");
        UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = System.IO.Path.Combine(androidPlayer, "OpenJDK");
        UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = System.IO.Path.Combine(androidPlayer, "SDK");
        UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = System.IO.Path.Combine(androidPlayer, "NDK");

        // NGUI raycasts physics colliders that layout code and tweens move every
        // frame; Unity 5 synced transforms automatically, Unity 2020 does not.
        Physics.autoSyncTransforms = true;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.kabam.bigrobot");
        PlayerSettings.bundleVersion = "3.1.0";
        PlayerSettings.Android.bundleVersionCode = 822754;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel19;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.X86;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.Mono2x);
        Debug.Log("3.1.0 Android target architectures: " + PlayerSettings.Android.targetArchitectures);
        Debug.Log("3.1.0 Android toolchain: Unity embedded SDK and JDK");

        if (System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1")
        {
            // No scenes reference the game's behaviours, so the linker would strip them and the engine modules they use.
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Disabled);
            PlayerSettings.stripEngineCode = false;
            // App icon: the launcher logo (Assets/Icons/revival-icon.png) for every legacy icon size.
            const string iconPath = "Assets/Icons/revival-icon.png";
            if (System.IO.File.Exists(iconPath))
            {
                AssetDatabase.ImportAsset(iconPath);
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                var sizes = PlayerSettings.GetIconSizesForTargetGroup(BuildTargetGroup.Android, IconKind.Application);
                var icons = new Texture2D[sizes.Length];
                for (int i = 0; i < icons.Length; i++) icons[i] = icon;
                PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Android, icons, IconKind.Application);
                Debug.Log("Launcher icon set for " + icons.Length + " sizes");
            }
        }
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled)
            .Select(scene => scene.path).ToArray();
        if (System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1")
            scenes = new[] { CreateBootstrapScene() };       // assetless launcher shell: only the bootstrap scene
        if (scenes.Length == 0)
            throw new System.InvalidOperationException("No enabled scenes are configured.");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = System.Environment.GetEnvironmentVariable("TFTF_310_OUTPUT"),
            target = BuildTarget.Android,
            options = BuildOptions.None
        };
        if (string.IsNullOrEmpty(options.locationPathName))
            throw new System.InvalidOperationException("TFTF_310_OUTPUT is required.");

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log("3.1.0 Android build: " + report.summary.result + " (" + report.summary.totalSize + " bytes)");
        if (report.summary.result != BuildResult.Succeeded)
            throw new System.InvalidOperationException("Android build failed: " + report.summary.result);
        // The batch editor can linger after BuildPlayer (idle shader-compiler
        // workers, licensing client); leave explicitly so callers do not stall.
        EditorApplication.Exit(0);
    }

    // Compile the player scripts only (seconds) so the managed DLLs can be
    // injected into an existing APK; see tools/fast_patch_managed.py.
    public static void CompileScriptsOnly()
    {
        string output = System.Environment.GetEnvironmentVariable("TFTF_310_SCRIPTS_OUT");
        if (string.IsNullOrEmpty(output))
            throw new System.InvalidOperationException("TFTF_310_SCRIPTS_OUT is required.");
        var settings = new UnityEditor.Build.Player.ScriptCompilationSettings
        {
            group = BuildTargetGroup.Android,
            target = BuildTarget.Android,
            options = UnityEditor.Build.Player.ScriptCompilationOptions.None
        };
        UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, output);
        Debug.Log("3.1.0 script compile finished: " + output);
    }
}
