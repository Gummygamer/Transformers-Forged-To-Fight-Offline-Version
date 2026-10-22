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

    public static RenderTexture Screen2D;
    // The finger: pixel position on Screen2D (origin bottom left, like Touch.position), whether it is down, and
    // whether it went up this frame (a click for TftfVrPanel).
    public static Vector2 PointerPixel;
    public static bool PointerDown, PointerUp, PointerOnScreen;

    static TftfVr _instance;
    readonly HashSet<Camera> _own = new HashSet<Camera>();
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
    bool _arenaWanted = true, _arena, _snap;
    float _zoom = 1f;
    bool _xWas, _aWas;
    int _stickWas;

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
        if (c.targetTexture == null && !OwnBuffers(c)) c.targetTexture = Screen2D;
    }

    static bool OwnBuffers(Camera c)
    {
        if (c.GetComponent<EB.Rendering.EBDeferredCamera>() == null) return false;
        var path = EB.Rendering.EBGBuffer.GetCurrentRenderPath(EB.Rendering.EBGBuffer.eMRT_BUFFER.kMain);
        return path == EB.Rendering.EBGBuffer.eRENDER_PATH.Deferred || path == EB.Rendering.EBGBuffer.eRENDER_PATH.ForwardWithRT;
    }

    static bool Pose(XRNode node, out Vector3 pos, out Quaternion rot)
    {
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
        if (x && !_xWas) _arenaWanted = !_arenaWanted;
        _xWas = x;
        float y = Stick(XRNode.LeftHand).y;
        if (Mathf.Abs(y) > 0.2f) _zoom = Mathf.Clamp(_zoom * Mathf.Exp(-y * Time.unscaledDeltaTime), 0.4f, 2.5f);
        UpdatePointer();
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
        if (!Pose(_hand, out pos, out rot))
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
        // A drag that leaves the screen keeps going along its edge, so a swipe is not cut short.
        if (hit || (_pressed && denom > 1e-4f))
            PointerPixel = new Vector2(Mathf.Clamp01(uv.x) * Width, Mathf.Clamp01(uv.y) * Height);
        PointerOnScreen = hit;
        if (down && !_pressed && hit) _pressed = true;
        else if (!down && _pressed) { _pressed = false; PointerUp = true; }
        PointerDown = _pressed;

        _laser.enabled = true;
        _laser.SetPosition(0, origin);
        _laser.SetPosition(1, hitPoint);
        _cursor.gameObject.SetActive(hit);
        if (hit)
        {
            _cursor.position = hitPoint - fwd * 0.005f;   // just in front of the screen
            _cursor.rotation = _screen.rotation;
            _cursor.localScale = Vector3.one * (_pressed ? 0.022f : 0.03f);
        }
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
        Camera fight = _arenaWanted && TftfFightState.InFight ? TftfFightState.FightCamera : null;
        if (_arena && fight != _source) SetArena(null);
        if (!_arena && fight != null) SetArena(fight);
        if (!_arena) return;
        // The fight camera's layers go to the arena eye; the game camera itself draws nothing (its setup may set them again).
        if (_source.cullingMask != 0) { _sourceMask = _source.cullingMask & ~(1 << Layer); _source.cullingMask = 0; }
        _arenaEye.cullingMask = _sourceMask;
        _arenaEye.clearFlags = _source.clearFlags == CameraClearFlags.Depth || _source.clearFlags == CameraClearFlags.Nothing
            ? CameraClearFlags.SolidColor : _source.clearFlags;
        _arenaEye.backgroundColor = _source.backgroundColor;
        _arenaEye.nearClipPlane = _source.nearClipPlane;
        _arenaEye.farClipPlane = _source.farClipPlane;
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
        }
        else
        {
            _source = source;
            _sourceMask = source.cullingMask & ~(1 << Layer);
            source.cullingMask = 0;
            if (source.targetTexture == Screen2D) source.targetTexture = _scratch;   // its clear would hide the HUD's transparency
            _arena = true;
            _snap = true;
        }
        _arenaEye.enabled = _arena;
        _eye.clearFlags = _arena ? CameraClearFlags.Depth : CameraClearFlags.SolidColor;   // the room eye adds only the HUD
        _floor.gameObject.SetActive(!_arena);
        _screen.GetComponent<MeshRenderer>().sharedMaterial = _arena ? _screenHud : _screenOpaque;
        _clear.backgroundColor = _arena ? Color.clear : Color.black;                      // the HUD on a transparent screen
        Debug.Log("[TFTF] VR arena mode " + (_arena ? "on (" + source.name + ")" : "off"));
    }

    // The seat: on the fight camera's line of sight, nearer to the framed point by the ratio of the fight camera's narrow
    // field of view to the headset's, eased so that the camera's own small moves do not shake the world, snapped on cuts.
    void PoseArena()
    {
        if (_source == null) return;
        Transform g = _source.transform;
        Vector3 fwd = g.forward;
        Vector3 toFocus = TftfFightState.FightFocus - g.position;
        float d = Vector3.Dot(toFocus, fwd);
        if (d < 1f) d = Mathf.Max(1f, toFocus.magnitude);
        float k = Mathf.Clamp(Mathf.Tan(_source.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(ArenaHalfFov * Mathf.Deg2Rad) * _zoom, 0.1f, 1f);
        Vector3 pos = g.position + fwd * d * (1f - k);
        Vector3 flat = fwd;
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
        // The head's offset from the seat in the room (the seat looks at the screen) becomes its offset from the arena seat.
        Vector3 seat = _screen.position - _screen.forward * Distance;
        Quaternion seatRot = Quaternion.Inverse(_screen.rotation);
        _arenaHead.localPosition = seatRot * (_head.position - seat);
        _arenaHead.localRotation = seatRot * _head.rotation;
    }
}
#endif
