using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class Recompile202Build
{
    public static void BuildAndroid()
    {
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
        PlayerSettings.bundleVersion = "2.0.2";
        PlayerSettings.Android.bundleVersionCode = 812553;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel19;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.X86;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.Mono2x);
        Debug.Log("2.0.2 Android target architectures: " + PlayerSettings.Android.targetArchitectures);
        Debug.Log("2.0.2 Android toolchain: Unity embedded SDK and JDK");

        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled)
            .Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            throw new System.InvalidOperationException("No enabled scenes are configured.");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = System.Environment.GetEnvironmentVariable("TFTF_202_OUTPUT"),
            target = BuildTarget.Android,
            options = BuildOptions.None
        };
        if (string.IsNullOrEmpty(options.locationPathName))
            throw new System.InvalidOperationException("TFTF_202_OUTPUT is required.");

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log("2.0.2 Android build: " + report.summary.result + " (" + report.summary.totalSize + " bytes)");
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
        string output = System.Environment.GetEnvironmentVariable("TFTF_202_SCRIPTS_OUT");
        if (string.IsNullOrEmpty(output))
            throw new System.InvalidOperationException("TFTF_202_SCRIPTS_OUT is required.");
        var settings = new UnityEditor.Build.Player.ScriptCompilationSettings
        {
            group = BuildTargetGroup.Android,
            target = BuildTarget.Android,
            options = UnityEditor.Build.Player.ScriptCompilationOptions.None
        };
        UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, output);
        Debug.Log("2.0.2 script compile finished: " + output);
    }
}
