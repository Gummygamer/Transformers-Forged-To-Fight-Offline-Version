// Newly authored. The online Arena button and panel (see ../../ONLINE_PVP.md), built from the game's own NGUI pieces: the
// Arena card's button sprites (btnMain, btn2nd, Common_box) and fonts (Vox_Bold titles, Tecnica_Bold text). TftfNetGlue.MarkArena
// adds it to the Arena screen; the button sits bottom right, clear of the arena card's ENTER / VIEW DETAILS. In-fight banners stay
// in TftfPvpPanel (first pass). Compiled into Assembly-CSharp. In the VR build the texts say headset instead of phone.
using UnityEngine;

public class TftfPvpArenaUi : MonoBehaviour
{
    const int PanelW = 760, PanelH = 560, Pad = 24;
#if TFTF_VR
    const string Device = "headset";
#else
    const string Device = "phone";
#endif

    UIAtlas _atlas;
    Font _textFont, _titleFont;
    Color _cardColor = new Color(0.04f, 0.07f, 0.09f, 1f), _dividerColor = new Color(1f, 1f, 1f, 0.5f);
    GameObject _root, _panel;
    UISprite _button;
    UILabel _buttonLabel, _info, _hostLabel, _findLabel;
    GameObject _host, _recentRow;
    UIInput _address, _room;
    UISprite _findBg;
    UISprite _details;
    bool _open, _built;
    string _lastRecent;

    void Update()
    {
        if (!_built && !Build()) return;
        bool fight = TftfNet.LockstepActive || Time.frameCount - TftfNet.FightFrame < 5;
        if (fight && _open) SetOpen(false);
        _button.gameObject.SetActive(!fight && !_open);
        Place();
        _buttonLabel.text = TftfNet.Connected ? "ONLINE: " + TftfNet.PeerName.ToUpperInvariant()
            : TftfNet.Status == TftfNet.State.Waiting ? "SEARCHING..." : "ONLINE PVP";
        if (_open) Refresh();
    }

    void OnDisable() { if (_open) SetOpen(false); }

    void OnDestroy() { if (_root != null) Destroy(_root); }

    // Takes the atlas, fonts and colours from the Arena card once it exists.
    bool Build()
    {
        foreach (var s in GetComponentsInChildren<UISprite>(true))
        {
            if (s.atlas == null) continue;
            if (_atlas == null) _atlas = s.atlas;
            if (s.spriteName == "CommonSquare" && s.name == "Bg") _cardColor = s.color;
            if (s.spriteName == "CommonSquare" && s.name.StartsWith("Divider")) _dividerColor = s.color;
        }
        foreach (var l in GetComponentsInChildren<UILabel>(true))
        {
            if (l.trueTypeFont == null) continue;
            if (_textFont == null && l.trueTypeFont.name.StartsWith("Tecnica")) _textFont = l.trueTypeFont;
            if (_titleFont == null && l.trueTypeFont.name.StartsWith("Vox")) _titleFont = l.trueTypeFont;
        }
        if (_atlas == null || _textFont == null) return false;
        if (_titleFont == null) _titleFont = _textFont;
        _built = true;

        int depth = 0;
        foreach (var p in GetComponentsInChildren<UIPanel>(true)) depth = Mathf.Max(depth, p.depth);
        _root = NGUITools.AddChild(gameObject);
        _root.name = "TftfPvp";
        _root.layer = gameObject.layer;
        _root.AddComponent<UIPanel>().depth = depth + 10;

        _button = Sprite(_root, "btn2nd", 280, 45, 1);
        _buttonLabel = Label(_button.gameObject, "ONLINE PVP", _textFont, 25, 260, 40, 2, NGUIText.Alignment.Center);
        _buttonLabel.alpha = 0.8f;
        Clickable(_button, delegate { SetOpen(true); });

        _panel = NGUITools.AddChild(_root);
        _panel.layer = gameObject.layer;
        var dim = Sprite(_panel, "CommonSquare", 4000, 4000, 10);
        dim.color = new Color(0f, 0f, 0f, 0.75f);
        NGUITools.AddWidgetCollider(dim.gameObject);                    // swallows taps meant for the Arena screen underneath
        Sprite(_panel, "CommonSquare", PanelW, PanelH, 11).color = new Color(_cardColor.r, _cardColor.g, _cardColor.b, 1f);

        const int inner = PanelW - 2 * Pad;
        float left = -PanelW / 2 + Pad, top = PanelH / 2 - Pad;
        Pos(Label(_panel, "ONLINE PVP", _titleFont, 34, inner, 40, 12, NGUIText.Alignment.Left), 0, top - 20);
        Divider(top - 46, inner);

        Section("RELAY SERVER", "ADDRESS OR ADDRESS:PORT  -  DEFAULT PORT " + TftfNet.DefaultPort, top - 64, inner);
        _address = Field(top - 148, inner, UIInput.KeyboardType.URL);
        _recentRow = NGUITools.AddChild(_panel);
        _recentRow.layer = gameObject.layer;
        Pos(_recentRow, left, top - 196);

        Section("ROOM CODE", "SAME CODE = SAME MATCH  -  EMPTY MATCHES ANYONE", top - 222, inner);
        _room = Field(top - 306, inner, UIInput.KeyboardType.Default);

        _info = Label(_panel, "", _textFont, 21, inner, 92, 12, NGUIText.Alignment.Left);
        _info.overflowMethod = UILabel.Overflow.ShrinkContent;
        _info.alpha = 0.7f;
        Pos(_info, 0, top - 390);

        float by = -PanelH / 2 + Pad + 29, bw = (inner - 2 * 16) / 3f;
        var host = Sprite(_panel, "btn2nd", (int)bw, 52, 12);
        _host = host.gameObject;
        Pos(host, left + bw / 2, by);
        _hostLabel = Label(_host, "", _textFont, 24, (int)bw - 16, 44, 13, NGUIText.Alignment.Center);
        Clickable(host, delegate { HostClicked(); });

        _findBg = Sprite(_panel, "btnMain", (int)bw, 52, 12);
        Pos(_findBg, left + bw * 1.5f + 16, by);
        _findLabel = Label(_findBg.gameObject, "", _textFont, 26, (int)bw - 16, 44, 13, NGUIText.Alignment.Center);
        Clickable(_findBg, delegate { FindClicked(); });

        var close = Sprite(_panel, "btn2nd", (int)bw, 52, 12);
        Pos(close, left + bw * 2.5f + 32, by);
        Label(close.gameObject, "CLOSE", _textFont, 24, (int)bw - 16, 44, 13, NGUIText.Alignment.Center);
        Clickable(close, delegate { SetOpen(false); });

        _panel.SetActive(false);
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
        _button.transform.localPosition = new Vector3(br.x - 48 - _button.width / 2f, y, 0f);
    }

