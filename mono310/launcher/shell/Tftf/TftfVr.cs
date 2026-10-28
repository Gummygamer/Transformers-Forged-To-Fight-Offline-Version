// Newly authored. VR build only (TFTF_VR, Meta Quest through the Oculus XR plugin). The game keeps rendering as on a
// phone, but into Screen2D, a 1920x1080 texture shown on a large screen in a dark room. The headset sees the room in
// stereo; a controller laser on the screen is the finger (TftfInput). Every game camera is pointed at Screen2D each
// frame (Retarget), and compat310.py P53 makes the game's screen size and backbuffer blits refer to it as well.
//
// In a fight the arena itself is drawn in stereo (arena mode): a second headset camera renders what the fight camera
// sees, from a seat on that camera's line of sight, close enough that the fighters fill about as much of the view as the
// screen did; the head moves freely around that seat. The game's own fight camera then draws nothing, and the screen
// shows only the HUD, transparent, in front of the arena. X on the left controller switches arena mode off and on, the
// left stick moves the seat nearer or farther.
//
// The home base is shown the same way (base mode): the base camera's view in stereo from a seat on its line of sight, the
// horizon kept level, the base at a tenth of its size (head moves and eye separation scaled by BaseScale) so that it reads
// as a model town below the screen, with depth. Pointed at the base itself (off the screen), the laser taps and drags the
// spot it touches, as a finger on that spot of the phone screen would.
//
// First-person (the default view in fights): the arena eye sits in the local fighter's head, facing the opponent, the
// world scaled so that the fighter's eye height is the player's. The fighter's body stays in view and its arms follow the
// player's tracked controllers or hands (TftfVrBody, TftfVrHands); the gestures of those hands play the fight
// (TftfVrCombat). Cinematics and the fight's intro and outro fall back to the arena seat. X cycles first-person, arena
// and flat.
#if TFTF_VR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

[DefaultExecutionOrder(-1000)]   // pointer and buttons are read before the game's scripts read touches
public sealed class TftfVr : MonoBehaviour
{
    public const int Width = 1920, Height = 1080;
    public const int Layer = 31;                     // unused by the game: the room, the screen and the laser
    const float ScreenWidth = 3.0f, Distance = 2.5f; // metres: about 60 degrees of view, like a near cinema seat
    const float TriggerOn = 0.55f, TriggerOff = 0.35f;
    const float ArenaHalfFov = 30f;                  // degrees: the fight framed as wide as the screen was
    const float BaseScale = 10f;                     // base mode: game units per real metre of head movement and eye separation
    const float PlayerEyeHeight = 1.6f;              // first-person: metres the fighter's eye height stands for
    const int ViewFirstPerson = 0, ViewArena = 1, ViewFlat = 2;

    public static RenderTexture Screen2D;
    // The finger: pixel position on Screen2D (origin bottom left, like Touch.position), whether it is down, and
    // whether it went up this frame (a click for TftfVrPanel).
    public static Vector2 PointerPixel;
    public static bool PointerDown, PointerUp, PointerOnScreen;

    static TftfVr _instance;
    readonly HashSet<Camera> _own = new HashSet<Camera>();
    // Arena mode: game cameras that cleared the screen opaque (a UI backdrop, the letterbox clear) and their own clear,
    // put back when arena mode ends.
    readonly Dictionary<Camera, CameraClearFlags> _cleared = new Dictionary<Camera, CameraClearFlags>();
    // Arena mode: game cameras that drew the arena's layers into the screen and their own culling mask. The cutscene
    // camera (StageCamera) stays enabled through the fight and would draw the arena from its last cutscene pose over the
    // stereo arena; the fight camera covers it on a phone.
    readonly Dictionary<Camera, int> _masked = new Dictionary<Camera, int>();
    Camera[] _cams = new Camera[32];
    Transform _head, _screen, _floor, _anchor, _arenaHead;
    Camera _eye, _clear, _arenaEye;
    LineRenderer _laser;
    Transform _cursor;
    XRNode _hand = XRNode.RightHand;
    bool _placed, _pressed;
    Material _uiMat, _uiTextMat, _screenOpaque, _screenHud;

    // Arena mode state.
    RenderTexture _scratch;
    Camera _source;
    int _sourceMask;
    int _view = ViewFirstPerson;
    bool _arena, _base, _snap, _fp;
    bool ArenaWanted { get { return _view != ViewFlat; } }
    float _scale = 1f;
    Vector3 _seat;            // room: where the head sits when it looks at the screen; maps the room onto the arena seat
    Quaternion _seatRot = Quaternion.identity;
    bool _pressedWorld;       // base mode: the press started on the base, not on the screen
    float _zoom = 1f;
    bool _xWas, _aWas;
    int _stickWas;

