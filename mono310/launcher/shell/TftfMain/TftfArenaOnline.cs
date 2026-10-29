// Newly authored. The Arena is an online mode (see ../../ONLINE_PVP.md): while its screen is open the game is in the P2P
// network and looks for an opponent among the other players in an Arena. Once paired, the next match both start is
// played against each other; a match started before an opponent is found is against the AI as before, and stops the
// search (TftfNetGlue.InterceptFightData), so nobody is paired in the middle of a match. A one-line status bottom right,
// in the Arena card's font, says which. TftfNetGlue.MarkArena adds this to the Arena screen. Compiled into Assembly-CSharp.
using UnityEngine;

public class TftfArenaOnline : MonoBehaviour
{
    const float RetryAfter = 5f;                // seconds after a failure or a lost opponent before searching again

    public static float LastOpen = -1000f;      // Time.realtimeSinceStartup the Arena screen was last open
    public static bool Hold;                    // a match against the AI was started: no search until the screen opens again

    GameObject _root;
    UILabel _status;
    UISprite _details;
    float _failedAt = -1f;

    void Update()
    {
        LastOpen = Time.realtimeSinceStartup;
        Search();
        if (_status == null && !Build()) return;
        bool fight = TftfNet.LockstepActive || Time.frameCount - TftfNet.FightFrame < 5;
        _status.gameObject.SetActive(!fight);
        Place();
        _status.text = Text();
    }

    void OnEnable() { Hold = false; }

    void Search()
    {
        if (Hold) return;
        var status = TftfNet.Status;
        if (status == TftfNet.State.Failed && _failedAt < 0f) _failedAt = Time.realtimeSinceStartup;
        if (status == TftfNet.State.Off || status == TftfNet.State.Failed && Time.realtimeSinceStartup - _failedAt > RetryAfter)
        {
            _failedAt = -1f;
            TftfNet.JoinOnline();
        }
    }

    // Leaving the Arena screen before an opponent is found stops the search.
    void OnDisable()
    {
        if (TftfNet.Status == TftfNet.State.Connecting || TftfNet.Status == TftfNet.State.Waiting) TftfNet.Disconnect();
    }

    void OnDestroy() { if (_root != null) Destroy(_root); }

    static string Text()
    {
        switch (TftfNet.Status)
        {
            case TftfNet.State.Matched: return "ONLINE  -  OPPONENT: " + TftfNet.PeerName.ToUpperInvariant();
            case TftfNet.State.Waiting: return "ONLINE  -  LOOKING FOR AN OPPONENT...";
            case TftfNet.State.Failed: return "OFFLINE  -  " + TftfNet.Message.ToUpperInvariant();
            default: return "JOINING THE ONLINE NETWORK...";
        }
    }

    // A label in the Arena card's text font, once the card exists.
    bool Build()
    {
        Font font = null;
        foreach (var l in GetComponentsInChildren<UILabel>(true))
            if (l.trueTypeFont != null && l.trueTypeFont.name.StartsWith("Tecnica")) { font = l.trueTypeFont; break; }
        if (font == null) return false;
        int depth = 0;
        foreach (var p in GetComponentsInChildren<UIPanel>(true)) depth = Mathf.Max(depth, p.depth);
        _root = NGUITools.AddChild(gameObject);
        _root.name = "TftfArenaOnline";
        _root.layer = gameObject.layer;
        _root.AddComponent<UIPanel>().depth = depth + 10;
        var go = NGUITools.AddChild(_root);
        go.layer = gameObject.layer;
        _status = go.AddComponent<UILabel>();
        _status.trueTypeFont = font;
        _status.fontSize = 22;
        _status.alignment = NGUIText.Alignment.Right;
        _status.overflowMethod = UILabel.Overflow.ShrinkContent;
        _status.width = 520;
        _status.height = 30;
        _status.depth = 2;
        _status.alpha = 0.8f;
        return true;
    }

    // Bottom right of the screen, level with the arena card's VIEW DETAILS button when there is one.
    void Place()
    {
        var cam = NGUITools.FindCameraForLayer(gameObject.layer);
        if (cam == null) return;
        Transform t = _root.transform;
        Vector3 br = t.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 0f, 0f)));
        if (_details == null && Time.frameCount % 30 == 0)
            foreach (var s in GetComponentsInChildren<UISprite>())
                if (s.spriteName == "btn2nd" && !s.transform.IsChildOf(t)) { _details = s; break; }
        float y = _details != null ? t.InverseTransformPoint(_details.transform.position).y : br.y + 70;
        _status.transform.localPosition = new Vector3(br.x - 48 - _status.width / 2f, y, 0f);
    }
}
