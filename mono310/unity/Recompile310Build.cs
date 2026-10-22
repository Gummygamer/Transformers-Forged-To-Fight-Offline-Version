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
        // By name: TftfBootstrap exists only in the shell project, and this file is also compiled into the full one.
        var bootstrap = System.AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("TftfBootstrap")).FirstOrDefault(t => t != null);
        if (bootstrap == null) throw new System.InvalidOperationException("TftfBootstrap not found (shell project only).");
        go.AddComponent(bootstrap);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
        return path;
    }

    public static void BuildAndroid()
    {
        if (System.Environment.GetEnvironmentVariable("TFTF_310_NO_INCLUDE") != "1") IncludeShaders();
        // Batch builds run with an isolated HOME, so select this editor's
        // bundled Android toolchain instead of relying on user EditorPrefs.
#if UNITY_ANDROID   // the Android module (and this API) is absent from editors without Android support
        string androidPlayer = System.IO.Path.Combine(
            EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer");
        UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = System.IO.Path.Combine(androidPlayer, "OpenJDK");
        UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = System.IO.Path.Combine(androidPlayer, "SDK");
        UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = System.IO.Path.Combine(androidPlayer, "NDK");
#endif

        // NGUI raycasts physics colliders that layout code and tweens move every
        // frame; Unity 5 synced transforms automatically, Unity 2020 does not.
        Physics.autoSyncTransforms = true;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1" ? "unicronslair.bigrobot" : "com.kabam.bigrobot");   // the assetless launcher is its own app
        PlayerSettings.bundleVersion = "3.1.0";
        PlayerSettings.Android.bundleVersionCode = 822754;
        string androidAbi = System.Environment.GetEnvironmentVariable("TFTF_ANDROID_ABI") ?? "armeabi-v7a";
        bool arm64Shell = System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1" && androidAbi == "arm64-v8a";
        if (androidAbi == "arm64-v8a" && !arm64Shell)
            throw new System.InvalidOperationException("ARM64 is currently supported only by the assetless launcher shell.");
        if (androidAbi != "armeabi-v7a" && androidAbi != "arm64-v8a")
            throw new System.InvalidOperationException("Unsupported TFTF_ANDROID_ABI: " + androidAbi);
        PlayerSettings.Android.targetArchitectures = arm64Shell ? AndroidArchitecture.ARM64 : AndroidArchitecture.ARMv7 | AndroidArchitecture.X86;
        PlayerSettings.Android.minSdkVersion = arm64Shell ? AndroidSdkVersions.AndroidApiLevel21 : AndroidSdkVersions.AndroidApiLevel19;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android,
            arm64Shell ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
        if (System.Environment.GetEnvironmentVariable("TFTF_VR") == "1")
        {
            if (!arm64Shell) throw new System.InvalidOperationException("TFTF_VR=1 needs the ARM64 launcher shell.");
            // By name: TftfVrBuildSetup and the XR packages exist only in the VR shell project.
            var vr = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("TftfVrBuildSetup")).FirstOrDefault(t => t != null);
            if (vr == null) throw new System.InvalidOperationException("TftfVrBuildSetup not found (VR shell project only).");
            vr.GetMethod("Configure").Invoke(null, null);
        }
        Debug.Log("3.1.0 Android target architectures: " + PlayerSettings.Android.targetArchitectures);
        Debug.Log("3.1.0 Android toolchain: Unity embedded SDK and JDK");

        if (System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1")
        {
            // No scenes reference the game's behaviours, so the linker would strip them and the engine modules they use.
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Disabled);
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.productName = "Transformers Forged to Fight Revival";   // name shown by Android outside the app
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

    // Windows 11 (x64) Mono build of the assetless launcher shell (TFTF_SHELL=1) or of the full
    // recompiled client (its enabled build scenes). Output: a folder holding TFTF.exe and its data.
    public static void BuildWindows()
    {
        bool shell = System.Environment.GetEnvironmentVariable("TFTF_SHELL") == "1";
        if (System.Environment.GetEnvironmentVariable("TFTF_310_NO_INCLUDE") != "1") IncludeShaders();
        Physics.autoSyncTransforms = true;
        PlayerSettings.bundleVersion = "3.1.0";
        if (shell)
        {
            PlayerSettings.productName = "Transformers Forged to Fight Revival";
            PlayerSettings.companyName = "Unicron's Lair";
        }
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        string[] scenes = shell ? new[] { CreateBootstrapScene() }
            : EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            throw new System.InvalidOperationException("No enabled scenes are configured.");
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = System.Environment.GetEnvironmentVariable("TFTF_310_OUTPUT"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        if (string.IsNullOrEmpty(options.locationPathName))
            throw new System.InvalidOperationException("TFTF_310_OUTPUT is required.");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log("3.1.0 Windows build: " + report.summary.result + " (" + report.summary.totalSize + " bytes)");
        if (report.summary.result != BuildResult.Succeeded)
            throw new System.InvalidOperationException("Windows build failed: " + report.summary.result);
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
