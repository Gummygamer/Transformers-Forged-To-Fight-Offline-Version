// Newly authored. Online Arena (see ../../ONLINE_PVP.md): pumps the connection every frame and shows the in-fight banners
// (connection state, waiting for the opponent's pick, AI takeover). IMGUI in the game's Tecnica font, 540 units high. The Arena
// button and panel are NGUI, in TftfMain/TftfPvpArenaUi.cs. Not shown in the VR build (it draws the game into a virtual screen).
using UnityEngine;

public sealed class TftfPvpPanel : MonoBehaviour
{
    GUIStyle _label, _small;
    Font _font;
    float _fontLookAt = -100f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
#if !TFTF_VR
        var go = new GameObject("TftfPvpPanel");
        DontDestroyOnLoad(go);
        go.AddComponent<TftfPvpPanel>();
#endif
    }

    void Update() { TftfNet.Pump(); }

    // The game's text font, once its UI has loaded it.
    void FindFont()
    {
        if (_font != null || Time.realtimeSinceStartup - _fontLookAt < 2f) return;
        _fontLookAt = Time.realtimeSinceStartup;
        foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            if (f.name.StartsWith("Tecnica_Bold")) { _font = f; break; }
        _label = null;
    }

    void OnGUI()
    {
        bool fight = TftfNet.LockstepActive;
        bool lost = Time.realtimeSinceStartup - TftfNet.LostAt < 5f;
        if (!fight && !lost && !TftfNet.Picking) return;
        FindFont();
        if (_label == null)
        {
            _label = new GUIStyle(GUI.skin.label) { fontSize = 24, wordWrap = true, alignment = TextAnchor.MiddleCenter, font = _font };
            _small = new GUIStyle(_label) { fontSize = 18 };
        }
        float s = Screen.height / 540f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
        float w = Screen.width / s, h = Screen.height / s;
        lost = lost && !fight;

        string text;
        if (lost) text = TftfNet.Message.Length > 0 ? TftfNet.Message + " The AI fights for them." : "The AI fights for the opponent.";
        else if (TftfNet.Picking) text = "Waiting for " + TftfNet.PeerName + " to pick a bot...";
        else if (TftfNet.StalledFor > 0.4f) text = "Waiting for " + TftfNet.PeerName + "...";
        else text = TftfNet.PeerName + (TftfNet.PingMs >= 0 ? "  " + TftfNet.PingMs + " ms" : "");
        bool big = lost || TftfNet.Picking || TftfNet.StalledFor > 0.4f;
        var r = big ? new Rect(w * 0.5f - 240, h * 0.4f, 480, 60) : new Rect(w * 0.5f - 110, h - 30, 220, 26);
        if (big)
        {
            // the game's banner look: a dark band with a thin light rule above and below
            var old = GUI.color;
            GUI.color = new Color(0.02f, 0.04f, 0.06f, 0.88f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), Texture2D.whiteTexture);
            GUI.color = old;
        }
        GUI.Label(r, text.ToUpperInvariant(), big ? _label : _small);
    }
}
