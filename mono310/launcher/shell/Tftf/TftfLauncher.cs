// Newly authored. First-run screen: the user picks their own 3.1.0 and 9.2.0 APKs, then the installer prepares the
// game data on the phone. Shown only until installation has completed once.
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public sealed class TftfLauncher : MonoBehaviour
{
    public const string LauncherVersion = "0.3.1";      // the launcher's own version; the game inside stays 3.1.0
    public const bool RestartAfterInstall = true;
    string _root;
    string[] _names = { "", "" };
    string[] _paths = { "", "" };
    bool _installing, _finished;
    byte[] _trees;
    Texture2D _logo;
    string _message = "";
    GUIStyle _label, _button, _small;

    public bool Finished { get { return _finished; } }

    public static TftfLauncher Create(string root)
    {
        var go = new GameObject("TftfLauncher");   // the Java picker reports to this name
        DontDestroyOnLoad(go);
        var cam = go.AddComponent<Camera>();       // nothing else renders yet: clear to black so the GUI redraws cleanly
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 0;
        var l = go.AddComponent<TftfLauncher>();
        l._root = root;
        return l;
    }

    IEnumerator Start()
    {
        var req = UnityWebRequest.Get(Application.streamingAssetsPath + "/trees.bin");
        yield return req.SendWebRequest();
        if (req.result == UnityWebRequest.Result.Success) _trees = req.downloadHandler.data;
        else _message = "Launcher data is missing from this build.";
        // Test hook: tftf/autopick.txt (two lines: the 3.1.0 and the 9.2.0 APK paths, readable by the app) pre-selects the
        // files, so automated tests do not have to drive the system file picker.
        try
        {
            string hook = Path.Combine(_root, "autopick.txt");
            if (File.Exists(hook))
            {
                var lines = File.ReadAllLines(hook);
                for (int slot = 0; slot < 2 && slot < lines.Length; slot++)
                {
                    string path = lines[slot].Trim();
                    if (path.Length == 0 || !File.Exists(path)) continue;
                    string problem = Validate(path, slot);
                    if (problem != null) { _message = problem; continue; }
                    _paths[slot] = path; _names[slot] = Path.GetFileName(path);
                }
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[TFTF] autopick failed: " + e.Message); }
        var logo = UnityWebRequestTexture.GetTexture(Application.streamingAssetsPath + "/revival-icon.png");
        yield return logo.SendWebRequest();
        if (logo.result == UnityWebRequest.Result.Success) _logo = DownloadHandlerTexture.GetContent(logo);
    }

    // The Java side keeps what the picker returned until it is fetched here (a message sent to Unity while the picker was open could
    // be lost when Android recreated the activity): name = display name, "" = cancelled.
    void Poll()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                for (int slot = 0; slot < 2; slot++)
                {
                    if (_copying[slot])
                    {
                        int state = activity.Call<int>("tftfCopyState", slot);
                        if (state == 1) continue;
                        _copying[slot] = false;
                        if (state == 2) Accept(slot, _copyDest[slot]);
                        else { _names[slot] = ""; _message = "Could not copy that file to the phone (is there enough free space?)."; }
                        continue;
                    }
                    string name = activity.Call<string>("tftfTakePicked", slot);
                    if (name == null || name.Length == 0) continue;
                    _names[slot] = name;
                    int fd = activity.Call<int>("tftfOpenPicked", slot);
                    string path = fd >= 0 ? "/proc/self/fd/" + fd : "";
                    if (path.Length > 0 && Validate(path, slot) != "unreadable") { Accept(slot, path); continue; }
                    // some providers (cloud folders, downloads managers) hand out descriptors that cannot be read as a file: copy it instead
                    _copyDest[slot] = Path.Combine(Application.persistentDataPath, "picked" + slot + ".apk");
                    _copying[slot] = true; _message = "Copying " + name + " to the phone...";
                    activity.Call("tftfCopyPicked", slot, _copyDest[slot]);
                }
        }
        catch (System.Exception e) { Debug.LogWarning("[TFTF] pick poll failed: " + e.Message); }
#endif
    }

    bool[] _copying = { false, false };
    string[] _copyDest = { "", "" };

    void Accept(int slot, string path)
    {
        string problem = Validate(path, slot);
        if (problem == null) { _paths[slot] = path; _message = ""; }
        else { _names[slot] = ""; _paths[slot] = ""; _message = problem; }
    }

    const long RequiredBytes = 1200L << 20;      // content package 450 MB + converted data + base files + working room

    static long FreeBytes()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var fs = new AndroidJavaObject("android.os.StatFs", Application.persistentDataPath))
                return fs.Call<long>("getAvailableBytes");
        }
        catch { return -1; }
