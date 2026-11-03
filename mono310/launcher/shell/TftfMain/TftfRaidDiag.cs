// Raid diagnostic (RAIDS_EXECUTION_PLAN.md B3). Logs the legacy quest database whenever it changes, so the cause of
// ID_UI_QUESTS_EXPIRED on raid team select can be read from logcat: which sets are in AvailableSets, their category,
// setId and summary keys, and whether GetQuestSetOfQuest("raid", "raid-npc-N") finds a set. Logs only on change.
// Separate from TftfBot.cs (not ours). Compiled into Assembly-CSharp with the rest of TftfMain.
using System.Collections.Generic;
using System.IO;
using System.Text;
using Legacy;
using Quests;
using UnityEngine;

// Board move command for the raid device check (RAIDS_NEXT_STEPS.md step 3). A host script writes
// "<seq> <NORTH|SOUTH|EAST|WEST>" to tftf/raidcmd.txt in the app's persistent data folder. The move goes through
// Legacy.QuestsManager.MoveDirection on the active raid, the same call a tap on the board makes, and each command is
// logged as [RAIDMOVE] seq=.. with its result, so the walk can be scripted without screen taps.
public class TftfRaidDiag : MonoBehaviour
{
    static TftfRaidDiag _inst;
    string _last = "";
    string _lastCmd = "";
    float _next;
    float _nextCmd;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (_inst != null) return;
        GameObject go = new GameObject("TftfRaidDiag");
        Object.DontDestroyOnLoad(go);
        _inst = go.AddComponent<TftfRaidDiag>();
    }

    void Update()
    {
        if (Time.unscaledTime >= _nextCmd)
        {
            _nextCmd = Time.unscaledTime + 1f;
            CheckCommand();
        }
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        string state = Describe();
        if (state == _last) return;
        _last = state;
        Debug.Log("[RAIDDIAG] " + state);
    }

    void CheckCommand()
    {
        string path = Path.Combine(Path.Combine(Application.persistentDataPath, "tftf"), "raidcmd.txt");
        string text;
        try
        {
            if (!File.Exists(path)) return;
            text = File.ReadAllText(path).Trim();
        }
        catch (System.Exception)
        {
            return;
        }
        if (text.Length == 0 || text == _lastCmd) return;
        _lastCmd = text;
        string[] parts = text.Split(' ');
        if (parts.Length < 2) return;
        string seq = parts[0];
        Legacy.QuestsManager.Direction dir;
        if (!System.Enum.TryParse(parts[1], true, out dir))
        {
            Debug.Log("[RAIDMOVE] seq=" + seq + " bad direction " + parts[1]);
            return;
        }
        Quests.ActiveQuest quest = RaidManager.Instance != null ? RaidManager.Instance.activeRaid : null;
        if (quest == null)
        {
            Debug.Log("[RAIDMOVE] seq=" + seq + " no active raid");
            return;
        }
        Debug.Log("[RAIDMOVE] seq=" + seq + " dir=" + dir + " qid=" + quest.qid);
        // the DB keys the raid by its quest id, which is the instance id without its "-0" suffix
        string dbQid = quest.qid;
        int suffix = dbQid.LastIndexOf("-0");
        if (suffix > 0) dbQid = dbQid.Substring(0, suffix);
        Legacy.QuestsManager.Instance.MoveDirection(dbQid, dir, delegate(string err, QuestDB db)
        {
            Debug.Log("[RAIDMOVE] seq=" + seq + " result=" + (string.IsNullOrEmpty(err) ? "ok" : err));
        });
    }

    static string Describe()
    {
        Legacy.QuestsManager qm = Legacy.QuestsManager.Instance;
        if (qm == null || qm.DB == null) return "no QuestsManager";
        Legacy.QuestDB db = qm.DB;
        StringBuilder sb = new StringBuilder();
        sb.Append("sets=").Append(db.AvailableSets.Count);
        foreach (var set in db.AvailableSets)
        {
            sb.Append(" | cat=").Append(set.category).Append(" setId=").Append(set.setId).Append(" keys=[");
            foreach (string key in set.QuestSummarys.Keys) sb.Append(key).Append(',');
            sb.Append(']');
        }
        for (int n = 1; n <= 3; n++)
        {
            string qid = "raid-npc-" + n;
            var found = db.GetQuestSetOfQuest("raid", qid);
            sb.Append(" | lookup ").Append(qid).Append('=').Append(found == null ? "null" : found.setId);
        }
        return sb.ToString();
    }
}
