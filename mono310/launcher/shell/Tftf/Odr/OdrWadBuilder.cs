// Newly authored. Port of mono310/scripts/build_odr_wad.py: packs the on-demand-resource bundles of the user's 9.2.0 APK
// into toc.wad (STORED zip) / toc.zip / manifest.json for the 3.1.0 client's ODRManager. Streams everything, so it needs
// little memory on a phone.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Tftf.Odr
{
    public static class OdrWadBuilder
    {
    static readonly string[] BasePacks = { "assets_base_odr", "assets_quest_odr", "assets_quest_fte_odr", "assets_common_odr", "common_odr", "primordial_odr", "primordial_base_odr", "buildings_odr", "characters_fx_procedural_odr", "ruinedcity_odr", "quintessa_odr", "unicron_odr", "characters_procedural_odr", "optimusprimal_bw_mp32_odr", "sharkticon_gs_brawler_odr", "bludgeon_gs_rd20_odr", "optimusprime_cin_tf_odr", "megatron_gs_leader2015_odr" };
    static readonly string[] CharacterPacks = { "Jetfire_GS_Leader2014_odr", "arcee_gs_deluxe2014_odr", "barricade_cin_dotm_odr", "blaster_gs_leader2016_odr", "bludgeon_gs_rd20_odr", "bonecrusher_cin_rotf_odr", "bumblebee_cin_dotm_odr", "bumblebee_gs_kabam_odr", "cheetor_bw_transmetal_odr", "cliffjumper_gs_kabam_odr", "cyclonus_gs_uw06_odr", "dinobot_bw_kabam_odr", "dirge_gs_deluxe2008_odr", "drift_cin_aoe_odr", "galvatron_gs_voyager2016_odr", "grimlock_gs_mp08_odr", "grindor_cin_rotf_odr", "hotrod_cin_tlk_odr", "hound_cin_tlk_odr", "ironhide_cin_rotf_odr", "ironhide_gs_kabam_odr", "jazz_gs_twm05_odr", "kickback_gs_kabam_odr", "megatron_cin_rotf_odr", "megatron_gs_leader2015_odr", "megatronus_gs_kabam_odr", "mirage_gs_deluxe2016_odr", "mixmaster_cin_rotf_odr", "motormaster_gs_voyager2015_odr", "necrotronus_gs_kabam_odr", "nemesisprime_gs_voyager2015_odr", "optimusprimal_bw_mp32_odr", "optimusprime_cin_tf_odr", "prowl_gs_deluxe2016_odr", "ramjet_gs_deluxe2008_odr", "ratchet_gs_kabam_odr", "rhinox_gs_voyager2014_odr", "rodimusprime_gs_mp09_odr", "scorponok_bw_kabam_odr", "sharkticon_gs_brawler_odr", "sharkticon_gs_demolition_odr", "sharkticon_gs_kabam_odr", "sharkticon_gs_scout_odr", "sharkticon_gs_tactician_odr", "sharkticon_gs_tech_odr", "sharkticon_gs_warrior_odr", "shockwave_gs_odr", "sideswipe_gs_odr", "skywarp_gs_leader2015_odr", "slipstream_gs_odr", "soundblaster_gs_mp13b_odr", "soundwave_gs_odr", "sunstreaker_gs_deluxe2008_odr", "tantrum_gs_kabam_odr", "thundercracker_gs_leader2015_odr", "ultramagnus_gs_leader_odr", "waspinator_gs_deluxe_odr", "wheeljack_gs_mp20_odr", "windblade_gs_odr" };
    static readonly string[] TowerRelicPacks = { "towers_odr", "relics_odr" };
    static readonly string[] TimeOfDayPacks = { "primordial", "base", "ruinedcity", "unicron", "quintessa_merged_odr", "mine" };
    static readonly string[] RequestedPaths = { "assets_quest/QuestRoot", "assets_quest/TapIndicator", "assets_quest/TapFeedback", "assets_quest/ItemPickup", "assets_quest/ItemTrail", "assets_quest/Ambush", "assets_quest/TriggerFight", "assets_quest/PlayerMarker", "assets_quest/ItemCard", "assets_quest/FinalBossLight", "assets_quest/NodeNumber", "assets_quest/GateOpened", "assets_quest/QuestComplete", "assets_base/BaseRoot", "assets_base/BaseTapFeedback", "assets_base/BaseRelicTapFeedback", "assets_base/BaseTapIndicator", "assets_base/BotPlaced", "assets_base/BotRemoved", "assets_base/ModPlaced", "assets_base/ModRemoved", "assets_base/CrystalClaim", "assets_base/RelicActive", "assets_base/RelicPlaced", "assets_base/RelicRemoved", "assets_base/RelicPedestalPlaced", "assets_base/RelicPedestalRemoved", "assets_base/BaseShieldActivate", "assets_base/BaseShieldDeactivate", "assets_base/BaseShieldActive", "assets_common/Boss", "assets_common/BossCard", "assets_common/RelicCard", "assets_common/Path", "assets_common/LinkedNode", "assets_common/LinkedNodeGlobal", "theme/primordial/prefabs/library_primordial", "theme/primordial_base/prefabs/library_primordial_base", "theme/common/prefabs/library_common", "buildings/prefabs/library_buildings" };
    static readonly string[] BaseApkBundles = { "character_audio", "character_fx", "characters", "moves", "quest" };

        static readonly string[] BundleKeyOrder = { "hash", "mount", "deterministic", "file", "ts", "manifest", "directory",
            "typetreehash", "compression", "deps", "crc", "contenthash", "size", "paths", "parent" };

        sealed class Entry { public string Name; public long DataOffset; public uint Crc; public long Size; }

        /// <summary>Builds toc.wad, toc.zip and manifest.json in outDir from the 9.2.0 APK.</summary>
        public static void Build(string donorApk, string outDir, string cdn, string cl, Action<string> log)
        {
            var packs = new List<string>(BasePacks);
            packs.AddRange(CharacterPacks.Where(c => !packs.Contains(c)));
            var requests = new List<string>(RequestedPaths);
            packs.AddRange(TimeOfDayPacks);
            for (int i = 0; i < 6; i++)
                foreach (var m in new[] { "forward", "deferred" })
                    requests.Add("primordial_merged/primordial_timeofday_" + i + "_" + m);
            foreach (var m in new[] { "forward", "deferred" }) requests.Add("base_merged/base_timeofday_0_" + m);
            packs = packs.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

            Directory.CreateDirectory(outDir);
            using (var apk = new ZipArchive(File.OpenRead(donorApk), ZipArchiveMode.Read))
            {
                var byLower = new Dictionary<string, ZipArchiveEntry>();
                foreach (var e in apk.Entries) byLower[e.FullName.ToLowerInvariant()] = e;
                var byExact = apk.Entries.ToDictionary(e => e.FullName, e => e);

                var tocs = new Dictionary<string, JObj>();                 // pack -> toc
                var bundleSrc = new Dictionary<string, KeyValuePair<string, ZipArchiveEntry>>(); // bundle -> (pack, entry)
                var bundleFile = new Dictionary<string, string>();
                foreach (var pack in packs)
                {
                    ZipArchiveEntry te;
                    if (!byExact.TryGetValue("assets/" + pack + "/toc.txt", out te)) throw new Exception("donor has no toc for " + pack);
                    JObj raw;
                    using (var r = new StreamReader(te.Open(), Encoding.UTF8)) raw = (JObj)JsonLite.Parse(r.ReadToEnd());
                    var toc = new JObj();
                    var bundles = new JObj();
                    var rawBundles = (JObj)raw["bundles"] ?? new JObj();
                    foreach (var kv in rawBundles.Items.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        var rec = Rewrite((JObj)kv.Value);
                        string file = (string)rec["file"];
                        ZipArchiveEntry src;
                        if (!byExact.TryGetValue("assets/assetpack/" + file, out src))
                        {
                            if (!byLower.TryGetValue(("assets/assetpack/" + file).ToLowerInvariant(), out src))
                                throw new Exception("donor bundle missing: " + file);
                            file = src.FullName.Substring("assets/assetpack/".Length);
                            rec["file"] = file;
                        }
                        if (bundleSrc.ContainsKey(kv.Key)) throw new Exception("duplicate bundle name " + kv.Key);
                        bundleSrc[kv.Key] = new KeyValuePair<string, ZipArchiveEntry>(pack, src);
                        bundleFile[kv.Key] = file;
                        bundles[kv.Key] = rec;
                    }
                    toc["bundles"] = bundles;
                    toc["files"] = raw["files"] ?? new List<object>();
                    toc["tags"] = raw["tags"] ?? new List<object>();
                    toc["version"] = raw["version"] ?? "";
                    toc["scenes"] = raw["scenes"] ?? new JObj();
                    tocs[pack] = toc;
                }
                ResolveRequests(requests, tocs);

                string wadPath = Path.Combine(outDir, "toc.wad");
                var entries = new List<Entry>();      // in order
                var tocNames = new List<string>();
                var bundleOffsets = new List<KeyValuePair<string, long>>();
                using (var wad = new FileStream(wadPath + ".tmp", FileMode.Create, FileAccess.ReadWrite))
                {
                    foreach (var pack in packs)
                    {
                        string tname = pack + "/toc.txt";
                        var body = Encoding.UTF8.GetBytes(JsonLite.Write(tocs[pack], true) + "\n");
                        entries.Add(StoreBytes(wad, tname, body));
                        tocNames.Add(tname);
                        foreach (var bname in ((JObj)tocs[pack]["bundles"]).Items.Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal))
                        {
                            var e = StoreStream(wad, bundleFile[bname], bundleSrc[bname].Value);
                            entries.Add(e);
                            bundleOffsets.Add(new KeyValuePair<string, long>(bname, e.DataOffset));
                        }
                        log("wad " + pack);
                    }
                    WriteCentralDirectory(wad, entries);
                }
                if (File.Exists(wadPath)) File.Delete(wadPath);
                File.Move(wadPath + ".tmp", wadPath);

                long wadSize = new FileInfo(wadPath).Length;
                uint wadCrc = FileCrc(wadPath);
                string zipPath = Path.Combine(outDir, "toc.zip");
                WriteDeflatedZip(zipPath, "toc.wad", wadPath, wadCrc, wadSize);
                long zipSize = new FileInfo(zipPath).Length;

                var bundlesJson = new List<object>();
                foreach (var kv in bundleOffsets)
                {
                    var rec = (JObj)((JObj)tocs[bundleSrc[kv.Key].Key]["bundles"])[kv.Key];
                    var b = new JObj();
                    b["name"] = kv.Key.ToLowerInvariant(); b["offset"] = kv.Value; b["deps"] = rec["deps"];
                    bundlesJson.Add(b);
                }
                var wadObj = new JObj();
                wadObj["name"] = "toc"; wadObj["url"] = "/odr/wad/toc.zip"; wadObj["filename"] = "toc.wad";
                wadObj["size"] = wadSize; wadObj["crc"] = (long)wadCrc; wadObj["zip_size"] = zipSize;
                wadObj["tocs"] = tocNames.Select(n => { var t = new JObj(); t["name"] = n; return (object)t; }).ToList();
                wadObj["bundles"] = bundlesJson;
                var build = new JObj();
                build["cl"] = cl; build["platform"] = "android"; build["check_crc"] = false; build["cdn"] = cdn;
                build["manifest"] = new List<object> { wadObj };
                var result = new JObj(); result["build"] = new List<object> { build };
                var man = new JObj(); man["error"] = null; man["result"] = result;
                File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonLite.Write(man, true) + "\n", new UTF8Encoding(false));
                log("wad done: " + wadSize + " bytes, zip " + zipSize);
            }
        }

        static JObj Rewrite(JObj rec)
        {
            var o = new JObj();
            foreach (var k in BundleKeyOrder) if (rec.Has(k)) o[k] = rec[k];
            foreach (var k in rec.Items.Select(i => i.Key).OrderBy(i => i, StringComparer.Ordinal)) if (!o.Has(k)) o[k] = rec[k];
            if (o["hash"] is long) o["hash"] = (long)(int)(uint)((long)o["hash"] & 0xFFFFFFFFL);
            var paths = (JObj)o["paths"] ?? new JObj();
            var sorted = new JObj();
            foreach (var kv in paths.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) sorted[kv.Key] = kv.Value;
            o["paths"] = sorted;
            o["deps"] = o["deps"] as List<object> ?? new List<object>();
            return o;
        }

        static void ResolveRequests(List<string> requests, Dictionary<string, JObj> tocs)
        {
            var index = new Dictionary<string, string[]>();          // lower key -> (pack, bundle, asset)
            foreach (var pack in tocs.Keys.OrderBy(p => p, StringComparer.Ordinal))
                foreach (var b in ((JObj)tocs[pack]["bundles"]).Items.OrderBy(i => i.Key, StringComparer.Ordinal))
                    foreach (var p in ((JObj)((JObj)b.Value)["paths"]).Items)
                    {
                        string k = p.Key.ToLowerInvariant();
                        if (!index.ContainsKey(k)) index[k] = new[] { pack, b.Key, (string)p.Value };
                    }
            foreach (var req in requests)
            {
                string key = req.Replace('\\', '/').ToLowerInvariant();
                if (index.ContainsKey(key)) continue;
                int slash = key.LastIndexOf('/');
                string prefix = slash < 0 ? "" : key.Substring(0, slash), leaf = key.Substring(slash + 1);
                Func<string, bool> sameLeaf = k => k.Substring(k.LastIndexOf('/') + 1) == leaf;
                var cands = index.Keys.Where(k => sameLeaf(k) && (k.LastIndexOf('/') < 0 ? "" : k.Substring(0, k.LastIndexOf('/'))).StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (cands.Count == 0) cands = index.Keys.Where(sameLeaf).ToList();
                if (cands.Count == 0) continue;
                cands.Sort(StringComparer.Ordinal);
                var t = index[cands[0]];
                ((JObj)((JObj)tocs[t[0]]["bundles"])[t[1]])["paths"] = AddPath((JObj)((JObj)((JObj)tocs[t[0]]["bundles"])[t[1]])["paths"], key, t[2]);
                index[key] = t;
            }
        }

        static JObj AddPath(JObj paths, string key, string asset)
        {
            paths[key] = asset;
            var sorted = new JObj();
            foreach (var kv in paths.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) sorted[kv.Key] = kv.Value;
            return sorted;
        }

        // ---- minimal zip writer (STORED entries, 32-bit sizes) ----

        static readonly uint[] CrcTable = MakeTable();
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        static uint CrcUpdate(uint crc, byte[] b, int n)
        {
            crc = ~crc;
            for (int i = 0; i < n; i++) crc = CrcTable[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        static uint FileCrc(string path)
        {
            uint crc = 0; var buf = new byte[1 << 20];
            using (var f = File.OpenRead(path)) { int n; while ((n = f.Read(buf, 0, buf.Length)) > 0) crc = CrcUpdate(crc, buf, n); }
            return crc;
        }

        static void W16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        static void W32(Stream s, uint v) { W16(s, (int)(v & 0xFFFF)); W16(s, (int)(v >> 16)); }

        static void Header(Stream s, string name, ushort method, uint crc, uint csize, uint usize)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            W32(s, 0x04034B50); W16(s, 20); W16(s, 0x0800); W16(s, method); W16(s, 0); W16(s, 0x21);
            W32(s, crc); W32(s, csize); W32(s, usize); W16(s, nb.Length); W16(s, 0); s.Write(nb, 0, nb.Length);
        }

        static Entry StoreBytes(Stream wad, string name, byte[] data)
        {
            long hdr = wad.Position;
            uint crc = CrcUpdate(0, data, data.Length);
            Header(wad, name, 0, crc, (uint)data.Length, (uint)data.Length);
            long dataOff = wad.Position;
            wad.Write(data, 0, data.Length);
            return new Entry { Name = name, DataOffset = dataOff, Crc = crc, Size = data.Length };
        }

        static Entry StoreStream(Stream wad, string name, ZipArchiveEntry src)
        {
            long hdr = wad.Position;
            Header(wad, name, 0, 0, 0, 0);
            long dataOff = wad.Position;
            uint crc = 0; long total = 0; var buf = new byte[256 * 1024];
            using (var s = src.Open())
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { crc = CrcUpdate(crc, buf, n); wad.Write(buf, 0, n); total += n; }
            }
            long end = wad.Position;
            wad.Position = hdr + 14; W32(wad, crc); W32(wad, (uint)total); W32(wad, (uint)total);
            wad.Position = end;
            return new Entry { Name = name, DataOffset = dataOff, Crc = crc, Size = total };
        }

        static void WriteCentralDirectory(Stream s, List<Entry> entries)
        {
            long cdStart = s.Position;
            foreach (var e in entries)
            {
                var nb = Encoding.UTF8.GetBytes(e.Name);
                long hdrOff = e.DataOffset - 30 - nb.Length;
                W32(s, 0x02014B50); W16(s, 0x031E); W16(s, 20); W16(s, 0x0800); W16(s, 0); W16(s, 0); W16(s, 0x21);
                W32(s, e.Crc); W32(s, (uint)e.Size); W32(s, (uint)e.Size); W16(s, nb.Length); W16(s, 0); W16(s, 0);
                W16(s, 0); W16(s, 0); W32(s, 0x81A40000u); W32(s, (uint)hdrOff); s.Write(nb, 0, nb.Length);
            }
            long cdSize = s.Position - cdStart;
            W32(s, 0x06054B50); W16(s, 0); W16(s, 0); W16(s, entries.Count); W16(s, entries.Count);
            W32(s, (uint)cdSize); W32(s, (uint)cdStart); W16(s, 0);
        }

        static void WriteDeflatedZip(string zipPath, string name, string src, uint crc, long size)
        {
            string tmp = zipPath + ".tmp";
            using (var o = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            {
                Header(o, name, 8, crc, 0, (uint)size);
                long dataOff = o.Position;
                using (var ds = new DeflateStream(o, CompressionMode.Compress, true))
                using (var f = File.OpenRead(src))
                {
                    var buf = new byte[256 * 1024]; int n;
                    while ((n = f.Read(buf, 0, buf.Length)) > 0) ds.Write(buf, 0, n);
                }
                long end = o.Position;
                uint csize = (uint)(end - dataOff);
                o.Position = dataOff - 30 - Encoding.UTF8.GetByteCount(name) + 18; W32(o, csize);
                o.Position = end;
                long cd = o.Position;
                var nb = Encoding.UTF8.GetBytes(name);
                W32(o, 0x02014B50); W16(o, 0x031E); W16(o, 20); W16(o, 0x0800); W16(o, 8); W16(o, 0); W16(o, 0x21);
                W32(o, crc); W32(o, csize); W32(o, (uint)size); W16(o, nb.Length); W16(o, 0); W16(o, 0);
                W16(o, 0); W16(o, 0); W32(o, 0x81A40000u); W32(o, 0); o.Write(nb, 0, nb.Length);
                long cdSize = o.Position - cd;
                W32(o, 0x06054B50); W16(o, 0); W16(o, 0); W16(o, 1); W16(o, 1); W32(o, (uint)cdSize); W32(o, (uint)cd); W16(o, 0);
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
        }
    }
}
