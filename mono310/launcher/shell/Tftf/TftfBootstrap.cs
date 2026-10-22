// Newly authored. The only scene of the shell: mounts the converted resources bundle and instantiates the root
// objects of the original boot scene (shipped by the converter as "scene/level0/<id>/<name>").
using System.Collections;
using System.IO;
using UnityEngine;

public class TftfBootstrap : MonoBehaviour
{
    static bool RestartApp()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                activity.Call("tftfRestart");
            return true;
        }
        catch (System.Exception e) { Debug.LogWarning("[TFTF] restart failed: " + e.Message); return false; }
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
        try
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            System.Diagnostics.Process.Start(exe);
            Application.Quit();
            return true;
        }
        catch (System.Exception e) { Debug.LogWarning("[TFTF] restart failed: " + e.Message); return false; }
#else
        return false;
#endif
    }

    IEnumerator Start()
    {
#if TFTF_VR
        TftfVr.Init();          // before any game code reads the screen size (TftfScreen)
#endif
        string dir = Path.Combine(Application.persistentDataPath, "tftf");
        Directory.CreateDirectory(dir);
        if (!TftfInstaller.IsInstalled(dir))
        {
            var launcher = TftfLauncher.Create(dir);
            while (!launcher.Finished) yield return null;
            Destroy(launcher.gameObject);
            // Start-up code of the game (file system, caches) ran before the files existed: begin again in a fresh
            // process so the first start is identical to every later one.
            if (TftfLauncher.RestartAfterInstall && RestartApp()) yield break;
        }
        string path = Path.Combine(dir, "resources.bundle");
        Debug.Log("[TFTF] bootstrap, bundle=" + path + " exists=" + File.Exists(path));
        if (!File.Exists(path)) { Debug.LogError("[TFTF] converted resources bundle missing"); yield break; }
        yield return TftfServer.Start(dir);
        var req = AssetBundle.LoadFromFileAsync(path);
        yield return req;
        var bundle = req.assetBundle;
        if (bundle == null) { Debug.LogError("[TFTF] resources bundle failed to load"); yield break; }
        TftfResources.Mount(bundle);
        TftfResources.MountShaderMap(Path.Combine(dir, "shadermap.txt"));
        TftfTextureProbe.Start(dir);
        TftfResources.Etc1Fix = File.Exists(Path.Combine(dir, "etc1fix.txt"));
        string movesPath = Path.Combine(dir, "moves92.bundle");
        if (File.Exists(movesPath))
        {
            var mreq = AssetBundle.LoadFromFileAsync(movesPath);
            yield return mreq;
            if (mreq.assetBundle != null) TftfResources.MountDonorMoves(mreq.assetBundle);
        }
        TftfResources.FixMaterialShaders(bundle);
        var fixer = new GameObject("TftfMaterialFixer");
        DontDestroyOnLoad(fixer);
        fixer.AddComponent<TftfMaterialFixer>();
        foreach (var name in bundle.GetAllAssetNames())
        {
            if (!name.StartsWith("scene/level0/", System.StringComparison.Ordinal)) continue;
            var src = bundle.LoadAsset<GameObject>(name);
            if (src == null) { Debug.LogError("[TFTF] boot object not loadable: " + name); continue; }
            var go = Instantiate(src);
            go.name = src.name;
        }
        Debug.Log("[TFTF] boot scene instantiated");
    }
}
