// Newly authored. Touch input for the story map and base cameras and the fight controls (compat310.py P52 routes their
// Input.touchCount and Input.GetTouch through here). Phones pass straight through; on a PC the left mouse button is one
// finger: a click taps a map node or attacks, a drag pans the board or swipes, holding blocks. A click shorter than a
// frame still reports Began, then Ended next frame. In the VR build the controller laser on the virtual screen is the
// finger, and the controller buttons press the fight controls (TftfVr); compat310.py P53 routes every touch read of the game here.
using UnityEngine;

public static class TftfInput
{
#if UNITY_STANDALONE
    static int _frame = -1;
    static bool _active, _endNext;
    static Touch _touch;
    static Vector2 _last;

    static void Refresh()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _active = false;
        if (Input.touchCount > 0) return;                                   // a touch screen wins over the mouse
        Vector2 p = Input.mousePosition;
        TouchPhase phase;
        if (_endNext) { phase = TouchPhase.Ended; _endNext = false; }
        else if (Input.GetMouseButtonDown(0)) { phase = TouchPhase.Began; _endNext = !Input.GetMouseButton(0) || Input.GetMouseButtonUp(0); }
        else if (Input.GetMouseButtonUp(0)) phase = TouchPhase.Ended;
        else if (Input.GetMouseButton(0)) phase = p == _last ? TouchPhase.Stationary : TouchPhase.Moved;
        else return;
        _active = true;
        _touch = new Touch { fingerId = 0, position = p, deltaPosition = phase == TouchPhase.Began ? Vector2.zero : p - _last, tapCount = 1, phase = phase };
        _last = p;
    }

    public static int touchCount { get { Refresh(); return Input.touchCount > 0 ? Input.touchCount : _active ? 1 : 0; } }

    public static Touch GetTouch(int index) { Refresh(); return Input.touchCount > 0 ? Input.GetTouch(index) : _touch; }

    public static Touch[] touches { get { return touchCount == 0 ? new Touch[0] : Input.touchCount > 0 ? Input.touches : new[] { _touch }; } }
#elif TFTF_VR
    // Finger 0 is the controller laser. In a fight, finger 1 works the attack zone (A taps it, the right stick flicked
    // left or right swipes across it) and finger 2 holds the block zone while a grip is held (TftfVr reads the buttons).
    const int Fingers = 3;
    static int _frame = -1, _count, _gesture;
    static readonly Touch[] _touches = new Touch[Fingers];
    static readonly bool[] _was = new bool[Fingers];
    static readonly Vector2[] _last = new Vector2[Fingers];
    static Vector2 _gesturePos, _gestureStep;

    static void Finger(int id, bool down, Vector2 p)
    {
        TouchPhase phase;
        if (down && !_was[id]) phase = TouchPhase.Began;
        else if (!down && _was[id]) phase = TouchPhase.Ended;
        else if (down) phase = p == _last[id] ? TouchPhase.Stationary : TouchPhase.Moved;
        else return;
        _touches[_count++] = new Touch { fingerId = id, position = p, deltaPosition = phase == TouchPhase.Began ? Vector2.zero : p - _last[id], tapCount = 1, phase = phase };
        _was[id] = down;
        _last[id] = p;
    }

    // Where the HUD reported the zone, or a guess (right or left of the screen) before it has.
    static Vector2 Zone(Vector2 reported, float fx)
    {
        return reported.x >= 0 ? reported : new Vector2(fx * TftfVr.Width, 0.35f * TftfVr.Height);
    }

    static void Refresh()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _count = 0;
        Finger(0, TftfVr.PointerDown, TftfVr.PointerPixel);
        bool fight = TftfFightState.InFight;
        int g = TftfVr.FightGesture();
        if (_gesture == 0 && fight && g != 0)
        {
            _gesturePos = Zone(TftfFightState.AttackCenter, 0.75f);
            _gestureStep = new Vector2(g == 2 ? 140f : g == 3 ? -140f : 0f, 0f);
            _gesture = g == 1 ? 2 : 5;               // a tap: down one frame; a swipe: down, three moves, up
        }
        if (_gesture > 0)
        {
            _gesture--;
            bool down = _gesture > 0;
            if (down && _was[1]) _gesturePos += _gestureStep;
            Finger(1, down, _gesturePos);
        }
        Finger(2, fight && TftfVr.BlockHeld, Zone(TftfFightState.BlockCenter, 0.25f));
    }

    public static int touchCount { get { Refresh(); return _count; } }

    public static Touch GetTouch(int index) { Refresh(); return _touches[index]; }

    public static Touch[] touches
    {
        get
        {
            Refresh();
            var a = new Touch[_count];
            System.Array.Copy(_touches, a, _count);
            return a;
        }
    }
#else
    public static int touchCount { get { return Input.touchCount; } }

    public static Touch GetTouch(int index) { return Input.GetTouch(index); }

    public static Touch[] touches { get { return Input.touches; } }
#endif
}
