// Newly authored. The only scene of the shell: mounts the converted resources bundle and instantiates the root
// objects of the original boot scene (shipped by the converter as "scene/level0/<id>/<name>").
using System.Collections;
using System.IO;
using UnityEngine;

public class TftfBootstrap : MonoBehaviour
{
    IEnumerator Start()
    {
        string dir = Path.Combine(Application.persistentDataPath, "tftf");
        string path = Path.Combine(dir, "resources.bundle");
        Debug.Log("[TFTF] bootstrap, bundle=" + path + " exists=" + File.Exists(path));
        if (!File.Exists(path)) { Debug.LogError("[TFTF] converted resources bundle missing"); yield break; }
        var req = AssetBundle.LoadFromFileAsync(path);
        yield return req;
        var bundle = req.assetBundle;
        if (bundle == null) { Debug.LogError("[TFTF] resources bundle failed to load"); yield break; }
        TftfResources.Mount(bundle);
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
