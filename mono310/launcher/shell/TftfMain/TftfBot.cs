// Newly authored. Test automation for device checks (mono310/launcher/tools/tftfbot/):
// - every window shown is logged as "[TFTFBOT] screen=<name>", so a host script can wait for screens instead of sleeping;
// - with tftf/bot.on present, the local fighter reports a compact fight state ("[TFTFBOT] state ...") and plays the actions
//   a host script writes to tftf/botcmd.txt ("<seq> <action>"). Off unless that file exists. Compiled into Assembly-CSharp.
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TftfBot : MonoBehaviour
{
    static TftfBot _inst;
    string _dir, _lastState;
    bool _on, _subscribed, _wasFighting;
    float _nextFlag, _nextCmd, _nextState;
    int _stateSeq, _lastCmd = -1;
    float _shootUntil, _nextShootInput;      // "shoot": back out of melee range, then fire whenever the game allows a ranged attack
    readonly List<KeyValuePair<float, System.Action<PlayerInput>>> _pending = new List<KeyValuePair<float, System.Action<PlayerInput>>>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (_inst != null) return;
        GameObject go = new GameObject("TftfBot");
        Object.DontDestroyOnLoad(go);
        _inst = go.AddComponent<TftfBot>();
    }

    void Start() { _dir = Path.Combine(Application.persistentDataPath, "tftf"); }

    void OnWindowIn(WindowManager.WindowInfo info) { Debug.Log("[TFTFBOT] screen=" + info.name); }

    void Update()
    {
        if (!_subscribed && WindowManager.Instance != null) { WindowManager.Instance.WindowTransitionInEndNotification += OnWindowIn; _subscribed = true; }
        float now = Time.realtimeSinceStartup;
        if (now >= _nextFlag) { _nextFlag = now + 1f; try { _on = File.Exists(Path.Combine(_dir, "bot.on")); } catch (System.Exception) { _on = false; } }
        if (!_on) return;

        BattleArbiter arbiter = BattleArbiter.Instance;
        bool fighting = arbiter != null && arbiter.IsCurrentState("Main");
        if (fighting != _wasFighting) { _wasFighting = fighting; _pending.Clear(); Debug.Log("[TFTFBOT] fight=" + (fighting ? "start" : "end")); }
        if (!fighting) return;
        PlayerController me = arbiter.GetFighter(0);
        PlayerInput input = me != null ? me.GetComponent<PlayerInput>() : null;
        if (input == null || me.Opponent == null) return;

        while (_pending.Count > 0 && now >= _pending[0].Key) { System.Action<PlayerInput> a = _pending[0].Value; _pending.RemoveAt(0); a(input); }      // At() adds in time order

        if (now >= _nextCmd) { _nextCmd = now + 0.03f; ReadCommand(now); }

        if (now < _shootUntil && now >= _nextShootInput && _pending.Count == 0)
        {
            if (me.CanShoot) { _nextShootInput = now + 0.2f; Debug.Log("[TFTFBOT] shoot input d=" + me.GetDistanceToOpponent().ToString("0.0")); At(now, p => p.OnReceiveAttackInput()); At(now + 0.05f, p => p.OnReleaseAttackInput()); }
            else if (me.IsInMeleeRange() && me.CanDodge) { _nextShootInput = now + 0.2f; At(now, p => p.OnDetectSwipe(SwipeDirection.Left)); }
        }

        string st = Describe(me);
        if (st != _lastState || now >= _nextState)
        {
            _lastState = st; _nextState = now + 0.5f;
            Debug.Log("[TFTFBOT] state seq=" + (++_stateSeq) + " busy=" + (_pending.Count > 0 ? 1 : 0) + " " + st);
        }
    }

    static string Describe(PlayerController me)
    {
        PlayerController op = me.Opponent;
        float d = me.GetDistanceToOpponent();
        string range = me.IsInMeleeRange() ? "melee" : d < 6f ? "near" : "far";
        return "hp=" + me.Attributes.NormalizedHealth.ToString("0.00") + " ophp=" + op.Attributes.NormalizedHealth.ToString("0.00")
            + " range=" + range + " me=" + Pose(me, false) + " op=" + Pose(op, true) + " special=" + me.Attributes.HighestAvailableSpecial();
    }

    static string Pose(PlayerController p, bool detail)
    {
        if (p.IsDead || p.IsHealthDepleted) return "down";
        if (p.Stunned) return "stunned";
        if (p.HitStunned) return "hitstun";
        if (p.IsSpecialAttacking) return "special";
        if (detail && p.IsInAttackStartup) return p.IsHeavyAttacking ? "heavy_startup" : "attack_startup";
        if (detail && p.IsInAttackRecovery) return "attack_recovery";
        if (p.IsHeavyAttacking) return "heavy";
        if (p.IsShooting) return "shooting";
        if (p.IsDashing) return "dashing";
        if (p.IsAttacking) return "attacking";
        if (p.IsBlocking) return "blocking";
        if (p.IsDodging) return "dodging";
        return "idle";
    }

    void ReadCommand(float now)
    {
        string text;
        try { string f = Path.Combine(_dir, "botcmd.txt"); if (!File.Exists(f)) return; text = File.ReadAllText(f).Trim(); } catch (System.Exception) { return; }
        string[] kv = text.Split(' ');
        int seq;
        if (kv.Length != 2 || !int.TryParse(kv[0], out seq) || seq == _lastCmd) return;
        _lastCmd = seq;
        _pending.Clear();
        _shootUntil = 0f;
        switch (kv[1])
        {
            case "light": At(now, p => p.OnReceiveAttackInput()); At(now + 0.05f, p => p.OnReleaseAttackInput()); break;
            case "heavy": At(now, p => p.OnReceiveAttackInput()); At(now + 0.7f, p => p.OnReleaseAttackInput()); break;
            case "block": At(now, p => p.OnReceiveBlockInput()); At(now + 0.6f, p => p.OnReleaseBlockInput()); break;
            case "dodge": At(now, p => p.OnDetectSwipe(SwipeDirection.Left)); break;
            case "dash": At(now, p => p.OnDetectSwipe(SwipeDirection.Right)); break;
            case "combo":
                At(now, p => p.OnDetectSwipe(SwipeDirection.Right));
                for (int i = 1; i <= 4; i++) { float t = now + 0.3f * i; At(t, p => p.OnReceiveAttackInput()); At(t + 0.05f, p => p.OnReleaseAttackInput()); }
                break;
            case "shoot": _shootUntil = now + 30f; break;
            case "special": At(now, p => p.OnReceiveSpecialInput()); break;
            default: return;      // "wait"
        }
        Debug.Log("[TFTFBOT] act seq=" + seq + " " + kv[1]);
    }

    // With tftf/bot.on: what a spawned effect really is. Logged at spawn and again while it should be on screen.
    public static void DiagSpawn(string kind, string prefab, GameObject inst)
    {
        if (_inst == null || !_inst._on) return;
        Debug.Log("[TFTFFX] " + kind + " " + prefab + " instance=" + (inst != null ? inst.name : "NULL"));
        if (inst != null) _inst.StartCoroutine(_inst.DiagLater(kind, prefab, inst));
    }

    System.Collections.IEnumerator DiagLater(string kind, string prefab, GameObject inst)
    {
        DumpFx(kind + " t0 " + prefab, inst);
        yield return new WaitForSeconds(0.12f);
        if (inst != null) DumpFx(kind + " t1 " + prefab, inst);
    }

    public static void DiagFire(string prefab, string fireMove, EB.MoveEditor.MoveSet set)
    {
        if (_inst == null || !_inst._on) return;
        Debug.Log("[TFTFFX] fire " + prefab + " move='" + fireMove + "' set=" + (set != null) + " found=" + (set != null && !string.IsNullOrEmpty(fireMove) && set.GetMove(fireMove) != null));
    }

    static void DumpFx(string tag, GameObject go)
    {
        Camera cam = Camera.main;
        Debug.Log("[TFTFFX] " + tag + " active=" + go.activeInHierarchy + " pos=" + go.transform.position + " scale=" + go.transform.lossyScale + " camMask=" + (cam != null ? cam.cullingMask.ToString("X") : "none"));
        foreach (Component c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) { Debug.Log("[TFTFFX]   missing script"); continue; }
            if (c is Transform) continue;
            string s = "[TFTFFX]   " + c.gameObject.name + " <" + c.GetType().Name + "> layer=" + c.gameObject.layer + " on=" + c.gameObject.activeInHierarchy;
            Renderer r = c as Renderer;
            if (r != null)
            {
                s += " enabled=" + r.enabled + " visible=" + r.isVisible;
                foreach (Material m in r.sharedMaterials)
                    s += m == null ? " mat=NULL" : " mat=" + m.name + " shader=" + m.shader.name + " ok=" + m.shader.isSupported + " q=" + m.renderQueue + " tex=" + (m.HasProperty("_MainTex") && m.mainTexture != null ? m.mainTexture.name : "-") + " col=" + (m.HasProperty("_Color") ? m.color.ToString() : "-");
                MeshRenderer mr = r as MeshRenderer;
                if (mr != null) { MeshFilter mf = r.GetComponent<MeshFilter>(); s += " mesh=" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.name + "/" + mf.sharedMesh.vertexCount : "NULL"); }
            }
            ParticleSystem ps = c as ParticleSystem;
            if (ps != null) s += " playing=" + ps.isPlaying + " count=" + ps.particleCount + " emit=" + ps.emission.enabled + " max=" + ps.main.maxParticles;
            Debug.Log(s);
        }
    }

    void At(float t, System.Action<PlayerInput> a) { _pending.Add(new KeyValuePair<float, System.Action<PlayerInput>>(t, a)); }
}
