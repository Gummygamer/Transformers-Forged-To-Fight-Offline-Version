namespace StoryPort
{
    // The server supplies the AI profile name; these weights mirror the 9.2
    // Default profile and MeleeCombo behavior tree used by Story encounters.
    public static class StoryPortEnemyAI
    {
        public enum MeleeMove { Light, Medium, Heavy }

        public static int ChooseComboLength(float roll, string profile)
        {
            if (profile != "Default") return 3;

            // Default profile: 1 hit = 0, 2 = .1, 3 = .4, 4 = 1, 5 = .5.
            float weighted = Clamp01(roll) * 2f;
            if (weighted < .1f) return 2;
            if (weighted < .5f) return 3;
            if (weighted < 1.5f) return 4;
            return 5;
        }

        public static MeleeMove ChooseMeleeMove(float roll, int hit, int comboLength, string profile)
        {
            if (profile == "Default" && hit >= comboLength)
            {
                // Non-heavy enders have weight 1 each; HeavyEnderWeight is .1.
                float weighted = Clamp01(roll) * 2.1f;
                if (weighted < 1f) return MeleeMove.Light;
                if (weighted < 2f) return MeleeMove.Medium;
                return MeleeMove.Heavy;
            }

            // The behavior tree selects Light or Medium during combo repeats.
            return Clamp01(roll) < .5f ? MeleeMove.Light : MeleeMove.Medium;
        }

        static float Clamp01(float value) { return value < 0f ? 0f : value > 1f ? 1f : value; }
    }
}
