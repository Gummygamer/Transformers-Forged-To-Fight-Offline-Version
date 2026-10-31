// Newly authored. Fills the game's string tables. The 3.1.0 APK has complete tables only for English and Spanish, and the
// game downloaded the rest from a CDN that is gone, so a missing string showed its raw id ("MS_ID_UI_FIGHT_KO"). The 9.2.0
// APK carries the full tables as loose serialized files; the installer copies them to tftf/lang92/<code>/<table>.json.
// Priority when a language loads, lowest first: 9.2.0 English, 3.1.0 English, 9.2.0 language, 3.1.0 language.
// Server/data/story_i18n.json translates the project's own story text, which no APK contains.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class TftfLanguages
{
    const string Folder = "lang92";
    const int ClassTextAsset = 49;
    const int MaxTableFile = 4 * 1024 * 1024;
    static readonly byte[] TableMark = Encoding.ASCII.GetBytes("\"strings\"");
    static readonly Regex Placeholder = new Regex(@"\{\d+[^}]*\}", RegexOptions.CultureInvariant);

    static Dictionary<string, string> _story;
    static string _storyLanguage;

    /// <summary>Installer step: copies every string table of the 9.2.0 APK. Returns the number of tables written.</summary>
    public static int Extract(ZipArchive z92, string root)
    {
        string dir = Path.Combine(root, Folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        int written = 0;
        foreach (var e in z92.Entries)
        {
            if (!IsLooseAsset(e) || e.Length > MaxTableFile) continue;
            byte[] data;
            using (var s = e.Open())
            {
                var ms = new MemoryStream((int)e.Length);
                s.CopyTo(ms);
                data = ms.ToArray();
            }
            if (IndexOf(data, TableMark) < 0) continue;
            foreach (var t in ReadTextAssets(data))
            {
                int cut = t.Key.LastIndexOf('_');
                if (cut <= 0 || t.Value.IndexOf("\"strings\"", StringComparison.Ordinal) < 0) continue;
                string target = Path.Combine(dir, t.Key.Substring(cut + 1).ToLowerInvariant());
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, t.Key.Substring(0, cut) + ".json"), t.Value, new UTF8Encoding(false));
                written++;
            }
        }
        return written;
    }

    static bool IsLooseAsset(ZipArchiveEntry e)
    {
        const string prefix = "assets/bin/Data/";
        return e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length == 32 && e.FullName.Length == prefix.Length + 32;
    }

    /// <summary>Reads (name, text) of each TextAsset in a Unity 2020 player file (format 22, no type trees).</summary>
    public static List<KeyValuePair<string, string>> ReadTextAssets(byte[] d)
    {
        var found = new List<KeyValuePair<string, string>>();
        if (d.Length < 64 || BigEndian32(d, 8) != 22 || d[16] != 0) return found;
        long dataOffset = ((long)BigEndian32(d, 32) << 32) | (uint)BigEndian32(d, 36);
        int p = 48;
        while (d[p] != 0) p++;
        p += 1 + 4;                                     // version string, platform
        if (d[p++] != 0) return found;                  // type trees: not how the 9.2.0 player ships
        int typeCount = BitConverter.ToInt32(d, p); p += 4;
        var classes = new int[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            classes[i] = BitConverter.ToInt32(d, p);
            p += 4 + 1 + 2 + (classes[i] == 114 ? 16 : 0) + 16;
        }
        int objectCount = BitConverter.ToInt32(d, p); p += 4;
        for (int i = 0; i < objectCount; i++)
        {
            p = (p + 3) & ~3;
            long start = BitConverter.ToInt64(d, p + 8);
            int type = BitConverter.ToInt32(d, p + 20);
            p += 24;
            if (type < 0 || type >= typeCount || classes[type] != ClassTextAsset) continue;
            int o = (int)(dataOffset + start);
            string name = ReadAligned(d, ref o);
            string text = ReadAligned(d, ref o);
            found.Add(new KeyValuePair<string, string>(name, text));
        }
        return found;
    }

    static string ReadAligned(byte[] d, ref int o)
    {
        int n = BitConverter.ToInt32(d, o);
        string s = Encoding.UTF8.GetString(d, o + 4, n);
        o = (o + 4 + n + 3) & ~3;
        return s;
    }

    static int BigEndian32(byte[] d, int p) { return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; }

    static int IndexOf(byte[] data, byte[] mark)
    {
        for (int i = 0; i <= data.Length - mark.Length; i++)
        {
            int j = 0;
            while (j < mark.Length && data[i + j] == mark[j]) j++;
            if (j == mark.Length) return i;
        }
        return -1;
    }

    static string Root { get { return Path.Combine(Application.persistentDataPath, "tftf"); } }

    /// <summary>Called by Localizer.LoadAllFromResources before it loads the 3.1.0 tables of the language.</summary>
    public static void LoadFallbacks(string code)
    {
        try
        {
            string lang = code.ToLowerInvariant();
            LoadFolder("en");
            foreach (var t in EB.Assets.LoadAll<TextAsset>("Languages/en"))
            {
                EB.Localizer.LoadStringsFromText(t.text);
                EB.Assets.Unload(t);
            }
            if (lang != "en") LoadOver(lang);
            LoadStory(lang);
            Debug.Log("[TFTF] language " + lang + ": " + EB.Localizer.Strings.Count + " strings");
        }
        catch (Exception e) { Debug.LogError("[TFTF] language fallbacks: " + e); }
    }

    static IEnumerable<string> Tables(string lang)
    {
        string dir = Path.Combine(Path.Combine(Root, Folder), lang);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : new string[0];
    }

    static void LoadFolder(string lang)
    {
        foreach (var f in Tables(lang)) EB.Localizer.LoadStringsFromText(File.ReadAllText(f));
    }

    // A 9.2.0 string can take other arguments than the 3.1.0 code passes, which would break string.Format: keep English then.
    static void LoadOver(string lang)
    {
        var strings = EB.Localizer.Strings;
        var english = new Dictionary<string, string>(strings);
        foreach (var f in Tables(lang))
        {
            var added = EB.Localizer.LoadStringsFromText(File.ReadAllText(f));
            foreach (DictionaryEntry kv in added)
            {
                string id = (string)kv.Key, en;
                if (english.TryGetValue(id, out en) && !SamePlaceholders(en, (string)kv.Value)) strings[id] = en;
            }
        }
    }

    public static bool SamePlaceholders(string a, string b)
    {
        var x = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (Match m in Placeholder.Matches(a)) x[m.Value] = true;
        var y = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (Match m in Placeholder.Matches(b)) y[m.Value] = true;
        if (x.Count != y.Count) return false;
        foreach (var k in x.Keys) if (!y.ContainsKey(k)) return false;
        return true;
    }

    static void LoadStory(string lang)
    {
        if (lang == _storyLanguage) return;
        _storyLanguage = lang;
        _story = null;
        if (lang == "en") return;
        string path = Path.Combine(Root, "Server/data/story_i18n.json");
        if (!File.Exists(path)) return;
        var all = EB.JSON.Parse(File.ReadAllText(path)) as Hashtable;
        var en = all == null ? null : all["en"] as ArrayList;
        var tr = all == null ? null : all[lang] as ArrayList;
        if (en == null || tr == null || en.Count != tr.Count) return;
        _story = new Dictionary<string, string>(en.Count);
        for (int i = 0; i < en.Count; i++) _story[(string)en[i]] = (string)tr[i];
    }

    /// <summary>Translates a line of the project's story as it arrives from the server; any other text passes through.</summary>
    public static string Story(string text)
    {
        string t;
        return _story != null && text != null && _story.TryGetValue(text, out t) ? t : text;
    }
}