#else
        return -1;
#endif
    }

    // What each game APK must contain; also recognises the two files picked in the wrong order.
    const string Marker310 = "assets/bin/Data/globalgamemanagers";
    const string Marker92 = "assets/assetpack/characters/moves.assetbundle";

    static string Validate(string path, int slot)
    {
        try
        {
            using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(path), System.IO.Compression.ZipArchiveMode.Read))
            {
                bool is310 = zip.GetEntry(Marker310) != null, is92 = zip.GetEntry(Marker92) != null;
                if (slot == 0 && is310) return null;
                if (slot == 1 && is92) return null;
                if (slot == 0 && is92) return "That is the 9.2.0 APK. Choose it with the second button, and pick the 3.1.0 APK here.";
                if (slot == 1 && is310) return "That is the 3.1.0 APK. Choose it with the first button, and pick the 9.2.0 APK here.";
                return "That file is not the " + (slot == 0 ? "3.1.0" : "9.2.0") + " game APK.";
            }
        }
        catch (System.Exception) { return path.StartsWith("/proc/self/fd/") ? "unreadable" : "That file could not be read as an APK."; }
    }

    void Pick(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            activity.Call("tftfPickFile", slot);
#endif
    }

    void Update()
    {
        Poll();
        if (_installing && TftfInstaller.Done) { _installing = false; _finished = true; }
        if (_installing && TftfInstaller.Error != null) { _installing = false; _message = "Installation failed: " + TftfInstaller.Error; }
    }

    void OnGUI()
    {
        if (_finished) return;
        float s = Screen.height / 540f;
        if (_label == null)
        {
            _label = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true, alignment = TextAnchor.MiddleCenter };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 22 };
        }
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
        float w = Screen.width / s, h = Screen.height / s;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.blackTexture);
        float x = w * 0.5f - 280;
        float tx = x;
        if (_logo != null) { GUI.DrawTexture(new Rect(x, 12, 84, 84), _logo); tx = x + 96; }
        GUI.Label(new Rect(tx, 16, 560 - (tx - x), 40), "Transformers: Forged to Fight - revival launcher", _label);
        GUI.Label(new Rect(tx, 58, 560 - (tx - x), 56), "Pick your own copies of the 3.1.0 and 9.2.0 game APKs. Nothing leaves your phone.", _small);
        GUI.Label(new Rect(x, h - 30, 560, 24), "Launcher " + LauncherVersion + "  (game build 3.1.0-822754)", _small);
        if (_installing)
        {
            GUI.Label(new Rect(x, 170, 560, 40), TftfInstaller.Status, _label);
            GUI.Box(new Rect(x, 230, 560, 24), "");
            GUI.DrawTexture(new Rect(x + 2, 232, 556 * TftfInstaller.Progress, 20), Texture2D.whiteTexture);
            return;
        }
        GUI.enabled = true;
        if (GUI.Button(new Rect(x, 130, 560, 54), _names[0].Length == 0 ? "Select the 3.1.0 APK" : "3.1.0: " + _names[0], _button)) Pick(0);
        if (GUI.Button(new Rect(x, 196, 560, 54), _names[1].Length == 0 ? "Select the 9.2.0 APK" : "9.2.0: " + _names[1], _button)) Pick(1);
        bool ready = _paths[0].Length > 0 && _paths[1].Length > 0 && _trees != null;
        GUI.enabled = ready;
        if (GUI.Button(new Rect(x, 276, 560, 60), "Install and play", _button))
        {
            long free = FreeBytes();
            if (free >= 0 && free < RequiredBytes)
            {
                _message = "Not enough free space: about " + (RequiredBytes >> 20) + " MB are needed, " + (free >> 20) + " MB are free.";
                return;
            }
            _installing = true; _message = "";
            TftfInstaller.Start(_root, _paths[0], _paths[1], _trees, Path.Combine(Application.persistentDataPath, "asset_cache/odr"));
            GUI.enabled = true;
            return;
        }
        GUI.enabled = true;
        if (_message.Length > 0) GUI.Label(new Rect(x, 356, 560, 80), _message, _small);
    }
}
