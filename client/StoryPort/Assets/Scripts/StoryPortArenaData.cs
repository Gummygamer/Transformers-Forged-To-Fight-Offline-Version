using System;
using System.Text.RegularExpressions;

namespace StoryPort
{
    /// <summary>Reads the arena selected by the server from one quest response.</summary>
    public static class StoryPortArenaData
    {
        public const string FallbackLevel = "chicago";
        public const int FallbackTod = 0;

        public static bool TryReadBattleEnemy(string json, out string key, out string level, out int tod)
        {
            key = "";
            level = FallbackLevel;
            tod = FallbackTod;
            if (!TryObject(json, "battleEnemy", out var battleEnemy)) return false;

            key = ReadString(battleEnemy, "key");
            var requestedLevel = ReadString(battleEnemy, "mapOverride");
            var requestedTod = ReadInteger(battleEnemy, "todIndex", -1);
            if (IsSupportedLevel(requestedLevel) && requestedTod >= 0 && requestedTod <= 2)
            {
                level = requestedLevel;
                tod = requestedTod;
            }
            return !string.IsNullOrEmpty(key);
        }

        public static bool IsSupportedLevel(string level)
        {
            switch (level)
            {
                case "chicago":
                case "hongkong":
                case "karnak":
                case "mine":
                case "rust":
                    return true;
                default:
                    return false;
            }
        }

        static string ReadString(string json, string key)
        {
            var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        static int ReadInteger(string json, string key, int fallback)
        {
            var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(-?\\d+)");
            int parsed;
            return match.Success && int.TryParse(match.Groups[1].Value, out parsed) ? parsed : fallback;
        }

        static bool TryObject(string json, string property, out string value)
        {
            value = "";
            var source = json ?? "";
            var match = Regex.Match(source, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\{");
            if (!match.Success) return false;

            int start = match.Index + match.Length - 1;
            int depth = 0;
            bool quoted = false;
            bool escaped = false;
            for (int i = start; i < source.Length; i++)
            {
                char c = source[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    value = source.Substring(start, i - start + 1);
                    return true;
                }
            }
            return false;
        }
    }
}