    // First-person: the local fighter's body, its arms driven by the player's hands.
    readonly TftfVrBody _body = new TftfVrBody();
    float _armWeight;
    readonly float[] _handMiss = new float[2];   // real metres between a driven hand and the player's (dev: vrshot)
    Vector3 _eyeSmooth;
    // Tuned by TftfVrCombat (tftf/vrcombat.txt): the eye in front of and above the head bone (fractions of the eye
    // height), the near clip in real metres (hides the collar and shoulders around the eye, not the hands), how much the
    // wrists follow the controllers and the grip's pitch from the controller's pointing axis (degrees).
    public static float FpEyeForward = 0.06f, FpEyeUp = 0.04f, FpNear = 0.15f, FpWrist = 1f, FpWristPitch;

    // Motion controls: a marker per hand (0 left, 1 right) in fights, coloured by TftfVrCombat.
    Transform[] _markers = new Transform[2];
    Material[] _markerMats = new Material[2];
    public static bool ShowHands;
    public static readonly Color[] HandColors = { new Color(0.3f, 0.7f, 1f, 0.8f), new Color(0.3f, 0.7f, 1f, 0.8f) };

    public static Transform ScreenTransform { get { return _instance != null ? _instance._screen : null; } }
    public static Material UiMaterial { get { return _instance._uiMat; } }
    public static Material UiTextMaterial { get { return _instance._uiTextMat; } }

    // Where the game's backbuffer composite goes: Screen2D, or a scratch target in arena mode (the arena is drawn in
    // stereo instead, and the screen keeps only the HUD).
    public static RenderTexture CompositeTarget { get { return _instance != null && _instance._arena ? _instance._scratch : Screen2D; } }

