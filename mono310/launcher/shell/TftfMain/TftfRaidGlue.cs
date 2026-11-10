// Start asynchronous raid discovery from home, independently of the Arena's matchmaking and lockstep state.
using System.IO;
using EB.Sparx;
using UnityEngine;

public sealed class TftfRaidGlue : MonoBehaviour
{
    static TftfRaidGlue _instance;
    float _next;
    bool _started, _reported;
    PVPRaidSelectScreen _raidScreen;
    int _reports;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (_instance != null) return;
        var go = new GameObject("TftfRaidGlue"); Object.DontDestroyOnLoad(go); _instance = go.AddComponent<TftfRaidGlue>();
    }
    void Update()
    {
        if (Time.unscaledTime < _next) return; _next = Time.unscaledTime + 2f;
        if (!_started && Object.FindObjectOfType<TransformersHomeScreen>() != null)
        {
            if (TftfMesh.CachePath == null) TftfMesh.CachePath = Path.Combine(Application.persistentDataPath, "tftf_p2p_peers.txt");
            TftfRaidNet.DisplayName = SocialUtils.GetLocalUserName();
            TftfRaidNet.Start(Path.Combine(Application.persistentDataPath, "tftf"), TftfNet.Bootstrap);
            _started = true;
        }
        _raidScreen = Object.FindObjectOfType<PVPRaidSelectScreen>();
        if (_started)
        {
            TftfRaidNet.DisplayName = SocialUtils.GetLocalUserName();
            if (Hub.Instance != null && Hub.Instance.StatsManager != null)
            {
                Hub.Instance.StatsManager.UserIntStats["sxp"] = TftfRaidNet.OwnShield;
                Hub.Instance.StatsManager.UserIntStats["bpi"] = TftfRaidNet.OwnPower;
            }
            if (_reports != TftfRaidNet.ReportsChanged && Quests.RaidManager.Instance != null)
            {
                _reports = TftfRaidNet.ReportsChanged;
                Quests.RaidManager.Instance.RefreshNow(true);
            }
        }
        if (!_reported && TftfRaidNet.Uid.Length > 0) { Debug.Log("[RAIDNET] signing self-test passed, uid=" + TftfRaidNet.Uid); _reported = true; }
    }
    void OnApplicationPause(bool paused) { TftfRaidNet.Paused = paused; }
    void OnApplicationQuit() { TftfRaidNet.Stop(); }
    void OnGUI()
    {
        if (!_started || _raidScreen == null) return;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = Mathf.Max(12, Screen.height / 40) };
        GUI.Label(new Rect(Screen.width * 0.5f, Screen.height * 0.08f, Screen.width * 0.47f, Screen.height * 0.04f), TftfRaidNet.Status, style);
    }
}
