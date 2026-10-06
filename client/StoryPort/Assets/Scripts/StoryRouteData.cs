using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StoryPort
{
    public sealed class StoryMapNode
    {
        public int x;
        public int y;
        public string label;
        public string boss;
        public bool isFinal;
        public bool isStart;
        // Dialogue set ids the server attaches to the tile: shown before the
        // fight ("dialogue") and after it is won ("dialoguePE").
        public string dialogue;
        public string dialogueAfter;
        public readonly List<Vector2Int> links = new List<Vector2Int>();
    }

    public sealed class DialogueLine
    {
        public string character;
        public string side;
        // Server text: English, or the English member of a locale map.
        public string line;
        // Catalog key for this line, and the server's per-locale map when it sent one.
        public string key;
        public Dictionary<string, string> translations;
        public bool inShadow;

        // The line in the player's selected language, falling back to English.
        public string Text { get { return StoryLocalization.Resolve(key, line, translations); } }
    }

    // The server owns the route. Keeping its parser independent of the screen
    // makes route changes checkable in the Editor before an Android build.
    public sealed class StoryRouteData
    {
        public readonly List<StoryMapNode> nodes = new List<StoryMapNode>();
        public int dimension;
        public bool hasMap;

        public static StoryMapNode ResolvePosition(List<StoryMapNode> nodes, int x, int y)
        {
            if (nodes == null) return null;
            var current = nodes.Find(node => node.x == x && node.y == y);
            if (current != null) return current;
            var start = nodes.Find(node => node.isStart);
            return start ?? nodes.Find(node => node.x == 0);
        }

        public static StoryRouteData Parse(string response, string qid)
        {
            var route = new StoryRouteData();
            string quest = ReadValue(response, qid);
            string map = ReadValue(quest, "map");
            if (string.IsNullOrEmpty(map)) return route;
            route.hasMap = true;
            int.TryParse(ReadValue(map, "gridDimension"), out route.dimension);
            var rows = SplitArray(ReadValue(map, "grid"));
            for (int x = 0; x < rows.Count; x++)
            {
                var cells = SplitArray(rows[x]);
                for (int y = 0; y < cells.Count; y++)
                {
                    string tile = cells[y];
                    if (ReadValue(tile, "walkable") != "true" || ReadValue(tile, "hidden") == "true") continue;
                    var node = new StoryMapNode
                    {
                        // Server grids put x in the outer row and y in the inner row.
                        x = x,
                        y = y,
                        label = ReadString(tile, "lab"),
                        boss = ReadString(tile, "boss"),
                        isFinal = ReadValue(tile, "final") == "true",
                        isStart = ReadValue(tile, "start") == "true",
                        dialogue = ReadString(tile, "dialogue"),
                        dialogueAfter = ReadString(tile, "dialoguePE")
                    };
                    foreach (var link in SplitArray(ReadValue(tile, "links")))
                    {
                        int linkX, linkY;
                        if (int.TryParse(ReadValue(link, "x"), out linkX) &&
                            int.TryParse(ReadValue(link, "y"), out linkY))
                            node.links.Add(new Vector2Int(linkX, linkY));
                    }
                    route.nodes.Add(node);
                }
            }
            if (route.dimension <= 0) route.dimension = rows.Count;
            return route;
        }

        // One dialogue set from a quest-detail dialogueTable.
        public static List<DialogueLine> ReadDialogue(string detailJson, string setId)
        {
            var lines = new List<DialogueLine>();
            if (string.IsNullOrEmpty(setId)) return lines;
            var table = ReadValue(detailJson, "dialogueTable");
            foreach (var entry in SplitArray(ReadValue(table, setId)))
            {
                // "line" is a plain string, or an object of locale -> text.
                string raw = ReadValue(entry, "line");
                var translations = raw.StartsWith("{", StringComparison.Ordinal) ? ReadStringMap(raw) : null;
                string english = translations != null
                    ? (translations.ContainsKey("en") ? translations["en"] : "")
                    : ReadString(entry, "line");
                lines.Add(new DialogueLine
                {
                    character = ReadString(entry, "character"),
                    side = ReadString(entry, "side"),
                    line = english,
                    key = StoryLocalization.KeyFor(setId, lines.Count),
                    translations = translations,
                    inShadow = ReadValue(entry, "inShadow") == "true"
                });
            }
            return lines;
        }

        // A flat JSON object of string values, e.g. {"en":"Hello","de":"Hallo"}.
        static Dictionary<string, string> ReadStringMap(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match pair in Regex.Matches(json, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string key = pair.Groups[1].Value;
                string value = pair.Groups[2].Value;
                try { value = Regex.Unescape(value); } catch (ArgumentException) { }
                map[key] = value;
            }
            return map;
        }

        public static string ReadString(string json, string key)
        {
            string value = ReadValue(json, key);
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return "";
            try { return Regex.Unescape(value.Substring(1, value.Length - 2)); }
            catch (ArgumentException) { return value.Substring(1, value.Length - 2); }
        }

        static string ReadValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:");
            if (!match.Success) return "";
            int start = match.Index + match.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return "";
            char first = json[start];
            if (first == '{' || first == '[')
            {
                char open = first, close = first == '{' ? '}' : ']';
                int depth = 0;
                bool inString = false, escaped = false;
                for (int i = start; i < json.Length; i++)
                {
                    char c = json[i];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') { inString = true; continue; }
                    if (c == open) depth++;
                    else if (c == close && --depth == 0) return json.Substring(start, i - start + 1);
                }
                return "";
            }
            if (first == '"')
            {
                bool escaped = false;
                for (int i = start + 1; i < json.Length; i++)
                {
                    if (escaped) escaped = false;
                    else if (json[i] == '\\') escaped = true;
                    else if (json[i] == '"') return json.Substring(start, i - start + 1);
                }
                return "";
            }
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
            return json.Substring(start, end - start).Trim();
        }

        static List<string> SplitArray(string array)
        {
            var items = new List<string>();
            if (string.IsNullOrEmpty(array) || array[0] != '[') return items;
            int start = -1, depth = 0;
            bool inString = false, escaped = false;
            for (int i = 1; i < array.Length; i++)
            {
                char c = array[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { if (start < 0) start = i; inString = true; continue; }
                if (c == '{' || c == '[') { if (start < 0) start = i; depth++; continue; }
                if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        items.Add(array.Substring(start, i - start + 1));
                        start = -1;
                    }
                    if (c == ']' && depth < 0) break;
                    continue;
                }
                if (c == ',' && depth == 0)
                {
                    if (start >= 0) items.Add(array.Substring(start, i - start).Trim());
                    start = -1;
                    continue;
                }
                if (!char.IsWhiteSpace(c) && start < 0) start = i;
            }
            return items;
        }
    }
}
