using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class RebuildRecoveredAndroid
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("RECOVERED_ANDROID_APK");
        if (string.IsNullOrEmpty(output))
            throw new InvalidOperationException("Set RECOVERED_ANDROID_APK to the local output APK path.");

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0 && File.Exists("Assets/Scenes/1_boot.unity"))
            scenes = new[] { "Assets/Scenes/1_boot.unity" };
        if (scenes.Length == 0)
            throw new InvalidOperationException("No enabled scenes are available to build.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.kabam.bigrobot");
        PlayerSettings.bundleVersion = "9.2.0";
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException(
                "Android build failed: " + report.summary.result + "; " + report.summary.totalErrors + " errors.");

        Debug.Log("Recovered Android APK: " + Path.GetFullPath(output));
    }
}
