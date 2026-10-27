// Newly authored. Connects the online lockstep (TftfNet, first-pass assembly) to the game's fight; the calls are inserted by
// compat310.py P54 (shell only). Compiled into Assembly-CSharp because it uses the game's types. See ../../ONLINE_PVP.md.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class TftfNetGlue
{
    static bool _replaying, _inner, _gating;
    static readonly bool[] _koSent = new bool[2];
    static readonly float[] _delta = new float[4];
    static TftfNetRunner _runner;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        TftfNet.NameProvider = SocialUtils.GetLocalUserName;
    }

    public static bool OnlineFightPending { get { return TftfNet.Armed || TftfNet.LockstepActive; } }

    // PVPScreen: the online button and panel live on the Arena screen.
    public static void MarkArena(MonoBehaviour screen)
    {
#if !TFTF_VR
        if (screen.GetComponent<TftfPvpArenaUi>() == null) screen.gameObject.AddComponent<TftfPvpArenaUi>();
#endif
    }

    // ---- before the fight: both players' bots ----

    // PVPUtil.GenerateFightData: while matched online, send this player's bot, wait for the opponent's, then build the fight
    // against it. True: handled here (the result handler is called later).
    public static bool InterceptFightData(string scene, EB.Action<FightData, string> resultHandler)
    {
        if (_inner || !TftfNet.Connected || PVPManager.Instance == null) return false;
        BCGMatchAttributes attributes = PVPManager.Instance.CurrentMatchAttributes;
        if (attributes == null || attributes.userHero == null) return false;
        if (_runner == null)
        {
            var go = new GameObject("TftfNetGlue");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<TftfNetRunner>();
        }
        _runner.StartCoroutine(Exchange(scene, resultHandler, attributes.userHero));
        return true;
    }

    static IEnumerator Exchange(string scene, EB.Action<FightData, string> resultHandler, BCGHeroDetails hero)
    {
        var mine = new TftfNet.Hero { Blueprint = hero.Blueprint, Rank = hero.Rank, Level = hero.Level, Sig = hero.SigLevel, Scene = scene };
        int round = TftfNet.SendHero(mine);
        TftfNet.Picking = true;
        float until = Time.realtimeSinceStartup + 120f;
        TftfNet.Hero theirs = null;
        while (TftfNet.Connected && Time.realtimeSinceStartup < until && (theirs = TftfNet.RemoteHero(round)) == null) yield return null;
        TftfNet.Picking = false;
        if (theirs == null && TftfNet.Connected) TftfNet.Message = TftfNet.PeerName + " did not pick a bot in time; this fight is against the AI.";
        // the host's arena is used on both phones (its size limits how far the fighters can move)
        string useScene = theirs != null && !TftfNet.IsHost && !string.IsNullOrEmpty(theirs.Scene) ? theirs.Scene : scene;
        _inner = true;
        try
        {
            PVPUtil.GenerateFightData(useScene, delegate(FightData data, string err)
            {
                if (theirs == null || data == null || !TftfNet.Connected) resultHandler(data, err);
                else Retarget(data, mine, theirs, round, resultHandler);
            });
        }
        finally
        {
            _inner = false;
        }
    }

    // The fight's second fighter becomes the opponent's bot. Both fighters get the base attributes of their bot, rank and
    // level from the local server, so both phones build the same two fighters.
    static void Retarget(FightData data, TftfNet.Hero mine, TftfNet.Hero theirs, int round, EB.Action<FightData, string> resultHandler)
    {
        BCGBlueprintBase blueprint = BCGHelper.GetBlueprint(theirs.Blueprint);
        if (blueprint == null || data.Fighters == null || data.Fighters.Length < 2)
        {
            TftfNet.Message = "This game does not know " + TftfNet.PeerName + "'s bot (" + theirs.Blueprint + "); this fight is against the AI.";
            resultHandler(data, string.Empty);
            return;
        }
        FighterData opponent = data.Fighters[1];
        opponent.Id = theirs.Blueprint;
        opponent.Blueprint = blueprint;
        opponent.PlayerName = TftfNet.PeerName;
        opponent.PlayerTag = string.Empty;
        opponent.SigLevel = theirs.Sig;
        for (int i = 0; i < 2; i++) data.Fighters[i].TeamData = null;     // team bonuses are only known on their own phone
        data.AutoFightEnabled = false;
        var levels = new List<BCGHeroRankLevel>
        {
            new BCGHeroRankLevel(mine.Blueprint, mine.Rank, mine.Level, mine.Sig),
            new BCGHeroRankLevel(theirs.Blueprint, theirs.Rank, theirs.Level, theirs.Sig)
        };
        BCGManager.Instance.GetHeroBaseAttributes(levels, delegate(string err, List<BCGHeroDetails> heroes)
        {
            if (string.IsNullOrEmpty(err) && heroes != null && heroes.Count == 2 && TftfNet.Connected)
            {
                for (int i = 0; i < 2; i++)
                {
                    data.Fighters[i].Attributes = heroes[i].AttributeData;
                    data.Fighters[i].BaseAttributes = heroes[i].AttributeData;
                }
                TftfNet.Arm(round);
                TftfNet.Message = "Fighting " + TftfNet.PeerName + ".";
            }
            else
            {
                TftfNet.Message = "Could not prepare the online fight; this fight is against the AI.";
            }
            resultHandler(data, string.Empty);
        });
    }

    // ---- during the fight ----

    // Simulation.RegisterComponents: the fighters exist; an armed online fight starts its lockstep.
    public static void FightStarted()
    {
        if (!TftfNet.Armed) return;
        _koSent[0] = _koSent[1] = false;
        TftfNet.BeginLockstep();
    }

    // Simulation.OnDestroy
    public static void FightEnded()
    {
        TftfNet.EndLockstep();
        _gating = false;
    }

    // BattleArbiter.EnableAI: neither fighter is AI-controlled in an online fight (the opponent's fighter is driven by the
    // opponent's inputs, and auto-fight would act outside the lockstep).
    public static bool FilterAI(bool enable)
    {
        return enable && !OnlineFightPending;
    }

    // PlayerInput.On*Input: the local player's inputs go into the lockstep instead of acting at once.
    public static bool Capture(PlayerInput input, char e)
    {
        if (_replaying || !TftfNet.LockstepActive || input.PlayerController == null || input.PlayerController.Id != 0) return false;
        TftfNet.LocalEvent(e);
        return true;
    }

    static void Apply(PlayerInput input, string events)
    {
        if (input == null || string.IsNullOrEmpty(events)) return;
        _replaying = true;
        try
        {
            foreach (char e in events)
            {
                switch (e)
                {
                    case 'a': input.OnReceiveAttackInput(); break;
                    case 'A': input.OnReleaseAttackInput(); break;
                    case 'b': input.OnReceiveBlockInput(); break;
                    case 'B': input.OnReleaseBlockInput(); break;
                    case 's': input.OnReceiveSpecialInput(); break;
                    default:
                        if (e >= '0' && e <= '9') input.OnDetectSwipe((SwipeDirection)(e - '0'));
                        break;
                }
            }
        }
        finally
        {
            _replaying = false;
        }
    }

    // Simulation.Update: the game only advances when the opponent's inputs for the next tick are here.
    public static void Gate(bool paused, float timeScale)
    {
        TftfNet.FightFrame = Time.frameCount;
        HandleLoss();
        if (TftfNet.LockstepActive)
        {
            BattleArbiter arbiter = BattleArbiter.Instance;
            if (arbiter == null || arbiter.IsCurrentState("Outro") || arbiter.IsCurrentState("Exit") || arbiter.IsCurrentState("Timeout"))
                TftfNet.EndLockstep();
        }
        if (!TftfNet.LockstepActive)
        {
            if (_gating)
            {
                _gating = false;
                Time.timeScale = paused ? 0f : timeScale;
            }
            return;
        }
        _gating = true;
        bool ready = TftfNet.NextTickReady();
        Time.timeScale = ready && !paused && TftfNet.LockstepActive ? timeScale : 0f;
    }

    // Simulation.FixedUpdate, first: false skips the tick (opponent's inputs missing). Otherwise both fighters' inputs
    // for this tick are applied.
    public static bool BeginTick(PlayerInput[] inputs)
    {
        HandleLoss();
        if (!TftfNet.LockstepActive) return true;
        string local, remote;
        if (!TftfNet.Advance(Time.fixedDeltaTime, out local, out remote)) return false;
        Apply(inputs[0], local);
        Apply(inputs[1], remote);
        return true;
    }

    // Simulation.FixedUpdate, last: the host reports health, meter and knock-outs; the guest corrects towards them.
    public static void EndTick(PlayerController[] players)
    {
        if (!TftfNet.LockstepActive || players[0] == null || players[1] == null || players[0].Attributes == null || players[1].Attributes == null) return;
        PlayerController c0 = players[TftfNet.Canonical(0)], c1 = players[TftfNet.Canonical(1)];
        if (TftfNet.IsHost)
        {
            TftfNet.SendSnapshot(c0.Attributes.Health, c1.Attributes.Health, c0.Attributes.Mana, c1.Attributes.Mana);
            for (int c = 0; c < 2; c++)
            {
                PlayerController f = c == 0 ? c0 : c1;
                if (!_koSent[c] && (f.IsDead || f.IsHealthDepleted))
                {
                    _koSent[c] = true;
                    TftfNet.SendKnockout(c);
                }
            }
            return;
        }
        if (TftfNet.TakeCorrection(_delta))
        {
            Adjust(c0, _delta[0], _delta[2]);
            Adjust(c1, _delta[1], _delta[3]);
        }
        for (int ko = TftfNet.TakeKnockout(); ko >= 0; ko = TftfNet.TakeKnockout())
        {
            PlayerController f = ko == 0 ? c0 : c1;
            if (!f.IsDead)
            {
                f.Attributes.Health = 0f;
                f.SwitchState("Dead");
            }
        }
        TftfNet.Record(c0.Attributes.Health, c1.Attributes.Health, c0.Attributes.Mana, c1.Attributes.Mana);
    }

    static void Adjust(PlayerController fighter, float health, float mana)
    {
        if (fighter.IsDead) return;
        PlayerAttributes a = fighter.Attributes;
        if (health != 0f) a.Health = Mathf.Clamp(a.Health + health, 1f, a.MaxHealth);      // only the host knocks out
        if (mana != 0f) a.Mana = Mathf.Clamp(a.Mana + mana, 0f, a.MaxMana);
    }

    // PlayerController.ApplyDamage: on the guest a fighter whose health runs out waits for the host's knock-out.
    public static bool HoldKnockout(PlayerController fighter)
    {
        if (!TftfNet.LockstepActive || TftfNet.IsHost) return false;
        fighter.Attributes.Health = 1f;
        return true;
    }

    // The opponent was lost during the fight: the AI takes over the opponent's fighter for the rest of it.
    static void HandleLoss()
    {
        if (!TftfNet.LostInFight) return;
        TftfNet.LostInFight = false;
        TftfNet.EndLockstep();
        BattleArbiter arbiter = BattleArbiter.Instance;
        if (arbiter != null && arbiter.GetFighter(1) != null) arbiter.EnableAI(1, true);
    }
}

public sealed class TftfNetRunner : MonoBehaviour
{
}
