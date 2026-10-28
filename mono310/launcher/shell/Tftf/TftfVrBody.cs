// Newly authored. VR build only (TFTF_VR). The local fighter's body in the first-person view of TftfVr: the player sees it
// and moves its arms. Found once per fighter: the head and, per side, the upper arm, forearm and hand bones (the
// Animator's humanoid map if the rig has one, else by name: the shallowest "hand"/"wrist" bone on each side of the root,
// its parent and grandparent, twist bones skipped). Every frame, after the game's animation and before rendering, each arm
// is bent with a two-bone IK so that its hand reaches the player's hand placed in the arena, the elbow down and out; the
// head is shrunk to nothing so the eye inside it sees out (only the arena eye draws the fighter in first-person).
// With a controller, the wrist then takes the controller's turn (Aim): the hand's knuckle axis (wrist to middle finger, or
// the forearm's line) along the controller's pointing axis and its thumb (the thumb bone, or the rig's up) along the
// controller's top, measured once per hand in the pose found at Bind.
#if TFTF_VR
using UnityEngine;

public sealed class TftfVrBody
{
    Transform _root, _head;
    readonly Transform[] _upper = new Transform[2], _fore = new Transform[2], _hand = new Transform[2];
    readonly Quaternion[] _gripInv = new Quaternion[2];   // hand bone's rotation in its grip frame (z knuckles, y thumb), inverted
    readonly float[] _fist = new float[2];                // wrist to the palm, where a controller is held
    Vector3 _headScale;
    bool _shrunk;

    public Transform Root { get { return _root; } }
    public Transform Head { get { return _head; } }
    public bool HasArm(int side) { return _hand[side] != null; }
    public Transform Hand(int side) { return _hand[side]; }

    public void Bind(Transform root)
    {
        if (root == _root) return;
        Release();
        _root = root;
        if (root == null) return;
        var an = root.GetComponentInChildren<Animator>();
        if (an != null && an.isHuman)
        {
            _head = an.GetBoneTransform(HumanBodyBones.Head);
            Arm(0, an.GetBoneTransform(HumanBodyBones.LeftHand), an.GetBoneTransform(HumanBodyBones.LeftLowerArm), an.GetBoneTransform(HumanBodyBones.LeftUpperArm),
                an.GetBoneTransform(HumanBodyBones.LeftMiddleProximal), an.GetBoneTransform(HumanBodyBones.LeftThumbProximal));
            Arm(1, an.GetBoneTransform(HumanBodyBones.RightHand), an.GetBoneTransform(HumanBodyBones.RightLowerArm), an.GetBoneTransform(HumanBodyBones.RightUpperArm),
                an.GetBoneTransform(HumanBodyBones.RightMiddleProximal), an.GetBoneTransform(HumanBodyBones.RightThumbProximal));
        }
        else
            FindByName(root);
        Debug.Log("[TFTF] VR body of " + root.name + ": head " + Name(_head) + ", left " + Name(_upper[0]) + "/" + Name(_fore[0]) + "/" + Name(_hand[0])
            + ", right " + Name(_upper[1]) + "/" + Name(_fore[1]) + "/" + Name(_hand[1]) + (an != null && an.isHuman ? " (humanoid)" : " (by name)")
            + ", reach " + Reach(0).ToString("0.00") + "/" + Reach(1).ToString("0.00") + ", fist " + _fist[0].ToString("0.00") + "/" + _fist[1].ToString("0.00"));
    }

    static string Name(Transform t) { return t != null ? t.name : "-"; }

    void Arm(int side, Transform hand, Transform fore, Transform upper, Transform middle = null, Transform thumb = null)
    {
        if (hand == null || fore == null || upper == null) return;
        if ((hand.position - fore.position).sqrMagnitude < 1e-6f || (fore.position - upper.position).sqrMagnitude < 1e-6f) return;
        _hand[side] = hand; _fore[side] = fore; _upper[side] = upper;

        Vector3 knuckles = middle != null && (middle.position - hand.position).sqrMagnitude > 1e-6f
            ? middle.position - hand.position : (hand.position - fore.position) * 0.5f;
        _fist[side] = knuckles.magnitude * 0.6f;   // the grip sits in the palm, short of the knuckles
        Vector3 up = thumb != null ? thumb.position - hand.position : _root.up;
        up = Vector3.ProjectOnPlane(up, knuckles);
        if (up.sqrMagnitude < 1e-8f) up = Vector3.ProjectOnPlane(_root.up, knuckles);
        if (up.sqrMagnitude < 1e-8f) up = Vector3.ProjectOnPlane(_root.forward, knuckles);
        _gripInv[side] = Quaternion.Inverse(Quaternion.LookRotation(knuckles, up)) * hand.rotation;
    }

    static readonly string[] NotABone = { "finger", "thumb", "index", "middle", "ring", "pinky", "nub", "end", "attach", "prop",
        "weapon", "fx", "ik_", "_ik", "target", "twist", "roll", "light", "socket", "handle", "eye", "jaw", "dummy", "point" };

