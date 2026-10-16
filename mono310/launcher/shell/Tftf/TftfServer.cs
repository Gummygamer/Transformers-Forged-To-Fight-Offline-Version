// Newly authored. Starts the offline game server inside the app: the Legible interpreter ships as
// liblegible.so (an executable in the native-library folder) and the authored server scripts ship as
// StreamingAssets/tftf_server.zip.
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

public static class TftfServer
{
    public const int Port = 8080;
    static Process process;

    public static IEnumerator Start(string root)
    {
        string serverDir = Path.Combine(root, "Server");
        string marker = Path.Combine(serverDir, ".extracted");
        string stamp = Application.version + "/" + Application.buildGUID;
        if (!File.Exists(marker) || File.ReadAllText(marker) != stamp)
        {
            var req = UnityWebRequest.Get(Application.streamingAssetsPath + "/tftf_server.zip");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                UnityEngine.Debug.LogError("[TFTF] server package missing: " + req.error);
                yield break;
            }
            string zipPath = Path.Combine(root, "tftf_server.zip");
            File.WriteAllBytes(zipPath, req.downloadHandler.data);
            if (Directory.Exists(serverDir)) Directory.Delete(serverDir, true);
            using (var za = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
                foreach (var entry in za.Entries)
                {
                    if (entry.Name.Length == 0) continue;
                    string target = Path.Combine(root, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = entry.Open()) using (var dst = File.Create(target)) src.CopyTo(dst);
                }
            File.Delete(zipPath);
            File.WriteAllText(marker, stamp);
        }
        Directory.CreateDirectory(Path.Combine(root, "odr"));
        TftfStaticServer.Start(Path.Combine(root, "odr"));
        string exe = NativeLibraryDir() + "/liblegible.so";
        if (!File.Exists(exe)) { UnityEngine.Debug.LogError("[TFTF] interpreter missing: " + exe); yield break; }
        var psi = new ProcessStartInfo(exe, "run Server/run_local.lbl")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.EnvironmentVariables["HTTP_PORT"] = Port.ToString();
        psi.EnvironmentVariables["TFTF_ODR_DIR"] = Path.Combine(root, "odr");
        string log = Path.Combine(root, "server.log");
        var sw = new StreamWriter(log, false) { AutoFlush = true };
        process = new Process { StartInfo = psi };
        process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sw) sw.WriteLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sw) sw.WriteLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        UnityEngine.Debug.Log("[TFTF] server started pid=" + process.Id);
        Application.quitting += Stop;
    }

    public static void Stop()
    {
        try { if (process != null && !process.HasExited) process.Kill(); } catch { }
    }

    static string NativeLibraryDir()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var info = activity.Call<AndroidJavaObject>("getApplicationInfo"))
            return info.Get<string>("nativeLibraryDir");
#else
        return ".";
#endif
    }
}
