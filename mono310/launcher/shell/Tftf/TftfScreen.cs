// Newly authored. Screen size and backbuffer target the game sees (compat310.py P53 routes its Screen.width/height reads
// and its backbuffer blits through here in the VR build). Outside VR, or before the VR rig exists, it is the real screen.
using UnityEngine;

public static class TftfScreen
{
#if TFTF_VR
    public static int width { get { return TftfVr.Screen2D != null ? TftfVr.Width : Screen.width; } }

    public static int height { get { return TftfVr.Screen2D != null ? TftfVr.Height : Screen.height; } }

    public static RenderTexture Target { get { return TftfVr.CompositeTarget; } }
#else
    public static int width { get { return Screen.width; } }

    public static int height { get { return Screen.height; } }

    public static RenderTexture Target { get { return null; } }
#endif
}
