// Newly authored. The first-run installation: reads the user's own 3.1.0 and 9.2.0 APKs and produces, under
// persistentDataPath/tftf, everything the recompiled game needs (nothing of the original game ships in this app).
//   base/assets/        the 3.1.0 APK's loose assets (bundles, manifests)
//   resources.bundle    the 3.1.0 player data converted for the Unity 2020 runtime
//   moves92.bundle      the 9.2.0 move files the donor fighters need
//   odr/                manifest.json + wad/toc.zip built from the 9.2.0 on-demand bundles
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Tftf.Convert;
using Tftf.Odr;

public sealed class ZipDataSource : IPlayerDataSource
{
    readonly ZipArchive _zip;
    public ZipDataSource(ZipArchive zip) { _zip = zip; }

    public byte[] Read(string name)
    {
        var e = _zip.GetEntry("assets/bin/Data/" + name);
        if (e == null) return null;
        using (var s = e.Open())
        {
            var ms = new MemoryStream((int)Math.Max(e.Length, 16));
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}

public static class TftfInstaller
{
    public const int Version = 1;                                     // bump to force a reinstall
    public static volatile string Status = "";
    public static volatile float Progress;
    public static volatile string Error;
    public static volatile bool Done;

    public static string Root { get { return _root; } }
    static string _root;

    public static bool IsInstalled(string root)
    {
        try { return File.Exists(Path.Combine(root, "ready.txt")) && File.ReadAllText(Path.Combine(root, "ready.txt")).Trim() == Version.ToString(); }
        catch { return false; }
    }

    /// <summary>Starts the installation on a worker thread. apk310/apk92 are readable (seekable) file paths.</summary>
    public static void Start(string root, string apk310, string apk92, byte[] treesBin)
    {
        _root = root; Done = false; Error = null; Progress = 0; Status = "Starting";
        new Thread(() =>
        {
            try { Run(root, apk310, apk92, treesBin); Done = true; }
            catch (Exception e) { Error = e.Message; UnityEngine.Debug.LogError("[TFTF] install failed: " + e); }
        }) { Name = "TftfInstaller", IsBackground = true }.Start();
    }

    static void Step(string status, float progress)
    {
        Status = status; Progress = progress;
        UnityEngine.Debug.Log("[TFTF] install: " + status);
    }

    static void Run(string root, string apk310, string apk92, byte[] treesBin)
    {
        Directory.CreateDirectory(root);
        try { File.Delete(Path.Combine(root, "ready.txt")); } catch { }

        using (var z310 = new ZipArchive(File.OpenRead(apk310), ZipArchiveMode.Read))
        {
            if (z310.GetEntry("assets/bin/Data/globalgamemanagers") == null)
                throw new Exception("The first file is not the 3.1.0 game APK (no player data found).");

            Step("Copying base game files", 0.02f);
            string baseDir = Path.Combine(root, "base/assets");
            int n = 0, total = 0;
            foreach (var e in z310.Entries) if (IsBaseAsset(e)) total++;
            foreach (var e in z310.Entries)
            {
                if (!IsBaseAsset(e)) continue;
                string target = Path.Combine(baseDir, e.FullName.Substring("assets/".Length));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var src = e.Open()) using (var dst = File.Create(target)) src.CopyTo(dst);
                if (++n % 20 == 0) Progress = 0.02f + 0.18f * n / Math.Max(1, total);
            }

            Step("Converting game data", 0.2f);
            var stats = new ConvertStats();
            var bundle = PlayerDataConverter.Convert(new ZipDataSource(z310), TreeDb.Load(treesBin), stats, s => { });
            File.WriteAllBytes(Path.Combine(root, "resources.bundle.tmp"), bundle);
            ReplaceFile(Path.Combine(root, "resources.bundle.tmp"), Path.Combine(root, "resources.bundle"));
            UnityEngine.Debug.Log("[TFTF] converted: " + stats);
        }

        using (var z92 = new ZipArchive(File.OpenRead(apk92), ZipArchiveMode.Read))
        {
            var moves = z92.GetEntry("assets/assetpack/characters/moves.assetbundle");
            if (moves == null) throw new Exception("The second file is not the 9.2.0 game APK (no character move data found).");
            Step("Extracting fighter move data", 0.4f);
            using (var src = moves.Open()) using (var dst = File.Create(Path.Combine(root, "moves92.bundle"))) src.CopyTo(dst);
        }

        Step("Building the content package (this takes a few minutes)", 0.45f);
        string odr = Path.Combine(root, "odr");
        if (Directory.Exists(odr)) Directory.Delete(odr, true);
        OdrWadBuilder.Build(apk92, odr, "http://127.0.0.1:8090", "822754", s => { Status = "Building the content package: " + s; });
        Directory.CreateDirectory(Path.Combine(odr, "wad"));
        File.Move(Path.Combine(odr, "toc.zip"), Path.Combine(odr, "wad/toc.zip"));
        File.Delete(Path.Combine(odr, "toc.wad"));

        File.WriteAllText(Path.Combine(root, "ready.txt"), Version.ToString());
        Step("Done", 1f);
    }

    static bool IsBaseAsset(ZipArchiveEntry e)
    {
        return e.Name.Length > 0 && e.FullName.StartsWith("assets/", StringComparison.Ordinal)
               && !e.FullName.StartsWith("assets/bin/", StringComparison.Ordinal);
    }

    static void ReplaceFile(string tmp, string dst)
    {
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(tmp, dst);
    }
}
