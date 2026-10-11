namespace StoryPort
{
    // The server supplies the AI profile name; these weights mirror the 9.2
    // Default profile and MeleeCombo behavior tree used by Story encounters.
    public static class StoryPortEnemyAI
    {
        public enum TurnAction { MeleeCombo, Shoot, CloseDistance, SpecialAttack, HeavyAttack }
        public enum MeleeMove { Light, Medium, Heavy }

        public static TurnAction ChooseTurnAction(float roll, bool outsideMeleeRange, bool rangedPersonality,
            bool canShoot, bool specialReady)
        {
            float melee = 0f, shoot = 0f, close = 0f, special = 0f, heavy = 0f;
            if (outsideMeleeRange)
            {
                close = 5.5f; // Act_CloseDistance in AIAct.
                if (rangedPersonality && canShoot) shoot = 3f; // Act_Shoot.
            }
            else
            {
                melee = 7f; // Act_MeleeCombo.
                heavy = 1f; // Act_HeavyAttack.
            }
            if (specialReady) special = 3f; // Act_SpecialAttack.

            float total = melee + shoot + close + special + heavy;
            float weighted = Clamp01(roll) * total;
            if (weighted >= total) weighted = total - .00001f;
            if (weighted < melee) return TurnAction.MeleeCombo;
            weighted -= melee;
            if (weighted < shoot) return TurnAction.Shoot;
            weighted -= shoot;
            if (weighted < close) return TurnAction.CloseDistance;
            weighted -= close;
            if (weighted < special) return TurnAction.SpecialAttack;
            if (heavy > 0f) return TurnAction.HeavyAttack;
            if (special > 0f) return TurnAction.SpecialAttack;
            if (close > 0f) return TurnAction.CloseDistance;
            if (shoot > 0f) return TurnAction.Shoot;
            return TurnAction.MeleeCombo;
        }

        public static int ChooseRangedVolleyHits(float roll, string personality)
        {
            if (personality != "Ranged") return 1;
            // Ranged SH weights: Shoot .15, PauseShoot .25, ShootX2 .2, ShootX3 .4.
            float weighted = Clamp01(roll);
            if (weighted < .15f) return 1;
            if (weighted < .6f) return 2;
            return 3;
        }

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
