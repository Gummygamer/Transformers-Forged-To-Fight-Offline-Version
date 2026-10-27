// Newly authored. VR shell build only (TFTF_VR=1; assemble_project.py copies this file into the VR project, which also
// has the Oculus XR plugin). Called by Recompile310Build.BuildAndroid before the player build: XR management starts the
// Oculus loader on Android, multi-pass stereo (the game's shaders have no single-pass stereo support), Quest 2 and later.
// The plugin's own build step then writes the Quest entries of the Android manifest; TftfVrHandTrackingManifest adds hand
// tracking to it (TftfVrHands reads tracked hands through OVRPlugin, which the plugin does not declare).
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;
using Unity.XR.Oculus;

public static class TftfVrBuildSetup
{
    const string Dir = "Assets/XR";

    static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        var a = AssetDatabase.LoadAssetAtPath<T>(path);
        if (a != null) return a;
        a = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(a, path);
        return a;
    }

    public static void Configure()
    {
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "XR");

        var perTarget = LoadOrCreate<XRGeneralSettingsPerBuildTarget>(Dir + "/XRGeneralSettings.asset");
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (general == null)
        {
            general = ScriptableObject.CreateInstance<XRGeneralSettings>();
            general.name = "Android Settings";
            AssetDatabase.AddObjectToAsset(general, perTarget);
            perTarget.SetSettingsForBuildTarget(BuildTargetGroup.Android, general);
        }
        if (general.Manager == null)
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = "Android Providers";
            AssetDatabase.AddObjectToAsset(manager, perTarget);
            general.Manager = manager;
        }
        general.InitManagerOnStart = true;
        general.Manager.automaticLoading = true;
        general.Manager.automaticRunning = true;

        var loader = LoadOrCreate<OculusLoader>(Dir + "/OculusLoader.asset");
        if (!general.Manager.activeLoaders.Contains(loader)) general.Manager.TryAddLoader(loader);

        var oculus = LoadOrCreate<OculusSettings>(Dir + "/OculusSettings.asset");
        oculus.m_StereoRenderingModeAndroid = OculusSettings.StereoRenderingModeAndroid.MultiPass;
        oculus.TargetQuest = false;                  // Quest 1 is out of support; the game is tested for Quest 2
        oculus.TargetQuest2 = true;
        oculus.FocusAware = true;
        oculus.V2Signing = true;
        EditorBuildSettings.AddConfigObject("Unity.XR.Oculus.Settings", oculus, true);

        EditorUtility.SetDirty(general.Manager);
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(perTarget);
        EditorUtility.SetDirty(oculus);
        AssetDatabase.SaveAssets();

        // Quest: API 29 or later, OpenGL ES 3 (the game's shaders are tested there, not on Vulkan), landscape.
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        Debug.Log("[TFTF] VR build: Oculus loader " + general.Manager.activeLoaders.Count + ", multi-pass, Quest 2, API 29, GLES3");
    }
}

// After the Oculus plugin's manifest step (its callbackOrder is 10000): hand tracking, optional, so controllers still work.
public class TftfVrHandTrackingManifest : IPostGenerateGradleAndroidProject
{
    const string Android = "http://schemas.android.com/apk/res/android";

    public int callbackOrder { get { return 10001; } }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string file = path + "/src/main/AndroidManifest.xml";
        var doc = new XmlDocument();
        doc.Load(file);
        var manifest = (XmlElement)doc.SelectSingleNode("/manifest");
        var application = (XmlElement)doc.SelectSingleNode("/manifest/application");
        Add(doc, manifest, "uses-permission", "name", "com.oculus.permission.HAND_TRACKING", null, null);
        Add(doc, manifest, "uses-feature", "name", "oculus.software.handtracking", "required", "false");
        Add(doc, application, "meta-data", "name", "com.oculus.handtracking.frequency", "value", "HIGH");
        Add(doc, application, "meta-data", "name", "com.oculus.handtracking.version", "value", "V2.0");
        doc.Save(file);
        Debug.Log("[TFTF] VR build: hand tracking declared in the manifest");
    }

    static void Add(XmlDocument doc, XmlElement parent, string tag, string key, string name, string key2, string value2)
    {
        foreach (XmlElement e in parent.SelectNodes(tag))
            if (e.GetAttribute(key, Android) == name) return;
        var el = doc.CreateElement(tag);
        el.SetAttribute(key, Android, name);
        if (key2 != null) el.SetAttribute(key2, Android, value2);
        parent.AppendChild(el);
    }
}
