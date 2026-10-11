using System.Collections.Generic;
using EB.Missions;
using UnityEngine;

namespace RecoverySources
{
    // Rebuilt from the original 9.2 ARM64 field, branch, and call observations.
    public static class MapRecovery
    {
        public static MapTile GetTile(Map map, int x, int y)
        {
            if (x < 0 || y < 0 || x >= map.gridDimension || y >= map.gridDimension)
            {
                return null;
            }

            MapTile[,] grid = map.grid;
            if (grid == null || x >= grid.GetLength(0) || y >= grid.GetLength(1))
            {
                return null;
            }

            return grid[x, y];
        }

        public static void AddBuffsFromTile(MapTile target, MapTile source)
        {
            if (source == null)
            {
                return;
            }

            List<Buff> attackerBuffs = source.sourceAttackerBuffs;
            if (attackerBuffs != null)
            {
                for (int i = 0; i < attackerBuffs.Count; i++)
                {
                    target.AddAttackerBuff(attackerBuffs[i]);
                }
            }

            List<Buff> defenderBuffs = source.sourceDefenderBuffs;
            if (defenderBuffs != null)
            {
                for (int i = 0; i < defenderBuffs.Count; i++)
                {
                    target.AddDefenderBuff(defenderBuffs[i]);
                }
            }
        }

        public static void AddBuffsFromSummary(MapTile target, Summary source)
        {
            if (source == null)
            {
                return;
            }

            List<Buff> buffs = source.buffs;
            if (buffs != null)
            {
                for (int i = 0; i < buffs.Count; i++)
                {
                    target.AddDefenderBuff(buffs[i]);
                }
            }
        }

        public static List<Buff> AddAttackerBuff(MapTile tile, Buff buff)
        {
            List<Buff> buffs = tile.attackerBuffs;
            if (buffs == null)
            {
                buffs = new List<Buff>();
            }

            buffs.Add(buff);
            return buffs;
        }

        public static List<Buff> AddDefenderBuff(MapTile tile, Buff buff)
        {
            List<Buff> buffs = tile.defenderBuffs;
            if (buffs == null)
            {
                buffs = new List<Buff>();
            }

            buffs.Add(buff);
            return buffs;
        }
    }
}
