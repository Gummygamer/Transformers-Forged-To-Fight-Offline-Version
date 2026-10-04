namespace StoryPort
{
    // The 9.2 AIAnticipateAttack tree chooses among these actions with the
    // Melee personality's weights. Keeping the choice separate makes combat
    // timing and visual reactions independently adjustable.
    public static class StoryPortEnemyDefense
    {
        public enum Action { Dodge, Block, Sidestep, Idle }

        const float DodgeWeight = .4f;
        const float BlockWeight = 1f;
        const float SidestepWeight = .2f;
        const float IdleWeight = .6f;

        public static Action Choose(float roll)
        {
            float weighted = Clamp01(roll) * (DodgeWeight + BlockWeight + SidestepWeight + IdleWeight);
            if (weighted < DodgeWeight) return Action.Dodge;
            if (weighted < DodgeWeight + BlockWeight) return Action.Block;
            if (weighted < DodgeWeight + BlockWeight + SidestepWeight) return Action.Sidestep;
            return Action.Idle;
        }

        static float Clamp01(float value) { return value < 0f ? 0f : value > 1f ? 1f : value; }
    }
}
