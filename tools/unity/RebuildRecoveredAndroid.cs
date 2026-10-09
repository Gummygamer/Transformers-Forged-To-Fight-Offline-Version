using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class RebuildRecoveredAndroid
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("RECOVERED_ANDROID_APK");
        if (string.IsNullOrEmpty(output))
            throw new InvalidOperationException("Set RECOVERED_ANDROID_APK to the local output APK path.");
        string keystore = Environment.GetEnvironmentVariable("RECOVERED_ANDROID_KEYSTORE");
        string keystorePassword = Environment.GetEnvironmentVariable("RECOVERED_ANDROID_KEYSTORE_PASSWORD");
        if (string.IsNullOrEmpty(keystore) || string.IsNullOrEmpty(keystorePassword))
            throw new InvalidOperationException("Set the local recovery keystore path and password.");
        string backendName = Environment.GetEnvironmentVariable("RECOVERED_SCRIPTING_BACKEND");
        if (string.IsNullOrEmpty(backendName))
            backendName = "IL2CPP";
        else if (string.Equals(backendName, "Mono", StringComparison.OrdinalIgnoreCase))
            backendName = "Mono2x";
        ScriptingImplementation backend;
        if (!Enum.TryParse(backendName, true, out backend))
            throw new InvalidOperationException("RECOVERED_SCRIPTING_BACKEND must be IL2CPP or Mono.");
        string architectureName = Environment.GetEnvironmentVariable("RECOVERED_ANDROID_ARCH");
        if (string.IsNullOrEmpty(architectureName))
            architectureName = "ARM64";
        AndroidArchitecture architecture;
        if (!Enum.TryParse(architectureName, true, out architecture))
            throw new InvalidOperationException("RECOVERED_ANDROID_ARCH must be ARM64 or ARMv7.");

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0 && File.Exists("Assets/Scenes/1_boot.unity"))
            scenes = new[] { "Assets/Scenes/1_boot.unity" };
        if (scenes.Length == 0)
            throw new InvalidOperationException("No enabled scenes are available to build.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        string androidPlayer = Path.Combine(
            EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer");
        AndroidExternalToolsSettings.jdkRootPath = Path.Combine(androidPlayer, "OpenJDK");
        AndroidExternalToolsSettings.sdkRootPath = Path.Combine(androidPlayer, "SDK");
        AndroidExternalToolsSettings.ndkRootPath = Path.Combine(androidPlayer, "NDK");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.kabam.bigrobot");
        PlayerSettings.bundleVersion = "9.2.0";
        PlayerSettings.Android.bundleVersionCode = 9200;
        PlayerSettings.Android.targetArchitectures = architecture;
        PlayerSettings.Android.useCustomKeystore = true;
        PlayerSettings.Android.keystoreName = keystore;
        PlayerSettings.Android.keystorePass = keystorePassword;
        PlayerSettings.Android.keyaliasName = "local-rebuild";
        PlayerSettings.Android.keyaliasPass = keystorePassword;
        string configuredKeystore = PlayerSettings.Android.keystoreName;
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string resolvedKeystore = Path.IsPathRooted(configuredKeystore)
            ? configuredKeystore : Path.Combine(projectRoot, configuredKeystore);
        Debug.Log("Recovery Android keystore: " + configuredKeystore
            + " (resolved: " + resolvedKeystore + ", exists: " + File.Exists(resolvedKeystore) + ")");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, backend);
        PlayerSettings.SetManagedStrippingLevel(
            BuildTargetGroup.Android, ManagedStrippingLevel.Disabled);

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

        Debug.Log("Recovered Android APK (" + backend + "): " + Path.GetFullPath(output));
    }
}
