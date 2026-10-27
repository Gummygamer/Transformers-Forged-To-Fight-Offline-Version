// Newly authored. Online Arena fights (see ../../ONLINE_PVP.md): the connection to the matchmaking relay
// (Server/pvp_relay.py), the lockstep of both players' fight inputs and the host's authoritative health snapshots.
// It knows no game types; TftfNetGlue (Assembly-CSharp, compat310.py P54) connects it to the fight, TftfPvpPanel is its UI.
//
// Lockstep: the fight simulation runs at a fixed 30 ticks per second on both phones. An input pressed during tick t is sent
// to the opponent and takes effect at tick t + InputDelay on both phones; a phone only simulates tick t once it holds the
// opponent's inputs for it (the game is paused meanwhile). Both phones therefore feed the same inputs into the same ticks.
// The simulation is not guaranteed to be bit-identical on both phones (floating point, Unity animation), so the host
// (role 0) also sends the health and special meter of both fighters every SnapshotEvery ticks and decides knock-outs; the
// guest corrects towards those values and never knocks a fighter out on its own.
//
// Each phone shows its own fighter as player 0. "Canonical" ids are the host's view: canonical 0 = the host's fighter.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public static class TftfNet
{
    public const string Protocol = "1";
    public const int DefaultPort = 7777;
    public const int InputDelay = 3;            // ticks between a press and its effect (100 ms at 30 ticks per second)
    public const int SnapshotEvery = 6;         // host health snapshots, ticks
    const float PeerTimeout = 30f;              // seconds without a line from the opponent before the match counts as lost
    const float PingEvery = 2f;
    const int TickWaitMs = 120;                 // a tick that is due inside a frame waits this long for the opponent's inputs
    const int History = 1024;
    const int EndAfterKnockout = 30;            // ticks of lockstep after a knock-out (both phones stop at the same tick)
    const int DrainTicks = 90;                  // empty input ticks sent when a phone leaves the lockstep, so the other is not stuck

    public enum State { Off, Connecting, Waiting, Matched, Failed }

    public class Hero
    {
        public string Blueprint, Scene;
        public int Rank, Level, Sig;
        public float Clock;                     // the sender's Time.fixedTime, to agree on the fight's clock
    }

    public static State Status { get; private set; }
    public static string Message = "";
    public static string PeerName = "";
    public static int Role;                     // 0 host, 1 guest
    public static bool IsHost { get { return Role == 0; } }
    public static int Seed;
    public static int PingMs = -1;
    public static Func<string> NameProvider;    // the player's in-game name (set by TftfNetGlue)

    // Set when the opponent is lost during a fight; TftfNetGlue hands the opponent's fighter to the AI and clears it.
    public static bool LostInFight;
    public static bool Picking;                 // waiting for the opponent's bot before a fight (set by TftfNetGlue)
    public static int FightFrame = -10;         // last frame a fight simulation ran (set by TftfNetGlue; hides the panel's button)
    public static float LostAt = -100f;

    public static string Address
    {
        get { return PlayerPrefs.GetString("tftf_pvp_address", ""); }
        set { PlayerPrefs.SetString("tftf_pvp_address", value ?? ""); }
    }

    public static string Room
    {
        get { return PlayerPrefs.GetString("tftf_pvp_room", ""); }
        set { PlayerPrefs.SetString("tftf_pvp_room", value ?? ""); }
    }

    public static string[] RecentAddresses
    {
        get
        {
            string s = PlayerPrefs.GetString("tftf_pvp_recent", "");
            return s.Length == 0 ? new string[0] : s.Split('\n');
        }
    }

    static void Remember(string address)
    {
        var list = new List<string> { address };
        foreach (var a in RecentAddresses)
            if (a != address && list.Count < 4) list.Add(a);
        PlayerPrefs.SetString("tftf_pvp_recent", string.Join("\n", list.ToArray()));
        PlayerPrefs.Save();
    }

    // ---- connection ----

    static readonly object _lock = new object();
    static readonly Queue<KeyValuePair<int, string>> _inbox = new Queue<KeyValuePair<int, string>>();
    static readonly object _sendLock = new object();
    static TcpClient _tcp;
    static NetworkStream _stream;
    static int _generation;
    static float _lastHeard, _lastPing;

    public static bool Connected { get { return Status == State.Matched; } }

    // Connects to the saved relay address, or to `address` (this phone's own relay) without saving it.
    public static void Connect(string address = null)
    {
        Disconnect();
        bool own = address != null;
        address = (address ?? Address).Trim();
        string host = address;
        int port = DefaultPort;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon)
        {
            host = address.Substring(0, colon);
            if (!int.TryParse(address.Substring(colon + 1), out port) || port <= 0 || port > 65535)
            {
                Fail("The port after ':' must be a number.");
                return;
            }
        }
        if (host.Length == 0) { Fail("Enter the relay server's address."); return; }
        if (!own) Remember(address);
        string room = Clean(Room.Trim());
        if (room.Length == 0) room = "-";
        string name = Clean(NameProvider != null ? SafeName() : "Player");
        int gen = ++_generation;
        Status = State.Connecting;
        Message = "Connecting to " + address + "...";
        var thread = new Thread(() => Run(gen, host, port, room, name)) { IsBackground = true, Name = "TftfNet" };
        thread.Start();
    }

    public static void Disconnect()
    {
        _generation++;
        Close();
        Status = State.Off;
        Message = "";
        PeerName = "";
        PingMs = -1;
        _remoteHeroes.Clear();
        _armedRound = -1;
        _round = 0;
        if (_active) { _active = false; LostInFight = true; LostAt = Time.realtimeSinceStartup; }
    }

    static string SafeName()
    {
        try { return NameProvider() ?? "Player"; } catch (Exception) { return "Player"; }
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            if (c > ' ' && c < 127) sb.Append(c);
        return sb.Length > 24 ? sb.ToString(0, 24) : sb.ToString();
    }

    static void Close()
    {
        try { if (_stream != null) _stream.Close(); } catch (Exception) { }
        try { if (_tcp != null) _tcp.Close(); } catch (Exception) { }
        _stream = null;
        _tcp = null;
    }

    static void Fail(string message)
    {
        Close();
        Status = State.Failed;
        Message = message;
    }

    static void Post(int gen, string line)
    {
        lock (_lock)
        {
            _inbox.Enqueue(new KeyValuePair<int, string>(gen, line));
            Monitor.PulseAll(_lock);
        }
    }

    // Background thread: connect, say hello, then read lines until the connection ends.
    static void Run(int gen, string host, int port, string room, string name)
    {
        try
        {
            var tcp = new TcpClient();
            var result = tcp.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(8000))
            {
                tcp.Close();
                Post(gen, "\0Could not reach " + host + ":" + port + " (timed out).");
                return;
            }
            tcp.EndConnect(result);
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            lock (_sendLock)
            {
                if (gen != _generation) { tcp.Close(); return; }
                _tcp = tcp;
                _stream = stream;
            }
            byte[] hello = Encoding.ASCII.GetBytes("HELLO " + Protocol + " " + room + " " + name + "\n");
            stream.Write(hello, 0, hello.Length);
            using (var reader = new StreamReader(stream, Encoding.ASCII))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (gen != _generation) return;
                    Post(gen, line);
                }
            }
            Post(gen, "\0The connection to the relay closed.");
        }
        catch (Exception e)
        {
            Post(gen, "\0Connection failed: " + e.Message);
        }
    }

    static void Send(string line)
    {
        lock (_sendLock)
        {
            if (_stream == null) return;
            try
            {
                byte[] b = Encoding.ASCII.GetBytes(line + "\n");
                _stream.Write(b, 0, b.Length);
            }
            catch (Exception e)
            {
                Post(_generation, "\0Connection lost: " + e.Message);
            }
        }
    }

    static long Now() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    // Handles every line received so far. Main thread only (panel Update, TftfNetGlue).
    public static void Pump()
    {
        while (true)
        {
            KeyValuePair<int, string> item;
            lock (_lock)
            {
                if (_inbox.Count == 0) break;
                item = _inbox.Dequeue();
            }
            if (item.Key == _generation) Handle(item.Value);
        }
        if (Status == State.Matched)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastPing > PingEvery)
            {
                _lastPing = now;
                Send("P " + Now());
            }
            if (now - _lastHeard > PeerTimeout) Lose("The opponent stopped responding.");
        }
    }

    static void Lose(string message)
    {
        bool wasMatched = Status == State.Matched;
        Fail(message);
        _generation++;
        if (_active) { _active = false; LostInFight = true; LostAt = Time.realtimeSinceStartup; }
        if (wasMatched) Debug.Log("[TFTF] online: " + message);
    }

    static void Handle(string line)
    {
        if (line.Length > 0 && line[0] == '\0') { Lose(line.Substring(1)); return; }
        _lastHeard = Time.realtimeSinceStartup;
        string[] p = line.Split(' ');
        switch (p[0])
        {
            case "WAIT":
                Status = State.Waiting;
                Message = "Waiting for an opponent...";
                break;
            case "MATCH":
                if (p.Length < 3) break;
                Role = p[1] == "0" ? 0 : 1;
                int.TryParse(p[2], out Seed);
                PeerName = p.Length > 3 ? string.Join(" ", p, 3, p.Length - 3) : "Opponent";
                Status = State.Matched;
                Message = "Matched with " + PeerName + ". Start a match in this Arena; the fight begins when you both have picked.";
                _lastPing = 0f;
                _round = 0;
                _remoteHeroes.Clear();
                _remote.Clear();
                Debug.Log("[TFTF] online: matched with " + PeerName + " role=" + Role);
                break;
            case "ERR":
                Fail(p.Length > 1 && p[1] == "version" ? "The relay runs a different version of online play." : "The relay refused the connection.");
                break;
            case "BYE":
                Lose("The opponent disconnected.");
                break;
            case "P":
                if (p.Length > 1) Send("O " + p[1]);
                break;
            case "O":
                long sent;
                if (p.Length > 1 && long.TryParse(p[1], out sent)) PingMs = (int)(Now() - sent);
                break;
            case "H":
                if (p.Length >= 7)
                {
                    int round;
                    if (!int.TryParse(p[1], out round)) break;
                    var h = new Hero { Blueprint = Uri.UnescapeDataString(p[2]), Scene = Uri.UnescapeDataString(p[6]) };
                    int.TryParse(p[3], out h.Rank);
                    int.TryParse(p[4], out h.Level);
                    int.TryParse(p[5], out h.Sig);
                    if (p.Length > 7) float.TryParse(p[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h.Clock);
                    _remoteHeroes[round] = h;
                }
                break;
            case "T":
                if (p.Length >= 4)
                {
                    int round, tick;
                    if (int.TryParse(p[1], out round) && int.TryParse(p[2], out tick)) _remote[Key(round, tick)] = p[3] == "-" ? "" : p[3];
                }
                break;
            case "S":
                if (p.Length >= 7 && !IsHost)
                {
                    int round, tick;
                    if (!int.TryParse(p[1], out round) || !int.TryParse(p[2], out tick) || round != _fightRound) break;
                    var s = new float[5];
                    s[0] = tick;
                    for (int i = 0; i < 4; i++) float.TryParse(p[3 + i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s[1 + i]);
                    _snapshots.Enqueue(s);
                }
                break;
            case "K":
                if (p.Length >= 4 && !IsHost)
                {
                    int round, loser;
                    int tick;
                    if (int.TryParse(p[1], out round) && round == _fightRound && int.TryParse(p[2], out tick) && int.TryParse(p[3], out loser))
                    {
                        _knockouts.Enqueue(loser);
                        if (_endTick < 0) _endTick = tick + EndAfterKnockout;
                    }
                }
                break;
        }
    }

    // ---- heroes picked for a fight ----

    static int _round;
    static readonly Dictionary<int, Hero> _remoteHeroes = new Dictionary<int, Hero>();

    // Starts the next fight of the match: sends this player's fighter and returns the fight's number.
    public static int SendHero(Hero h)
    {
        int round = ++_round;
        h.Clock = Time.fixedTime;
        _sentClock[round] = h.Clock;
        Send("H " + round + " " + Uri.EscapeDataString(h.Blueprint) + " " + h.Rank + " " + h.Level + " " + h.Sig + " " + Uri.EscapeDataString(h.Scene ?? "")
             + " " + h.Clock.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return round;
    }

    static readonly Dictionary<int, float> _sentClock = new Dictionary<int, float>();

    public static Hero RemoteHero(int round)
    {
        Hero h;
        return _remoteHeroes.TryGetValue(round, out h) ? h : null;
    }

    // ---- lockstep ----

    static int _armedRound = -1, _fightRound = -1, _tick, _lastRecorded = -1, _endTick = -1;
    static bool _active;
    static float _simTime, _armedTime, _stallStart = -1f;
    static readonly StringBuilder _pending = new StringBuilder();
    static readonly Dictionary<long, string> _remote = new Dictionary<long, string>();
    static readonly Dictionary<int, string> _localSchedule = new Dictionary<int, string>();
    static readonly Queue<float[]> _snapshots = new Queue<float[]>();
    static readonly Queue<int> _knockouts = new Queue<int>();
    static readonly float[,] _history = new float[History, 4];
    static readonly int[] _historyTick = new int[History];
    static readonly uint[] _rng = new uint[3];

    static long Key(int round, int tick) { return ((long)round << 32) | (uint)tick; }

    public static bool LockstepActive { get { return _active; } }
    public static int Tick { get { return _tick; } }                 // the number of ticks simulated so far in this fight
    public static float SimTime { get { return _simTime; } }

    // How long the fight has been waiting for the opponent (0 = not waiting).
    public static float StalledFor { get { return _active && _stallStart >= 0f ? Time.realtimeSinceStartup - _stallStart : 0f; } }

    public static int Canonical(int localId) { return Role == 0 ? localId : 1 - localId; }

    // The next fight that starts (Simulation registers its fighters) is the online fight of this round. Both phones start
    // the fight's clock at the same value, later than either phone's own clock (the game's timers only ever see it go forward).
    public static void Arm(int round)
    {
        Hero theirs = RemoteHero(round);
        float mine;
        _sentClock.TryGetValue(round, out mine);
        _armedTime = (float)Math.Ceiling(Math.Max(mine, theirs != null ? theirs.Clock : 0f)) + 900f;
        _armedRound = round;
    }
    public static bool Armed { get { return _armedRound >= 0 && Status == State.Matched; } }

    public static void BeginLockstep()
    {
        if (!Armed) return;
        _fightRound = _armedRound;
        _armedRound = -1;
        _active = true;
        LostInFight = false;
        _tick = 0;
        _lastRecorded = -1;
        _endTick = -1;
        _simTime = Math.Max(_armedTime, Time.fixedTime + 1f);
        if (_simTime != _armedTime) Debug.LogWarning("[TFTF] online: fight clock behind this phone's clock; the phones' timers may differ");
        _stallStart = -1f;
        _pending.Length = 0;
        _localSchedule.Clear();
        _snapshots.Clear();
        _knockouts.Clear();
        for (int i = 0; i < History; i++) _historyTick[i] = -1;
        for (int i = 0; i < 3; i++)
        {
            uint x = (uint)Seed * 2654435761u ^ (uint)(i + 1) * 40503u;
            _rng[i] = x == 0 ? 0x9E3779B9u : x;
        }
        var stale = new List<long>();
        foreach (var k in _remote.Keys)
            if ((int)(k >> 32) < _fightRound) stale.Add(k);
        foreach (var k in stale) _remote.Remove(k);
        Debug.Log("[TFTF] online: fight " + _fightRound + " lockstep started");
    }

    // Leaves the fight's lockstep (knock-out reached, outro, fight left). The opponent may still need this phone's inputs for
    // a few ticks, so empty ones are sent ahead.
    public static void EndLockstep()
    {
        _armedRound = -1;
        if (!_active) return;
        Debug.Log("[TFTF] online: fight " + _fightRound + " ended at tick " + _tick);
        _active = false;
        for (int i = 0; i < DrainTicks; i++) Send("T " + _fightRound + " " + (_tick + InputDelay + i) + " -");
    }

    static void GiveUp(string message)
    {
        Message = message;
        Debug.Log("[TFTF] online: " + message);
        EndLockstep();
        LostInFight = true;
        LostAt = Time.realtimeSinceStartup;
    }

    // A local input during the fight: it is sent with the next tick and applied InputDelay ticks later.
    public static void LocalEvent(char e) { _pending.Append(e); }

    static bool Ready { get { return _tick < InputDelay || _remote.ContainsKey(Key(_fightRound, _tick)); } }

    // Before each frame: whether the game may simulate the next tick (TftfNetGlue sets the time scale from it).
    public static bool NextTickReady()
    {
        Pump();
        if (!_active) return true;
        bool ready = Ready;
        if (ready) _stallStart = -1f;
        else if (_stallStart < 0f) _stallStart = Time.realtimeSinceStartup;
        else if (StalledFor > (_tick == 0 ? 60f : 15f))      // the start waits for the slower phone to load the fight
        {
            GiveUp(_tick == 0 ? "The opponent's fight did not start." : "The opponent's game stopped.");
            return true;
        }
        return ready;
    }

    // Start of a simulation tick. False: the opponent's inputs for it have not arrived (the tick is skipped).
    public static bool Advance(float dt, out string local, out string remote)
    {
        local = remote = "";
        Pump();
        if (_active && _endTick >= 0 && _tick >= _endTick) EndLockstep();
        if (!_active) return true;
        if (!Ready)
        {
            long until = Now() + TickWaitMs;
            lock (_lock)
            {
                while (_inbox.Count == 0 && Now() < until) Monitor.Wait(_lock, (int)Math.Max(1, until - Now()));
            }
            Pump();
            if (!_active) return true;
            if (!Ready)
            {
                if (_stallStart < 0f) _stallStart = Time.realtimeSinceStartup;
                return false;
            }
        }
        _stallStart = -1f;
        string mine = _pending.ToString();
        _pending.Length = 0;
        _localSchedule[_tick + InputDelay] = mine;
        Send("T " + _fightRound + " " + (_tick + InputDelay) + " " + (mine.Length == 0 ? "-" : mine));
        if (_localSchedule.TryGetValue(_tick, out local)) _localSchedule.Remove(_tick);
        else local = "";
        long key = Key(_fightRound, _tick);
        if (_remote.TryGetValue(key, out remote)) _remote.Remove(key);
        else remote = "";
        _tick++;
        _simTime += dt;
        return true;
    }

    // A random number for the fight: one stream per fighter (canonical id) plus one shared stream (localId < 0), all
    // seeded alike on both phones. Outside an online fight this is Unity's random generator.
    public static float RandomValue(int localId)
    {
        if (!_active) return UnityEngine.Random.value;
        int i = localId < 0 || localId > 1 ? 2 : Canonical(localId);
        uint x = _rng[i];
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng[i] = x;
        return (x >> 8) * (1f / 16777216f);
    }

    // ---- host authority ----

    // Host: after a tick, the fighters' health and meter (canonical order). Sent every SnapshotEvery ticks.
    public static void SendSnapshot(float hp0, float hp1, float mana0, float mana1)
    {
        if (!_active || !IsHost || _tick % SnapshotEvery != 0) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Send("S " + _fightRound + " " + _tick + " " + hp0.ToString("R", ci) + " " + hp1.ToString("R", ci) + " " + mana0.ToString("R", ci) + " " + mana1.ToString("R", ci));
    }

    public static void SendKnockout(int canonicalLoser)
    {
        if (!_active || !IsHost) return;
        Send("K " + _fightRound + " " + _tick + " " + canonicalLoser);
        if (_endTick < 0) _endTick = _tick + EndAfterKnockout;
    }

    // Guest: after a tick, the same values as the host would send, so a later host snapshot can be compared with them.
    public static void Record(float hp0, float hp1, float mana0, float mana1)
    {
        int slot = _tick % History;
        _historyTick[slot] = _tick;
        _history[slot, 0] = hp0;
        _history[slot, 1] = hp1;
        _history[slot, 2] = mana0;
        _history[slot, 3] = mana1;
        _lastRecorded = _tick;
    }

    // Guest: the difference between the host's snapshots and this phone's values at the same ticks (canonical order), to
    // add to the current values. False when there is nothing to correct.
    public static bool TakeCorrection(float[] delta)
    {
        bool any = false;
        for (int i = 0; i < 4; i++) delta[i] = 0f;
        while (_snapshots.Count > 0 && _snapshots.Peek()[0] <= _lastRecorded)
        {
            float[] s = _snapshots.Dequeue();
            int tick = (int)s[0];
            int slot = tick % History;
            if (_historyTick[slot] != tick) continue;
            for (int i = 0; i < 4; i++)
            {
                float d = s[1 + i] - _history[slot, i];
                if (Math.Abs(d) < 0.01f) continue;
                delta[i] += d;
                any = true;
                for (int t = tick; t <= _lastRecorded; t++)       // later snapshots compare against corrected values
                    if (_historyTick[t % History] == t) _history[t % History, i] += d;
            }
        }
        return any;
    }

    // Guest: a knock-out the host decided (canonical id of the fighter that lost), or -1.
    public static int TakeKnockout() { return _knockouts.Count > 0 ? _knockouts.Dequeue() : -1; }
}

// Marks a screen on which the online panel offers its button (TftfNetGlue adds it to the Arena screen).
public sealed class TftfArenaMarker : MonoBehaviour
{
    static int _count;
    public static bool Visible { get { return _count > 0; } }
    void OnEnable() { _count++; }
    void OnDisable() { _count--; }
}
