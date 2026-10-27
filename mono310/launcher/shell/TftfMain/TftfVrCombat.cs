// Newly authored. VR build only (TFTF_VR; mono310/launcher/QUEST_VR.md). Motion controls for fights: the head and the two
// hands (controllers or, with the controllers put down, tracked hands: TftfVrHands) are read from TftfVr every frame of a
// fight's main phase and their gestures are played on the local fighter's PlayerInput, as TftfBot plays its actions (so
// online Arena fights capture them too):
// - a fast punch forward with one hand: light attack; with both hands at once, or a quick step in: dash (medium attack);
// - both hands raised above the head, then brought down: heavy attack (held past the game's 0.2 s, as a held attack zone);
// - both forearms up in front of the face: block, for as long as the guard is held;
// - a quick lean back: dodge back; a quick lean to a side: sidestep;
// - both fists held together in front of the chest, or B: the highest special the meter allows.
// It also reports the fighters for the first-person view (TftfFightState.ReportFighters), colours the hand markers and
// buzzes the controllers on hits. Y on the left controller turns motion controls off and on.
//
// Thresholds can be tuned without a rebuild: tftf/vrcombat.txt, lines "<name> <value>" (names as in Tune below), read at
// the start of every fight. Compiled into Assembly-CSharp (it uses the game's fight types).
#if TFTF_VR
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TftfVrCombat : MonoBehaviour
{
    static TftfVrCombat _inst;

    // Metres and metres per second, in the frame of the head's yaw (x right, y up, z forward).
    readonly Dictionary<string, float> Tune = new Dictionary<string, float>
    {
        { "eye", 0.92f },          // the fighter's eye height, as a fraction of its collider height
        { "jab", 1.8f },           // hand speed forward for a punch
        { "jabreach", 0.18f },     // ... reached at least this far in front of the head
        { "both", 1.2f },          // the other hand's speed forward that makes a punch a two-hand dash
        { "fist", 0.5f },          // a tracked hand punches only when this closed (0 open, 1 fist)
        { "lean", 0.9f },          // head speed for a lean or a step
        { "leandist", 0.12f },     // ... and how far from where the head rests
        { "guardy", -0.15f },      // guard: both hands above this height relative to the eyes
        { "guardz", 0.4f },        // ... within this far in front
        { "guardgap", 0.32f },     // ... and this close to each other
        { "raise", 0.1f },         // heavy: both hands this far above the eyes
    };

    bool _on = true, _yWas, _bWas;
    PlayerController _me;
    PlayerInput _input;
    float _myHp, _opHp;

    // Tracking, body frame.
    bool _valid, _neutralSet;
    Vector3 _head, _headVel, _headNeutral;
    Quaternion _yawInv;
    readonly bool[] _handValid = new bool[2], _handTracked = new bool[2];
    readonly Vector3[] _handPrev = new Vector3[2], _handVel = new Vector3[2], _rel = new Vector3[2];
    readonly float[] _closed = new float[2];
    Vector3 _headPrev;

    // Gestures.
    float _jabReady, _leanReady, _guardUntil, _specialReady, _together, _releaseAttackAt = -1f, _raisedAt;
    bool _blocking, _raised;
    int _lastPunch = 1;
    readonly float[] _flash = new float[2];
    readonly Color[] _flashColor = new Color[2];

    static readonly Color Idle = new Color(0.3f, 0.7f, 1f, 0.8f), Guard = new Color(0.3f, 1f, 0.45f, 0.9f),
        Raised = new Color(1f, 0.85f, 0.2f, 0.9f), Hit = new Color(1f, 0.5f, 0.1f, 1f), Hurt = new Color(1f, 0.15f, 0.15f, 1f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (_inst != null) return;
        GameObject go = new GameObject("TftfVrCombat");
        Object.DontDestroyOnLoad(go);
        _inst = go.AddComponent<TftfVrCombat>();
    }

    void Update()
    {
        bool y = TftfVr.HandButton(0, 1);
        if (y && !_yWas) { _on = !_on; Debug.Log("[TFTF] VR motion controls " + (_on ? "on" : "off")); }
        _yWas = y;

        BattleArbiter arbiter = BattleArbiter.Instance;
        PlayerController me = arbiter != null && arbiter.IsCurrentState("Main") ? arbiter.GetFighter(0) : null;
        PlayerInput input = me != null ? me.GetComponent<PlayerInput>() : null;
        if (input == null || me.Opponent == null || me.Attributes == null || me.Opponent.Attributes == null) { Stop(); return; }
        if (me != _me) Begin(me, input);

        bool cinematic = me.IsInCinematic || me.Opponent.IsInCinematic;
        if (!cinematic) TftfFightState.ReportFighters(me.transform, EyeHeight(me), me.Opponent.Center);
        float now = Time.realtimeSinceStartup;
        Track();
        if (_releaseAttackAt >= 0f && now >= _releaseAttackAt) { _releaseAttackAt = -1f; _input.OnReleaseAttackInput(); }
        TftfVr.ShowHands = _on && _valid;
        if (_on && !cinematic && _valid) Recognise(now);
        else EndHolds();
        Feedback(now);
    }

    void Begin(PlayerController me, PlayerInput input)
    {
        Stop();
        _me = me;
        _input = input;
        _myHp = me.Attributes.NormalizedHealth;
        _opHp = me.Opponent.Attributes.NormalizedHealth;
        _neutralSet = false;
        ReadTune();
        Debug.Log("[TFTF] VR motion controls " + (_on ? "ready" : "off (Y)") + ", eye height " + EyeHeight(me).ToString("0.00"));
    }

    void Stop()
    {
        if (_me == null) return;
        EndHolds();
        if (_releaseAttackAt >= 0f && _input != null) _input.OnReleaseAttackInput();
        _releaseAttackAt = -1f;
        _me = null;
        _input = null;
        TftfVr.ShowHands = false;
    }

    float EyeHeight(PlayerController me)
    {
        float h = me.Height * me.transform.lossyScale.y;
        if (h <= 0.1f) h = me.CenterOffset.y * 2f;
        return Mathf.Max(h * Tune["eye"], 0.5f);
    }

    void ReadTune()
    {
        try
        {
            string f = Path.Combine(Application.persistentDataPath, "tftf/vrcombat.txt");
            if (!File.Exists(f)) return;
            foreach (string line in File.ReadAllLines(f))
            {
                string[] kv = line.Trim().Split(' ');
                float v;
                if (kv.Length == 2 && Tune.ContainsKey(kv[0]) && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                    Tune[kv[0]] = v;
            }
            Debug.Log("[TFTF] VR motion controls tuned from tftf/vrcombat.txt");
        }
        catch (System.Exception) { }
    }

    // Head and hands into the frame of the head's yaw, with smoothed velocities.
    void Track()
    {
        Vector3 hp; Quaternion hr;
        _valid = TftfVr.HeadPose(out hp, out hr);
        if (!_valid) return;
        float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
        float k = 1f - Mathf.Exp(-dt / 0.03f);
        _yawInv = Quaternion.Inverse(Quaternion.Euler(0f, hr.eulerAngles.y, 0f));
        if (!_neutralSet) { _headPrev = hp; _headNeutral = hp; _headVel = Vector3.zero; _neutralSet = true; }
        _headVel = Vector3.Lerp(_headVel, _yawInv * (hp - _headPrev) / dt, k);
        _headPrev = hp;
        _head = hp;
        _headNeutral = Vector3.Lerp(_headNeutral, hp, 1f - Mathf.Exp(-dt / 1.5f));   // where the head rests
        for (int h = 0; h < 2; h++)
        {
            Vector3 p; Quaternion r;
            bool was = _handValid[h];
            _handValid[h] = TftfVr.HandPose(h, out p, out r, out _closed[h], out _handTracked[h]);
            if (!_handValid[h]) continue;
            _handVel[h] = was ? Vector3.Lerp(_handVel[h], _yawInv * (p - _handPrev[h]) / dt, k) : Vector3.zero;
            _handPrev[h] = p;
            _rel[h] = _yawInv * (p - hp);
        }
    }

    void Recognise(float now)
    {
        bool both = _handValid[0] && _handValid[1];

        // Heavy: both hands raised above the head hold the attack; bringing them down lets go.
        bool raised = both && _rel[0].y > Tune["raise"] && _rel[1].y > Tune["raise"];
        if (raised && !_raised && !_blocking)
        {
            _raised = true;
            _raisedAt = now;
            _releaseAttackAt = -1f;
            _input.OnReceiveAttackInput();
        }
        else if (_raised && (!both || _rel[0].y < -0.05f || _rel[1].y < -0.05f || now - _raisedAt > 2f))
        {
            _raised = false;
            _input.OnReleaseAttackInput();
            Debug.Log("[TFTF] VR gesture heavy (" + (now - _raisedAt).ToString("0.00") + " s)");
        }
        if (_raised) return;

        // Block: the guard, held; it drops at once when a hand moves fast (a punch from the guard).
        float fast = Mathf.Max(_handValid[0] ? _handVel[0].magnitude : 0f, _handValid[1] ? _handVel[1].magnitude : 0f);
        bool guard = both && fast < 1.2f && InGuard(0) && InGuard(1) && (_rel[0] - _rel[1]).magnitude < Tune["guardgap"];
        if (guard) _guardUntil = now + 0.12f;
        bool block = fast < 1.2f && now < _guardUntil;
        if (block != _blocking)
        {
            _blocking = block;
            if (block) _input.OnReceiveBlockInput(); else _input.OnReleaseBlockInput();
            Debug.Log("[TFTF] VR gesture block " + (block ? "on" : "off"));
        }

        // Punches: one hand light, both at once a dash.
        if (now >= _jabReady)
            for (int h = 0; h < 2; h++)
            {
                if (!Punching(h, Tune["jab"])) continue;
                int o = 1 - h;
                bool two = _handValid[o] && _handVel[o].z > Tune["both"];
                _lastPunch = h;
                _jabReady = now + 0.28f;
                if (two) Act(now, "dash", p => p.OnDetectSwipe(SwipeDirection.Right));
                else Light(now);
                break;
            }

        // The head: a quick lean back dodges, a step in dashes, a lean to a side sidesteps.
        Vector3 off = _yawInv * (_head - _headNeutral);
        float lean = Tune["lean"], dist = Tune["leandist"];
        if (now >= _leanReady)
        {
            if (_headVel.z < -lean && off.z < -dist) Act(now, "dodge", p => p.OnDetectSwipe(SwipeDirection.Left));
            else if (_headVel.z > lean && off.z > dist && fast < Tune["jab"]) Act(now, "step in", p => p.OnDetectSwipe(SwipeDirection.Right));
            else if (_headVel.x < -lean && off.x < -dist * 1.25f) Act(now, "sidestep left", p => p.OnDetectSwipe(SwipeDirection.Up));
            else if (_headVel.x > lean && off.x > dist * 1.25f) Act(now, "sidestep right", p => p.OnDetectSwipe(SwipeDirection.Down));
        }

        // Special: B, or both fists held together in front of the chest.
        bool b = TftfVr.HandButton(1, 1);
        bool together = both && (_rel[0] - _rel[1]).magnitude < 0.12f && _rel[0].z > 0.25f && _rel[1].z > 0.25f && fast < 0.8f;
        _together = together ? _together + Time.unscaledDeltaTime : 0f;
        if (((b && !_bWas) || _together > 0.35f) && now >= _specialReady && _me.Attributes.HighestAvailableSpecial() > 0)
        {
            _specialReady = now + 1.5f;
            _together = 0f;
            Act(now, "special", p => p.OnReceiveSpecialInput());
        }
        _bWas = b;
    }

    bool InGuard(int h)
    {
        Vector3 r = _rel[h];
        return r.y > Tune["guardy"] && r.y < 0.15f && r.z > 0.03f && r.z < Tune["guardz"] && Mathf.Abs(r.x) < 0.35f;
    }

    bool Punching(int h, float speed)
    {
        if (!_handValid[h] || _handVel[h].z < speed || _rel[h].z < Tune["jabreach"]) return false;
        return !_handTracked[h] || _closed[h] >= Tune["fist"];
    }

    void Light(float now)
    {
        if (_blocking) { _blocking = false; _input.OnReleaseBlockInput(); }   // the game drops a tap made while blocking
        _input.OnReceiveAttackInput();
        _releaseAttackAt = now + 0.05f;                                      // a tap: well under the 0.2 s of a heavy
        Debug.Log("[TFTF] VR gesture light (" + (_lastPunch == 0 ? "left" : "right") + ")");
    }

    void Act(float now, string name, System.Action<PlayerInput> a)
    {
        if (_blocking && name != "special") { _blocking = false; _input.OnReleaseBlockInput(); }
        a(_input);
        _leanReady = now + 0.6f;
        _jabReady = Mathf.Max(_jabReady, now + 0.28f);
        Debug.Log("[TFTF] VR gesture " + name);
    }

    void EndHolds()
    {
        if (_input == null) return;
        if (_blocking) { _blocking = false; _input.OnReleaseBlockInput(); }
        if (_raised) { _raised = false; _input.OnReleaseAttackInput(); }
        _together = 0f;
    }

    // Hits felt and seen: the punching hand buzzes and flashes when the opponent loses health, both when the player does.
    void Feedback(float now)
    {
        if (_me == null) return;
        float my = _me.Attributes.NormalizedHealth, op = _me.Opponent.Attributes.NormalizedHealth;
        if (op < _opHp - 1e-4f)
        {
            TftfVr.Buzz(_lastPunch, 0.6f, 0.08f);
            _flash[_lastPunch] = now + 0.15f;
            _flashColor[_lastPunch] = Hit;
        }
        if (my < _myHp - 1e-4f)
            for (int h = 0; h < 2; h++)
            {
                TftfVr.Buzz(h, _blocking ? 0.3f : 0.9f, 0.12f);
                _flash[h] = now + 0.2f;
                _flashColor[h] = Hurt;
            }
        _myHp = my;
        _opHp = op;
        for (int h = 0; h < 2; h++)
            TftfVr.HandColors[h] = now < _flash[h] ? _flashColor[h] : _raised ? Raised : _blocking ? Guard : Idle;
    }
}
#endif
