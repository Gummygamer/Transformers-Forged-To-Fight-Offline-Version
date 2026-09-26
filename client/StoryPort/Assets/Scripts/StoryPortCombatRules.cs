using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StoryPort
{
    // Combat constants served by /bcg/getLoginData: attackValues (share of the
    // attack stat, special energy gained, crit chance and crit multiplier per
    // move) and each blueprint's special damage ratios s1..s3 and class. The
    // defaults below are only used when the server has not answered yet.
    public class StoryPortCombatRules
    {
        public struct Move
        {
            public float Share, Mana, CritChance, CritDamage;
        }

        // Special energy that fills one of the three special bars.
        public const float ManaPerBar = 300f;

        readonly Dictionary<string, Move> moves = new Dictionary<string, Move>
        {
            { "Light", new Move { Share = .35f, Mana = 50f, CritChance = .05f, CritDamage = 1.5f } },
            { "Medium", new Move { Share = .6f, Mana = 75f, CritChance = .05f, CritDamage = 1.5f } },
            { "Heavy", new Move { Share = 1f, Mana = 120f, CritChance = .05f, CritDamage = 1.5f } },
        };
        readonly Dictionary<string, float[]> specials = new Dictionary<string, float[]>();
        readonly Dictionary<string, string> classes = new Dictionary<string, string>();

        public bool Loaded { get; private set; }

        public void Load(string loginJson)
        {
            if (string.IsNullOrEmpty(loginJson)) return;
            var attackValues = Regex.Match(loginJson, "\"attackValues\"\\s*:\\s*\\{(.*?\\})\\s*\\}", RegexOptions.Singleline);
            if (attackValues.Success)
                foreach (Match entry in Regex.Matches(attackValues.Groups[1].Value, "\"(\\w+)\"\\s*:\\s*\\{([^{}]*)\\}"))
                    moves[entry.Groups[1].Value] = new Move
                    {
                        Share = Number(entry.Groups[2].Value, "a", .35f),
                        Mana = Number(entry.Groups[2].Value, "m", 50f),
                        CritChance = Number(entry.Groups[2].Value, "c", .05f),
                        CritDamage = Number(entry.Groups[2].Value, "d", 1.5f),
                    };
            // Blueprint entries are flat objects keyed by bot id and carry s1..s3.
            foreach (Match blueprint in Regex.Matches(loginJson, "\"([a-z0-9_]+)\"\\s*:\\s*\\{([^{}]*\"s1\"[^{}]*)\\}"))
            {
                var body = blueprint.Groups[2].Value;
                specials[blueprint.Groups[1].Value] = new[] { Number(body, "s1", 1.75f), Number(body, "s2", 2.5f), Number(body, "s3", 3.5f) };
                var klass = Regex.Match(body, "\"cl\"\\s*:\\s*\"([^\"]*)\"");
                if (klass.Success) classes[blueprint.Groups[1].Value] = klass.Groups[1].Value;
            }
            Loaded = specials.Count > 0;
        }

        public Move MoveFor(string animatorState)
        {
            var kind = animatorState.StartsWith("Medium", StringComparison.Ordinal) ? "Medium"
                : animatorState.StartsWith("Heavy", StringComparison.Ordinal) ? "Heavy" : "Light";
            return moves.TryGetValue(kind, out var move) ? move : moves["Light"];
        }

        public float SpecialRatio(string botKey, int level)
        {
            level = Math.Max(1, Math.Min(3, level));
            return specials.TryGetValue(botKey ?? "", out var ratios) ? ratios[level - 1] : new[] { 1.75f, 2.5f, 3.5f }[level - 1];
        }

        public string ClassOf(string botKey)
        {
            return classes.TryGetValue(botKey ?? "", out var klass) ? klass : "";
        }

        // Server hero rating: (max health + attack) / 20.
        public static int Rating(float maxHealth, float attack)
        {
            return (int)((maxHealth + attack) / 20f);
        }

        static float Number(string body, string key, float fallback)
        {
            var match = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
            return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }
    }
}
