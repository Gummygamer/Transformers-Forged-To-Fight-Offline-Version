// Newly authored. The matchmaking relay of online Arena fights, run on this phone (the panel's "Host on this phone"), so
// other phones can connect to it instead of to a computer running Server/pvp_relay.py. Same protocol and behaviour as that
// script (see ../../ONLINE_PVP.md): pair by room code, then forward every line unchanged; BYE when the opponent goes.
// One background thread accepts, one thread per connection reads. Knows no game types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class TftfRelay
{
    const int MaxLine = 4096;
    const int HelloTimeoutMs = 15000;

    sealed class Conn
    {
        public TcpClient Tcp;
        public NetworkStream Stream;
        public string Room, Name;
        public Conn Peer;
        public bool Closed;
        readonly object _send = new object();

        public void Send(string line)
        {
            lock (_send)
            {
                if (Closed) return;
                try
                {
                    byte[] b = Encoding.ASCII.GetBytes(line + "\n");
                    Stream.Write(b, 0, b.Length);
                }
                catch (Exception) { Close(); }
            }
        }

        public void Close()
        {
            Closed = true;
            try { Tcp.Close(); } catch (Exception) { }
        }
    }

    static readonly object _lock = new object();
    static readonly Dictionary<string, Conn> _waiting = new Dictionary<string, Conn>();
    static readonly List<Conn> _all = new List<Conn>();
    static readonly System.Random _random = new System.Random();
    static TcpListener _listener;

    public static bool Running { get { return _listener != null; } }
    public static int Port { get; private set; }
    public static string Error = "";

    public static int Pairs
    {
        get
        {
            lock (_lock)
            {
                int n = 0;
                foreach (var c in _all) if (c.Peer != null && !c.Closed) n++;
                return n / 2;
            }
        }
    }

    public static bool Start(int port)
    {
        Stop();
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Error = "";
            new Thread(() => Accept(listener)) { IsBackground = true, Name = "TftfRelay" }.Start();
            return true;
        }
        catch (Exception e)
        {
            Error = "Could not open port " + port + ": " + e.Message;
            _listener = null;
            return false;
        }
    }

    public static void Stop()
    {
        var listener = _listener;
        _listener = null;
        if (listener != null) try { listener.Stop(); } catch (Exception) { }
        lock (_lock)
        {
            foreach (var c in _all) c.Close();
            _all.Clear();
            _waiting.Clear();
        }
    }

    static void Accept(TcpListener listener)
    {
        while (_listener == listener)
        {
            TcpClient tcp;
            try { tcp = listener.AcceptTcpClient(); }
            catch (Exception) { return; }        // stopped
            tcp.NoDelay = true;
            var c = new Conn { Tcp = tcp, Stream = tcp.GetStream() };
            lock (_lock) _all.Add(c);
            new Thread(() => Serve(c)) { IsBackground = true, Name = "TftfRelayConn" }.Start();
        }
    }

    static void Serve(Conn c)
    {
        try
        {
            using (var reader = new StreamReader(c.Stream, Encoding.ASCII))
            {
                c.Tcp.ReceiveTimeout = HelloTimeoutMs;
                string hello = reader.ReadLine();
                c.Tcp.ReceiveTimeout = 0;
                if (hello == null) return;
                string[] p = hello.Trim().Split(' ');
                if (p.Length < 4 || p[0] != "HELLO") { c.Send("ERR hello"); return; }
                if (p[1] != TftfNet.Protocol) { c.Send("ERR version"); return; }
                c.Room = p[2].Length > 32 ? p[2].Substring(0, 32) : p[2];
                c.Name = string.Join("_", p, 3, p.Length - 3);
                if (c.Name.Length > 24) c.Name = c.Name.Substring(0, 24);
                Conn host = null;
                lock (_lock)
                {
                    Conn other;
                    if (_waiting.TryGetValue(c.Room, out other) && !other.Closed)
                    {
                        _waiting.Remove(c.Room);
                        host = other;
                        host.Peer = c;
                        c.Peer = host;
                    }
                    else _waiting[c.Room] = c;
                }
                if (host == null) c.Send("WAIT");
                else
                {
                    int seed = _random.Next(1, int.MaxValue);
                    host.Send("MATCH 0 " + seed + " " + c.Name);
                    c.Send("MATCH 1 " + seed + " " + host.Name);
                }
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > MaxLine) break;
                    Conn peer = c.Peer;
                    if (peer != null) peer.Send(line);        // nothing is sent before MATCH; such lines are dropped
                }
            }
        }
        catch (Exception) { }
        finally
        {
            Conn peer;
            lock (_lock)
            {
                Conn w;
                if (c.Room != null && _waiting.TryGetValue(c.Room, out w) && w == c) _waiting.Remove(c.Room);
                _all.Remove(c);
                peer = c.Peer;
            }
            c.Close();
            if (peer != null)
            {
                peer.Send("BYE");
                peer.Close();
            }
        }
    }

    // This phone's address on the local network, for the other players to enter ("" when unknown).
    public static string LocalAddress()
    {
        try
        {
            // a UDP "connection" sends nothing; it only makes the system choose the outgoing interface
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect("10.255.255.255", 9);
                var ip = ((IPEndPoint)s.LocalEndPoint).Address;
                if (!IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any)) return ip.ToString();
            }
        }
        catch (Exception) { }
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)) return ua.Address.ToString();
            }
        }
        catch (Exception) { }
        return "";
    }
}
