namespace StoryPort
{
    // The 9.2 AIAnticipateAttack tree chooses among these actions with weights
    // supplied by the enemy's AI personality.
    public static class StoryPortEnemyDefense
    {
        public enum Action { Dodge, Block, Sidestep, Idle }

        public static Action Choose(float roll)
        {
            return Choose(roll, StoryPortArenaData.FallbackPersonality);
        }

        public static Action Choose(float roll, string personality)
        {
            float dodge, block, sidestep, idle;
            GetWeights(personality, out dodge, out block, out sidestep, out idle);
            float weighted = Clamp01(roll) * (dodge + block + sidestep + idle);
            if (weighted < dodge) return Action.Dodge;
            if (weighted < dodge + block) return Action.Block;
            if (weighted < dodge + block + sidestep) return Action.Sidestep;
            return Action.Idle;
        }

        static void GetWeights(string personality, out float dodge, out float block, out float sidestep, out float idle)
        {
            // Values come from the converted 9.2 gameplay/aipersonalities assets.
            switch (personality ?? "")
            {
                case "Ranged":
                    dodge = .2f; block = 1f; sidestep = .8f; idle = .05f;
                    break;
                case "Balanced":
                    dodge = .125f; block = .75f; sidestep = .075f; idle = .1f;
                    break;
                case "EasyFTE":
                    dodge = 0f; block = 1f; sidestep = 0f; idle = .6f;
                    break;
                case "MeleeTank":
                    dodge = .3f; block = 1f; sidestep = .2f; idle = .6f;
                    break;
                case "Melee":
                default:
                    dodge = .4f; block = 1f; sidestep = .2f; idle = .6f;
                    break;
            }
        }

        static float Clamp01(float value) { return value < 0f ? 0f : value > 1f ? 1f : value; }
    }
}
