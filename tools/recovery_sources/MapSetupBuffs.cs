using System.Collections.Generic;
using EB.Missions;
using UnityEngine;

namespace RecoverySources
{
    // Authored from the 9.2 ARM64 trace for EB.Missions.Map.SetupBuffs.
    // The repair tool writes the returned flag bits through the destination Map setters.
    public static class MapSetupBuffs
    {
        public static int Replace(Map map, Summary summary)
        {
            int flags = (map.hasBuffs ? 1 : 0) | (map.hasLinkBuffs ? 2 : 0);
            if (summary == null)
            {
                return flags;
            }

            List<Buff> summaryBuffs = summary.buffs;
            int summaryBuffCount = summaryBuffs == null ? 0 : summaryBuffs.Count;
            List<MapTile> tiles = map.gridTiles;
            if (tiles == null)
            {
                return flags;
            }

            for (int tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
            {
                MapTile source = tiles[tileIndex];
                if (source == null)
                {
                    continue;
                }

                if (source.buffsAreGlobal)
                {
                    flags |= 1;
                    for (int targetIndex = 0; targetIndex < tiles.Count; targetIndex++)
                    {
                        MapTile target = tiles[targetIndex];
                        if (target != null)
                        {
                            target.AddBuffsFromTile(source);
                        }
                    }
                }
                else
                {
                    List<Vector2> buffTargets = source.buffTargets;
                    if (buffTargets != null)
                    {
                        int targetCount = buffTargets.Count;
                        for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
                        {
                            Vector2 position = buffTargets[targetIndex];
                            MapTile target = map.GetTile((int)position.x, (int)position.y);
                            if (target == null)
                            {
                                continue;
                            }

                            flags |= 1;
                            bool containsSourcePosition = false;
                            if (targetCount == 1)
                            {
                                int sourceX = (int)source.position.x;
                                int sourceY = (int)source.position.y;
                                for (int i = 0; i < buffTargets.Count; i++)
                                {
                                    Vector2 candidate = buffTargets[i];
                                    if (Mathf.FloorToInt(candidate.x) == sourceX &&
                                        Mathf.FloorToInt(candidate.y) == sourceY)
                                    {
                                        containsSourcePosition = true;
                                        break;
                                    }
                                }
                            }

                            if (targetCount != 1 || !containsSourcePosition)
                            {
                                flags |= 2;
                            }
                            target.AddBuffsFromTile(source);
                        }
                    }
                }

                if (summaryBuffCount >= 1)
                {
                    flags |= 1;
                    source.AddBuffsFromSummary(summary);
                }
            }

            return flags;
        }
    }
}