    public static void Init()
    {
        if (_instance != null) return;
        Screen2D = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "TftfVrScreen", useMipMap = false };
        Screen2D.Create();
        var go = new GameObject("TftfVr");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TftfVr>();
    }

    static Material Mat(string shader, Texture tex, Color color)
    {
        var m = new Material(Resources.Load<Shader>("TftfVr/" + shader)) { mainTexture = tex, color = color };
        return m;
    }

    static Texture2D GridTexture()
    {
        const int n = 128;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool line = x < 2 || y < 2;
                px[y * n + x] = line ? new Color32(70, 110, 160, 255) : new Color32(14, 16, 22, 255);
            }
        t.SetPixels32(px);
        t.Apply(true);
        return t;
    }

    static Texture2D DotTexture()
    {
        const int n = 32;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
                byte a = (byte)(Mathf.Clamp01((1f - d) * 3f) * 255);
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
        t.SetPixels32(px);
        t.Apply();
        return t;
    }

    GameObject Quad(string name, Material mat)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(q.GetComponent<Collider>());          // the laser hit is computed against the screen plane
        q.name = name;
        q.layer = Layer;
        q.transform.SetParent(transform, false);
        q.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return q;
    }

    // A camera that renders to the headset, posed by this script rather than by Unity's automatic head tracking.
    Camera HeadsetCamera(Transform t, float depth)
    {
        var c = t.gameObject.AddComponent<Camera>();
        c.stereoTargetEye = StereoTargetEyeMask.Both;
        c.depth = depth;
        XRDevice.DisableAutoXRCameraTracking(c, true);
        _own.Add(c);
        return c;
    }

    void Awake()
    {
        // Room eye: renders only the VR room (screen, floor, laser).
        _head = new GameObject("TftfVrHead").transform;
        _head.SetParent(transform, false);
        _eye = HeadsetCamera(_head, 100);
        _eye.clearFlags = CameraClearFlags.SolidColor;
        _eye.backgroundColor = new Color(0.015f, 0.017f, 0.025f);
        _eye.cullingMask = 1 << Layer;
        _eye.nearClipPlane = 0.05f;
        _eye.farClipPlane = 200f;

        // Arena eye: in arena mode, the fight in stereo, drawn before the room eye (which then adds only the HUD).
        _anchor = new GameObject("TftfVrArenaSeat").transform;
        DontDestroyOnLoad(_anchor.gameObject);
        _arenaHead = new GameObject("TftfVrArenaHead").transform;
        _arenaHead.SetParent(_anchor, false);
        _arenaEye = HeadsetCamera(_arenaHead, 90);
        _arenaEye.enabled = false;

        // Clears Screen2D before the game's cameras draw into it (the first of them may clear depth only).
        _clear = new GameObject("TftfVrScreenClear").AddComponent<Camera>();
        _clear.transform.SetParent(transform, false);
        _clear.clearFlags = CameraClearFlags.SolidColor;
        _clear.backgroundColor = Color.black;
        _clear.cullingMask = 0;
        _clear.depth = -10000;
        _clear.targetTexture = Screen2D;
        _clear.stereoTargetEye = StereoTargetEyeMask.None;
        _own.Add(_clear);
        _scratch = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32) { name = "TftfVrScratch" };

        _uiMat = Mat("TftfVrUi", Texture2D.whiteTexture, Color.white);
        _uiTextMat = Mat("TftfVrText", Texture2D.whiteTexture, Color.white);
        _screenOpaque = Mat("TftfVrOpaque", Screen2D, Color.white);
        _screenHud = Mat("TftfVrUi", Screen2D, Color.white);

        var screen = Quad("TftfVrScreen", _screenOpaque);
        _screen = screen.transform;
        _screen.localScale = new Vector3(ScreenWidth, ScreenWidth * Height / Width, 1);

        var floor = Quad("TftfVrFloor", Mat("TftfVrOpaque", GridTexture(), Color.white));
        floor.transform.localRotation = Quaternion.Euler(90, 0, 0);
        floor.transform.localScale = new Vector3(40, 40, 1);
        floor.GetComponent<MeshRenderer>().sharedMaterial.mainTextureScale = new Vector2(40, 40);
        _floor = floor.transform;

        _laser = new GameObject("TftfVrLaser").AddComponent<LineRenderer>();
        _laser.gameObject.layer = Layer;
        _laser.transform.SetParent(transform, false);
        _laser.useWorldSpace = true;
        _laser.positionCount = 2;
        _laser.widthMultiplier = 0.004f;
        _laser.sharedMaterial = Mat("TftfVrUi", Texture2D.whiteTexture, new Color(1f, 0.55f, 0.12f, 0.7f));
        _cursor = Quad("TftfVrCursor", Mat("TftfVrUi", DotTexture(), new Color(1f, 0.6f, 0.15f, 1f))).transform;
        _cursor.localScale = Vector3.one * 0.03f;
        for (int h = 0; h < 2; h++)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(m.GetComponent<Collider>());
            m.name = h == 0 ? "TftfVrHandL" : "TftfVrHandR";
            m.layer = Layer;
            m.transform.SetParent(transform, false);
            m.transform.localScale = Vector3.one * 0.08f;
            _markerMats[h] = Mat("TftfVrUi", DotTexture(), HandColors[h]);
            m.GetComponent<MeshRenderer>().sharedMaterial = _markerMats[h];
            m.SetActive(false);
            _markers[h] = m.transform;
        }

        Camera.onPreCull += Retarget;
        Application.onBeforeRender += BeforeRender;
        var inputs = new List<XRInputSubsystem>();
        SubsystemManager.GetInstances(inputs);
        foreach (var s in inputs) s.trackingOriginUpdated += _ => _placed = false;   // the user recentred (Oculus button)
    }

    void OnDestroy()
    {
        Camera.onPreCull -= Retarget;
        Application.onBeforeRender -= BeforeRender;
    }

    // Game cameras render into Screen2D, never to the headset. The deferred scene camera writes its own buffers
    // (SetTargetBuffers) and composites through EBImage, which P53 points at CompositeTarget, so its target stays unset.
    void Retarget(Camera c)
    {
        if (c == null || _own.Contains(c)) return;
        c.cullingMask &= ~(1 << Layer);
        c.stereoTargetEye = StereoTargetEyeMask.None;
        // In arena mode the fight camera draws nothing, but its image effect (TftfBloom) still blits a full opaque frame:
        // it goes to the scratch target every frame, as the game sets its target back to none.
        if (_arena && c == _source)
        {
            if (!OwnBuffers(c) && c.targetTexture != _scratch) c.targetTexture = _scratch;
            return;
        }
        if (c.targetTexture == null && !OwnBuffers(c)) c.targetTexture = Screen2D;
        // In arena mode the screen is the HUD over the stereo arena: an opaque clear on it would hide the arena. Only
        // _clear clears it, to transparent.
        if (_arena && c.targetTexture == Screen2D && (c.clearFlags == CameraClearFlags.SolidColor || c.clearFlags == CameraClearFlags.Skybox))
        {
            if (!_cleared.ContainsKey(c)) _cleared[c] = c.clearFlags;
            c.clearFlags = CameraClearFlags.Depth;
        }
        if (_arena && c.targetTexture == Screen2D && (c.cullingMask & _sourceMask) != 0)
        {
            if (!_masked.ContainsKey(c)) _masked[c] = c.cullingMask;
            c.cullingMask &= ~_sourceMask;
        }
    }

    void RestoreClears()
    {
        foreach (var kv in _cleared)
            if (kv.Key != null && kv.Key.clearFlags == CameraClearFlags.Depth) kv.Key.clearFlags = kv.Value;
        _cleared.Clear();
        foreach (var kv in _masked)
            if (kv.Key != null && (kv.Key.cullingMask & kv.Value) == kv.Key.cullingMask) kv.Key.cullingMask = kv.Value;
        _masked.Clear();
    }

    static bool OwnBuffers(Camera c)
    {
        // A disabled camera is rendered by hand into buffers of its own (SetTargetBuffers leaves targetTexture null): the
        // planar reflection camera and its light camera. Pointed at Screen2D, the mirrored scene drew over the frame (magenta
        // upside-down silhouettes in Karnak). The light cameras composite through EBImage, like the deferred camera.
        if (!c.enabled || c.GetComponent<EB.Rendering.EBLightCamera>() != null) return true;
        if (c.GetComponent<EB.Rendering.EBDeferredCamera>() == null) return false;
        var path = EB.Rendering.EBGBuffer.GetCurrentRenderPath(EB.Rendering.EBGBuffer.eMRT_BUFFER.kMain);
        return path == EB.Rendering.EBGBuffer.eRENDER_PATH.Deferred || path == EB.Rendering.EBGBuffer.eRENDER_PATH.ForwardWithRT;
    }

    static int HandIndex(XRNode node) { return node == XRNode.LeftHand ? 0 : 1; }

    static bool IsHand(XRNode node) { return node == XRNode.LeftHand || node == XRNode.RightHand; }

    // A tracked hand (controllers put down) is posed by TftfVrHands: its root for the grip pose, its pointer pose for the laser.
    static bool Pose(XRNode node, out Vector3 pos, out Quaternion rot, bool pointer = false)
    {
        if (IsHand(node) && TftfVrHands.IsTracked(HandIndex(node)))
            return pointer ? TftfVrHands.PointerPoseOf(HandIndex(node), out pos, out rot) : TftfVrHands.RootPoseOf(HandIndex(node), out pos, out rot);
        var d = InputDevices.GetDeviceAtXRNode(node);
        pos = Vector3.zero; rot = Quaternion.identity;
        if (!d.isValid) return false;
        bool p = node == XRNode.Head ? d.TryGetFeatureValue(CommonUsages.centerEyePosition, out pos) : false;
        bool r = node == XRNode.Head ? d.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rot) : false;
        if (!p) p = d.TryGetFeatureValue(CommonUsages.devicePosition, out pos);
        if (!r) r = d.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
        return p && r;
    }

    void TrackHead()
    {
        Vector3 p; Quaternion r;
        if (Pose(XRNode.Head, out p, out r)) { _head.localPosition = p; _head.localRotation = r; }
    }

    // After every LateUpdate (the fight camera has moved), just before rendering: the latest head pose.
    [BeforeRenderOrder(-100)]
    void BeforeRender()
    {
        TrackHead();
        if (_arena) PoseArena();
        DriveBody();
        if (_arena) MaybeShoot();   // after the arms are driven
    }

    // In front of the head at eye height, turned to face it (yaw only); again after every recentre or a stick click.
    void Place()
    {
        Vector3 fwd = _head.forward; fwd.y = 0;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        _screen.position = _head.position + fwd * Distance;
        _screen.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        // Floor about 1.2 m below the eyes when the tracking origin is the head (seated); otherwise the real floor.
        float floorY = _head.localPosition.y > 0.8f ? transform.position.y : _head.position.y - 1.2f;
        _floor.position = new Vector3(_head.position.x, floorY, _head.position.z);
        _placed = true;
        _snap = true;
    }

    static InputDevice Hand(XRNode node) { return InputDevices.GetDeviceAtXRNode(node); }

    static bool Button(XRNode node, InputFeatureUsage<bool> usage)
    {
        bool v;
        return Hand(node).TryGetFeatureValue(usage, out v) && v;
    }

    static float Trigger(XRNode node)
    {
        if (TftfVrHands.IsTracked(HandIndex(node))) return TftfVrHands.Pinch(HandIndex(node));   // a pinch is the trigger
        float v;
        return Hand(node).TryGetFeatureValue(CommonUsages.trigger, out v) ? v : 0f;
    }

    static Vector2 Stick(XRNode node)
    {
        Vector2 v;
        return Hand(node).TryGetFeatureValue(CommonUsages.primary2DAxis, out v) ? v : Vector2.zero;
    }

    // Fight buttons for TftfInput, read once per frame: 1 = A pressed (tap the attack zone), 2 / 3 = right stick
    // flicked right / left (swipe forward / back across it), 0 = nothing new.
    public static int FightGesture()
    {
        if (_instance == null) return 0;
        bool a = Button(XRNode.RightHand, CommonUsages.primaryButton);
        int g = a && !_instance._aWas ? 1 : 0;
        _instance._aWas = a;
        float x = Stick(XRNode.RightHand).x;
        int dir = x > 0.7f ? 1 : x < -0.7f ? -1 : 0;
        if (dir != 0 && _instance._stickWas == 0) g = dir > 0 ? 2 : 3;
        if (Mathf.Abs(x) < 0.3f) _instance._stickWas = 0;
        else if (dir != 0) _instance._stickWas = dir;
        return g;
    }

    // Motion controls (TftfVrCombat), all in tracking space: the head, and a hand (controller grip or tracked hand) with
    // how closed it is (a tracked hand's fist, a controller's grip), whether it is a tracked hand.
    public static bool HeadPose(out Vector3 pos, out Quaternion rot) { return Pose(XRNode.Head, out pos, out rot); }

    public static bool HandPose(int hand, out Vector3 pos, out Quaternion rot, out float closed, out bool tracked)
    {
        XRNode node = hand == 0 ? XRNode.LeftHand : XRNode.RightHand;
        tracked = TftfVrHands.IsTracked(hand);
        float g = 0f;
        closed = tracked ? TftfVrHands.Fist(hand) : Hand(node).TryGetFeatureValue(CommonUsages.grip, out g) ? g : 0f;
        return Pose(node, out pos, out rot);
    }

    // A controller's buttons (false for a tracked hand): 0 = A / X, 1 = B / Y.
    public static bool HandButton(int hand, int button)
    {
        XRNode node = hand == 0 ? XRNode.LeftHand : XRNode.RightHand;
        return Button(node, button == 0 ? CommonUsages.primaryButton : CommonUsages.secondaryButton);
    }

    // Controller haptics (nothing on a tracked hand).
    public static void Buzz(int hand, float amplitude, float seconds)
    {
        if (TftfVrHands.IsTracked(hand)) return;
        Hand(hand == 0 ? XRNode.LeftHand : XRNode.RightHand).SendHapticImpulse(0, Mathf.Clamp01(amplitude), seconds);
    }

    // Block: either grip held.
    public static bool BlockHeld
    {
        get { return _instance != null && (Button(XRNode.LeftHand, CommonUsages.gripButton) || Button(XRNode.RightHand, CommonUsages.gripButton)); }
    }

    void Update()
    {
        TrackHead();
        if (!_placed && InputDevices.GetDeviceAtXRNode(XRNode.Head).isValid) Place();
        if (Button(XRNode.RightHand, CommonUsages.primary2DAxisClick) || Button(XRNode.LeftHand, CommonUsages.primary2DAxisClick)) Place();
        bool x = Button(XRNode.LeftHand, CommonUsages.primaryButton);
        if (x && !_xWas) { _view = (_view + 1) % 3; Debug.Log("[TFTF] VR view " + (_view == ViewFirstPerson ? "first-person" : _view == ViewArena ? "arena" : "flat")); }
        _xWas = x;
        float y = Stick(XRNode.LeftHand).y;
        if (Mathf.Abs(y) > 0.2f) _zoom = Mathf.Clamp(_zoom * Mathf.Exp(-y * Time.unscaledDeltaTime), 0.4f, 2.5f);
        UpdatePointer();
        UpdateMarkers();
    }

    void UpdateMarkers()
    {
        for (int h = 0; h < 2; h++)
        {
            Vector3 p = Vector3.zero; Quaternion r = Quaternion.identity; float c; bool tr;
            bool on = ShowHands && !(_fp && _body.HasArm(h)) && HandPose(h, out p, out r, out c, out tr);
            _markers[h].gameObject.SetActive(on);
            if (!on) continue;
            _markers[h].position = transform.TransformPoint(p);
            _markers[h].rotation = transform.rotation * r;
            _markerMats[h].color = HandColors[h];
        }
    }

    void UpdatePointer()
    {
        // The hand whose trigger was pulled last is the pointer.
        XRNode other = _hand == XRNode.RightHand ? XRNode.LeftHand : XRNode.RightHand;
        if (!_pressed && Trigger(other) > TriggerOn) _hand = other;
        float trig = Trigger(_hand);
        bool down = _pressed ? trig > TriggerOff : trig > TriggerOn;

        Vector3 pos; Quaternion rot;
        PointerUp = false;
        if (!Pose(_hand, out pos, out rot, true))
        {
            _laser.enabled = false; _cursor.gameObject.SetActive(false);
            if (_pressed) { _pressed = false; PointerDown = false; PointerUp = true; }
            PointerOnScreen = false;
            return;
        }
        Vector3 origin = transform.TransformPoint(pos);
        Vector3 dir = transform.rotation * rot * Vector3.forward;
        Vector3 fwd = _screen.forward;                 // away from the viewer; the quad's visible side faces back
        float denom = Vector3.Dot(dir, fwd);
        bool hit = false;
        Vector2 uv = Vector2.zero;
        Vector3 hitPoint = origin + dir * 4f;
        if (denom > 1e-4f)
        {
            float t = Vector3.Dot(_screen.position - origin, fwd) / denom;
            if (t > 0)
            {
                hitPoint = origin + dir * t;
                Vector3 local = _screen.InverseTransformPoint(hitPoint);
                uv = new Vector2(local.x + 0.5f, local.y + 0.5f);
                hit = uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
            }
        }
        // Base mode: off the screen (or in a press that started off it) the laser points at the base itself.
        Vector2 basePixel = Vector2.zero;
        float baseDist = 0f;
        bool onBase = _arena && _base && (!hit || (_pressed && _pressedWorld)) && BasePoint(pos, rot, out basePixel, out baseDist);
        if (onBase)
        {
            PointerPixel = basePixel;
            hitPoint = origin + dir * Mathf.Min(baseDist / _scale, 20f);
        }
        // A drag that leaves the screen keeps going along its edge, so a swipe is not cut short.
        else if (hit || (_pressed && !_pressedWorld && denom > 1e-4f))
            PointerPixel = new Vector2(Mathf.Clamp01(uv.x) * Width, Mathf.Clamp01(uv.y) * Height);
        PointerOnScreen = hit;
        if (down && !_pressed && (hit || onBase)) { _pressed = true; _pressedWorld = !hit; }
        else if (!down && _pressed) { _pressed = false; PointerUp = true; }
        PointerDown = _pressed;

        // In a fight with motion controls the laser shows only on the screen (a punch must not wave it around the arena).
        _laser.enabled = !ShowHands || hit || _pressed;
        _laser.SetPosition(0, origin);
        _laser.SetPosition(1, hitPoint);
        _cursor.gameObject.SetActive(hit && !onBase);
        if (hit && !onBase)
        {
            _cursor.position = hitPoint - fwd * 0.005f;   // just in front of the screen
            _cursor.rotation = _screen.rotation;
            _cursor.localScale = Vector3.one * (_pressed ? 0.022f : 0.03f);
        }
    }

    // Base mode: the pixel of the base camera's image at the spot the laser touches on the base (a collider of the camera's
    // layers, else the ground plane at the focus height), and how far along the laser that spot is, in game units.
    bool BasePoint(Vector3 trackingPos, Quaternion trackingRot, out Vector2 pixel, out float dist)
    {
        pixel = Vector2.zero; dist = 0f;
        if (_source == null) return false;
        Vector3 o = ArenaPoint(trackingPos);
        Vector3 d = _anchor.rotation * (_seatRot * (transform.rotation * trackingRot * Vector3.forward));
        Vector3 p;
        RaycastHit h;
        if (Physics.Raycast(o, d, out h, _source.farClipPlane, _sourceMask)) { p = h.point; dist = h.distance; }
        else
        {
            float y = TftfFightState.BaseFocus.y;
            if (d.y > -1e-3f || o.y <= y) return false;
            dist = (o.y - y) / -d.y;
            p = o + d * dist;
        }
        Vector3 v = _source.WorldToViewportPoint(p);
        if (v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) return false;
        pixel = new Vector2(v.x * Width, v.y * Height);
        return true;
    }

    // A tracking-space point (head, eye, hand) placed in the arena: its offset from the room seat, scaled, from the arena seat.
    Vector3 ArenaPoint(Vector3 tracking)
    {
        return _anchor.TransformPoint(_seatRot * (transform.TransformPoint(tracking) - _seat) * _scale);
    }

    void LateUpdate()
    {
        UpdateArena();
        int n = Camera.allCamerasCount;
        if (_cams.Length < n) _cams = new Camera[n * 2];
        n = Camera.GetAllCameras(_cams);
        for (int i = 0; i < n; i++) Retarget(_cams[i]);
    }

    void UpdateArena()
    {
        Camera fight = ArenaWanted && TftfFightState.InFight ? TftfFightState.FightCamera : null;
        Camera home = fight == null && ArenaWanted && TftfFightState.AtBase ? TftfFightState.BaseCamera : null;
        Camera want = fight != null ? fight : home;
        bool wantBase = fight == null && home != null;
        if (_arena && (want != _source || wantBase != _base)) SetArena(null);
        if (!_arena && want != null) { _base = wantBase; SetArena(want); }
        if (!_arena) return;
        // The fight camera's layers go to the arena eye; the game camera itself draws nothing (its setup may set them again).
        if (_source.cullingMask != 0) { _sourceMask = _source.cullingMask & ~(1 << Layer); _source.cullingMask = 0; }
        _arenaEye.cullingMask = _sourceMask;
        _arenaEye.clearFlags = _source.clearFlags == CameraClearFlags.Depth || _source.clearFlags == CameraClearFlags.Nothing
            ? CameraClearFlags.SolidColor : _source.clearFlags;
        _arenaEye.backgroundColor = _source.backgroundColor;
        _arenaEye.nearClipPlane = _source.nearClipPlane;
        _arenaEye.farClipPlane = _source.farClipPlane;
        if (_fp) _arenaEye.nearClipPlane = FpNear * _scale;   // the fighter's own collar and shoulders clipped, its hands kept
        // The fight camera's rendering path: the player settings resolve a new camera's to VertexLit, which skips every
        // ForwardBase pass of the game's shaders (a Quest 2 saw black but for the sun flare and a few effects).
        _arenaEye.renderingPath = _source.renderingPath;
        _arenaEye.allowHDR = _source.allowHDR;
        _arenaEye.useOcclusionCulling = _source.useOcclusionCulling;
        _arenaEye.layerCullDistances = _source.layerCullDistances;
        _arenaEye.layerCullSpherical = _source.layerCullSpherical;
    }

    // Dev: the headset's view cannot be captured over adb. While tftf/vrshot.txt exists (checked every second in arena
    // mode, deleted after use), PNGs in tftf/: the arena eye's view without stereo (vrshot-seat.png) and the screen
    // texture, the HUD (vrshot-screen.png). In first-person also the fighter seen from in front and to its right
    // (vrshot-body.png), and the log gives how far each driven hand is from where the player's hand is.
    float _shotCheck;
    void MaybeShoot()
    {
        if (Time.unscaledTime < _shotCheck) return;
        _shotCheck = Time.unscaledTime + 1f;
        string dir = System.IO.Path.Combine(Application.persistentDataPath, "tftf");
        string flag = System.IO.Path.Combine(dir, "vrshot.txt");
        try { if (!System.IO.File.Exists(flag)) return; System.IO.File.Delete(flag); } catch (System.Exception) { return; }
        var go = new GameObject("TftfVrShot");
        var cam = go.AddComponent<Camera>();
        _own.Add(cam);
        var rt = new RenderTexture(1024, 576, 24, RenderTextureFormat.ARGB32);
        try
        {
            cam.enabled = false;
            cam.CopyFrom(_arenaEye);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.targetTexture = rt;
            cam.aspect = 16f / 9f;
            go.transform.SetPositionAndRotation(_arenaHead.position, _arenaHead.rotation);
            cam.fieldOfView = 2f * ArenaHalfFov;
            cam.Render();
            Save(rt, System.IO.Path.Combine(dir, "vrshot-seat.png"));
            Save(Screen2D, System.IO.Path.Combine(dir, "vrshot-screen.png"));
            if (_fp && _body.Root != null)
            {
                Vector3 at = _anchor.position - _anchor.up * 0.5f * _scale;
                go.transform.position = at + (_anchor.forward * 1.6f + _anchor.right * 1.2f + _anchor.up * 0.3f) * _scale;
                go.transform.LookAt(at);
                cam.nearClipPlane = 0.05f * _scale;
                cam.Render();
                Save(rt, System.IO.Path.Combine(dir, "vrshot-body.png"));
                Debug.Log("[TFTF] VR body: scale " + _scale.ToString("0.00") + ", hand misses (m) " + _handMiss[0].ToString("0.000") + "/" + _handMiss[1].ToString("0.000"));
            }
            Debug.Log("[TFTF] VR shots saved");
        }
        catch (System.Exception e) { Debug.Log("[TFTF] VR shots failed: " + e); }
        _own.Remove(cam);
        cam.targetTexture = null;
        Destroy(go);
        rt.Release();
    }

    static void Save(RenderTexture rt, string path)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        Destroy(tex);
    }
    void SetArena(Camera source)
    {
        if (source == null)
        {
            if (_source != null)
            {
                if (_source.cullingMask == 0) _source.cullingMask = _sourceMask;
                if (_source.targetTexture == _scratch) _source.targetTexture = null;   // Retarget points it at Screen2D again
            }
            _source = null;
            _arena = false;
            _base = false;
            _arenaEye.ResetStereoViewMatrices();
            RestoreClears();
        }
        else
        {
            _source = source;
            _sourceMask = source.cullingMask & ~(1 << Layer);
            source.cullingMask = 0;
            if (source.targetTexture == Screen2D) source.targetTexture = _scratch;   // and in Retarget: it would hide the HUD's transparency
            _arena = true;
            _snap = true;
        }
        _scale = _base ? BaseScale : 1f;
        _fp = false;
        _body.Release();
        _pressedWorld = false;
        _arenaEye.enabled = _arena;
        _eye.clearFlags = _arena ? CameraClearFlags.Depth : CameraClearFlags.SolidColor;   // the room eye adds only the HUD
        _floor.gameObject.SetActive(!_arena);
        _screen.GetComponent<MeshRenderer>().sharedMaterial = _arena ? _screenHud : _screenOpaque;
        _clear.backgroundColor = _arena ? Color.clear : Color.black;                      // the HUD on a transparent screen
        Debug.Log("[TFTF] VR stereo mode " + (_arena ? "on (" + (_base ? "base, " : "arena, ") + source.name + ")" : "off (" + (ArenaWanted ? "camera gone" : "X") + ")"));
    }

    // The seat: on the fight camera's line of sight, nearer to the framed point by the ratio of the fight camera's narrow
    // field of view to the headset's, eased so that the camera's own small moves do not shake the world, snapped on cuts.
    void PoseArena()
    {
        if (_source == null) return;
        bool fp = !_base && _view == ViewFirstPerson && TftfFightState.FirstPersonReady;
        if (fp != _fp)
        {
            _fp = fp;
            _snap = true;
            if (!fp) _body.Shrink(false);
            Debug.Log("[TFTF] VR first-person " + (fp ? "on" : "off"));
        }
        if (fp) PoseFirstPerson();
        else PoseSeat();
        // The head's offset from the seat in the room (the seat looks at the screen) becomes its offset from the arena seat.
        _seat = _screen.position - _screen.forward * Distance;
        _seatRot = Quaternion.Inverse(_screen.rotation);
        _arenaHead.localPosition = _seatRot * (_head.position - _seat) * _scale;
        _arenaHead.localRotation = _seatRot * _head.rotation;
        StereoEyes();
    }

    // First-person: the eye in the fighter's head (eased, so the animation's bob is damped; on the root at its eye height if
    // the rig has no head bone), facing the opponent (yaw only, so a hit reaction never tilts the world), scaled so that
    // the fighter's eye height is a standing player's.
    void PoseFirstPerson()
    {
        Transform self = TftfFightState.Self;
        _body.Bind(self);
        Vector3 look = Vector3.ProjectOnPlane(TftfFightState.OpponentCenter - self.position, Vector3.up);
        if (look.sqrMagnitude < 1e-4f) look = Vector3.ProjectOnPlane(self.forward, Vector3.up);
        if (look.sqrMagnitude < 1e-4f) look = Vector3.forward;
        Quaternion rot = Quaternion.LookRotation(look, Vector3.up);
        float h = TftfFightState.SelfEyeHeight;
        Vector3 pos = self.position + Vector3.up * h;
        if (_body.Head != null)
        {
            // The head bone is at the skull's base, behind the face: the eye goes to the front of the face.
            Vector3 eye = _body.Head.position + Vector3.up * h * FpEyeUp + look.normalized * h * FpEyeForward;
            _eyeSmooth = _snap ? eye : Vector3.Lerp(_eyeSmooth, eye, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            pos = _eyeSmooth;
        }
        _scale = Mathf.Clamp((pos.y - self.position.y) / PlayerEyeHeight, 1f, 8f);
        float dt = Time.unscaledDeltaTime;
        if (_snap || (_anchor.position - pos).magnitude > 3f * _scale || Quaternion.Angle(_anchor.rotation, rot) > 60f)
        {
            _anchor.SetPositionAndRotation(pos, rot);
            _snap = false;
        }
        else
            _anchor.SetPositionAndRotation(Vector3.Lerp(_anchor.position, pos, 1f - Mathf.Exp(-20f * dt)),
                                           Quaternion.Slerp(_anchor.rotation, rot, 1f - Mathf.Exp(-8f * dt)));
    }

    void PoseSeat()
    {
        _scale = _base ? BaseScale : 1f;
        Transform g = _source.transform;
        Vector3 fwd = g.forward;
        Vector3 toFocus = (_base ? TftfFightState.BaseFocus : TftfFightState.FightFocus) - g.position;
        float d = Vector3.Dot(toFocus, fwd);
        if (d < 1f) d = Mathf.Max(1f, toFocus.magnitude);
        float k = Mathf.Clamp(Mathf.Tan(_source.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(ArenaHalfFov * Mathf.Deg2Rad) * _zoom, 0.1f, 1f);
        Vector3 pos = g.position + fwd * d * (1f - k);
        Vector3 flat = fwd;
        if (_base) flat = Vector3.ProjectOnPlane(fwd.y > -0.99f ? fwd : g.up, Vector3.up);   // the base looks down: level horizon
        Quaternion rot = Quaternion.LookRotation(flat, Vector3.up);
        float dt = Time.unscaledDeltaTime;
        if (_snap || Quaternion.Angle(_anchor.rotation, rot) > 35f || (_anchor.position - pos).magnitude > d * 0.6f)
        {
            _anchor.SetPositionAndRotation(pos, rot);
            _snap = false;
        }
        else
            _anchor.SetPositionAndRotation(Vector3.Lerp(_anchor.position, pos, 1f - Mathf.Exp(-8f * dt)),
                                           Quaternion.Slerp(_anchor.rotation, rot, 1f - Mathf.Exp(-6f * dt)));
    }

    // First-person, after the game's animation and the eye's pose: the head shrunk (the eye is inside it) and each arm
    // bent so that the fighter's hand is where the player's hand is in the arena, eased in over a fifth of a second. With a
    // controller the wrist also takes the controller's turn and the fighter's fist, not its wrist, goes to the grip.
    void DriveBody()
    {
        if (!_fp || _body.Root == null) { _armWeight = 0f; return; }    // off the seat the arms are the animation's
        _armWeight = Mathf.MoveTowards(_armWeight, ShowHands ? 1f : 0f, Time.unscaledDeltaTime * 5f);
        _body.Shrink(true);
        Vector3 right = _anchor.right, up = _anchor.up, fwd = _anchor.forward;
        for (int h = 0; h < 2; h++)
        {
            Vector3 p; Quaternion r; float c; bool tr;
            if (!_body.HasArm(h) || !HandPose(h, out p, out r, out c, out tr)) continue;
            float reach = _body.Reach(h);
            Vector3 pole = _body.Shoulder(h) + (-up + (h == 0 ? -right : right) * 0.6f - fwd * 0.3f) * reach;
            Vector3 target = ArenaPoint(p);
            bool wrist = !tr && FpWrist > 0f;   // a tracked hand's root pose has other axes: its wrist stays the forearm's
            Quaternion grip = _anchor.rotation * _seatRot * transform.rotation * r * Quaternion.Euler(-FpWristPitch, 0f, 0f);
            if (wrist) target -= grip * Vector3.forward * _body.FistOffset(h);
            _body.Solve(h, target, pole, _armWeight);
            if (wrist) _body.Aim(h, grip, _armWeight * FpWrist);
            _handMiss[h] = (_body.Hand(h).position - target).magnitude / _scale;
        }
    }

    // Scaled world (base mode): the eyes as far apart as the scaled head moves, set by hand; at scale 1 the headset's own.
    void StereoEyes()
    {
        Vector3 l, r;
        var d = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (_scale == 1f || !d.TryGetFeatureValue(CommonUsages.leftEyePosition, out l) || !d.TryGetFeatureValue(CommonUsages.rightEyePosition, out r))
        {
            _arenaEye.ResetStereoViewMatrices();
            return;
        }
        _arenaEye.SetStereoViewMatrix(Camera.StereoscopicEye.Left, View(ArenaPoint(l)));
        _arenaEye.SetStereoViewMatrix(Camera.StereoscopicEye.Right, View(ArenaPoint(r)));
    }

    Matrix4x4 View(Vector3 eye)
    {
        return Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(eye, _arenaHead.rotation, Vector3.one).inverse;
    }
}
#endif
