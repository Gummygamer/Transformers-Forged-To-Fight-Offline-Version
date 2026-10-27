// Newly authored. VR build only (TFTF_VR). Hand tracking on Meta Quest: the Oculus XR plugin 1.11.2 exposes controllers
// only, but the libOVRPlugin.so it ships exports the hand state (ovrp_GetHandState), as the Oculus Integration reads it.
// Read once per frame per hand: whether the hand is tracked, its root and pointer poses in tracking space (Unity axes),
// the index pinch (the trigger of a tracked hand) and how far the fingers are curled (a fist). The manifest entries that
// let the system track hands for this app are written by TftfVrBuildSetup (TftfVrHandTrackingManifest).
//
// The state is read into a raw buffer rather than a marshalled struct: the layout (OVRPlugin's HandStateInternal) is
// status int, root pose (quat xyzw, vec3), 24 bone rotations (quat xyzw, each relative to its parent bone), pinch flags,
// 5 pinch strengths, pointer pose, hand scale, confidence, 5 finger confidences, 2 timestamps: 512 bytes.
#if TFTF_VR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

public static class TftfVrHands
{
    [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
    static extern int ovrp_GetHandState(int step, int hand, IntPtr state);

    const int StepRender = -1, StateBytes = 1024;    // twice the known layout, should a newer plugin append fields
    const int RootPose = 1, Bones = 8, PinchStrength = 105, PointerPose = 110;
    const int Tracked = 1, InputValid = 2;            // HandStatus flags

    struct Hand
    {
        public bool Tracked, Pointer;
        public Vector3 Pos, PointerPos;
        public Quaternion Rot, PointerRot;
        public float Pinch, Fist;
    }

    static readonly Hand[] _hands = new Hand[2];
    static readonly float[] _f = new float[StateBytes / 4];
    static IntPtr _buf;
    static int _frame = -1;
    static bool _broken;

    // 0 = left, 1 = right (OVRPlugin's Hand enum).
    public static bool IsTracked(int hand) { Refresh(); return _hands[hand].Tracked; }

    public static bool RootPoseOf(int hand, out Vector3 pos, out Quaternion rot)
    {
        Refresh();
        pos = _hands[hand].Pos; rot = _hands[hand].Rot;
        return _hands[hand].Tracked;
    }

    public static bool PointerPoseOf(int hand, out Vector3 pos, out Quaternion rot)
    {
        Refresh();
        pos = _hands[hand].PointerPos; rot = _hands[hand].PointerRot;
        return _hands[hand].Tracked && _hands[hand].Pointer;
    }

    // 0..1: the index finger's pinch strength.
    public static float Pinch(int hand) { Refresh(); return _hands[hand].Tracked ? _hands[hand].Pinch : 0f; }

    // 0 (open hand) .. 1 (fist): the mean curl of the index, middle and ring fingers.
    public static float Fist(int hand) { Refresh(); return _hands[hand].Tracked ? _hands[hand].Fist : 0f; }

    static void Refresh()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        for (int h = 0; h < 2; h++) _hands[h].Tracked = false;
        if (_broken) return;
        try
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(StateBytes);
            for (int h = 0; h < 2; h++) Read(h);
        }
        catch (Exception e)   // DllNotFound / EntryPointNotFound: no hand tracking, controllers only
        {
            _broken = true;
            Debug.Log("[TFTF] VR hand tracking unavailable: " + e.Message);
        }
    }

    static void Read(int h)
    {
        if (ovrp_GetHandState(StepRender, h, _buf) < 0) return;   // ovrpResult: failures are negative
        int status = Marshal.ReadInt32(_buf, 0);
        Marshal.Copy(_buf, _f, 0, _f.Length);
        if ((status & Tracked) == 0) return;
        Hand s = new Hand { Tracked = true, Pointer = (status & InputValid) != 0 };
        s.Rot = Rot(RootPose); s.Pos = Pos(RootPose + 4);
        s.PointerRot = Rot(PointerPose); s.PointerPos = Pos(PointerPose + 4);
        s.Pinch = Mathf.Clamp01(_f[PinchStrength + 1]);
        // Fingers 6-8 index, 9-11 middle, 12-14 ring (OVR skeleton bone ids); the first two joints of each bend in a fist.
        float bend = 0f;
        for (int b = 6; b <= 12; b += 3) bend += Angle(b) + Angle(b + 1);
        s.Fist = Mathf.Clamp01((bend / 6f - 25f) / 55f);
        _hands[h] = s;
    }

    // OVR space is right-handed: z and the rotation's x and y flip into Unity's.
    static Vector3 Pos(int i) { return new Vector3(_f[i], _f[i + 1], -_f[i + 2]); }
    static Quaternion Rot(int i) { return new Quaternion(-_f[i], -_f[i + 1], _f[i + 2], _f[i + 3]); }

    static float Angle(int bone)
    {
        int i = Bones + bone * 4;
        float w = Mathf.Clamp(Mathf.Abs(_f[i + 3]), 0f, 1f);
        return 2f * Mathf.Acos(w) * Mathf.Rad2Deg;
    }
}
#endif
