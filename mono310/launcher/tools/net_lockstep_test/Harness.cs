// Newly authored. One phone's side of the lockstep test (run.sh): matches through the P2P network or the relay, plays fights of random inputs
// with timing jitter and writes, per tick, both fighters' inputs, random rolls and clock in the host's order.
using System; using System.IO; using System.Linq; using System.Threading;
static class Harness
{
    static void Main(string[] a)
    {
        if (a[0] == "node")              // a P2P network node that only carries other players' lines: node <bootstrap port> <id>
        {
            TftfMesh.TestId = a[2];
            string err = TftfMesh.Join("127.0.0.1:" + a[1]);
            if (err != null) { Console.Error.WriteLine(err); Environment.Exit(3); }
            Thread.Sleep(Timeout.Infinite);
        }
        string name = a[0]; int port = int.Parse(a[1]); int ticks = int.Parse(a[2]); string outPath = a[3]; int rounds = int.Parse(a[4]);
        bool dropMid = a.Length > 5 && a[5] == "drop";
        // MESH_ID set: through the P2P network with bootstrap port `port`; MESH_BLOCK: node ids this one cannot reach directly
        // MESH_SUFFIX: appended to every id, so that a second run is a new game as a restarted game is (fresh id)
        string meshId = Environment.GetEnvironmentVariable("MESH_ID"), block = Environment.GetEnvironmentVariable("MESH_BLOCK") ?? "";
        string suffix = Environment.GetEnvironmentVariable("MESH_SUFFIX") ?? "";
        if (!string.IsNullOrEmpty(meshId))
        {
            meshId += suffix;
            TftfMesh.TestId = meshId;
            TftfMesh.TestBlock = id => block.Split(',').Any(b => b.Length > 0 && b + suffix == id);
        }
        TftfMesh.CachePath = Environment.GetEnvironmentVariable("MESH_CACHE") ?? "";   // empty: remembers nothing
        TftfNet.Room = "t"; TftfNet.NameProvider = () => name;
        UnityEngine.Time.fixedTime = name == "A" ? 1234.5f : 98.25f;
        if (string.IsNullOrEmpty(meshId)) { TftfNet.Address = "127.0.0.1:" + port; TftfNet.Connect(); }
        else                             // as the Arena joins; MESH_BOOTSTRAP: a real node (host:port) instead of the local one
        {
            string bootstrap = Environment.GetEnvironmentVariable("MESH_BOOTSTRAP");
            Environment.SetEnvironmentVariable("TFTF_P2P_BOOTSTRAP", string.IsNullOrEmpty(bootstrap) ? "127.0.0.1:" + port : bootstrap);
            TftfNet.JoinOnline();
        }
        var until = DateTime.Now.AddSeconds(10);
        while (TftfNet.Status != TftfNet.State.Matched) { TftfNet.Pump(); Thread.Sleep(5); if (DateTime.Now > until || TftfNet.Status == TftfNet.State.Failed) { Console.Error.WriteLine("no match " + TftfNet.Message); Environment.Exit(2); } }
        var rnd = new System.Random(name.GetHashCode());
        using (var w = new StreamWriter(outPath))
        {
            for (int r = 0; r < rounds; r++)
            {
                int round = TftfNet.SendHero(new TftfNet.Hero { Blueprint = "bot_" + name, Rank = 1, Level = 2, Sig = 0, Scene = "chicago" });
                TftfNet.Hero h = null;
                while ((h = TftfNet.RemoteHero(round)) == null) { TftfNet.Pump(); Thread.Sleep(2); }
                w.WriteLine("round " + round + " opp " + h.Blueprint);
                TftfNet.Arm(round); TftfNet.BeginLockstep();
                w.WriteLine("clock " + TftfNet.SimTime);
                int stalls = 0;
                while (TftfNet.LockstepActive && TftfNet.Tick < ticks)
                {
                    if (rnd.NextDouble() < 0.3) TftfNet.LocalEvent("aAbBs1234"[rnd.Next(9)]);
                    if (rnd.NextDouble() < 0.05) Thread.Sleep(rnd.Next(1, 60));   // jitter
                    if (dropMid && TftfNet.Tick == ticks / 2) { TftfNet.Disconnect(); break; }
                    if (!TftfNet.NextTickReady()) { stalls++; Thread.Sleep(1); continue; }
                    string l, rm;
                    if (!TftfNet.Advance(1f / 30f, out l, out rm)) { stalls++; continue; }
                    int t = TftfNet.Tick - 1;
                    string c0 = TftfNet.IsHost ? l : rm, c1 = TftfNet.IsHost ? rm : l;
                    int id0 = TftfNet.Canonical(0), id1 = TftfNet.Canonical(1);
                    float r0 = TftfNet.RandomValue(id0), r1 = TftfNet.RandomValue(id1), rs = TftfNet.RandomValue(-1);
                    w.WriteLine(t + " " + c0 + "|" + c1 + " " + r0.ToString("R") + " " + r1.ToString("R") + " " + rs.ToString("R") + " " + TftfNet.SimTime.ToString("R"));
                    if (TftfNet.IsHost && t == ticks - 40) TftfNet.SendKnockout(1);
                    Thread.Sleep(rnd.Next(0, 3));
                }
                Console.Error.WriteLine(name + " round " + round + " ended tick " + TftfNet.Tick + " stalls " + stalls + " active " + TftfNet.LockstepActive + " lost " + TftfNet.LostInFight + " msg " + TftfNet.Message);
                if (r == 0 && !string.IsNullOrEmpty(meshId)) Console.Error.WriteLine(name + " direct " + TftfMesh.Direct);
                TftfNet.EndLockstep();
                TftfNet.LostInFight = false;
            }
        }
        Thread.Sleep(200);
        TftfNet.Disconnect();
        Thread.Sleep(100);
        TftfMesh.Stop();                 // the node outlives a match; Mono does not exit while its thread is in a socket call
    }
}
