// Newly authored. The few UnityEngine members TftfNet uses, so the lockstep test runs without Unity (run.sh).
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public static class Time
    {
        static readonly System.Diagnostics.Stopwatch _w = System.Diagnostics.Stopwatch.StartNew();
        public static float realtimeSinceStartup { get { return (float)_w.Elapsed.TotalSeconds; } }
        public static float fixedTime = 0f;
        public static float fixedDeltaTime = 1f / 30f;
    }
    public static class PlayerPrefs
    {
        static readonly Dictionary<string, string> _d = new Dictionary<string, string>();
        public static string GetString(string k, string d) { string v; return _d.TryGetValue(k, out v) ? v : d; }
        public static void SetString(string k, string v) { _d[k] = v; }
        public static void Save() { }
    }
    public static class Debug
    {
        public static void Log(object o) { System.Console.Error.WriteLine(o); }
        public static void LogWarning(object o) { System.Console.Error.WriteLine("W " + o); }
    }
    public static class Application { public static string persistentDataPath { get { return System.IO.Path.GetTempPath(); } } }
    public static class Random { public static float value { get { return 0.5f; } } }
}
