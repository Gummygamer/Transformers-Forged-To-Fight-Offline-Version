// Newly authored. The online Arena P2P network (see ../../ONLINE_PVP.md): every game that plays online is a node. The
// bootstrap node (Server/p2p_bootstrap) is only the first hop: it tells a joining game the address the internet sees it at
// and introduces it to a few games already in the network, which open their NATs towards each other (UDP hole punching).
// From then on the games do everything among themselves: they gossip their neighbours and introduce them to each other,
// flood "looking for an opponent" through the network, pair players, and carry the fight's lines either directly or, when
// two players' NATs do not let them reach each other, hop by hop through other games.
//
// So that the network does not depend on the bootstrap node (which could be flooded, or silent once its spending cap is
// reached), each game remembers the games it was linked with and its own UDP port (CachePath). When it has no neighbours it
// first tries those games again, a few at a time in random order, from the same port, and asks the bootstrap node only
// after all of them failed. One that answers is enough: the network's own introductions do the rest.
//
// TftfNet uses this instead of a relay connection: it sees the same lines a relay would send (WAIT, MATCH, BYE and the
// opponent's lines), so the lockstep code does not change. Lines between the two players are numbered, acknowledged and
// resent, so they arrive complete and in order as over TCP.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class TftfMesh
{
    public const string Tag = "TF1";
    const int Want = 3, MaxLinks = 8;                 // neighbours wanted / kept
    const long PunchEvery = 250, PunchFor = 12000;    // ms
    const long KeepaliveEvery = 4000, LinkTimeout = 20000, GossipEvery = 6000, KnownFor = 30000, RouteFor = 30000;
    const long SeekEvery = 2500, OfferTimeout = 4000, JoinTimeout = 12000, ResendAfter = 200;
    const int Ttl = 8, SeenKept = 8192, MaxDatagram = 1200;
    const int Cached = 32, ProbeBatch = 4;            // games remembered / tried at once
    const long ProbeFor = 3000, CacheSaveEvery = 5000, CacheKeptDays = 30;

    class Link
    {
        public string Id, Locals = "-";
        public IPEndPoint Ep;                           // where it answered from, once up
        public List<IPEndPoint> Candidates = new List<IPEndPoint>();
        public bool Up;
        public long Started, LastHeard, LastSent, PunchFor = TftfMesh.PunchFor;
        public string Desc { get { return Id + "/" + Ep + "/" + Locals; } }
    }

    class Known { public string Desc, Via; public long Seen; }
    class Route { public string Via; public long At; }

    // Test hooks (tools/net_lockstep_test): a fixed node id, and peers this node must not link to directly.
    public static string TestId;
    public static Func<string, bool> TestBlock;

    // File that keeps the games this one was linked with, and its port, between runs (set by TftfNet; null or empty: none).
    public static string CachePath;

    static readonly object _gate = new object();
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly System.Random _random = new System.Random();
    static Socket _sock;
    static int _gen;
    static string _id = "", _pub = "-", _locals = "-", _bootstrap = "";
    static IPEndPoint _bootstrapEp;
    static long _lastJoin = -100000, _lastGossip, _lastSeek = -100000, _startedAt, _lastTimers;
    static bool _heardBootstrap;
    static string _cookie = NoCookie;                  // the bootstrap node's proof that this node receives at its address
    const string NoCookie = "0000000000000000";        // as long as a cookie, so the first join is not shorter than C
    // discovery while this node has no neighbours: remembered games first, then the bootstrap node
    static bool _discovering, _cacheDirty;
    static readonly List<string> _probe = new List<string>();
    static int _probeNext;
    static long _probeAt = -100000, _bootstrapSince = -1, _cacheSavedAt;
    static int _port;
    static readonly Dictionary<string, KeyValuePair<string, long>> _cache = new Dictionary<string, KeyValuePair<string, long>>();   // id -> desc, unix seconds
    static int _msgId;
    static readonly Dictionary<string, Link> _links = new Dictionary<string, Link>();
    static readonly Dictionary<string, Link> _byEp = new Dictionary<string, Link>();
    static readonly Dictionary<string, Known> _known = new Dictionary<string, Known>();
    static readonly Dictionary<string, Route> _routes = new Dictionary<string, Route>();
    static readonly HashSet<string> _seen = new HashSet<string>();
    static readonly Queue<string> _seenOrder = new Queue<string>();

    // matchmaking and the session with the opponent
    static Action<string> _deliver;
    static bool _seeking, _waitSent;
    static string _room = "-", _name = "Player";
    static string _offerTo, _offerSid, _offerName;
    static int _offerSeed;
    static long _offerAt;
    static string _peer, _sid;
    static int _sendSeq, _recvNext;
    static readonly SortedDictionary<int, string> _unacked = new SortedDictionary<int, string>();
    static readonly Dictionary<int, long> _sentAt = new Dictionary<int, long>();
    static readonly Dictionary<int, string> _future = new Dictionary<int, string>();

    public static bool Running { get { return _sock != null; } }
    public static string Id { get { return _id; } }
    public static string PublicAddress { get { return _pub; } }
    public static bool InSession { get { return _peer != null; } }
    public static bool Direct { get { lock (_gate) { Link l; return _peer != null && _links.TryGetValue(_peer, out l) && l.Up; } } }

    public static int Neighbours
    {
        get { lock (_gate) { int n = 0; foreach (var l in _links.Values) if (l.Up) n++; return n; } }
    }

    public static int KnownNodes { get { lock (_gate) { return Neighbours + _known.Count; } } }

    static long Now() { return _clock.ElapsedMilliseconds; }

    // Joins the network (if not yet) through `bootstrap` (host:port) and looks for an opponent with the same room code. Every
    // line for TftfNet goes to `deliver`, on the network thread: WAIT, MATCH <role> <seed> <name>, the opponent's lines, BYE,
    // or "\0" + a message when the network cannot be reached.
    public static void Seek(string bootstrap, string room, string name, Action<string> deliver)
    {
        string error = Join(bootstrap);
        lock (_gate)
        {
            EndSession(true);
            _deliver = deliver;
            if (error != null) { Deliver("\0Could not open a network socket: " + error); _deliver = null; return; }
            _room = room;
            _name = name;
            _seeking = true;
            _waitSent = false;
            _offerTo = null;
            _startedAt = Now();
            if (_bootstrapSince >= 0) _bootstrapSince = _startedAt;      // another full wait for the bootstrap node
            _lastSeek = -100000;
        }
    }

    // Joins the network without looking for an opponent (only carries other players' lines). Null, or why it failed.
    public static string Join(string bootstrap)
    {
        lock (_gate)
            if (_sock != null && bootstrap == _bootstrap) return null;
        Stop();
        lock (_gate)
        {
            _bootstrap = bootstrap;
            _startedAt = Now();
            _id = TestId ?? NewId(12);
            _msgId = _random.Next(1 << 30);           // other nodes remember message ids: a restarted node must not reuse them
            LoadCache();
            try
            {
                _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // the remembered port, so that the games that remember this one find it there again; never shared with
                // another socket (Mono allows that by default), so a port in use falls back to a new one
                _sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                try { _sock.Bind(new IPEndPoint(IPAddress.Any, _port)); }
                catch (SocketException) { _sock.Bind(new IPEndPoint(IPAddress.Any, 0)); }
                _sock.ReceiveTimeout = 50;
            }
            catch (Exception e)
            {
                _sock = null;
                return e.Message;
            }
            _port = ((IPEndPoint)_sock.LocalEndPoint).Port;
            _cacheDirty = true;
            _discovering = false;
            _bootstrapSince = -1;
            _locals = LocalEndpoints(_port);
            int gen = ++_gen;
            var sock = _sock;
            new Thread(() => Run(gen, sock)) { IsBackground = true, Name = "TftfMesh" }.Start();
        }
        return null;
    }

    // Stops looking for an opponent and ends the session with the current one (the opponent is told). The node stays in the
    // network, so it keeps carrying other players' lines while the game runs.
    public static void Leave()
    {
        lock (_gate)
        {
            EndSession(true);
            _seeking = false;
            _offerTo = null;
            _deliver = null;
        }
    }

    // Leaves the network altogether.
    public static void Stop()
    {
        lock (_gate)
        {
            if (_sock != null) SaveCache(Now());
            EndSession(true);
            _seeking = false;
            _deliver = null;
            _gen++;
            if (_sock != null) try { _sock.Close(); } catch (Exception) { }
            _sock = null;
            _bootstrapEp = null;
            _heardBootstrap = false;
            _cookie = NoCookie;
            _pub = "-";
            _links.Clear();
            _byEp.Clear();
            _known.Clear();
            _routes.Clear();
        }
    }

    // A line to the opponent, delivered once and in order.
    public static void Send(string line)
    {
        lock (_gate)
        {
            if (_peer == null) return;
            int seq = ++_sendSeq;
            _unacked[seq] = line;
            _sentAt[seq] = Now();
            SendTo(_peer, "S " + _sid + " " + (_recvNext - 1) + " " + seq + " " + line);
        }
    }

    // ---- network thread ----

    static void Run(int gen, Socket sock)
    {
        var buf = new byte[2048];
        string host = _bootstrap;
        int port = TftfNet.DefaultPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0) { int.TryParse(host.Substring(colon + 1), out port); host = host.Substring(0, colon); }
        long resolvedAt = -100000;
        while (true)
        {
            if (_bootstrapEp == null && Now() - resolvedAt > 5000)
            {
                resolvedAt = Now();
                try
                {
                    foreach (var a in Dns.GetHostAddresses(host))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { lock (_gate) if (gen == _gen) _bootstrapEp = new IPEndPoint(a, port); break; }
                }
                catch (Exception) { }
            }
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = 0;
            try { n = sock.ReceiveFrom(buf, ref from); }
            catch (SocketException) { }                 // timeout, or an ICMP "unreachable" from an earlier send
            catch (ObjectDisposedException) { return; }
            lock (_gate)
            {
                if (gen != _gen) return;
                try
                {
                    if (n > 0) Handle((IPEndPoint)from, Encoding.ASCII.GetString(buf, 0, n));
                    long now = Now();
                    if (now - _lastTimers >= 40) { _lastTimers = now; Timers(now); }
                }
                catch (ThreadAbortException) { return; }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[TFTF] mesh: " + e); }
            }
        }
    }

    static void Raw(IPEndPoint ep, string text)
    {
        if (_sock == null || ep == null) return;
        byte[] b = Encoding.ASCII.GetBytes(Tag + " " + text);
        if (b.Length > MaxDatagram) return;
        try { _sock.SendTo(b, ep); } catch (Exception) { }
    }

    static void ToLink(Link l, string text)
    {
        Raw(l.Ep, text);
        l.LastSent = Now();
    }

    static void Deliver(string line)
    {
        var d = _deliver;
        if (d != null) d(line);
    }

    static void Timers(long now)
    {
        int up = 0;
        var drop = new List<string>();
        foreach (var l in _links.Values)
        {
            if (l.Up)
            {
                if (now - l.LastHeard > LinkTimeout) { drop.Add(l.Id); continue; }
                up++;
                if (now - l.LastSent > KeepaliveEvery) Hello(l.Ep, l);
            }
            else if (now - l.Started > l.PunchFor) drop.Add(l.Id);
            else if (now - l.LastSent >= PunchEvery)
            {
                foreach (var ep in l.Candidates) Hello(ep, null);
                l.LastSent = now;
            }
        }
        foreach (var id in drop) RemoveLink(id);

        Discover(now, up);
        if (_cacheDirty && now - _cacheSavedAt > CacheSaveEvery) SaveCache(now);

        if (now - _lastGossip > GossipEvery)
        {
            _lastGossip = now;
            Gossip(now, up);
        }

        if (_seeking)
        {
            if (!_heardBootstrap && up == 0 && _bootstrapSince >= 0 && now - _bootstrapSince > JoinTimeout)
            {
                _seeking = false;
                Deliver("\0Could not reach the online network (" + _bootstrap + ").");
                _deliver = null;
                return;
            }
            if (!_waitSent && (_heardBootstrap || up > 0)) { _waitSent = true; Deliver("WAIT"); }
            if (_offerTo != null && now - _offerAt > OfferTimeout) _offerTo = null;
            if (up > 0 && now - _lastSeek > SeekEvery)
            {
                _lastSeek = now;
                Broadcast("SEEK " + _room + " " + _name + " " + MyDesc());
            }
        }

        if (_peer != null)
        {
            int resent = 0;
            foreach (var kv in _unacked)
            {
                if (resent >= 32) break;
                if (now - _sentAt[kv.Key] < ResendAfter) continue;
                _sentAt[kv.Key] = now;
                SendTo(_peer, "S " + _sid + " " + (_recvNext - 1) + " " + kv.Key + " " + kv.Value);
                resent++;
            }
        }
    }

    // Without neighbours: the remembered games, ProbeBatch at a time for ProbeFor each in random order, then the bootstrap
    // node every 2 s. With neighbours the bootstrap node only gets a heartbeat every 25 s, so that it can introduce later
    // newcomers to this game; this game's own discovery then goes through its neighbours (Gossip).
    static void Discover(long now, int up)
    {
        if (up > 0)
        {
            _discovering = false;
            _bootstrapSince = -1;
            if (_bootstrapEp != null && now - _lastJoin > 25000) SendJoin(now, up);
            return;
        }
        if (!_discovering)
        {
            _discovering = true;
            _probe.Clear();
            foreach (var kv in _cache) _probe.Add(kv.Value.Key);
            for (int i = _probe.Count - 1; i > 0; i--) { int j = _random.Next(i + 1); var t = _probe[i]; _probe[i] = _probe[j]; _probe[j] = t; }
            _probeNext = 0;
            _probeAt = -100000;
        }
        if (_probeNext < _probe.Count)
        {
            if (now - _probeAt < ProbeFor) return;
            _probeAt = now;
            for (int n = 0; n < ProbeBatch && _probeNext < _probe.Count; n++) Punch(_probe[_probeNext++], ProbeFor);
            return;
        }
        if (now - _probeAt < ProbeFor) return;          // the last batch still has its time
        if (_bootstrapSince < 0) _bootstrapSince = now;
        if (_bootstrapEp != null && now - _lastJoin > 2000) SendJoin(now, up);
    }

    static void SendJoin(long now, int up)
    {
        _lastJoin = now;
        Raw(_bootstrapEp, "J " + _id + " " + up + " " + _locals + " " + _cookie);
    }

    // ---- remembered games ----

    // File: "port <n>", then "<descriptor> <unix seconds last linked>" per game.
    static void LoadCache()
    {
        _cache.Clear();
        _port = 0;
        if (string.IsNullOrEmpty(CachePath)) return;
        try
        {
            if (!File.Exists(CachePath)) return;
            long oldest = UnixNow() - CacheKeptDays * 86400;
            foreach (var line in File.ReadAllLines(CachePath))
            {
                string[] p = line.Split(' ');
                long seen;
                if (p.Length == 2 && p[0] == "port") { int.TryParse(p[1], out _port); if (_port < 1024 || _port > 65535) _port = 0; continue; }
                if (p.Length != 2 || !long.TryParse(p[1], out seen) || seen < oldest) continue;
                string[] d = p[0].Split('/');
                if (d.Length != 3 || d[0].Length == 0 || d[0].Length > 24 || d[0] == _id) continue;
                _cache[d[0]] = new KeyValuePair<string, long>(p[0], seen);
            }
        }
        catch (Exception e) { UnityEngine.Debug.LogWarning("[TFTF] mesh: could not read " + CachePath + ": " + e.Message); }
    }

    static void SaveCache(long now)
    {
        _cacheSavedAt = now;
        _cacheDirty = false;
        long unix = UnixNow();
        foreach (var l in _links.Values)
            if (l.Up) _cache[l.Id] = new KeyValuePair<string, long>(l.Desc, unix);
        var entries = new List<KeyValuePair<string, long>>(_cache.Values);
        entries.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (entries.Count > Cached) entries.RemoveRange(Cached, entries.Count - Cached);
        _cache.Clear();
        var sb = new StringBuilder("port " + _port + "\n");
        foreach (var e in entries)
        {
            _cache[e.Key.Split('/')[0]] = e;
            sb.Append(e.Key).Append(' ').Append(e.Value).Append('\n');
        }
        if (string.IsNullOrEmpty(CachePath)) return;
        try
        {
            File.WriteAllText(CachePath + ".tmp", sb.ToString());
            if (File.Exists(CachePath)) File.Delete(CachePath);
            File.Move(CachePath + ".tmp", CachePath);
        }
        catch (Exception e) { UnityEngine.Debug.LogWarning("[TFTF] mesh: could not write " + CachePath + ": " + e.Message); }
    }

    static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

    // Tells each neighbour about the others, and asks a neighbour to introduce this node to one of its neighbours while it
    // has too few.
    static void Gossip(long now, int up)
    {
        var descs = new List<string>();
        foreach (var l in _links.Values) if (l.Up) descs.Add(l.Desc);
        foreach (var l in _links.Values)
        {
            if (!l.Up) continue;
            var sb = new StringBuilder("G");
            int n = 0;
            foreach (var d in descs)
                if (!d.StartsWith(l.Id + "/") && n++ < 8) sb.Append(' ').Append(d);
            ToLink(l, sb.ToString());
        }
        var stale = new List<string>();
        foreach (var kv in _known) if (now - kv.Value.Seen > KnownFor || _links.ContainsKey(kv.Key)) stale.Add(kv.Key);
        foreach (var k in stale) _known.Remove(k);
        if (up >= Want || _known.Count == 0) return;
        var candidates = new List<Known>(_known.Values);
        var pick = candidates[_random.Next(candidates.Count)];
        Link via;
        if (_links.TryGetValue(pick.Via, out via) && via.Up) ToLink(via, "X " + pick.Desc.Split('/')[0]);
    }

    static void Hello(IPEndPoint ep, Link l)
    {
        Raw(ep, "H " + _id + " " + ep + " " + _locals);
        if (l != null) l.LastSent = Now();
    }

    // Starts sending to a peer from its descriptor (id/endpoint/locals) until it answers.
    static void Punch(string desc, long punchFor = PunchFor)
    {
        string[] d = desc.Split('/');
        if (d.Length < 3 || d[0].Length == 0 || d[0] == _id) return;
        if (TestBlock != null && TestBlock(d[0])) return;
        Link l;
        if (_links.TryGetValue(d[0], out l))
        {
            if (l.Up) return;
        }
        else
        {
            if (_links.Count >= MaxLinks && d[0] != _peer && d[0] != _offerTo) return;
            l = new Link { Id = d[0], Started = Now() };
            _links[d[0]] = l;
        }
        l.Started = Now();
        l.PunchFor = punchFor;
        l.Candidates.Clear();
        AddEndpoint(l.Candidates, d[1], false);
        foreach (var e in d[2].Split(',')) AddEndpoint(l.Candidates, e, true);
        l.LastSent = 0;
    }

    // A local address (`local`) is only a shortcut for players on the same network, so only a private one is used: a
    // public one there would aim this game's hellos at whatever address another game put in.
    static void AddEndpoint(List<IPEndPoint> list, string s, bool local)
    {
        int colon = s.LastIndexOf(':');
        IPAddress a;
        int port;
        if (colon <= 0 || !IPAddress.TryParse(s.Substring(0, colon), out a) || !int.TryParse(s.Substring(colon + 1), out port)) return;
        if (port <= 0 || port > 65535 || list.Count >= 6 || local && !IsPrivate(a)) return;
        var ep = new IPEndPoint(a, port);
        foreach (var e in list) if (e.Equals(ep)) return;
        list.Add(ep);
    }

    // 10/8, 172.16/12, 192.168/16, 100.64/10 (carrier NAT), 169.254/16 (link-local); as Server/p2p_bootstrap's PRIVATE_NETS.
    static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168
            || b[0] == 100 && (b[1] & 0xC0) == 64 || b[0] == 169 && b[1] == 254;
    }

    static void RemoveLink(string id)
    {
        Link l;
        if (!_links.TryGetValue(id, out l)) return;
        _links.Remove(id);
        if (l.Ep != null) { Link b; if (_byEp.TryGetValue(l.Ep.ToString(), out b) && b == l) _byEp.Remove(l.Ep.ToString()); }
        var gone = new List<string>();
        foreach (var kv in _routes) if (kv.Value.Via == id) gone.Add(kv.Key);
        foreach (var k in gone) _routes.Remove(k);
    }

    static string MyDesc() { return _id + "/" + _pub + "/" + _locals; }

    static void Handle(IPEndPoint from, string text)
    {
        string[] p = text.Split(new[] { ' ' }, 3);
        if (p.Length < 2 || p[0] != Tag) return;
        string rest = p.Length > 2 ? p[2] : "";
        long now = Now();
        Link link;
        _byEp.TryGetValue(from.ToString(), out link);
        if (link != null && link.Up) link.LastHeard = now;
        bool fromBootstrap = _bootstrapEp != null && from.Equals(_bootstrapEp);
        switch (p[1])
        {
            case "C":                   // the bootstrap node's return-address check: join again with this cookie
            {
                if (!fromBootstrap) return;
                string c = rest.Trim();
                if (c.Length == 0 || c.Length > 32 || c == _cookie) return;
                _cookie = c;
                int up = 0;
                foreach (var l in _links.Values) if (l.Up) up++;
                SendJoin(now, up);
                break;
            }
            case "W":
                if (!fromBootstrap) return;
                _heardBootstrap = true;
                string[] w = rest.Split(' ');
                if (w.Length > 0 && w[0].Length > 0) _pub = w[0];
                for (int i = 1; i < w.Length; i++) Punch(w[i]);
                break;
            case "I":
                if (fromBootstrap || (link != null && link.Up)) Punch(rest.Trim());
                break;
            case "H":
            {
                string[] h = rest.Split(' ');
                if (h.Length < 1 || h[0].Length == 0 || h[0] == _id) return;
                if (TestBlock != null && TestBlock(h[0])) return;
                Link l;
                if (!_links.TryGetValue(h[0], out l))
                {
                    if (_links.Count >= MaxLinks + 2 && h[0] != _peer) return;
                    l = new Link { Id = h[0], Started = now };
                    _links[h[0]] = l;
                }
                if (h.Length > 2) l.Locals = h[2];
                if (h.Length > 1 && _pub == "-") _pub = h[1];
                bool answer = !l.Up || !from.Equals(l.Ep);
                if (!l.Up) _cacheDirty = true;
                if (l.Ep != null && !from.Equals(l.Ep)) _byEp.Remove(l.Ep.ToString());
                l.Up = true;
                l.Ep = from;
                l.LastHeard = now;
                _byEp[from.ToString()] = l;
                _known.Remove(l.Id);
                if (answer) Hello(from, l);
                break;
            }
            case "G":
                if (link == null || !link.Up) return;
                foreach (var d in rest.Split(' '))
                {
                    string id = d.Split('/')[0];
                    if (id.Length == 0 || id == _id || _links.ContainsKey(id) || d.Split('/').Length < 3) continue;
                    _known[id] = new Known { Desc = d, Via = link.Id, Seen = now };
                }
                break;
            case "X":
            {
                if (link == null || !link.Up) return;
                Link target;
                if (_links.TryGetValue(rest.Trim(), out target) && target.Up && target != link)
                {
                    ToLink(target, "I " + link.Desc);
                    ToLink(link, "I " + target.Desc);
                }
                break;
            }
            case "R":
                if (link == null || !link.Up) return;
                Routed(link, rest);
                break;
        }
    }

    // ---- routing ----

    // R <src> <dst> <message id> <ttl> <body>; dst "*" goes to every node. A node remembers which neighbour a node's messages
    // first came from and sends messages for that node back the same way; without such a route it floods them.
    static void Routed(Link from, string rest)
    {
        string[] r = rest.Split(new[] { ' ' }, 5);
        if (r.Length < 5) return;
        string src = r[0], dst = r[1];
        int ttl;
        if (src == _id || !int.TryParse(r[3], out ttl)) return;
        if (!FirstTime(src + " " + r[2])) return;
        long now = Now();
        _routes[src] = new Route { Via = from.Id, At = now };     // the first copy to arrive came the fastest way
        if (dst == _id || dst == "*") Message(src, r[4]);
        if (dst != _id && ttl > 1) Forward(src, dst, r[2], ttl - 1, r[4], from.Id);
    }

    static bool FirstTime(string key)
    {
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        if (_seenOrder.Count > SeenKept) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    static void Forward(string src, string dst, string mid, int ttl, string body, string except)
    {
        string packet = "R " + src + " " + dst + " " + mid + " " + ttl + " " + body;
        Link l;
        if (dst != "*" && _links.TryGetValue(dst, out l) && l.Up) { ToLink(l, packet); return; }
        Route route;
        if (dst != "*" && _routes.TryGetValue(dst, out route) && Now() - route.At < RouteFor && route.Via != except
            && _links.TryGetValue(route.Via, out l) && l.Up)
        {
            ToLink(l, packet);
            return;
        }
        foreach (var n in _links.Values)
            if (n.Up && n.Id != except) ToLink(n, packet);
    }

    static void SendTo(string dst, string body)
    {
        string mid = (++_msgId).ToString();
        FirstTime(_id + " " + mid);
        Forward(_id, dst, mid, Ttl, body, null);
    }

    static void Broadcast(string body) { SendTo("*", body); }

    // ---- matchmaking and session ----

    static void Message(string src, string body)
    {
        string[] m = body.Split(new[] { ' ' }, 2);
        string rest = m.Length > 1 ? m[1] : "";
        switch (m[0])
        {
            case "SEEK":            // SEEK <room> <name> <desc>; the lower id offers, so two players never offer to each other
            {
                string[] s = rest.Split(' ');
                if (s.Length < 3 || !_seeking || _peer != null || _offerTo != null || s[0] != _room || string.CompareOrdinal(_id, src) >= 0) return;
                _offerTo = src;
                _offerSid = NewId(8);
                _offerSeed = _random.Next(1, int.MaxValue);
                _offerName = s[1];
                _offerAt = Now();
                SendTo(src, "OFFER " + _offerSid + " " + _offerSeed + " " + _room + " " + _name + " " + MyDesc());
                Punch(s[2]);
                break;
            }
            case "OFFER":           // OFFER <sid> <seed> <room> <name> <desc>
            {
                string[] o = rest.Split(' ');
                if (o.Length < 5) return;
                int seed;
                if (!_seeking || _peer != null || _offerTo != null || o[2] != _room || !int.TryParse(o[1], out seed))
                {
                    SendTo(src, "NO " + o[0]);
                    return;
                }
                StartSession(src, o[0]);
                SendTo(src, "ACCEPT " + o[0] + " " + _name + " " + MyDesc());
                Punch(o[4]);
                Deliver("MATCH 1 " + seed + " " + o[3]);
                break;
            }
            case "ACCEPT":          // ACCEPT <sid> <name> <desc>
            {
                string[] a = rest.Split(' ');
                if (a.Length < 3 || src != _offerTo || a[0] != _offerSid || _peer != null) return;
                Accepted(src, a[1]);
                Punch(a[2]);
                break;
            }
            case "NO":
                if (src == _offerTo && rest == _offerSid) _offerTo = null;
                break;
            case "S":               // S <sid> <ack> <seq> <line>
            {
                string[] s = rest.Split(new[] { ' ' }, 4);
                int ack, seq;
                if (s.Length < 4 || !int.TryParse(s[1], out ack) || !int.TryParse(s[2], out seq)) return;
                if (_peer == null && src == _offerTo && s[0] == _offerSid) Accepted(src, _offerName);   // the ACCEPT was lost
                if (src != _peer || s[0] != _sid) { SendTo(src, "BYE " + s[0]); return; }
                Acked(ack);
                if (seq == _recvNext)
                {
                    Deliver(s[3]);
                    _recvNext++;
                    string next;
                    while (_future.TryGetValue(_recvNext, out next))
                    {
                        _future.Remove(_recvNext);
                        Deliver(next);
                        _recvNext++;
                    }
                }
                else if (seq > _recvNext && _future.Count < 4096) _future[seq] = s[3];
                SendTo(src, "A " + _sid + " " + (_recvNext - 1));
                break;
            }
            case "A":               // A <sid> <ack>
            {
                string[] a = rest.Split(' ');
                int ack;
                if (a.Length >= 2 && src == _peer && a[0] == _sid && int.TryParse(a[1], out ack)) Acked(ack);
                break;
            }
            case "BYE":
                if (src == _peer && rest == _sid)
                {
                    EndSession(false);
                    Deliver("BYE");
                }
                break;
        }
    }

    static void Accepted(string src, string name)
    {
        StartSession(src, _offerSid);
        _offerTo = null;
        Deliver("MATCH 0 " + _offerSeed + " " + name);
    }

    static void StartSession(string peer, string sid)
    {
        _peer = peer;
        _sid = sid;
        _seeking = false;
        _sendSeq = 0;
        _recvNext = 1;
        _unacked.Clear();
        _sentAt.Clear();
        _future.Clear();
    }

    static void Acked(int ack)
    {
        while (_unacked.Count > 0)
        {
            int first = 0;
            foreach (var k in _unacked.Keys) { first = k; break; }
            if (first > ack) break;
            _unacked.Remove(first);
            _sentAt.Remove(first);
        }
    }

    static void EndSession(bool tell)
    {
        if (_peer == null) return;
        if (tell && _sock != null) { SendTo(_peer, "BYE " + _sid); SendTo(_peer, "BYE " + _sid); }
        _peer = null;
        _sid = null;
        _unacked.Clear();
        _sentAt.Clear();
        _future.Clear();
    }

    // ---- helpers ----

    static string NewId(int length)
    {
        const string digits = "0123456789abcdef";
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++) sb.Append(digits[_random.Next(16)]);
        return sb.ToString();
    }

    // This node's addresses on its own networks (for players on the same Wi-Fi), "a:p,b:p" or "-".
    static string LocalEndpoints(int port)
    {
        var list = new List<string>();
        try
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect("10.255.255.255", 9);             // sends nothing; only picks the outgoing interface
                var ip = ((IPEndPoint)s.LocalEndPoint).Address;
                if (!ip.Equals(IPAddress.Any) && !IPAddress.IsLoopback(ip)) list.Add(ip + ":" + port);
            }
        }
        catch (Exception) { }
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address) || list.Count >= 3) continue;
                    string e = ua.Address + ":" + port;
                    if (!list.Contains(e)) list.Add(e);
                }
            }
        }
        catch (Exception) { }
        return list.Count == 0 ? "-" : string.Join(",", list.ToArray());
    }
}
