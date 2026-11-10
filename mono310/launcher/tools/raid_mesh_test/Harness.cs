using System;
using System.IO;
using System.Threading;
using Tftf.Odr;
class RaidMeshHarness
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("ok " + name); }
    static void Main(string[] args)
    {
        if (args[0] == "node")
        {
            Directory.CreateDirectory(args[3]); TftfMesh.TestId = args[4]; TftfMesh.CachePath = Path.Combine(args[3], "peers.txt");
            TftfRaidNet.DisplayName = args[4]; TftfRaidNet.Start(args[3], args[1], args[2]);
            int seconds = int.Parse(args[5]); for (int i = 0; i < seconds; i++) { Thread.Sleep(1000); Console.WriteLine(TftfRaidNet.Uid + " " + TftfRaidNet.Status); Console.Out.Flush(); }
            TftfRaidNet.Stop(); TftfMesh.Stop(); return;
        }
        string root = args[1]; Directory.CreateDirectory(root);
        using (var key = new TftfRaidNet.Identity(Path.Combine(root, "key")))
        using (var other = new TftfRaidNet.Identity(Path.Combine(root, "other")))
        {
            long now = TftfRaidNet.Now();
            var fields = (JObj)JsonLite.Parse("{\"name\":\"Test Bot Ω\",\"tag\":\"\",\"shield\":0,\"defenders\":[{\"key\":\"optimusprime_cin_tf\",\"level\":12},{\"key\":\"megatron_gs_leader2015\",\"level\":15}]}");
            var s = TftfRaidNet.Snapshot.Create(key, fields, now);
            Check(s != null && s.Uid == key.Uid && s.Defenders.Count == 2 && s.Name == "Test Bot Ω", "signed snapshot round trip");
            Check(s.Wire.Length <= 1050, "snapshot datagram budget");
            Check(TftfRaidNet.Snapshot.Parse(s.Wire.Replace(":12", ":13"), now) == null, "tampered defender rejected");
            Check(TftfRaidNet.Snapshot.Parse(s.Wire.Replace(key.Public, other.Public), now) == null, "wrong public key rejected");
            Check(TftfRaidNet.Snapshot.Parse(s.Wire, now + 604801) == null, "expired snapshot rejected");
            Check(TftfRaidNet.Snapshot.Parse(new string('x', 1051), now) == null, "oversized snapshot rejected");
            Check(TftfRaidNet.Snapshot.Parse("2" + s.Wire.Substring(1), now) == null, "unknown protocol rejected");
            Check(TftfRaidNet.Snapshot.Parse("not a record", now) == null, "malformed record rejected");
            using (var reloaded = new TftfRaidNet.Identity(Path.Combine(root, "key"))) Check(reloaded.Uid == key.Uid, "identity survives restart");
            var result = (JObj)JsonLite.Parse("{\"_id\":\"raid-result-1\",\"networkDefender\":\"" + other.Uid + "\",\"snapshotVersion\":" + now + ",\"attackerWon\":true,\"endTime\":" + now + ",\"journal\":[{\"action\":\"join\",\"attackTeam\":{\"entities\":[{\"key\":\"optimusprime_cin_tf\"}]}}]}");
            var outcome = TftfRaidNet.Outcome.Create(key, result);
            Check(outcome != null && outcome.Won && outcome.Defender == other.Uid, "signed result round trip");
            Check(TftfRaidNet.Outcome.Parse(outcome.Wire.Replace(other.Uid, key.Uid), now) == null, "tampered result rejected");
        }
    }
}