    static bool Plain(string n)
    {
        foreach (string s in NotABone) if (n.Contains(s)) return false;
        return true;
    }

    static int Depth(Transform t, Transform root)
    {
        int d = 0;
        for (; t != null && t != root; t = t.parent) d++;
        return d;
    }

    static Transform Up(Transform t, Transform root)
    {
        t = t != null ? t.parent : null;
        while (t != null && t != root && (t.name.ToLowerInvariant().Contains("twist") || t.name.ToLowerInvariant().Contains("roll"))) t = t.parent;
        return t == root ? null : t;
    }

    void FindByName(Transform root)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        int headDepth = int.MaxValue;
        var best = new Transform[2];
        var bestDepth = new int[] { int.MaxValue, int.MaxValue };
        foreach (Transform t in all)
        {
            string n = t.name.ToLowerInvariant();
            if (!Plain(n)) continue;
            int d = Depth(t, root);
            if (n.Contains("head") && d < headDepth) { _head = t; headDepth = d; }
            if (!n.Contains("hand") && !n.Contains("wrist")) continue;
            int side = Vector3.Dot(t.position - root.position, root.right) < 0f ? 0 : 1;
            if (d < bestDepth[side]) { best[side] = t; bestDepth[side] = d; }
        }
        for (int s = 0; s < 2; s++)
        {
            Transform fore = Up(best[s], root);
            Arm(s, best[s], fore, Up(fore, root));
        }
    }

    public void Release()
    {
        Shrink(false);
        _root = _head = null;
        for (int s = 0; s < 2; s++) _upper[s] = _fore[s] = _hand[s] = null;
    }

    // The head scaled to nothing (again every frame: an animated scale would undo it), its own scale put back when done.
    public void Shrink(bool on)
    {
        if (_head == null) { _shrunk = false; return; }
        if (on)
        {
            if (!_shrunk) { _headScale = _head.localScale; _shrunk = true; }
            _head.localScale = Vector3.one * 1e-3f;
        }
        else if (_shrunk)
        {
            _head.localScale = _headScale;
            _shrunk = false;
        }
    }

    // Two-bone IK: the hand of `side` toward `target` (weight 0..1), the elbow turned toward `pole`.
    public void Solve(int side, Vector3 target, Vector3 pole, float weight)
    {
        Transform a = _upper[side], b = _fore[side], c = _hand[side];
        if (c == null || weight <= 0f) return;
        Vector3 pa = a.position, pb = b.position, pc = c.position;
        float la = (pb - pa).magnitude, lb = (pc - pb).magnitude;
        Vector3 goal = Vector3.Lerp(pc, target, weight);
        float d = Mathf.Clamp((goal - pa).magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);

        // The elbow bends to the angle that puts the hand at the goal's distance from the shoulder.
        Vector3 ba = pa - pb, bc = pc - pb;
        Vector3 axis = Vector3.Cross(ba, bc);
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(ba, pole - pb);   // a straight arm bends toward the pole
        if (axis.sqrMagnitude > 1e-8f)
        {
            float now = Vector3.Angle(ba, bc);
            float want = Mathf.Acos(Mathf.Clamp((la * la + lb * lb - d * d) / (2f * la * lb), -1f, 1f)) * Mathf.Rad2Deg;
            b.rotation = Quaternion.AngleAxis(want - now, axis.normalized) * b.rotation;
        }

        // The shoulder aims the hand at the goal, then turns about that line to put the elbow on the pole's side.
        a.rotation = Quaternion.FromToRotation(c.position - pa, goal - pa) * a.rotation;
        Vector3 line = (goal - pa).normalized;
        Vector3 e = Vector3.ProjectOnPlane(b.position - pa, line), p = Vector3.ProjectOnPlane(pole - pa, line);
        if (e.sqrMagnitude > 1e-6f && p.sqrMagnitude > 1e-6f)
            a.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(e, p, line) * weight, line) * a.rotation;
    }

    // The hand of `side` turned so that its grip frame is `grip` (weight 0..1).
    public void Aim(int side, Quaternion grip, float weight)
    {
        Transform c = _hand[side];
        if (c == null || weight <= 0f) return;
        c.rotation = Quaternion.Slerp(c.rotation, grip * _gripInv[side], Mathf.Clamp01(weight));
    }

    // Wrist to the palm (a controller is held there).
    public float FistOffset(int side) { return _fist[side]; }

    // Shoulder to hand at full stretch (for the pole's distance).
    public float Reach(int side)
    {
        if (_hand[side] == null) return 0f;
        return (_fore[side].position - _upper[side].position).magnitude + (_hand[side].position - _fore[side].position).magnitude;
    }

    public Vector3 Shoulder(int side) { return _upper[side] != null ? _upper[side].position : Vector3.zero; }
}
#endif
