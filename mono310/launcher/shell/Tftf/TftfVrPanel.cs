// Newly authored. VR build only. The launcher's own screens (first-run screen, splash) draw with IMGUI, which the
// headset never shows; in VR they draw through this panel instead: a world-space canvas laid over the VR screen, filled
// each frame between Begin and End in the same rectangles (pixels of the 1920x1080 screen, origin top left) and clicked
// with the controller laser.
#if TFTF_VR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class TftfVrPanel : MonoBehaviour
{
    readonly List<Graphic> _items = new List<Graphic>();
    int _used;
    Font _font;
    RectTransform _root;

    public static TftfVrPanel Create(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = TftfVr.Layer;
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)go.transform;
        rt.SetParent(TftfVr.ScreenTransform, false);
        rt.sizeDelta = new Vector2(TftfVr.Width, TftfVr.Height);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localPosition = new Vector3(0, 0, -0.002f);            // just in front of the screen quad
        rt.localScale = new Vector3(1f / TftfVr.Width, 1f / TftfVr.Height, 1);
        var p = go.AddComponent<TftfVrPanel>();
        p._root = rt;
        p._font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return p;
    }

    public void Begin() { _used = 0; }

    public void End()
    {
        for (int i = _used; i < _items.Count; i++) _items[i].gameObject.SetActive(false);
    }

    T Next<T>() where T : Graphic
    {
        while (_used < _items.Count && !(_items[_used] is T))
        {
            Destroy(_items[_used].gameObject);                   // the screen changed shape: rebuild from here
            _items.RemoveAt(_used);
        }
        if (_used == _items.Count)
        {
            var go = new GameObject("item", typeof(RectTransform));
            go.layer = TftfVr.Layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(_root, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            var g = go.AddComponent<T>();
            g.raycastTarget = false;
            if (g is Text) { ((Text)(Graphic)g).font = _font; g.material = TftfVr.UiTextMaterial; }
            else g.material = TftfVr.UiMaterial;
            _items.Add(g);
        }
        var item = (T)_items[_used++];
        item.gameObject.SetActive(true);
        item.transform.SetAsLastSibling();
        return item;
    }

    static void Place(Graphic g, Rect r)
    {
        var rt = g.rectTransform;
        rt.anchoredPosition = new Vector2(r.x, -r.y);
        rt.sizeDelta = new Vector2(r.width, r.height);
    }

    public void Image(Rect r, Texture tex, Color color)
    {
        var g = Next<RawImage>();
        g.texture = tex != null ? tex : Texture2D.whiteTexture;
        g.color = color;
        Place(g, r);
    }

    public void Label(Rect r, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = false)
    {
        var g = Next<Text>();
        g.text = text;
        g.fontSize = size;
        g.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        g.alignment = anchor;
        g.horizontalOverflow = HorizontalWrapMode.Wrap;
        g.verticalOverflow = VerticalWrapMode.Overflow;
        g.color = color;
        Place(g, r);
    }

    public static bool Hover(Rect r)
    {
        if (!TftfVr.PointerOnScreen && !TftfVr.PointerUp) return false;
        Vector2 p = TftfVr.PointerPixel;
        return r.Contains(new Vector2(p.x, TftfVr.Height - p.y));
    }

    // Clicked when the trigger is released over the button.
    public bool Button(Rect r, string text, int size, bool enabled)
    {
        bool hover = enabled && Hover(r);
        Color face = !enabled ? new Color(0.18f, 0.18f, 0.2f) : hover && TftfVr.PointerDown ? new Color(0.85f, 0.42f, 0.08f)
            : hover ? new Color(0.36f, 0.38f, 0.44f) : new Color(0.25f, 0.27f, 0.32f);
        Image(r, null, face);
        Label(r, text, size, enabled ? Color.white : new Color(0.55f, 0.55f, 0.55f));
        return hover && TftfVr.PointerUp;
    }
}
#endif
