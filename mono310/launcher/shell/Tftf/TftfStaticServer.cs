// Newly authored. Serves the on-device ODR content (manifest-referenced toc.zip / toc.wad) on 127.0.0.1:8090 with
// HTTP Range support, replacing Server/odr_static_server.py. Plain sockets: HttpListener pulls in Mono.Security, which
// the Unity 2020 player does not ship.
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class TftfStaticServer
{
    public const int Port = 8090;
    static TcpListener listener;

    public static void Start(string root)
    {
        if (listener != null) return;
        listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        var t = new Thread(() => Loop(root)) { IsBackground = true, Name = "TftfStaticServer" };
        t.Start();
        UnityEngine.Debug.Log("[TFTF] static server on :" + Port + " root=" + root);
    }

    static void Loop(string root)
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(root, c));
        }
    }

    static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return b < 0 && sb.Length == 0 ? null : sb.ToString();
    }

    static void Serve(string root, TcpClient c)
    {
        try
        {
            using (c)
            {
                c.SendTimeout = 60000; c.ReceiveTimeout = 60000;
                var net = c.GetStream();
                while (true)   // keep-alive
                {
                    string req = ReadLine(net);
                    if (string.IsNullOrEmpty(req)) return;
                    string range = null;
                    string h;
                    while (!string.IsNullOrEmpty(h = ReadLine(net)))
                        if (h.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = h.Substring(6).Trim();
                    var parts = req.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0];
                    string rel = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
                    if (rel.StartsWith("odr/", StringComparison.Ordinal)) rel = rel.Substring(4);
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) || !File.Exists(full))
                    {
                        Send(net, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long total = fs.Length, start = 0, end = total - 1;
                        string status = "200 OK", extra = "";
                        if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                        {
                            var r = range.Substring(6).Split('-');
                            if (r[0].Length > 0) { start = long.Parse(r[0], CultureInfo.InvariantCulture); if (r.Length > 1 && r[1].Length > 0) end = Math.Min(end, long.Parse(r[1], CultureInfo.InvariantCulture)); }
                            else if (r.Length > 1 && r[1].Length > 0) start = Math.Max(0, total - long.Parse(r[1], CultureInfo.InvariantCulture));
                            status = "206 Partial Content";
                            extra = "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n";
                        }
                        long count = Math.Max(0, end - start + 1);
                        Send(net, "HTTP/1.1 " + status + "\r\nAccept-Ranges: bytes\r\n" + extra +
                                  "Content-Type: application/octet-stream\r\nContent-Length: " + count + "\r\n\r\n");
                        if (method != "HEAD")
                        {
                            fs.Seek(start, SeekOrigin.Begin);
                            var buf = new byte[256 * 1024];
                            while (count > 0)
                            {
                                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
                                if (n <= 0) break;
                                net.Write(buf, 0, n);
                                count -= n;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception) { }          // a client closing its connection is normal
    }

    static void Send(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }
}
