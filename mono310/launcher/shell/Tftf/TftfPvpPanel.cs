// Newly authored. The online Arena panel (see ../../ONLINE_PVP.md): an "Online" button on the Arena screen opens a panel
// to choose the relay server and room and to find an opponent; during an online fight it shows the connection state.
// IMGUI, 540 units high like the first-run screen. Not shown in the VR build (it draws the game into a virtual screen).
using UnityEngine;

public sealed class TftfPvpPanel : MonoBehaviour
{
    bool _open;
    string _address, _room;
    TouchScreenKeyboard _keyboard;
    int _editing;                       // 1 address, 2 room
    Behaviour[] _blocked;               // the game's NGUI input cameras, disabled while the panel is open
    GUIStyle _label, _small, _button, _field, _box;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
#if !TFTF_VR
        var go = new GameObject("TftfPvpPanel");
        DontDestroyOnLoad(go);
        go.AddComponent<TftfPvpPanel>();
#endif
    }

    void Update()
    {
        TftfNet.Pump();
        if (_keyboard != null)
        {
            if (_keyboard.status == TouchScreenKeyboard.Status.Done)
            {
                if (_editing == 1) _address = _keyboard.text.Trim();
                else _room = _keyboard.text.Trim();
                _keyboard = null;
            }
            else if (_keyboard.status != TouchScreenKeyboard.Status.Visible) _keyboard = null;
        }
        if (_open && (!TftfArenaMarker.Visible || TftfNet.LockstepActive || Time.frameCount - TftfNet.FightFrame < 5)) SetOpen(false);
    }

    void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (open)
        {
            _address = TftfNet.Address;
            _room = TftfNet.Room;
            // taps on the panel must not reach the Arena screen underneath
            var list = new System.Collections.Generic.List<Behaviour>();
            foreach (var cam in Camera.allCameras)
            {
                var ui = cam.GetComponent("UICamera") as Behaviour;
                if (ui != null && ui.enabled) { ui.enabled = false; list.Add(ui); }
            }
            _blocked = list.ToArray();
        }
        else
        {
            Save();
            if (_blocked != null)
                foreach (var ui in _blocked)
                    if (ui != null) ui.enabled = true;
            _blocked = null;
            _keyboard = null;
        }
    }

    void Save()
    {
        if (_address != null) TftfNet.Address = _address;
        if (_room != null) TftfNet.Room = _room;
        PlayerPrefs.Save();
    }

    void OnGUI()
    {
        bool fight = TftfNet.LockstepActive;
        bool lost = Time.realtimeSinceStartup - TftfNet.LostAt < 5f;
        if (!_open && !fight && !lost && !TftfNet.Picking && !TftfArenaMarker.Visible) return;
        if (_label == null)
        {
            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true, alignment = TextAnchor.MiddleLeft };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, alignment = TextAnchor.MiddleLeft };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
            _box = new GUIStyle(GUI.skin.box);
        }
        float s = Screen.height / 540f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
        float w = Screen.width / s, h = Screen.height / s;
        if (fight || lost || TftfNet.Picking) { FightStatus(w, h, lost && !fight); return; }
        if (!_open)
        {
            if (Time.frameCount - TftfNet.FightFrame < 5) return;
            string text = TftfNet.Connected ? "Online: " + TftfNet.PeerName : TftfNet.Status == TftfNet.State.Waiting ? "Online: searching..." : "Online PVP";
            if (GUI.Button(new Rect(w * 0.5f - 110, h - 64, 220, 44), text, _button)) SetOpen(true);
            return;
        }
        Panel(w, h);
    }

    void FightStatus(float w, float h, bool lost)
    {
        string text;
        if (lost) text = TftfNet.Message.Length > 0 ? TftfNet.Message + " The AI fights for them." : "The AI fights for the opponent.";
        else if (TftfNet.Picking) text = "Waiting for " + TftfNet.PeerName + " to pick a bot...";
        else if (TftfNet.StalledFor > 0.4f) text = "Waiting for " + TftfNet.PeerName + "...";
        else text = TftfNet.PeerName + (TftfNet.PingMs >= 0 ? "  " + TftfNet.PingMs + " ms" : "");
        bool big = lost || TftfNet.Picking || TftfNet.StalledFor > 0.4f;
        var r = big ? new Rect(w * 0.5f - 220, h * 0.4f, 440, 56) : new Rect(w * 0.5f - 110, h - 30, 220, 26);
        if (big) GUI.Box(r, GUIContent.none, _box);
        var style = big ? _label : _small;
        var old = style.alignment;
        style.alignment = TextAnchor.MiddleCenter;
        GUI.Label(r, text, style);
        style.alignment = old;
    }

    void Panel(float w, float h)
    {
        float pw = Mathf.Min(620, w - 32), ph = 460;
        float x = (w - pw) * 0.5f, y = (h - ph) * 0.5f;
        GUI.Box(new Rect(x - 8, y - 8, pw + 16, ph + 16), GUIContent.none, _box);
        GUI.Box(new Rect(x - 8, y - 8, pw + 16, ph + 16), GUIContent.none, _box);     // twice: the default skin is translucent
        GUI.Label(new Rect(x, y, pw, 32), "Online PVP", _label);
        bool idle = TftfNet.Status == TftfNet.State.Off || TftfNet.Status == TftfNet.State.Failed;

        GUI.Label(new Rect(x, y + 40, pw, 24), "Relay server (address or address:port, default port " + TftfNet.DefaultPort + ")", _small);
        _address = Field(new Rect(x, y + 66, pw, 44), _address, 1, idle, TouchScreenKeyboardType.URL);
        var recent = TftfNet.RecentAddresses;
        float rx = x;
        for (int i = 0; i < recent.Length && idle; i++)
        {
            if (recent[i] == _address || recent[i].Length == 0) continue;
            float bw = Mathf.Min(200, pw - (rx - x));
            if (bw < 80) break;
            if (GUI.Button(new Rect(rx, y + 116, bw, 32), recent[i], _small)) _address = recent[i];
            rx += bw + 8;
        }

        GUI.Label(new Rect(x, y + 156, pw, 24), "Room code (players with the same code are matched; empty = anyone)", _small);
        _room = Field(new Rect(x, y + 182, pw, 44), _room, 2, idle, TouchScreenKeyboardType.Default);

        string info = TftfNet.Message.Length > 0 ? TftfNet.Message
            : "Both players open this Arena, connect to the same relay and room, then start a match. Each fight starts when both have picked a bot.";
        if (TftfRelay.Running)
        {
            string ip = HostAddress();
            info = "This phone is the relay: others enter " + (ip.Length > 0 ? ip : "this phone's Wi-Fi address")
                   + (TftfRelay.Port == TftfNet.DefaultPort ? "" : ":" + TftfRelay.Port) + " (same Wi-Fi, or port " + TftfRelay.Port
                   + " forwarded to this phone). " + (TftfRelay.Pairs > 0 ? TftfRelay.Pairs + " match(es) running. " : "") + "\n" + info;
        }
        else if (TftfRelay.Error.Length > 0) info = TftfRelay.Error + "\n" + info;
        GUI.Label(new Rect(x, y + 234, pw, 110), info, _small);

        if (TftfRelay.Running)
        {
            if (GUI.Button(new Rect(x, y + ph - 112, pw, 48), "Stop hosting", _button))
            {
                TftfNet.Disconnect();
                TftfRelay.Stop();
            }
        }
        else if (idle && GUI.Button(new Rect(x, y + ph - 112, pw, 48), "Host on this phone (others connect to it)", _button))
        {
            Save();
            if (TftfRelay.Start(HostPort())) TftfNet.Connect("127.0.0.1:" + TftfRelay.Port);
        }

        float by = y + ph - 56, bw2 = (pw - 16) / 3f;
        if (idle)
        {
            if (GUI.Button(new Rect(x, by, bw2 * 2 + 8, 52), "Find opponent", _button))
            {
                Save();
                if (TftfRelay.Running) TftfNet.Connect("127.0.0.1:" + TftfRelay.Port);     // the host plays on its own relay
                else TftfNet.Connect();
            }
        }
        else if (GUI.Button(new Rect(x, by, bw2 * 2 + 8, 52), TftfNet.Connected ? "Leave online play" : "Cancel", _button))
            TftfNet.Disconnect();
        if (GUI.Button(new Rect(x + (bw2 + 8) * 2, by, bw2, 52), "Close", _button)) SetOpen(false);
    }

    // The port to host on: the one in the relay address field if it has one, else the default.
    int HostPort()
    {
        int port;
        string a = _address ?? "";
        int colon = a.LastIndexOf(':');
        return colon > 0 && int.TryParse(a.Substring(colon + 1), out port) && port > 0 && port < 65536 ? port : TftfNet.DefaultPort;
    }

    string _hostIp;
    float _hostIpAt = -100f;

    string HostAddress()
    {
        if (Time.realtimeSinceStartup - _hostIpAt > 5f)
        {
            _hostIp = TftfRelay.LocalAddress();
            _hostIpAt = Time.realtimeSinceStartup;
        }
        return _hostIp;
    }

    string Field(Rect r, string value, int id, bool editable, TouchScreenKeyboardType type)
    {
        value = value ?? "";
        if (!editable)
        {
            GUI.Label(r, value, _field);
            return value;
        }
        if (TouchScreenKeyboard.isSupported)
        {
            if (GUI.Button(r, value.Length == 0 ? "(tap to enter)" : value, _field))
            {
                _editing = id;
                _keyboard = TouchScreenKeyboard.Open(value, type, false, false, false, false);
            }
            return value;
        }
        return GUI.TextField(r, value, 64, _field);
    }
}