    void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (open)
        {
            _address.value = TftfNet.Address;
            _room.value = TftfNet.Room;
            _lastRecent = null;
        }
        else Save();
        _panel.SetActive(open);
    }

    void Save()
    {
        TftfNet.Address = _address.value.Trim();
        TftfNet.Room = _room.value.Trim();
        PlayerPrefs.Save();
    }

    bool Idle { get { return TftfNet.Status == TftfNet.State.Off || TftfNet.Status == TftfNet.State.Failed; } }

    void HostClicked()
    {
        if (TftfRelay.Running)
        {
            TftfNet.Disconnect();
            TftfRelay.Stop();
        }
        else if (Idle)
        {
            Save();
            if (TftfRelay.Start(HostPort())) TftfNet.Connect("127.0.0.1:" + TftfRelay.Port);
        }
    }

    void FindClicked()
    {
        if (!Idle)
        {
            TftfNet.Disconnect();
            return;
        }
        Save();
        if (TftfRelay.Running) TftfNet.Connect("127.0.0.1:" + TftfRelay.Port);     // the host plays on its own relay
        else TftfNet.Connect();
    }

    void Refresh()
    {
        bool idle = Idle;
        _address.GetComponent<Collider>().enabled = idle;
        _room.GetComponent<Collider>().enabled = idle;
        _host.SetActive(idle || TftfRelay.Running);
        _hostLabel.text = TftfRelay.Running ? "STOP HOSTING" : "HOST ON THIS " + Device.ToUpperInvariant();
        _findLabel.text = idle ? "FIND OPPONENT" : TftfNet.Connected ? "LEAVE" : "CANCEL";

        string info = TftfNet.Message.Length > 0 ? TftfNet.Message
            : "Both players open this Arena, connect to the same relay and room, then start a match. Each fight starts when both have picked a bot.";
        if (TftfRelay.Running)
        {
            string ip = HostAddress();
            info = "This " + Device + " is the relay: others enter " + (ip.Length > 0 ? ip : "this " + Device + "'s Wi-Fi address")
                   + (TftfRelay.Port == TftfNet.DefaultPort ? "" : ":" + TftfRelay.Port) + " (same Wi-Fi, or port " + TftfRelay.Port
                   + " forwarded to this " + Device + "). " + (TftfRelay.Pairs > 0 ? TftfRelay.Pairs + " match(es) running. " : "") + "\n" + info;
        }
        else if (TftfRelay.Error.Length > 0) info = TftfRelay.Error + "\n" + info;
        _info.text = info;

        string recent = idle ? string.Join("\n", TftfNet.RecentAddresses) + "\n" + _address.value : "";
        if (recent != _lastRecent)
        {
            _lastRecent = recent;
            BuildRecent(idle);
        }
    }

    // Up to three earlier relay addresses as small buttons under the address field.
    void BuildRecent(bool idle)
    {
        for (int i = _recentRow.transform.childCount - 1; i >= 0; i--) Destroy(_recentRow.transform.GetChild(i).gameObject);
        if (!idle) return;
        float x = 0;
        int shown = 0;
        foreach (var a in TftfNet.RecentAddresses)
        {
            if (a.Length == 0 || a == _address.value.Trim() || shown == 3) continue;
            var chip = Sprite(_recentRow, "btn2nd", 220, 34, 12);
            Pos(chip, x + 110, 0);
            var l = Label(chip.gameObject, a, _textFont, 20, 204, 30, 13, NGUIText.Alignment.Center);
            l.alpha = 0.8f;
            string addr = a;
            Clickable(chip, delegate { _address.value = addr; });
            x += 232;
            shown++;
        }
    }

    // The port to host on: the one in the relay address field if it has one, else the default.
    int HostPort()
    {
        int port;
        string a = _address.value ?? "";
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

    // ---- widgets ----

    void Section(string title, string hint, float y, int width)
    {
        Pos(Label(_panel, title, _textFont, 27, width, 32, 12, NGUIText.Alignment.Left), 0, y - 16);
        var h = Label(_panel, hint, _textFont, 19, width, 24, 12, NGUIText.Alignment.Left);
        h.alpha = 0.7f;
        Pos(h, 0, y - 44);
    }

    void Divider(float y, int width)
    {
        var d = Sprite(_panel, "CommonSquare", width, 2, 12);
        d.color = _dividerColor;
        Pos(d, 0, y);
    }

    UIInput Field(float y, int width, UIInput.KeyboardType keyboard)
    {
        var bg = Sprite(_panel, "Common_box", width, 50, 12);    // a white sprite the game tints; dark like its input wells
        bg.color = new Color(0f, 0f, 0f, 0.55f);
        Pos(bg, 0, y);
        Divider(y - 25, width);
        var label = Label(bg.gameObject, "TAP TO ENTER", _textFont, 26, width - 24, 40, 13, NGUIText.Alignment.Left);
        label.overflowMethod = UILabel.Overflow.ClampContent;
        var input = bg.gameObject.AddComponent<UIInput>();
        input.label = label;
        input.keyboardType = keyboard;
        input.characterLimit = 64;
        input.onReturnKey = UIInput.OnReturnKey.Submit;
        NGUITools.AddWidgetCollider(bg.gameObject);
        return input;
    }

    UISprite Sprite(GameObject parent, string name, int w, int h, int depth)
    {
        var go = NGUITools.AddChild(parent);
        go.layer = gameObject.layer;
        go.name = name;
        var s = go.AddComponent<UISprite>();
        s.atlas = _atlas;
        s.spriteName = name;
        s.type = UIBasicSprite.Type.Sliced;
        s.width = w;
        s.height = h;
        s.depth = depth;
        return s;
    }

    UILabel Label(GameObject parent, string text, Font font, int size, int w, int h, int depth, NGUIText.Alignment align)
    {
        var go = NGUITools.AddChild(parent);
        go.layer = gameObject.layer;
        go.name = "Label";
        var l = go.AddComponent<UILabel>();
        l.trueTypeFont = font;
        l.fontSize = size;
        l.alignment = align;
        l.overflowMethod = UILabel.Overflow.ShrinkContent;
        l.width = w;
        l.height = h;
        l.depth = depth;
        l.text = text;
        return l;
    }

    void Clickable(UISprite s, System.Action click)
    {
        NGUITools.AddWidgetCollider(s.gameObject);
        var colour = s.gameObject.AddComponent<UIButtonColor>();
        colour.tweenTarget = s.gameObject;
        UIEventListener.Get(s.gameObject).onClick = delegate { click(); };
    }

    static void Pos(Component c, float x, float y) { Pos(c.gameObject, x, y); }

    static void Pos(GameObject go, float x, float y) { go.transform.localPosition = new Vector3(x, y, 0f); }
}
