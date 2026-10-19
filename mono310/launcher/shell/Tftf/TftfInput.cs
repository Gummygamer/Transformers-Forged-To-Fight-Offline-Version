// Newly authored. Touch input for the story map and base cameras (compat310.py P52 routes their Input.touchCount and
// Input.GetTouch through here). Phones pass straight through; on a PC the left mouse button is one finger, so a click
// taps a map node and a drag pans the board. A click shorter than a frame still reports Began, then Ended next frame.
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
#else
    public static int touchCount { get { return Input.touchCount; } }

    public static Touch GetTouch(int index) { return Input.GetTouch(index); }
#endif
}
