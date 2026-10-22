// Newly authored. Fight helpers of the shell build (patches P49, P50 and, in the VR build, P53 in scripts/compat310.py).
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

    // P53 (VR): the fight camera and the point it frames (CameraController reports both every frame of a fight), and the
    // screen-pixel centres of the HUD's attack and block zones (ActionButton), for the stereo arena and the controller
    // buttons of TftfVr / TftfInput.
    public static Camera FightCamera;
    public static Vector3 FightFocus;
    static int _cameraFrame = -10;
    public static Vector2 AttackCenter = new Vector2(-1, -1), BlockCenter = new Vector2(-1, -1);

    public static bool InFight { get { return FightCamera != null && FightCamera.enabled && Time.frameCount - _cameraFrame <= 2; } }

    public static void ReportCamera(Camera camera, Vector3 focus)
    {
        FightCamera = camera;
        FightFocus = focus;
        _cameraFrame = Time.frameCount;
    }

    public static void ReportButton(string name, Vector3 screenPoint)
    {
        if (name == "AttackButton") AttackCenter = screenPoint;
        else if (name == "BlockButton") BlockCenter = screenPoint;
    }
}
