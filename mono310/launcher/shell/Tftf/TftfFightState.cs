// Newly authored. Fight (and, in VR, home base) helpers of the shell build (patches P49, P50 and, in the VR build, P53 in scripts/compat310.py).
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

    // P53 (VR): the home base's camera and the point it looks at (BaseCameraController reports both every frame it is the
    // active camera), for the stereo base of TftfVr.
    public static Camera BaseCamera;
    public static Vector3 BaseFocus;
    static int _baseFrame = -10;

    public static bool AtBase { get { return BaseCamera != null && BaseCamera.enabled && Time.frameCount - _baseFrame <= 2; } }

    public static void ReportBase(Camera camera, Vector3 focus)
    {
        BaseCamera = camera;
        BaseFocus = focus;
        _baseFrame = Time.frameCount;
    }

    // VR: the local fighter and its opponent (TftfVrCombat reports them every frame of a fight's main phase, not during
    // cinematics), for the first-person view of TftfVr: its root, eye height above the root and the opponent's centre.
    public static Transform Self;
    public static float SelfEyeHeight;
    public static Vector3 OpponentCenter;
    static int _fightersFrame = -10;

    public static bool FirstPersonReady { get { return Self != null && InFight && Time.frameCount - _fightersFrame <= 2; } }

    public static void ReportFighters(Transform self, float eyeHeight, Vector3 opponentCenter)
    {
        Self = self;
        SelfEyeHeight = eyeHeight;
        OpponentCenter = opponentCenter;
        _fightersFrame = Time.frameCount;
    }

    public static void ReportButton(string name, Vector3 screenPoint)
    {
        if (name == "AttackButton") AttackCenter = screenPoint;
        else if (name == "BlockButton") BlockCenter = screenPoint;
    }
}
