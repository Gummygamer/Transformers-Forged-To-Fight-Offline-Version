// Newly authored. Fight helpers of the shell build (patches P49 and P50 in scripts/compat310.py).
using System.IO;
using UnityEngine;

public static class TftfFightState
{
    // P49: whether the last quest fight was won (the quest board plays a tile's post-fight dialogue on every return to the board and must
    // not do so after a loss).
    public static bool LastWon = true;

    // P50 (dev): while tftf/fullmana.txt exists the player's special meter stays full, to reach the 3-bar cinematic special quickly.
    static float _next;
    static bool _fullMana;

    public static bool FullMana
    {
        get
        {
            if (Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + 1f;
                try { _fullMana = File.Exists(Path.Combine(Application.persistentDataPath, "tftf/fullmana.txt")); } catch (System.Exception) { _fullMana = false; }
            }
            return _fullMana;
        }
    }
}
