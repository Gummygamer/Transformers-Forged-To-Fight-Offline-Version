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
#if UNITY_STANDALONE_WIN
    public const int Version = 4;                                     // bump to force a reinstall
    const int BundlePlatform = 19;     // StandaloneWindows64: the player refuses the APKs' bundles stamped for Android
#else
    public const int Version = 2;                                     // bump to force a reinstall
    const int BundlePlatform = 0;      // phone: the APKs' bundles already match the player
#endif
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
    public static void Start(string root, string apk310, string apk92, byte[] treesBin, string odrCacheDir)
    {
        _root = root; Done = false; Error = null; Progress = 0; Status = "Starting";
        new Thread(() =>
        {
            try { Run(root, apk310, apk92, treesBin, odrCacheDir); Done = true; }
            catch (Exception e) { Error = e.Message; UnityEngine.Debug.LogError("[TFTF] install failed: " + e); Note("FAILED: " + e); }
        }) { Name = "TftfInstaller", IsBackground = true }.Start();
    }

    static void Step(string status, float progress)
    {
        Status = status; Progress = progress;
        UnityEngine.Debug.Log("[TFTF] install: " + status);
        Note(status);
    }

    // A small log next to the installed data, for bug reports: tftf/install.log
    static void Note(string line)
    {
        try { File.AppendAllText(Path.Combine(_root, "install.log"), DateTime.Now.ToString("s") + " " + line + "\n"); }
        catch { }
    }

    static void Run(string root, string apk310, string apk92, byte[] treesBin, string odrCacheDir)
    {
        Directory.CreateDirectory(root);
        try { File.Delete(Path.Combine(root, "ready.txt")); } catch { }
        if (BundlePlatform != 0)
        {
            SerializedFile.TargetPlatform = BundlePlatform;
            TextureTranscode.EtcToBc1 = true;     // the PC player cannot sample ETC1; DXT1 has the same block size
        }

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
                CopyEntry(e, target, e.Name.EndsWith(".assetbundle", StringComparison.Ordinal));
                if (++n % 20 == 0) Progress = 0.02f + 0.18f * n / Math.Max(1, total);
            }

            Step("Converting game data", 0.2f);
            var stats = new ConvertStats();
            var bundle = PlayerDataConverter.Convert(new ZipDataSource(z310), TreeDb.Load(treesBin), stats, s => { });
            File.WriteAllBytes(Path.Combine(root, "resources.bundle.tmp"), bundle);
            ReplaceFile(Path.Combine(root, "resources.bundle.tmp"), Path.Combine(root, "resources.bundle"));
            File.WriteAllText(Path.Combine(root, "shadermap.txt"), stats.ShaderMap);
            UnityEngine.Debug.Log("[TFTF] converted: " + stats);
        }

        using (var z92 = new ZipArchive(File.OpenRead(apk92), ZipArchiveMode.Read))
        {
            var moves = z92.GetEntry("assets/assetpack/characters/moves.assetbundle");
            if (moves == null) throw new Exception("The second file is not the 9.2.0 game APK (no character move data found).");
            Step("Extracting fighter move data", 0.4f);
            CopyEntry(moves, Path.Combine(root, "moves92.bundle"), true);
        }

        Step("Building the content package (this takes a few minutes)", 0.45f);
        string odr = Path.Combine(root, "odr");
        int packs = 0;
        if (Directory.Exists(odr)) Directory.Delete(odr, true);
        OdrWadBuilder.Build(apk92, odr, "http://127.0.0.1:8090", "822754", s =>
        {
            Status = "Building the content package";
            if (s.StartsWith("wad ", StringComparison.Ordinal) && ++packs <= 80) Progress = 0.45f + 0.5f * packs / 80f;
        }, writeZip: false, bundleTransform: BundlePlatform != 0 ? b => BundleRetarget.Retarget(b, BundlePlatform) : (Func<byte[], byte[]>)null);
        // The game would download the package from the content server and unpack it into its cache (minutes). Put the
        // finished WAD where that unpacking would leave it: <cache>/<sha1 of Path.Combine(url path, wad name)>.wad
        // (DownloadExtractor joins them with Path.Combine, so the separator is '\' on Windows).
        Directory.CreateDirectory(odrCacheDir);
        string cached = Path.Combine(odrCacheDir, Sha1Hex(Path.Combine("/odr/wad/toc.zip", "toc.wad")) + ".wad");
        if (File.Exists(cached)) File.Delete(cached);
        File.Move(Path.Combine(odr, "toc.wad"), cached);

        File.WriteAllText(Path.Combine(root, "ready.txt"), Version.ToString());
        Step("Done", 1f);
    }

    static string Sha1Hex(string text)
    {
        using (var sha = System.Security.Cryptography.SHA1.Create())
        {
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
            var sb = new System.Text.StringBuilder();
            foreach (var b in h) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }

    static void CopyEntry(ZipArchiveEntry e, string target, bool bundle)
    {
        if (!bundle || BundlePlatform == 0)
        {
            using (var src = e.Open()) using (var dst = File.Create(target)) src.CopyTo(dst);
            return;
        }
        var ms = new MemoryStream((int)e.Length);
        using (var src = e.Open()) src.CopyTo(ms);
        File.WriteAllBytes(target, BundleRetarget.Retarget(ms.ToArray(), BundlePlatform));
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
