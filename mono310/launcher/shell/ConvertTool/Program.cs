// PC test harness for Tftf.Convert: converts an extracted assets/bin/Data folder with the same code the launcher runs
// on the phone. Usage: mono ConvertTool.exe <Data dir> <trees.bin> <out bundle>
using System;
using System.IO;
using Tftf.Convert;

class DirSource : IPlayerDataSource
{
    readonly string _dir;
    public DirSource(string dir) { _dir = dir; }
    public byte[] Read(string name)
    {
        string p = Path.Combine(_dir, name);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }
}

static class Program
{
    static int Main(string[] a)
    {
        if (a.Length == 5 && a[0] == "odr")      // odr <9.2.0 apk> <out dir> <cdn> <cl>
        {
            Tftf.Odr.OdrWadBuilder.Build(a[1], a[2], a[3], a[4], Console.WriteLine);
            return 0;
        }
        if (a.Length != 3) { Console.Error.WriteLine("usage: ConvertTool <Data dir> <trees.bin> <out bundle>"); return 2; }
        var db = TreeDb.Load(File.ReadAllBytes(a[1]));
        var stats = new ConvertStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bundle = PlayerDataConverter.Convert(new DirSource(a[0]), db, stats, s => { });
        File.WriteAllBytes(a[2], bundle);
        Console.WriteLine(stats + " bytes=" + bundle.Length + " seconds=" + sw.Elapsed.TotalSeconds.ToString("F1"));
        return 0;
    }
}
