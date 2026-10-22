// Newly authored. The revival splash: shown full screen right after the Kabam logo, before the title screen
// (BootFlow calls TftfSplash.Show(), patched in by compat310.py P44). Big logo plus "Unicron's Lair".
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public sealed class TftfSplash : MonoBehaviour
{
    public const string Title = "UNICRON'S LAIR";
    const float Hold = 2.4f, Fade = 0.5f;       // seconds fully visible, seconds of fade in and out

    Texture2D _logo;
    GUIStyle _style;
    float _alpha;

    public static IEnumerator Show()
    {
        var go = new GameObject("TftfSplash");
        DontDestroyOnLoad(go);
        var splash = go.AddComponent<TftfSplash>();
        yield return splash.Run();
        if (splash._logo != null) Destroy(splash._logo);
#if TFTF_VR
        if (splash._panel != null) Destroy(splash._panel.gameObject);
#endif
        Destroy(go);
    }

    IEnumerator Run()
    {
        var req = UnityWebRequestTexture.GetTexture(TftfLauncher.StreamUrl("revival-splash.png"));
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) yield break;      // no splash is better than a broken one
        _logo = DownloadHandlerTexture.GetContent(req);
        for (float t = 0; t < Fade; t += Time.unscaledDeltaTime) { _alpha = t / Fade; yield return null; }
        _alpha = 1;
        float held = 0;
        while (held < Hold && !(held > 0.6f && TftfInput.touchCount > 0))      // a tap skips it after a moment
        {
            held += Time.unscaledDeltaTime; yield return null;
        }
        for (float t = 0; t < Fade; t += Time.unscaledDeltaTime) { _alpha = 1 - t / Fade; yield return null; }
        _alpha = 0;
    }

#if TFTF_VR
    TftfVrPanel _panel;

    // The headset does not show IMGUI: the same picture on the VR screen.
    void LateUpdate()
    {
        if (_panel == null) _panel = TftfVrPanel.Create("TftfSplashPanel");
        _panel.Begin();
        if (_logo != null && _alpha > 0)
        {
            float w = TftfVr.Width, h = TftfVr.Height, size = h * 0.74f;
            _panel.Image(new Rect(0, 0, w, h), null, new Color(0, 0, 0, _alpha));
            _panel.Image(new Rect((w - size) * 0.5f, h * 0.03f, size, size), _logo, new Color(1, 1, 1, _alpha));
            _panel.Label(new Rect(0, h * 0.78f, w, h * 0.18f), Title, Mathf.RoundToInt(h * 0.075f), new Color(1f, 0.55f, 0.12f, _alpha), TextAnchor.MiddleCenter, true);
        }
        _panel.End();
    }
#else
    void OnGUI()
    {
        if (_logo == null || _alpha <= 0) return;
        if (_style == null)
            _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        float w = Screen.width, h = Screen.height;
        GUI.color = new Color(1, 1, 1, _alpha);
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.blackTexture);
        float size = h * 0.74f;
        GUI.DrawTexture(new Rect((w - size) * 0.5f, h * 0.03f, size, size), _logo, ScaleMode.ScaleToFit);
        _style.fontSize = Mathf.RoundToInt(h * 0.075f);
        _style.normal.textColor = new Color(1f, 0.55f, 0.12f, _alpha);
        GUI.Label(new Rect(0, h * 0.78f, w, h * 0.18f), Title, _style);
    }
#endif
}
