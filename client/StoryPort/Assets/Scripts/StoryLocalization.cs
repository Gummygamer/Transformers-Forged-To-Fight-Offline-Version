using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StoryPort
{
    // Selected-language story text. The server sends English (or a locale map) in
    // each dialogue line; catalogs under Resources/StoryPort/Localization add the
    // other languages, keyed by set id and line position so the server wire format
    // stays unchanged. Every failure path ends at English rather than a blank line.
    public static class StoryLocalization
    {
        public const string DefaultLocale = "en";
        public const string PrefKey = "storyport.language";
        const string CatalogFolder = "StoryPort/Localization/";

        // Selectable languages, in the order the original client lists them.
        public static readonly string[] Locales =
        {
            "en", "zh-CN", "zh-TW", "pt", "fr", "it", "de", "es", "ru", "ko", "ja", "tr", "ar", "id", "th", "no", "nl"
        };
        public static readonly string[] NativeNames =
        {
            "English", "简体中文", "繁體中文", "Português", "Français", "Italiano", "Deutsch", "Español", "Русский",
            "한국어", "日本語", "Türkçe", "العربية", "Bahasa Indonesia", "ไทย", "Norsk", "Nederlands"
        };

        // Raised after the selected language changes, so open screens can redraw.
        public static event Action Changed;

        static readonly Dictionary<string, Dictionary<string, string>> catalogs =
            new Dictionary<string, Dictionary<string, string>>();
        static Func<string, string> catalogSource = LoadResourceCatalog;
        static Func<string> savedLocale = () => PlayerPrefs.GetString(PrefKey, "");
        static Action<string> saveLocale = value => { PlayerPrefs.SetString(PrefKey, value); PlayerPrefs.Save(); };
        static string current;

        // The current locale: the saved choice, else the device language, else English.
        public static string Locale
        {
            get
            {
                if (current == null) current = Normalize(savedLocale()) ?? FromSystemLanguage(SystemLanguageOrDefault());
                return current;
            }
        }

        public static void SetLocale(string locale)
        {
            string normalized = Normalize(locale) ?? DefaultLocale;
            if (normalized == current) return;
            current = normalized;
            saveLocale(normalized);
            if (Changed != null) Changed();
        }

        public static void CycleLocale()
        {
            SetLocale(Locales[(Array.IndexOf(Locales, Locale) + 1) % Locales.Length]);
        }

        public static string NativeName(string locale)
        {
            int index = Array.IndexOf(Locales, Normalize(locale) ?? DefaultLocale);
            return NativeNames[index < 0 ? 0 : index];
        }

        // Maps a stored or requested code onto a supported one, or null when unknown.
        public static string Normalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            code = code.Trim().Replace('_', '-');
            foreach (var locale in Locales)
                if (string.Equals(locale, code, StringComparison.OrdinalIgnoreCase)) return locale;
            if (string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (string.Equals(code, "nb", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "nn", StringComparison.OrdinalIgnoreCase)) return "no";
            int dash = code.IndexOf('-');
            return dash > 0 ? Normalize(code.Substring(0, dash)) : null;
        }

        public static string FromSystemLanguage(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified: return "zh-CN";
                case SystemLanguage.ChineseTraditional: return "zh-TW";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Dutch: return "nl";
                default: return DefaultLocale;
            }
        }

        static SystemLanguage SystemLanguageOrDefault()
        {
            try { return ReadSystemLanguage(); }
            catch (Exception) { return SystemLanguage.English; }
        }

        // Kept apart so callers outside the Unity player can still catch its failure.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static SystemLanguage ReadSystemLanguage() { return Application.systemLanguage; }

        // Stable catalog key for a dialogue line: set id plus 1-based position.
        public static string KeyFor(string setId, int index)
        {
            return "ID_STORY_" + (setId ?? "").ToUpperInvariant() + "_" + (index + 1).ToString("D3");
        }

        // Chrome strings share the dialogue catalogs under ID_STORY_UI_* keys.
        public static string Ui(string key, string english)
        {
            return Lookup(Locale, key) ?? english;
        }

        // Text for one line in the current language. translations is the server's
        // per-locale map when it sent one; english is the server's plain string.
        public static string Resolve(string key, string english, Dictionary<string, string> translations)
        {
            return Resolve(Locale, key, english, translations);
        }

        public static string Resolve(string locale, string key, string english, Dictionary<string, string> translations)
        {
            locale = Normalize(locale) ?? DefaultLocale;
            string text = Translation(translations, locale);
            if (!string.IsNullOrEmpty(text)) return text;
            text = Lookup(locale, key);
            if (text != null) return text;
            if (!string.IsNullOrEmpty(english)) return english;
            text = Translation(translations, DefaultLocale);
            if (!string.IsNullOrEmpty(text)) return text;
            return Lookup(DefaultLocale, key) ?? "";
        }

        static string Translation(Dictionary<string, string> translations, string locale)
        {
            if (translations == null) return null;
            foreach (var entry in translations)
                if (Normalize(entry.Key) == locale && !string.IsNullOrEmpty(entry.Value)) return entry.Value;
            return null;
        }

        static string Lookup(string locale, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string text;
            return Catalog(locale).TryGetValue(key, out text) && !string.IsNullOrEmpty(text) ? text : null;
        }

        static Dictionary<string, string> Catalog(string locale)
        {
            Dictionary<string, string> catalog;
            if (catalogs.TryGetValue(locale, out catalog)) return catalog;
            string source = null;
            try { source = catalogSource(locale); }
            catch (Exception exception) { Debug.LogWarning("StoryPort catalog " + locale + " failed to load: " + exception.Message); }
            catalog = Parse(source);
            catalogs[locale] = catalog;
            return catalog;
        }

        static string LoadResourceCatalog(string locale)
        {
            var asset = Resources.Load<TextAsset>(CatalogFolder + "dialogue_" + locale);
            return asset != null ? asset.text : null;
        }

        // Catalog files are UTF-8 lines of KEY<TAB>text. '#' starts a comment; \n, \t
        // and \\ escape inside the text. Later duplicates lose to the first entry.
        public static Dictionary<string, string> Parse(string source)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(source)) return entries;
            foreach (var raw in source.Split('\n'))
            {
                string row = raw.TrimEnd('\r');
                if (row.Length == 0 || row[0] == '#') continue;
                int tab = row.IndexOf('\t');
                if (tab <= 0) continue;
                string key = row.Substring(0, tab).Trim();
                if (key.Length == 0 || entries.ContainsKey(key)) continue;
                entries[key] = Unescape(row.Substring(tab + 1));
            }
            return entries;
        }

        static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { text.Append(c); continue; }
                char next = value[++i];
                text.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next);
            }
            return text.ToString();
        }

        // Test hooks: swap catalog and preference storage, and drop cached state.
        public static void ConfigureForTests(Func<string, string> source, Func<string> read, Action<string> write)
        {
            catalogSource = source ?? LoadResourceCatalog;
            savedLocale = read ?? (() => PlayerPrefs.GetString(PrefKey, ""));
            saveLocale = write ?? (value => { PlayerPrefs.SetString(PrefKey, value); PlayerPrefs.Save(); });
            ResetCache();
        }

        public static void ResetCache()
        {
            catalogs.Clear();
            current = null;
        }
    }
}
