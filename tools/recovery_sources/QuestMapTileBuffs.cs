extern alias game;

using System.Collections.Generic;
using EB.Missions;

using QuestMapTile = game::Quests.MapTile;

namespace RecoverySources
{
    // Newly authored AI-assisted recovery code; no proprietary source text is reproduced.
    // Authored from the exact 9.2 ARM64 control-flow trace for
    // Quests.MapTile.GetIncomingBuffList (0x01094720). The trace classifies
    // User-scope buffs by source X and appends the resulting buckets in order.
    public static class QuestMapTileBuffs
    {
        public static List<Buff> GetIncomingBuffList(QuestMapTile tile)
        {
            List<Buff> attacker = tile.GetActiveAttackerBuffs();
            List<Buff> defender = tile.GetActiveDefenderBuffs();
            int capacity = (attacker == null ? 0 : attacker.Count) +
                (defender == null ? 0 : defender.Count);
            var byDifferentX = new List<Buff>(capacity);
            var bySameX = new List<Buff>(capacity);
            var otherScopes = new List<Buff>(capacity);

            for (int sourceIndex = 0; sourceIndex < 2; sourceIndex++)
            {
                List<Buff> sourceBuffs = sourceIndex == 0 ? attacker : defender;
                if (sourceBuffs == null)
                {
                    continue;
                }

                for (int i = 0; i < sourceBuffs.Count; i++)
                {
                    Buff entry = sourceBuffs[i];
                    EB.Missions.MapTile source = entry.source;
                    if (source != null && source.x == tile.x && source.y == tile.y)
                    {
                        continue;
                    }

                    EB.Sparx.Buff.Scope scope = entry.buff.scope;
                    if (scope < EB.Sparx.Buff.Scope.User)
                    {
                        otherScopes.Add(entry);
                    }
                    else if (scope == EB.Sparx.Buff.Scope.User)
                    {
                        if (source == null)
                        {
                            otherScopes.Add(entry);
                        }
                        else
                        {
                            float deltaX = source.position.x - tile.position.x;
                            if (deltaX * deltaX >= 1e-10f)
                            {
                                byDifferentX.Add(entry);
                            }
                            else
                            {
                                bySameX.Add(entry);
                            }
                        }
                    }
                }
            }

            var result = new List<Buff>(capacity);
            result.AddRange(byDifferentX);
            result.AddRange(bySameX);
            result.AddRange(otherScopes);
            return result;
        }

    }
}
