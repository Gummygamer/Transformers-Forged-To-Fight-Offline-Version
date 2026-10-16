// Newly authored. Serves the on-device ODR content (manifest-referenced toc.zip / toc.wad) on 127.0.0.1:8090 with
// HTTP Range support, replacing Server/odr_static_server.py.
using System;
using System.IO;
using System.Net;
using System.Threading;

public static class TftfStaticServer
{
    public const int Port = 8090;
    static HttpListener listener;

    public static void Start(string root)
    {
        if (listener != null) return;
        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
        listener.Start();
        var t = new Thread(() => Loop(root)) { IsBackground = true, Name = "TftfStaticServer" };
        t.Start();
        UnityEngine.Debug.Log("[TFTF] static server on :" + Port + " root=" + root);
    }

    static void Loop(string root)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(root, ctx));
        }
    }

    static void Serve(string root, HttpListenerContext ctx)
    {
        try
        {
            string rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).TrimStart('/');
            if (rel.StartsWith("odr/", StringComparison.Ordinal)) rel = rel.Substring(4);
            string full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
            }
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long total = fs.Length, start = 0, end = total - 1;
                string range = ctx.Request.Headers["Range"];
                ctx.Response.AddHeader("Accept-Ranges", "bytes");
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var parts = range.Substring(6).Split('-');
                    if (parts[0].Length > 0) { start = long.Parse(parts[0]); if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1])); }
                    else if (parts[1].Length > 0) start = Math.Max(0, total - long.Parse(parts[1]));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + total);
                }
                long count = Math.Max(0, end - start + 1);
                ctx.Response.ContentLength64 = count;
                ctx.Response.ContentType = "application/octet-stream";
                if (ctx.Request.HttpMethod != "HEAD")
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[256 * 1024];
                    while (count > 0)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
                        if (n <= 0) break;
                        ctx.Response.OutputStream.Write(buf, 0, n);
                        count -= n;
                    }
                }
            }
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }
}
