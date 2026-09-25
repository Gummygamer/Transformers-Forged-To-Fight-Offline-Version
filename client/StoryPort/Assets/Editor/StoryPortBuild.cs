#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace StoryPort.Editor
{
    public static class StoryPortBuild
    {
        public static void BuildAndroid()
        {
            StoryPortAssetSetup.BuildLocalAssetCatalog();
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel28;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel36;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.Mono2x);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;
            AssetDatabase.SaveAssets();
            Debug.Log("StoryPort Android architectures: " + PlayerSettings.Android.targetArchitectures);
            var output = Environment.GetEnvironmentVariable("STORYPORT_APK");
            if (string.IsNullOrEmpty(output)) output = "StoryPort.apk";
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/StoryPort.unity" },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("StoryPort Android build failed: " + report.summary.result);
            Debug.Log("StoryPort APK: " + output + " (" + report.summary.totalSize + " bytes)");
        }
    }
}
#endif
