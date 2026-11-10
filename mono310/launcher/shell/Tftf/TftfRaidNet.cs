// Asynchronous raids use signed records over the existing mesh. No Arena session or combat inputs are involved.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Tftf.Odr;

public static class TftfRaidNet
{
    public const string Content = "tftf-310-raid-v1";
    const long Week = 604800, Fortnight = 1209600;
    const int Limit = 1050;
    public static volatile string DisplayName = "Commander", Status = "Offline — NPC opponents available";
    public static volatile bool Paused;
    public static volatile string LastError = "";
    public static volatile int OwnShield, OwnPower, ReportsChanged;
    static volatile bool _running;
    static Thread _thread;
    static string _root, _url, _bootstrap;
    static readonly Dictionary<string, Snapshot> _store = new Dictionary<string, Snapshot>();
    static readonly Dictionary<string, Outcome> _outbox = new Dictionary<string, Outcome>();
    static readonly Dictionary<string, Outcome> _inbox = new Dictionary<string, Outcome>();
    static readonly Dictionary<string, long> _sent = new Dictionary<string, long>();
    static readonly Dictionary<string, long> _applied = new Dictionary<string, long>();
    static readonly Dictionary<string, long> _requested = new Dictionary<string, long>();
    static readonly Dictionary<string, string> _nodes = new Dictionary<string, string>();
    static readonly Dictionary<string, long> _linkWindow = new Dictionary<string, long>();
    static readonly Dictionary<string, int> _linkCount = new Dictionary<string, int>();
    static readonly HashSet<long> _versions = new HashSet<long>();
    static readonly Dictionary<long, Snapshot> _published = new Dictionary<long, Snapshot>();
    static Snapshot _own;
    static Identity _identity;
    static int _page;
    static long _digestAt, _queryAt, _syncAt;
    public static string Uid { get { return _identity == null ? "" : _identity.Uid; } }
    public static bool Running { get { return _running; } }
    public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
    public static string Text(JObj o, string key, string fallback = "") { return o != null && o[key] != null ? Convert.ToString(o[key], CultureInfo.InvariantCulture) : fallback; }
    public static long Number(JObj o, string key) { long n; return long.TryParse(Text(o, key), out n) ? n : 0; }
    static List<object> Array(JObj o, string key) { return o != null ? o[key] as List<object> ?? new List<object>() : new List<object>(); }
    static JObj Object(params object[] pairs) { var o = new JObj(); for (int i = 0; i < pairs.Length; i += 2) o[(string)pairs[i]] = pairs[i + 1]; return o; }
    static string Escape(string s) { return Uri.EscapeDataString(s ?? ""); }
    static string Unescape(string s) { return Uri.UnescapeDataString(s); }
    static bool Token(string s, int max) { if (string.IsNullOrEmpty(s) || s.Length > max) return false; foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false; return true; }
    static bool NumericUid(string s) { long n; return long.TryParse(s, out n) && n > 0 && n < (1L << 48) && s == n.ToString(CultureInfo.InvariantCulture); }
    static string Encode(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    static byte[] Decode(string text) { string s = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')); }
    static string Key(RSAParameters p) { return Encode(p.Modulus) + ":" + Encode(p.Exponent); }
    static RSAParameters PublicKey(string key)
    {
        string[] p = key.Split(':');
        if (p.Length != 2) throw new FormatException("Invalid raid key");
        var rsa = new RSAParameters { Modulus = Decode(p[0]), Exponent = Decode(p[1]) };
        if (rsa.Modulus.Length != 128 || rsa.Exponent.Length > 4 || rsa.Exponent.Length == 0) throw new FormatException("Invalid raid key size");
        return rsa;
    }
    static string KeyUid(string key)
    {
        var p = PublicKey(key);
        using (var hash = SHA256.Create())
        {
            byte[] bytes = hash.ComputeHash(Encoding.ASCII.GetBytes(Key(p)));
            long uid = 0; for (int i = 0; i < 6; i++) uid = (uid << 8) | bytes[i];
            return uid.ToString(CultureInfo.InvariantCulture);
        }
    }
    static bool Verify(string uid, string key, string body, string signature)
    {
        try
        {
            if (!NumericUid(uid) || KeyUid(key) != uid || signature.Length > 180) return false;
            using (var rsa = new RSACryptoServiceProvider(1024))
            {
                rsa.PersistKeyInCsp = false; rsa.ImportParameters(PublicKey(key));
                return rsa.VerifyData(Encoding.ASCII.GetBytes(body), CryptoConfig.MapNameToOID("SHA256"), Decode(signature));
            }
        }
        catch { return false; }
    }
    public sealed class Identity : IDisposable
    {
        readonly RSACryptoServiceProvider _rsa;
        public readonly string Uid, Public;
        public Identity(string path)
        {
            _rsa = new RSACryptoServiceProvider(1024); _rsa.PersistKeyInCsp = false;
            if (File.Exists(path)) _rsa.FromXmlString(File.ReadAllText(path));
            else Atomic(path, _rsa.ToXmlString(true));
            Public = Key(_rsa.ExportParameters(false)); Uid = KeyUid(Public);
            string proof = Sign("raid-key-self-test");
            if (!Verify(Uid, Public, "raid-key-self-test", proof)) throw new CryptographicException("Raid signing self-test failed");
        }
        public string Sign(string body) { return Encode(_rsa.SignData(Encoding.ASCII.GetBytes(body), CryptoConfig.MapNameToOID("SHA256"))); }
        public void Dispose() { _rsa.Dispose(); }
    }
    public sealed class Snapshot
    {
        public string Uid, Name, Tag, Content, Public, Wire;
        public long Version, Shield;
        public readonly List<object> Defenders = new List<object>();
        public JObj Json() { return Object("uid", Uid, "name", Name, "tag", Tag, "version", Version, "shield", Shield, "content", Content, "defenders", Defenders); }
        public static Snapshot Parse(string wire, long now)
        {
            try
            {
                if (wire == null || wire.Length > Limit) return null;
                string[] f = wire.Split(' '); long version, shield, medals;
                if (f.Length != 11 || f[0] != "1" || !NumericUid(f[1]) || !long.TryParse(f[2], out version) || version > now + 300 || version < now - Week || !long.TryParse(f[3], out shield) || shield < 0 || shield > now + Week || !long.TryParse(f[6], out medals) || medals < 0 || !Token(f[7], 40)) return null;
                var s = new Snapshot { Uid = f[1], Version = version, Shield = shield, Name = Unescape(f[4]), Tag = Unescape(f[5]), Content = f[7], Public = f[9], Wire = wire };
                if (s.Name.Length > 40 || s.Tag.Length > 16 || s.Name.IndexOf('\n') >= 0 || s.Name.IndexOf('\r') >= 0) return null;
                var seen = new HashSet<string>();
                foreach (string item in f[8].Split(','))
                {
                    string[] d = item.Split(':'); int level;
                    if (d.Length != 2 || !Token(d[0], 64) || !seen.Add(d[0]) || !int.TryParse(d[1], out level) || level < 1 || level > 100 || s.Defenders.Count >= 8) return null;
                    s.Defenders.Add(Object("key", d[0], "level", level));
                }
                if (s.Defenders.Count == 0 || !Verify(s.Uid, s.Public, wire.Substring(0, wire.LastIndexOf(' ')), f[10])) return null;
                return s;
            }
            catch { return null; }
        }
        public static Snapshot Create(Identity key, JObj fields, long version)
        {
            string name = Text(fields, "name", "Commander"), tag = Text(fields, "tag");
            if (name.Length > 40) name = name.Substring(0, 40); if (tag.Length > 16) tag = tag.Substring(0, 16);
            var defenders = new List<string>();
            foreach (object item in Array(fields, "defenders")) { var d = item as JObj; string bid = Text(d, "key"); if (Token(bid, 64)) defenders.Add(bid + ":" + Number(d, "level")); if (defenders.Count == 8) break; }
            while (defenders.Count > 0)
            {
                string body = "1 " + key.Uid + " " + version + " " + Number(fields, "shield") + " " + Escape(name) + " " + Escape(tag) + " 0 " + TftfRaidNet.Content + " " + string.Join(",", defenders.ToArray()) + " " + key.Public;
                string wire = body + " " + key.Sign(body);
                if (wire.Length <= Limit) return Parse(wire, Now());
                defenders.RemoveAt(defenders.Count - 1);
            }
            return null;
        }
    }
    public sealed class Outcome
    {
        public string Id, Defender, Attacker, Name, Public, Wire;
        public long Version, End, LastSent;
        public bool Won;
        public List<object> Team = new List<object>(), Attackers = new List<object>(), Defenders = new List<object>();
        public JObj Json() { return Object("id", Id, "defender", Defender, "attacker", Attacker, "version", Version, "end", End, "won", Won, "name", Name, "team", Team, "attackers", Attackers, "defenders", Defenders); }
        public static Outcome Parse(string wire, long now)
        {
            try
            {
                if (wire == null || wire.Length > Limit) return null;
                string[] f = wire.Split(' '); long version, end;
                if (f.Length != 11 || f[0] != "1" || !Token(f[1], 100) || !NumericUid(f[2]) || !NumericUid(f[3]) || f[2] == f[3] || !long.TryParse(f[4], out version) || version < now - Fortnight || version > now + 300 || (f[5] != "0" && f[5] != "1") || !long.TryParse(f[6], out end) || end < now - Fortnight || end > now + 300) return null;
                var r = new Outcome { Id = f[1], Defender = f[2], Attacker = f[3], Version = version, Won = f[5] == "1", End = end, Name = Unescape(f[8]), Public = f[9], Wire = wire };
                if (!r.Id.StartsWith(r.Attacker + "-", StringComparison.Ordinal) || r.Name.Length > 40) return null;
                string[] health = f[7].Split('|'); if (health.Length != 2) return null;
                var seen = new HashSet<string>();
                foreach (string item in health[0].Split(','))
                {
                    string[] d = item.Split(':'); double hp;
                    if (d.Length != 2 || !Token(d[0], 64) || !seen.Add(d[0]) || r.Team.Count >= 3 || !Health(d[1], out hp)) return null;
                    r.Team.Add(d[0]); r.Attackers.Add(Object("key", d[0], "hp", hp));
                }
                var indices = new HashSet<int>();
                if (health[1] != "-") foreach (string item in health[1].Split(','))
                {
                    string[] d = item.Split(':'); int index; double hp;
                    if (d.Length != 2 || !int.TryParse(d[0], out index) || index < 0 || index >= 8 || !indices.Add(index) || !Health(d[1], out hp)) return null;
                    r.Defenders.Add(Object("index", index, "hp", hp));
                }
                return Verify(r.Attacker, r.Public, wire.Substring(0, wire.LastIndexOf(' ')), f[10]) ? r : null;
            }
            catch { return null; }
        }
        static bool Health(string text, out double hp)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hp) && !double.IsNaN(hp) && !double.IsInfinity(hp) && hp >= 0 && hp <= 1;
        }
        static double Hp(JObj entry)
        {
            double hp; return Health(Text(entry, "hp", "1"), out hp) ? hp : 1;
        }
        public static Outcome Create(Identity key, JObj result)
        {
            var team = new List<string>();
            foreach (object entry in Array(result, "journal"))
            {
                var e = entry as JObj; if (Text(e, "action") != "join") continue;
                foreach (object item in Array(e != null ? e["attackTeam"] as JObj : null, "entities")) { string bid = Text(item as JObj, "key"); if (Token(bid, 64) && team.Count < 3) team.Add(bid); }
            }
            if (team.Count == 0) return null;
            var attackers = new Dictionary<string, double>(); foreach (string bid in team) attackers[bid] = 1;
            var defenders = new Dictionary<string, int>(); var defenderHp = new List<double>();
            foreach (object entry in Array(result, "journal"))
            {
                var e = entry as JObj;
                if (Text(e, "action") == "start") foreach (object item in Array(e["defenseTeam"] as JObj, "entities"))
                {
                    string bid = Text(item as JObj, "key"); if (!defenders.ContainsKey(bid) && defenderHp.Count < 8) { defenders[bid] = defenderHp.Count; defenderHp.Add(1); }
                }
                var updates = e == null ? null : e["updates"] as JObj;
                foreach (object item in Array(updates, "attackers")) { var d = item as JObj; string bid = Text(d, "key"); if (attackers.ContainsKey(bid)) attackers[bid] = Hp(d); }
                foreach (object item in Array(updates, "defenders")) { var d = item as JObj; int index; if (defenders.TryGetValue(Text(d, "key"), out index)) defenderHp[index] = Hp(d); }
            }
            var ah = new List<string>(); foreach (string bid in team) ah.Add(bid + ":" + attackers[bid].ToString("0.####", CultureInfo.InvariantCulture));
            var dh = new List<string>(); for (int i = 0; i < defenderHp.Count; i++) dh.Add(i + ":" + defenderHp[i].ToString("0.####", CultureInfo.InvariantCulture));
            string health = string.Join(",", ah.ToArray()) + "|" + (dh.Count == 0 ? "-" : string.Join(",", dh.ToArray()));
            string body = "1 " + key.Uid + "-" + Text(result, "_id") + " " + Text(result, "networkDefender") + " " + key.Uid + " " + Number(result, "snapshotVersion") + " " + (Text(result, "attackerWon") == "True" ? "1" : "0") + " " + Number(result, "endTime") + " " + health + " " + Escape(DisplayName) + " " + key.Public;
            return Parse(body + " " + key.Sign(body), Now());
        }
    }
    public static void Atomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp"; File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
    static string PathFor(string suffix) { return Path.Combine(_root, "tftf_raid_" + suffix + ".txt"); }
    static string[] Lines(string suffix) { string p = PathFor(suffix); return File.Exists(p) ? File.ReadAllLines(p) : new string[0]; }
    static void Save()
    {
        var lines = new List<string>(); foreach (var s in _store.Values) lines.Add(s.Wire); Atomic(PathFor("store"), string.Join("\n", lines.ToArray()));
        lines.Clear(); foreach (var r in _outbox.Values) lines.Add(r.Wire); Atomic(PathFor("outbox"), string.Join("\n", lines.ToArray()));
        lines.Clear(); foreach (var r in _inbox.Values) lines.Add(r.Wire); Atomic(PathFor("inbox"), string.Join("\n", lines.ToArray()));
        lines.Clear(); foreach (var s in _published.Values) if (s.Version > Now() - Week) lines.Add(s.Wire); Atomic(PathFor("versions"), string.Join("\n", lines.ToArray()));
        SaveIds("sent", _sent); SaveIds("applied", _applied);
        if (_own != null) Atomic(PathFor("own"), _own.Wire);
    }
    static void SaveIds(string name, Dictionary<string, long> ids)
    {
        while (ids.Count > 2048) { string oldest = null; foreach (var kv in ids) if (oldest == null || kv.Value < ids[oldest]) oldest = kv.Key; ids.Remove(oldest); }
        var lines = new List<string>(); var remove = new List<string>();
        foreach (var kv in ids) { if (kv.Value < Now() - Fortnight) remove.Add(kv.Key); else lines.Add(kv.Key + " " + kv.Value); }
        foreach (string id in remove) ids.Remove(id);
        Atomic(PathFor(name), string.Join("\n", lines.ToArray()));
    }
    static void LoadIds(string name, Dictionary<string, long> ids)
    {
        foreach (string line in Lines(name)) { var p = line.Split(' '); long n; if (p.Length == 2 && long.TryParse(p[1], out n) && n > Now() - Fortnight) ids[p[0]] = n; }
    }
    static void Accept(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.Uid == Uid) return;
        Snapshot previous;
        if (_store.TryGetValue(snapshot.Uid, out previous) && previous.Version >= snapshot.Version) return;
        _store[snapshot.Uid] = snapshot;
        if (_store.Count > 256) { Snapshot oldest = null; foreach (var s in _store.Values) if (oldest == null || s.Version < oldest.Version) oldest = s; _store.Remove(oldest.Uid); _nodes.Remove(oldest.Uid); }
    }
    public static void Start(string root, string bootstrap, string serverUrl = "http://127.0.0.1:8080")
    {
        if (_running) return;
        _root = root; _bootstrap = bootstrap; _url = serverUrl.TrimEnd('/'); _running = true;
        _thread = new Thread(Work) { IsBackground = true, Name = "TftfRaidNet" }; _thread.Start();
    }
    public static void Stop() { _running = false; if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(5000); }
    static void Work()
    {
        try
        {
            Directory.CreateDirectory(_root); _identity = new Identity(PathFor("key"));
            foreach (string line in Lines("store")) Accept(Snapshot.Parse(line, Now()));
            foreach (string line in Lines("outbox")) { var r = Outcome.Parse(line, Now()); if (r != null && r.Attacker == Uid && _outbox.Count < 64) _outbox[r.Id] = r; }
            foreach (string line in Lines("inbox")) { var r = Outcome.Parse(line, Now()); if (r != null && r.Defender == Uid && _inbox.Count < 64) _inbox[r.Id] = r; }
            foreach (string line in Lines("versions")) { var s = Snapshot.Parse(line, Now()); if (s != null && s.Uid == Uid) { _versions.Add(s.Version); _published[s.Version] = s; } }
            LoadIds("sent", _sent); LoadIds("applied", _applied);
            string own = PathFor("own"); if (File.Exists(own)) { var s = Snapshot.Parse(File.ReadAllText(own), Now()); if (s != null && s.Uid == Uid) _own = s; }
            if (!string.IsNullOrEmpty(_bootstrap)) TftfMesh.Join(_bootstrap);
            while (_running)
            {
                if (!Paused)
                {
                    try { Tick(); }
                    catch (Exception e) { LastError = e.GetType().Name; Status = "Offline — NPC opponents available"; }
                }
                Thread.Sleep(500);
            }
            Save();
        }
        catch (Exception e) { LastError = e.GetType().Name; Status = "Offline — NPC opponents available"; }
        finally { if (_identity != null) _identity.Dispose(); _running = false; }
    }
    static JObj Http(string route, JObj body = null)
    {
        var request = (HttpWebRequest)WebRequest.Create(_url + route); request.Timeout = 4000; request.ReadWriteTimeout = 4000; request.Proxy = null;
        if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(JsonLite.Write(body, false)); request.Method = "POST"; request.ContentType = "application/json"; request.ContentLength = bytes.Length; using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
        using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
        {
            string text = reader.ReadToEnd(); if (text.Length > 2000000) throw new IOException("Raid bridge response too large");
            var envelope = JsonLite.Parse(text) as JObj; if (envelope == null || envelope["error"] != null || !(envelope["result"] is JObj)) throw new IOException("Raid bridge rejected request");
            return (JObj)envelope["result"];
        }
    }
    static string Fingerprint(JObj fields) { return JsonLite.Write(Object("name", fields["name"], "tag", fields["tag"], "shield", fields["shield"], "content", fields["content"], "defenders", fields["defenders"]), false); }
    static void Sync(long now)
    {
        var fields = Http("/tftf/raid/fields"); fields["name"] = DisplayName;
        OwnShield = (int)Math.Min(int.MaxValue, Number(fields, "shield"));
        OwnPower = (int)Math.Min(int.MaxValue, Number(fields, "power"));
        var candidate = Snapshot.Create(_identity, fields, _own == null ? now : _own.Version);
        if (candidate == null) _own = null;
        else if (_own == null || Fingerprint(candidate.Json()) != Fingerprint(_own.Json()) || _own.Version < now - 86400)
        {
            _own = Snapshot.Create(_identity, fields, Math.Max(now, _own == null ? 0 : _own.Version + 1));
            if (_own != null)
            {
                _versions.Add(_own.Version); _published[_own.Version] = _own;
                while (_published.Count > 256) { long oldest = long.MaxValue; foreach (long v in _published.Keys) oldest = Math.Min(oldest, v); _published.Remove(oldest); _versions.Remove(oldest); }
            }
            Save();
        }
        foreach (object item in Array(fields, "outgoing"))
        {
            var result = item as JObj; string id = Uid + "-" + Text(result, "_id");
            if (_sent.ContainsKey(id) || _outbox.ContainsKey(id) || _outbox.Count >= 64) continue;
            var outcome = Outcome.Create(_identity, result); if (outcome != null) { _outbox[outcome.Id] = outcome; Save(); }
        }
        var snapshots = new List<object>(); var stale = new List<string>();
        foreach (var s in _store.Values) { if (s.Version < now - Week) stale.Add(s.Uid); else snapshots.Add(s.Json()); }
        foreach (string uid in stale) { _store.Remove(uid); _nodes.Remove(uid); }
        Outcome incoming = null; foreach (var r in _inbox.Values) { incoming = r; break; }
        JObj report = incoming == null ? new JObj() : incoming.Json();
        if (incoming != null) { Snapshot published; if (_published.TryGetValue(incoming.Version, out published)) report["snapshot"] = published.Json(); }
        Http("/tftf/raid/sync", Object("uid", Uid, "name", DisplayName, "snapshots", snapshots, "incoming", report));
        if (incoming != null) { _applied[incoming.Id] = now; _inbox.Remove(incoming.Id); Save(); ReportsChanged++; Ack(incoming); }
    }
    static void Ack(Outcome result)
    {
        string body = "1 " + result.Id + " " + result.Attacker + " " + Uid + " " + _identity.Public;
        TftfMesh.SendService("*", "RK " + body + " " + _identity.Sign(body));
    }
    static void Handle(TftfMesh.ServicePacket packet, long now)
    {
        int space = packet.Body.IndexOf(' '); if (space < 0) return;
        string command = packet.Body.Substring(0, space), rest = packet.Body.Substring(space + 1);
        if (command == "RD" && packet.Neighbour)
        {
            int requests = 0;
            foreach (string entry in rest.Split(','))
            {
                string[] f = entry.Split(':'); long version; if (f.Length < 2 || !NumericUid(f[0]) || !long.TryParse(f[1], out version)) continue;
                if (f.Length == 3 && f[2] == packet.Source && _store.ContainsKey(f[0]))
                {
                    string previous; bool changed = !_nodes.TryGetValue(f[0], out previous) || previous != f[2]; _nodes[f[0]] = f[2];
                    if (changed) foreach (var r in _outbox.Values) if (r.Defender == f[0]) r.LastSent = 0;
                }
                Snapshot s; long requested;
                if (_requested.Count >= 4096 || f[0] == Uid || (_store.TryGetValue(f[0], out s) && s.Version >= version) || (_requested.TryGetValue(f[0], out requested) && now - requested < 60) || requests >= 4) continue;
                TftfMesh.ToNeighbour(packet.Source, "RG " + f[0]); _requested[f[0]] = now; requests++;
            }
        }
        else if (command == "RG" && packet.Neighbour)
        {
            long window; if (!_linkWindow.TryGetValue(packet.Source, out window) || now - window >= 60) { _linkWindow[packet.Source] = now; _linkCount[packet.Source] = 0; }
            if (_linkCount[packet.Source] >= 4) return; _linkCount[packet.Source]++;
            Snapshot s; if (_own != null && rest == Uid) TftfMesh.ToNeighbour(packet.Source, "RS " + _own.Wire); else if (_store.TryGetValue(rest, out s)) TftfMesh.ToNeighbour(packet.Source, "RS " + s.Wire);
        }
        else if ((command == "RS" && packet.Neighbour) || command == "RA") { var s = Snapshot.Parse(rest, now); if (s != null) { Accept(s); Save(); } }
        else if (command == "RQ" && !packet.Neighbour)
        {
            long previous; string key = "query-" + packet.Source; if (_requested.Count >= 4096 || (_requested.TryGetValue(key, out previous) && now - previous < 60)) return; _requested[key] = now;
            if (_own != null) TftfMesh.SendService(packet.Source, "RA " + _own.Wire);
        }
        else if (command == "RR")
        {
            var r = Outcome.Parse(rest, now); if (r == null || r.Defender != Uid || !_versions.Contains(r.Version)) return;
            if (_applied.ContainsKey(r.Id)) { Ack(r); return; }
            if (_inbox.Count < 64) { _inbox[r.Id] = r; Save(); _syncAt = 0; }
        }
        else if (command == "RK")
        {
            string[] f = rest.Split(' '); Outcome r;
            if (f.Length != 6 || f[0] != "1" || f[2] != Uid || !_outbox.TryGetValue(f[1], out r) || f[3] != r.Defender || !Verify(f[3], f[4], rest.Substring(0, rest.LastIndexOf(' ')), f[5])) return;
            _outbox.Remove(r.Id); _sent[r.Id] = now; Save();
        }
    }
    static void Tick()
    {
        long now = Now();
        foreach (var p in TftfMesh.DrainServices()) Handle(p, now);
        if (now - _syncAt >= 5) { Sync(now); _syncAt = now; }
        if (now - _digestAt >= 30)
        {
            var expiredRequests = new List<string>(); foreach (var kv in _requested) if (kv.Value < now - 300) expiredRequests.Add(kv.Key); foreach (string key in expiredRequests) _requested.Remove(key);
            var oldLinks = new List<string>(); foreach (var kv in _linkWindow) if (kv.Value < now - 300) oldLinks.Add(kv.Key); foreach (string key in oldLinks) { _linkWindow.Remove(key); _linkCount.Remove(key); }
            var entries = new List<string>(); if (_own != null) entries.Add(Uid + ":" + _own.Version + ":" + TftfMesh.Id);
            var store = new List<Snapshot>(_store.Values); store.Sort((a, b) => b.Version.CompareTo(a.Version));
            for (int i = 0; i < Math.Min(28, store.Count); i++) { var s = store[(_page + i) % store.Count]; entries.Add(s.Uid + ":" + s.Version); }
            if (store.Count > 0) _page = (_page + 28) % store.Count;
            string digest = "RD " + string.Join(",", entries.ToArray()); if (entries.Count > 0) foreach (string link in TftfMesh.LinkIds()) TftfMesh.ToNeighbour(link, digest);
            _digestAt = now;
        }
        if (_store.Count < 3 && now - _queryAt >= 60) { TftfMesh.SendService("*", "RQ 0 3", 4); _queryAt = now; }
        if (TftfMesh.Neighbours > 0) foreach (var r in _outbox.Values)
        {
            if (now - r.LastSent < 600) continue; string node; TftfMesh.SendService(_nodes.TryGetValue(r.Defender, out node) ? node : "*", "RR " + r.Wire); r.LastSent = now;
        }
        Status = string.IsNullOrEmpty(_bootstrap) || TftfMesh.Neighbours == 0 ? (_store.Count == 0 ? "Offline — NPC opponents available" : "Offline — " + _store.Count + " cached player bases") : "Online — " + _store.Count + " player bases";
    }
}
