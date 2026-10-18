#!/usr/bin/env python3
"""Mechanical repairs so ILSpy output of the 3.1.0 (Unity 5.4 / mcs) assemblies compiles
under Unity 2020.3 / C# 8. Each repair class logs how many files/sites it touched.
Usage: compat310.py <Assets dir>   (also imported by assemble_project.py)
"""
import os, re, sys
from pathlib import Path
from collections import OrderedDict

LOG = OrderedDict()

def note(cls, n=1):
    LOG[cls] = LOG.get(cls, 0) + n

def rd(p): return p.read_text(encoding="utf-8-sig")
def wr(p, t): p.write_text(t, encoding="utf-8")

def match_brace(t, i):
    d = 0
    for j in range(i, len(t)):
        if t[j] == "{": d += 1
        elif t[j] == "}":
            d -= 1
            if d == 0: return j
    raise ValueError

# P20/P22/P26 and the default-valued G3 signature match froze the title screen on device (bisected); kept disabled.
SKIP = ("P20 ","P22 ","P26 ")
def replace_in(root, rel, old, new, cls, regex=False):
    if cls.startswith(SKIP): return
    p = root / rel
    if not p.exists(): return
    t = rd(p)
    n = len(re.findall(old, t)) if regex else t.count(old)
    if n:
        t = re.sub(old, new, t) if regex else t.replace(old, new)
        wr(p, t); note(cls, n)

def generic(root):
    for p in list(root.rglob("*.cs")):
        t = rd(p); o = t
        # G1: ILSpy marks explicit interface impls 'virtual' (illegal)
        t = re.sub(r"(?m)^(\s*)virtual (?=[\w<>\[\],\.? ]+ [\w<>\.]+\.\w+\s*(=>|\{|\())", r"\1", t)
        if t != o: note("G1 drop 'virtual' on explicit interface impl")
        # G2: mangled (_002E) compiler-generated explicit impl duplicates: drop the member
        for m in list(re.finditer(r"(?m)^[ \t]*(?:virtual )?[\w<>\[\],\.]+ System_002E\w+\s*(?=\{|\()", t)):
            pass
        while True:
            m = re.search(r"(?m)^[ \t]*(?:virtual )?[\w<>\[\],\.]+ System_002E\w+[^\n{]*\n\s*\{", t)
            if not m: break
            end = match_brace(t, t.index("{", m.start()))
            t = t[:m.start()] + t[end + 1:]
            note("G2 drop mangled _002E explicit-interface duplicate")
        # G3: ILSpy loses iterator closure captures: "T name2 = default(T);" where the method has parameter "T name"
        lines = t.split("\n"); changed = 0
        for i, ln in enumerate(lines):
            m = re.match(r"^(\s*)(.+?) (\w+?)(\d+) = default\((.+)\);\s*$", ln)
            if not m or m.group(2) != m.group(5): continue
            typ, base = m.group(2), m.group(3)
            sig = re.compile(r"[\(,]\s*" + re.escape(typ) + r" " + re.escape(base) + r"\s*[,\)]")
            if any(sig.search(lines[j]) for j in range(max(0, i - 90), i)):
                lines[i] = f"{m.group(1)}{typ} {base}{m.group(4)} = {base};"; changed += 1
        if changed:
            t = "\n".join(lines); note("G3 iterator closure alias restored (default -> parameter)", changed)
        if t != o: wr(p, t)

def specific(fp, main):
    replace_in(fp, "EB/DownloadHandlerStream.cs", "UnityEngine.Experimental.Networking", "UnityEngine.Networking",
               "S1 Experimental.Networking -> Networking")
    for rel in ("FontLocalizer.cs", "UIMaskMaterialManager.cs", "EB/Assets.cs"):
        replace_in(fp, rel, r"(?<![\w.])Tuple<", "EB.Collections.Tuple<", "S2 ambiguous Tuple<>", regex=True)
    for rel in ("GooglePlayGames/Native/ConversionUtils.cs", "GooglePlayGames/Native/NativeClient.cs"):
        replace_in(fp, rel, r"(?<![\w.])Types\.", "GooglePlayGames.Native.Cwrapper.Types.", "S3 ambiguous Types", regex=True)
    replace_in(fp, "EB/DownloadExtractor.cs", r"(?<![\w.])WebRequest\b", "EB.Net.WebRequest", "S4 ambiguous WebRequest", regex=True)
    replace_in(fp, "EB/UI/DataBinding/ProcessMemberBinding.cs",
               "const BindingFlags BindingFlags = BindingFlags.Static | BindingFlags.Public;",
               "const BindingFlags BindingFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;",
               "S5 const BindingFlags self-reference")
    # S8 Unity 5 -> 2020 API renames / removals
    for p in list(fp.rglob("*.cs")) + list(main.rglob("*.cs")):
        t = rd(p); o = t
        t, n = re.subn(r"(?<![\w.])Profiler\.", "UnityEngine.Profiling.Profiler.", t)
        if n: note("S8 Profiler -> UnityEngine.Profiling.Profiler", n)
        t, n = re.subn(r"\.generateMips\b", ".autoGenerateMips", t)
        if n: note("S27 RenderTexture.generateMips -> autoGenerateMips", n)
        t, n = re.subn(r"\bmipmap: ", "mipChain: ", t)
        if n: note("S9 Texture2D/Cubemap mipmap: -> mipChain:", n)
        t, n = re.subn(r"\bleaveStreamOpen: ", "leaveInnerStreamOpen: ", t)
        if n: note("S10 SslStream leaveStreamOpen -> leaveInnerStreamOpen", n)
        t, n = re.subn(r"Application\.platform (==|!=) RuntimePlatform\.(OSX|Windows)WebPlayer",
                       lambda m: "false" if m[1] == "==" else "true", t)
        if n: note("S11 WebPlayer platform checks -> constant", n)
        t, n = re.subn(r"(?<![\w.])Tuple<", "EB.Collections.Tuple<", t) if "namespace EB.Collections" not in t else (t, 0)
        if n: note("S2b ambiguous Tuple<> (any file)", n)
        t, n = re.subn(r"\[Optional\] (?!ref |out )((?:[\w.]+)(?:<[^()\[\]]*?>)?(?:\[\])?\??) (\w+)(?=\s*[,)])", r"\1 \2 = default", t)
        if n: note("S20 [Optional] param -> '= default'", n)
        t, n = re.subn(r"Application\.bundleIdentifier", "Application.identifier", t)
        if n: note("S12 Application.bundleIdentifier -> identifier", n)
        t, n = re.subn(r"System_002ECollections_002EGeneric_002EICollection_005B\w+_002Eget_Count\(\)", "base.Count", t)
        if n: note("S13 mangled get_Count call -> base.Count", n)
        if t != o: wr(p, t)
    replace_in(fp, "EB/BugReport.cs", 'CallStatic<AndroidJavaObject>("getInstance", null)',
               'CallStatic<AndroidJavaObject>("getInstance", (object[])null)', "S14 CallStatic null params overload")
    replace_in(fp, "EB/Replication/Manager.cs", "methodInfo.GetCustomAttributes(typeof(RPC), inherit: true).Length > 0",
               'Array.Exists(methodInfo.GetCustomAttributes(inherit: true), a => a.GetType().Name == "RPC")',
               "S15 legacy [RPC] attribute lookup by name")
    for rel in ("NGUIMath.cs", "NGUITools.cs", "PropertyReference.cs"):
        replace_in(fp, rel, r"(?<![\w.])Debug\.", "UnityEngine.Debug.", "S17 ambiguous Debug (System.Diagnostics vs UnityEngine)", regex=True)
    replace_in(fp, "TexturePoolManager.cs", r"(?<![\w.])Cache\.", "EB.Cache.", "S18 ambiguous Cache", regex=True)
    replace_in(main, "PerformanceManager.cs", r"(?<![\w.])TrailRenderer\b", "EB.Rendering.TrailRenderer",
               "S19 ambiguous TrailRenderer", regex=True)
    replace_in(main, "BuffValidationLogger.cs", r"(?<![\w.])Debug\.", "EB.Debug.", "S21 ambiguous Debug -> EB.Debug", regex=True)
    replace_in(main, "ReplicatedSequence.cs", r"(?<![\w.])(RPCMode|NetworkStateSynchronization)\b", r"EB.Replication.\1",
               "S22 ambiguous legacy-network enums", regex=True)
    for p in main.rglob("*.cs"):
        t = rd(p)
        if "markNoLogerReadable" in t:
            wr(p, t.replace("markNoLogerReadable", "markNoLongerReadable")); note("S23 UploadMeshData param typo fixed in 5.5+")
    replace_in(main, "MOTDWidget.cs", "new EB.Action<TransformersMOTDItem, MOTDTemplate>(HandleMOTDClick)",
               "new EB.Action<TransformersMOTDItem, MOTDTemplate>((i, d) => HandleMOTDClick(i, d))",
               "S24 method w/ optional param -> delegate lambda")
    replace_in(main, "TransformersHomeScreen.cs",
               "userProfile.GetMedalCount(), 1f, string.Empty, pitchShift: false, string.Empty, string.Empty)",
               "userProfile.GetMedalCount(), 1f, string.Empty, pitchShift: false, string.Empty, string.Empty, separators: true)",
               "S25 ambiguous overload (optional params)")
    p = main / "HeroesScreen.cs"
    if p.exists():
        t = rd(p)
        t2 = re.sub(r"foreach \(KeyValuePair<string, Building> building in Hub\.Instance\.BaseManager\.user\.buildings\)(\s*\{\s*)list\.Add\(building\.Value\);",
                    r"foreach (KeyValuePair<string, Building> buildingEntry in Hub.Instance.BaseManager.user.buildings)\1list.Add(buildingEntry.Value);", t)
        if t2 != t: wr(p, t2); note("S26 local name shadowing (CS0136)")
    replace_in(fp, "EB/Sparx/Hub.cs", "flag = AndroidClient.SkipPlayServicesInitialization();", "flag = true;",
               "S28 skip Play Services/GPG init (original GMS app id/resources unavailable; crashes on boot)")
    # --- equivalents of the IL patches in tools/patch_firstpass.py (offline revival) ---
    replace_in(fp, "EB/Sparx/EndPoint.cs",
               "protected bool HasInternetConnectivity => Application.internetReachability != NetworkReachability.NotReachable;",
               "protected bool HasInternetConnectivity => true;", "P1 EndPoint.HasInternetConnectivity -> true")
    replace_in(fp, "EB/Sparx/HttpEndPoint.cs",
               "public override bool IsHavingNetworkProblems => _retryCount >= 2 || !base.HasInternetConnectivity;",
               "public override bool IsHavingNetworkProblems => false;", "P2 HttpEndPoint.IsHavingNetworkProblems -> false")
    replace_in(fp, "EB/Sparx/PushManager.cs",
               "\t\t\telse\n\t\t\t{\n\t\t\t\tbase.State = SubSystemState.Error;\n\t\t\t}\n\t\t}\n\n\t\tpublic WebSocketState GetSocketState",
               "\t\t\telse\n\t\t\t{\n\t\t\t\tbase.State = SubSystemState.Connected;\n\t\t\t}\n\t\t}\n\n\t\tpublic WebSocketState GetSocketState",
               "P3 PushManager.OnGetPushToken without websocket -> Connected")
    # P4: keep the original "wait for every subsystem" guard (so TutorialManager has fetched before OnLoggedIn picks
    # the FTE or Home flow), but never wait more than 30s for subsystems that cannot connect offline.
    replace_in(fp, "EB/Sparx/Hub.cs",
               "\t\tprivate void SubSystemConnect()\n\t\t{\n\t\t\tDebug.Log(\"[SparxHub] SubSystemConnect\");\n",
               "\t\tprivate float _tftfConnectStart;\n\n\t\tprivate void SubSystemConnect()\n\t\t{\n\t\t\t_tftfConnectStart = UnityEngine.Time.realtimeSinceStartup;\n\t\t\tDebug.Log(\"[SparxHub] SubSystemConnect\");\n",
               "P4a Hub connect start time")
    replace_in(fp, "EB/Sparx/Hub.cs",
               "\t\t\tif (!flag)\n\t\t\t{\n\t\t\t\treturn;\n\t\t\t}\n\t\t\tDebug.Log(\"[SparxHub] SubSystemConnected\");",
               "\t\t\tif (!flag && UnityEngine.Time.realtimeSinceStartup - _tftfConnectStart < 30f)\n\t\t\t{\n\t\t\t\treturn;\n\t\t\t}\n\t\t\tDebug.Log(\"[SparxHub] SubSystemConnected\");",
               "P4 Hub.SubSystemConnecting waits for subsystems (30s cap)")
    replace_in(fp, "EB/Sparx/InvitesManager.cs",
               "string key = $\"{category}.{type}\";\n\t\t\tif (!privacySettings.ContainsKey(key))",
               "string key = $\"{category}.{type}\";\n\t\t\tif (privacySettings == null || !privacySettings.ContainsKey(key))",
               "P5 InvitesManager.InvitePrivacySetting null-safe")
    # logging: EB.Debug only buffers in release; mirror to logcat
    p = fp / "EB/Debug.cs"
    if p.exists():
        t = rd(p)
        for lvl, name in ((" I: ", "Log"), (" W: ", "LogWarning"), (" E: ", "LogError")):
            old = '_buffer.Push(text);\n\t\t\t\tif (Debug.LogMessageReceived != null)\n\t\t\t\t{\n\t\t\t\t\tDebug.LogMessageReceived(text, string.Empty, LogType.%s' % {"Log":"Log","LogWarning":"Warning","LogError":"Error"}[name]
            new = '_buffer.Push(text);\n\t\t\t\tUnityEngine.Debug.%s(text);\n\t\t\t\tif (Debug.LogMessageReceived != null)\n\t\t\t\t{\n\t\t\t\t\tDebug.LogMessageReceived(text, string.Empty, LogType.%s' % (name, {"Log":"Log","LogWarning":"Warning","LogError":"Error"}[name])
            if old in t:
                t = t.replace(old, new, 1); note("P6 mirror EB.Debug to UnityEngine.Debug (logcat)")
        wr(p, t)
    replace_in(fp, "EB/Sparx/Hub.cs",
               "\t\tprivate void Awake()\n\t\t{\n\t\t\tInstance = this;",
               "\t\tprivate void Awake()\n\t\t{\n\t\t\tSystem.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;\n"
               "\t\t\tSystem.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;\n"
               "\t\t\tSystem.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;\n\t\t\tInstance = this;",
               "P8 invariant culture (JSON floats were serialized as '304,79' on pt-BR devices)")
    replace_in(fp, "EB/Rendering/EBPostFXManager.cs", "\t\t\t\t_CurrentPostFX[i]?.Apply(src, depth, camera);",
               "\t\t\t\ttry { _CurrentPostFX[i]?.Apply(src, depth, camera); } catch (System.Exception) { }",
               "P9 post-FX effects: swallow per-effect exceptions (stub shaders -> NRE -> critical-error popup)")
    replace_in(fp, "EB/Rendering/EBPostFXManager.cs",
               "\t\t\t\tif (_CompositeShader == null)\n\t\t\t\t{\n\t\t\t\t\tDebug.LogError(\"Could not load postfx shader \" + text);\n\t\t\t\t\treturn;\n\t\t\t\t}",
               "\t\t\t\tif (_CompositeShader == null)\n\t\t\t\t{\n\t\t\t\t\t_CompositeShader = Shader.Find(\"Hidden/EB/Blit\");\n\t\t\t\t}\n\t\t\t\tif (_CompositeShader == null)\n\t\t\t\t{\n\t\t\t\t\tDebug.LogError(\"Could not load postfx shader \" + text);\n\t\t\t\t\treturn;\n\t\t\t\t}",
               "P10 generated post-FX composite shader falls back to Hidden/EB/Blit")
    # P11: Shader.Find -> ShaderFallback.Find (Resources/Shader310 copy when Shader.Find misses)
    wr(fp / "ShaderFallback.cs", """using UnityEngine;
using System.Text.RegularExpressions;

public static class ShaderFallback
{
	public static Shader Find(string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		Shader s = Shader.Find(name);
		if (s != null) return s;
		s = Resources.Load<Shader>("Shader310/" + Regex.Replace(name, "[^A-Za-z0-9]+", "_"));
		if (s != null) return s;
		// Never hand back null: a Material(null) throws and the game turns that into a critical error.
		foreach (string fb in new string[] { "Sprites/Default", "UI/Default", "Unlit/Transparent", "Hidden/Internal-Colored" })
		{
			s = Shader.Find(fb);
			if (s != null) return s;
		}
		Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
		return all != null && all.Length > 0 ? all[0] : null;
	}
}
""")
    for p in list(fp.rglob("*.cs")) + list(main.rglob("*.cs")):
        if p.name == "ShaderFallback.cs": continue
        t = rd(p)
        t2, n = re.subn(r"(?<![\w.])Shader\.Find\(", "ShaderFallback.Find(", t)
        if n: wr(p, t2); note("P11 Shader.Find -> ShaderFallback.Find", n)
    # D1: runtime scene diagnostics (cameras, renderers) logged every 15s; remove once rendering is understood
    wr(fp / "TftfDiag.cs", """using System.Text;
using UnityEngine;

public class TftfDiag : MonoBehaviour
{
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Boot()
	{
		GameObject go = new GameObject("TftfDiag");
		DontDestroyOnLoad(go);
		go.AddComponent<TftfDiag>();
	}

	private float _next = 20f;
	private float _nextGlobals = 0f;
	private float _nextFast = 0f;
	private static bool _fastSwap;

	private static bool _lodLogged;
	private static readonly System.Collections.Generic.HashSet<int> _swapped = new System.Collections.Generic.HashSet<int>();
	private static Shader _swapShader;
	private static Shader _fxShader;
	private static Shader _skyShader;
	private static bool _skyMissLogged;

	// The game's lights only push shader globals (EBLight), so nothing casts a shadow: one real directional light follows the scene's sun
	// (_EBDirectionalLightDirection) and casts soft shadows onto and from the TFTF/PbrLite materials. tftf/noshadows.txt switches it off.
	private static Light _sun;
	private static void EnsureSun()
	{
		if (System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "tftf/noshadows.txt")))
		{
			if (_sun != null) _sun.enabled = false;
			return;
		}
		if (_sun == null)
		{
			GameObject g = new GameObject("TftfSun");
			DontDestroyOnLoad(g);
			_sun = g.AddComponent<Light>();
			_sun.type = LightType.Directional;
			_sun.color = Color.white;
			_sun.intensity = 1f;
			_sun.shadows = LightShadows.Soft;
			_sun.shadowStrength = 0.95f;
			_sun.shadowBias = 0.05f;
			_sun.shadowNormalBias = 0.4f;
			_sun.renderMode = LightRenderMode.ForcePixel;
			QualitySettings.shadows = ShadowQuality.All;
			QualitySettings.shadowResolution = ShadowResolution.High;
			QualitySettings.shadowDistance = 30f;
			QualitySettings.shadowCascades = 1;
			QualitySettings.shadowProjection = ShadowProjection.StableFit;
			Debug.Log("[DIAG] quality before: textureLimit=" + QualitySettings.masterTextureLimit + " pixelLights=" + QualitySettings.pixelLightCount + " aniso=" + QualitySettings.anisotropicFiltering + " lodBias=" + QualitySettings.lodBias + " levels=" + string.Join(",", QualitySettings.names));
			QualitySettings.masterTextureLimit = 0;          // full-resolution textures (the game runs at the Fastest level)
			QualitySettings.pixelLightCount = Mathf.Max(1, QualitySettings.pixelLightCount);
			QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
			QualitySettings.lodBias = Mathf.Max(1f, QualitySettings.lodBias);
			Debug.Log("[DIAG] TFTF sun light created: supportsShadows=" + SystemInfo.supportsShadows + " quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " shadows=" + QualitySettings.shadows + " dist=" + QualitySettings.shadowDistance);
		}
		_sun.enabled = true;
	}

	// Every frame: the key light comes from the camera's upper left front (TFTF/PbrLite reads _TftfLightDir), and the shadow light matches it.
	// fights are lit by a much brighter arena than the base and story map
	private const float DefaultExposure = 1.35f, DefaultSun = 3f, FightExposure = 0.3f, FightSun = 1.5f, FightAmbient = 1f, FightRim = 1f, FightSat = 1.4f, FightContrast = 1.2f, FightFog = 0.45f, FightWarm = 0.05f;
	private const float RobotMenuExposure = 0.72f, RobotMenuSat = 0.88f;      // robots on the squad / selection screens (not in a fight)
	private static readonly string[] TuneNames = { "exposure", "sun", "ambient", "rim", "sat", "contrast", "fog", "warm" };
	// BattleArbiter lives in the main assembly, which this first-pass script cannot reference: reach it by reflection.
	private static System.Type _arbiterType;
	private static bool InFight()
	{
		try
		{
			if (_arbiterType == null) _arbiterType = System.Type.GetType("BattleArbiter, Assembly-CSharp");
			if (_arbiterType == null) return false;
			object inst = _arbiterType.GetProperty("Instance").GetValue(null, null);
			if (inst == null) return false;
			System.Reflection.MethodInfo m = _arbiterType.GetMethod("IsCurrentState");
			foreach (string st in new string[] { "Intro", "Main", "CinematicSpecial", "WaitForKnockout", "Timeout", "Outro" })
				if ((bool)m.Invoke(inst, new object[] { st })) return true;
		}
		catch (System.Exception) { }
		return false;
	}

	// The average colour of the arena's sky texture, pushed as _TftfSky (rgb scaled up so a mid sky gives about the old fixed colour).
	private static Texture _skySampled;
	private static Vector3 _skyRgb;
	private static void SampleSky()
	{
		foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
		{
			if (r.name != "Sky" || r.sharedMaterial == null) continue;
			Texture t = r.sharedMaterial.mainTexture;
			if (t == null && r.sharedMaterial.HasProperty("_base_tex")) t = r.sharedMaterial.GetTexture("_base_tex");
			if (t == null || t == _skySampled) return;
			_skySampled = t;
			RenderTexture rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(t, rt);
			RenderTexture prev = RenderTexture.active;
			RenderTexture.active = rt;
			Texture2D px = new Texture2D(8, 8, TextureFormat.RGBA32, false);
			px.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(rt);
			Color sum = Color.black;
			Color[] cs = px.GetPixels();
			foreach (Color c in cs) sum += c;
			Object.Destroy(px);
			Color avg = sum / cs.Length;
			Color g = new Color(Mathf.Pow(avg.r, 2.2f), Mathf.Pow(avg.g, 2.2f), Mathf.Pow(avg.b, 2.2f));      // the shader lights in linear space
			float peak = Mathf.Max(0.02f, Mathf.Max(g.r, Mathf.Max(g.g, g.b)));
			float k = Mathf.Clamp(0.34f / peak, 0.4f, 6f);                                                   // normalise so the brightest channel is about 0.34
			_skyRgb = new Vector3(g.r * k, g.g * k, g.b * k);
			Debug.Log("[DIAG] sky colour from " + t.name + " avg=" + avg + " -> " + (g * k));
			return;
		}
	}

	private static float _dbgNext;
	private static void AimSun()
	{
		if (Time.realtimeSinceStartup >= _dbgNext)
		{
			_dbgNext = Time.realtimeSinceStartup + 1f;
			float dv = 0f;
			try { string f = System.IO.Path.Combine(Application.persistentDataPath, "tftf/dbg.txt"); if (System.IO.File.Exists(f)) float.TryParse(System.IO.File.ReadAllText(f).Trim(), out dv); } catch (System.Exception) { }
			Shader.SetGlobalFloat("_TftfDebug", dv);
			bool inFight = InFight();
			// lighting values, per screen type; tftf/tune.txt overrides them with "name value" lines (see TuneNames) without a rebuild
			float[] cur = inFight ? new float[] { FightExposure, FightSun, FightAmbient, FightRim, FightSat, FightContrast, FightFog, FightWarm } : new float[] { DefaultExposure, DefaultSun, 1f, 1f, 1f, 1f, 0f };
			try
			{
				string tf = System.IO.Path.Combine(Application.persistentDataPath, "tftf/tune.txt");
				if (System.IO.File.Exists(tf))
					foreach (string tl in System.IO.File.ReadAllLines(tf))
					{
						string[] kv = tl.Trim().Split(new char[] { ' ', '=', ':' }, System.StringSplitOptions.RemoveEmptyEntries);
						float tv;
						if (kv.Length != 2 || !float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tv)) continue;
						string kn = kv[0].ToLowerInvariant();
						bool fightKey = kn.StartsWith("f");
						if (fightKey != inFight) continue;
						int ki = System.Array.IndexOf(TuneNames, fightKey ? kn.Substring(1) : kn);
						if (ki >= 0) cur[ki] = tv;
					}
			}
			catch (System.Exception) { }
			Shader.SetGlobalVector("_TftfTune", new Vector4(cur[0], cur[1], cur[6], cur[7]));
			Shader.SetGlobalVector("_TftfGrade", new Vector4(cur[2], cur[3], cur[4], cur[5]));
			Shader.SetGlobalVector("_TftfRobot", inFight ? new Vector4(1f, 1f, 0f, 0f) : new Vector4(RobotMenuExposure, RobotMenuSat, 0f, 0f));
			SampleSky();
			Shader.SetGlobalVector("_TftfSky", _skyRgb.sqrMagnitude > 0f ? new Vector4(_skyRgb.x, _skyRgb.y, _skyRgb.z, inFight ? 1f : 0.2f) : Vector4.zero);
			// dev: tftf/hide.txt lists renderer-name parts (one per line) to switch off; the matches are logged
			try
			{
				string hf = System.IO.Path.Combine(Application.persistentDataPath, "tftf/hide.txt");
				if (System.IO.File.Exists(hf))
				{
					string[] hl = System.IO.File.ReadAllLines(hf);
					foreach (Renderer hr in Object.FindObjectsOfType<Renderer>())
					{
						if (!hr.enabled) continue;
						foreach (string hp in hl) if (hp.Trim().Length > 0 && hr.name.IndexOf(hp.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0) { hr.enabled = false; Debug.Log("[TFTF] hid renderer " + hr.name); break; }
					}
				}
			}
			catch (System.Exception) { }
		}
		Camera cam = Camera.main;
		if (cam == null) foreach (Camera c in Camera.allCameras) if (c.name == "RigCamera") cam = c;
		if (cam == null) { Shader.SetGlobalVector("_TftfLightDir", Vector4.zero); return; }
		Transform t = cam.transform;
		Vector3 L = (-0.9f * t.right + new Vector3(0f, 0.55f, 0f) - 0.05f * t.forward).normalized;      // from the side: surfaces facing the camera are only partly lit
		Shader.SetGlobalVector("_TftfLightDir", new Vector4(L.x, L.y, L.z, 0f));
		if (_sun != null && _sun.enabled) _sun.transform.rotation = Quaternion.LookRotation(-L);
	}

	// The donor PBR shaders draw nothing in this pipeline: give those renderers a plain textured material.
	private static void SwapPbr()
	{
		if (_swapShader == null) _swapShader = ShaderFallback.Find("TFTF/PbrLite"); if (_swapShader == null || _swapShader.name != "TFTF/PbrLite") _swapShader = ShaderFallback.Find("TFTF/LitTex"); if (_swapShader == null || (_swapShader.name != "TFTF/LitTex" && _swapShader.name != "TFTF/PbrLite")) _swapShader = ShaderFallback.Find("EBG/UI/Opaque");
		if (_swapShader == null) return;
		if (_fxShader == null) _fxShader = ShaderFallback.Find("TFTF/AddFx");
		string[] prefs = new string[] { "_base_tex", "_BaseTex", "_MainTex", "_BaseMap", "_AlbedoTex", "_Albedo", "_DiffuseTex", "_Diffuse" };
		foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
		{
			Material[] mats = r.sharedMaterials;
			bool changed = false;
			for (int i = 0; i < mats.Length; i++)
			{
				Material m = mats[i];
				if (m == null || m.shader == null) continue;
				if (m.name.EndsWith("_swap")) continue;      // already a stand-in
				string n = m.shader.name;
				// the blob "contact shadow" quads under the feet and hips use an opaque white-on-black mask that the stand-in shader draws as
				// cards; the TFTF sun light casts real shadows now, so these are switched off
				if (n == "EB/Misc/ContactShadow") { if (r.enabled) r.enabled = false; continue; }
				if (!(n.StartsWith("Hidden/PBR") || n.StartsWith("Hidden/GridReveal") || n.Length == 0 || !m.shader.isSupported)) continue;      // empty name: a donor shader that did not load (magenta)
				if (n.Length == 0 || !m.shader.isSupported)      // lights, sky and flares are drawn by the game's own deferred passes: leave them alone
				{
					string rn = r.name;
					if (rn.Contains("LightMerged") || rn.Contains("Arealight") || rn.Contains("LensFlare") || rn.Contains("Shadow")) continue;
					if (rn == "Sky" || m.name.StartsWith("skybox"))
					{
						// the sky dome's donor shader did not compile (magenta sky): its own texture on an unlit background shader
						if (_skyShader == null) _skyShader = ShaderFallback.Find("TFTF/SkyLite");
						Texture skyTex = m.HasProperty("_base_tex") ? m.GetTexture("_base_tex") : null;
						if (skyTex == null)
						{
							// a material whose shader failed exposes no properties: find the dome's texture by name (skybox_0 day, _1 night, _2 sandstorm)
							string want = m.name.Contains("skybox_1") ? "night" : m.name.Contains("skybox_2") ? "sandstorm" : "day";
							foreach (Texture2D ct in Resources.FindObjectsOfTypeAll<Texture2D>())
							{
								string tn = ct.name.ToLowerInvariant();
								if (tn.Contains("sky") && tn.Contains(want) && !tn.StartsWith("fx_")) { skyTex = ct; break; }
							}
							if (skyTex == null) foreach (Texture2D ct in Resources.FindObjectsOfTypeAll<Texture2D>()) { string tn = ct.name.ToLowerInvariant(); if (tn.Contains("sky") && !tn.StartsWith("fx_") && !tn.Contains("mist")) { skyTex = ct; break; } }
						}
						if (_skyShader == null || skyTex == null) { if (!_skyMissLogged) { _skyMissLogged = true; Debug.Log("[DIAG] sky swap skipped: shader=" + (_skyShader != null) + " tex=" + (skyTex != null) + " mat=" + m.name); } continue; }
						Material sm = new Material(_skyShader);
						sm.name = m.name + "_swap";
						sm.mainTexture = skyTex;
						mats[i] = sm; changed = true;
						Debug.Log("[DIAG] swapped sky " + m.name + " tex=" + skyTex.name);
						continue;
					}
				}
				int id = m.GetInstanceID();
				Texture tex = null;
				string texProp = null;
				foreach (string pn in prefs) { if (m.HasProperty(pn)) { tex = m.GetTexture(pn); if (tex != null) { texProp = pn; break; } } }
				if (tex == null) foreach (string pn in m.GetTexturePropertyNames()) { if (pn.Contains("emissive") || pn.Contains("damage") || pn.Contains("translucency") || pn.Contains("normal") || pn.Contains("composite") || pn.Contains("metallic") || pn.Contains("roughness") || pn.Contains("_ao")) continue; Texture t = m.GetTexture(pn); if (t != null) { tex = t; break; } }
				bool tftfFx = _fxShader != null && (m.renderQueue >= 2500 || m.name.StartsWith("fx_"));     // glow cards, trails, hit/special effects
				Material nm = new Material(tftfFx ? _fxShader : _swapShader);
				nm.name = m.name + "_swap";
				if (tex != null) nm.mainTexture = tex;
				// keep the texture's tiling: terrain materials tile by world units (0.035 per metre), which the default 1 turned into noise
				if (texProp != null) { nm.mainTextureScale = m.GetTextureScale(texProp); nm.mainTextureOffset = m.GetTextureOffset(texProp); }
				nm.color = Color.white;
				if (nm.HasProperty("_Blend") && n.StartsWith("Hidden/PBRTerrain")) nm.SetVector("_Blend", new Vector4(0f, 1f, 0f, 1f));      // world-projected terrain
				if (nm.HasProperty("_IsSkinned")) nm.SetFloat("_IsSkinned", r is SkinnedMeshRenderer ? 1f : 0f);
				if (nm.HasProperty("_Base2Tex") && m.HasProperty("_base2_tex") && m.HasProperty("_mask_tex") && m.GetTexture("_base2_tex") != null && m.GetTexture("_mask_tex") != null)
				{
					nm.SetTexture("_Base2Tex", m.GetTexture("_base2_tex"));
					nm.SetTexture("_MaskTex", m.GetTexture("_mask_tex"));
					Vector2 s2 = m.GetTextureScale("_base2_tex"), o2 = m.GetTextureOffset("_base2_tex"), sm = m.GetTextureScale("_mask_tex"), om = m.GetTextureOffset("_mask_tex");
					nm.SetVector("_Base2ST", new Vector4(s2.x, s2.y, o2.x, o2.y));
					nm.SetVector("_MaskST", new Vector4(sm.x, sm.y, om.x, om.y));
					nm.SetVector("_Blend", new Vector4(m.HasProperty("_blendoffset_range") ? m.GetFloat("_blendoffset_range") : 0f, m.HasProperty("_blendremap_range") ? m.GetFloat("_blendremap_range") : 1f, 1f, n.StartsWith("Hidden/PBRTerrain") ? 1f : 0f));
				}
				if (tftfFx)
				{
					// effect meshes glow with their emissive map (or base map) in their emissive tint; nothing to draw otherwise
					Texture fxTex = m.HasProperty("_emissive_tex") ? m.GetTexture("_emissive_tex") : null;
					if (fxTex == null && m.HasProperty("_base_tex")) fxTex = m.GetTexture("_base_tex");
					if (fxTex != null) nm.mainTexture = fxTex;
					Color fxCol = m.HasProperty("_emissive_intensity_col") ? m.GetColor("_emissive_intensity_col") : Color.white;
					nm.color = fxTex != null ? fxCol : Color.black;
				}
				if (nm.HasProperty("_NormalTex"))
				{
					Texture tn = m.HasProperty("_normal_tex") ? m.GetTexture("_normal_tex") : null, tm = m.HasProperty("_metallic_tex") ? m.GetTexture("_metallic_tex") : null;
					Texture tr = m.HasProperty("_roughness_tex") ? m.GetTexture("_roughness_tex") : null, ta = m.HasProperty("_ao_tex") ? m.GetTexture("_ao_tex") : null, te = m.HasProperty("_emissive_tex") ? m.GetTexture("_emissive_tex") : null;
					if (tn != null) nm.SetTexture("_NormalTex", tn);
					if (tm != null) nm.SetTexture("_MetalTex", tm);
					if (tr != null) nm.SetTexture("_RoughTex", tr);
					if (ta != null) nm.SetTexture("_AoTex", ta);
					if (te != null) { nm.SetTexture("_EmisTex", te); if (m.HasProperty("_emissive_intensity_col")) nm.SetColor("_EmisColor", m.GetColor("_emissive_intensity_col") * 3f); }
					nm.SetVector("_Flags", new Vector4(tn != null ? 1f : 0f, tm != null ? 1f : 0f, tr != null ? 1f : 0f, ta != null ? 1f : 0f));
					nm.SetFloat("_MetalAmt", tm != null ? 1f : (m.HasProperty("_metallic_range") ? Mathf.Clamp01(m.GetFloat("_metallic_range")) : 0f));
					Texture tc = m.HasProperty("_pbr_composite_tex") ? m.GetTexture("_pbr_composite_tex") : null;      // robots: R roughness, G AO, B emissive packed together
					if (tc != null && tm == null && tr == null && ta == null) { nm.SetTexture("_CompTex", tc); nm.SetFloat("_CompOn", 1f); if (m.HasProperty("_emissive_intensity_col")) nm.SetColor("_EmisColor", m.GetColor("_emissive_intensity_col") * 3f); }
					nm.SetFloat("_RoughAmt", tr != null ? 1f : (m.HasProperty("_roughness_range") ? Mathf.Clamp01(m.GetFloat("_roughness_range")) : 0.6f));
				}
				mats[i] = nm; changed = true;
				if (_swapped.Add(id)) Debug.Log("[DIAG] swapped " + r.name + " shader=" + n.Substring(0, Mathf.Min(48, n.Length)) + " tex=" + (tex != null ? tex.name : "none") + " props=" + string.Join(",", m.GetTexturePropertyNames()));
			}
			if (changed)
			{
				r.sharedMaterials = mats;
				// the game's renderers have Unity shadows switched off (it had its own shadow system): the TFTF sun needs them
				bool isSky = r.name == "Sky";
				r.shadowCastingMode = isSky ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
				r.receiveShadows = !isSky;
				MeshFilter mf2 = r.GetComponent<MeshFilter>();
				Mesh m2 = mf2 != null ? mf2.sharedMesh : null;
				if (!(r is SkinnedMeshRenderer) && m2 != null && m2.isReadable && m2.colors32.Length == 0)
				{
					Color32[] white = new Color32[m2.vertexCount];
					for (int ci = 0; ci < white.Length; ci++) white[ci] = new Color32(255, 255, 255, 255);
					m2.colors32 = white;
					Debug.Log("[DIAG] painted white vertex colors on " + r.name + " (" + white.Length + ")");
				}
			}
		}
	}

	private static void ApplyGlobals()
	{
		if (!_lodLogged) { _lodLogged = true; Debug.Log("[DIAG] globalMaximumLOD(before)=" + Shader.globalMaximumLOD + " rtCount=" + SystemInfo.supportedRenderTargetCount + " gles=" + SystemInfo.graphicsDeviceVersion); }
		Shader.globalMaximumLOD = 100000;
		Shader.SetGlobalFloat("_EBExposure", 1f);
		Shader.SetGlobalFloat("_EBOneOverExposure", 1f);
		Shader.SetGlobalFloat("_EBSkyLuminanceScale", 1f / 32f);
		Shader.SetGlobalFloat("_EBEmissiveExposure", 1f);
		Shader.SetGlobalFloat("_AODirectInfluence", 1f);
		Shader.SetGlobalVector("_EBLightmapAdjust", new Vector4(1f, 1f, 1f, 1f));
		Shader.SetGlobalVector("_EBDirectionalLightDirection", new Vector4(0.3f, 0.8f, -0.5f, 0f));
		Shader.SetGlobalVector("_EBDirectionalLightLuminanceIntensity", new Vector4(1f, 1f, 1f, 1f));
		Shader.SetGlobalVector("_PMREMScale", new Vector4(1f, 1f, 1f, 1f));
		Shader.SetGlobalVector("_FogParams", Vector4.zero);
		Shader.SetGlobalTexture("_lm", Texture2D.whiteTexture);
	}

	private Camera _rig;

	private void LateUpdate()
	{
		if (_rig == null)
		{
			foreach (Camera c in Camera.allCameras) if (c.name == "RigCamera") _rig = c;
		}
		if (_rig != null) _rig.ResetProjectionMatrix();
		AimSun();
	}

	private void Update()
	{
		if (QualitySettings.skinWeights != SkinWeights.FourBones) QualitySettings.skinWeights = SkinWeights.FourBones;
		// attack and hit effects spawn mid-fight with donor shaders that did not load: swap them four times a second while a battle runs
		if (_fastSwap && Time.realtimeSinceStartup >= _nextFast) { _nextFast = Time.realtimeSinceStartup + 0.25f; SwapPbr(); }
		if (Time.realtimeSinceStartup >= _nextGlobals)
		{
			_nextGlobals = Time.realtimeSinceStartup + 1f;
			_fastSwap = InFight();
			if (!EB.Rendering.EBTimeOfDayManager.IsInitialized || EB.Rendering.EBTimeOfDayManager.Instance.GetActiveTimeOfDay() == null) ApplyGlobals();
			SwapPbr();
			EnsureSun();
			// The 16:9 UI viewport leaves bars on wider phones: clear them to black instead of Unity's default skybox.
			RenderSettings.skybox = null;
			foreach (Camera sc in Camera.allCameras) if (sc.clearFlags == CameraClearFlags.Skybox && sc.targetTexture == null) { sc.clearFlags = CameraClearFlags.SolidColor; sc.backgroundColor = Color.black; }
		}
		if (Time.realtimeSinceStartup < _next) return;
		_next = Time.realtimeSinceStartup + 15f;
		StringBuilder sb = new StringBuilder("[DIAG] cameras:");
		foreach (Camera c in Camera.allCameras)
		{
			sb.Append(" {").Append(c.name).Append(" en=").Append(c.enabled).Append(" mask=").Append(c.cullingMask)
			  .Append(" depth=").Append(c.depth).Append(" clear=").Append(c.clearFlags).Append(" rt=").Append(c.targetTexture != null)
			  .Append(" rect=").Append(c.rect).Append(" path=").Append(c.actualRenderingPath).Append("}");
		}
		Debug.Log(sb.ToString());
		foreach (Camera cc in Camera.allCameras)
		{
			if (cc.name == "RigCamera")
			{
				cc.clearFlags = CameraClearFlags.SolidColor;
				cc.backgroundColor = Color.black;
				Debug.Log("[DIAG] RigCamera pos=" + cc.transform.position + " rot=" + cc.transform.eulerAngles + " ortho=" + cc.orthographic + " fov=" + cc.fieldOfView + " near=" + cc.nearClipPlane + " far=" + cc.farClipPlane + " hdr=" + cc.allowHDR + " msaa=" + cc.allowMSAA);
			}
		}
		foreach (UITexture ut in Object.FindObjectsOfType<UITexture>())
		{
			if (ut.mainTexture == null || ut.mainTexture.name.IndexOf("title_") < 0) continue;
			StringBuilder ub = new StringBuilder("[DIAG] UITex " + ut.mainTexture.name + " " + ut.mainTexture.width + "x" + ut.mainTexture.height + " widget=" + ut.width + "x" + ut.height + " pivot=" + ut.pivot + " lossy=" + ut.transform.lossyScale + " anchors=" + (ut.leftAnchor.target != null) + (ut.rightAnchor.target != null) + (ut.topAnchor.target != null) + (ut.bottomAnchor.target != null) + " chain:");
			for (Transform ct = ut.transform; ct != null; ct = ct.parent)
			{
				ub.Append(" /").Append(ct.name).Append("[");
				foreach (Component cc2 in ct.GetComponents<Component>()) if (cc2 != null && !(cc2 is Transform)) ub.Append(cc2.GetType().Name).Append(",");
				ub.Append("]");
			}
			Debug.Log(ub.ToString());
		}
		foreach (UIRoot ur in Object.FindObjectsOfType<UIRoot>()) Debug.Log("[DIAG] UIRoot " + ur.name + " scaling=" + ur.scalingStyle + " manualH=" + ur.manualHeight + " active=" + ur.activeHeight + " lossy=" + ur.transform.lossyScale);
		Renderer[] rs = Object.FindObjectsOfType<Renderer>();
		int on = 0, off = 0;
		var shaders = new System.Collections.Generic.Dictionary<string, int>();
		int shown = 0;
		foreach (Renderer r in rs)
		{
			if (r.enabled && r.gameObject.activeInHierarchy) on++; else off++;
			Material m = r.sharedMaterial;
			string sn = m == null ? "<nomat>" : (m.shader == null ? "<noshader>" : m.shader.name);
			int n; shaders.TryGetValue(sn, out n); shaders[sn] = n + 1;
			if (r.enabled && r.gameObject.activeInHierarchy && rs.Length < 40) { Transform tt = r.transform; string pth = tt.name; while (tt.parent != null && pth.Length < 80) { tt = tt.parent; pth = tt.name + "/" + pth; } Debug.Log("[DIAG] R " + pth + " | " + sn + " | mat=" + (m == null ? "-" : m.name) + " tex=" + (m == null || m.mainTexture == null ? "-" : m.mainTexture.name) + " q=" + (m == null ? 0 : m.renderQueue)); }
		}
		sb = new StringBuilder("[DIAG] renderers on=" + on + " off=" + off + " shaders:");
		foreach (var kv in shaders) { if (shown++ < 14) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value); }
		Debug.Log(sb.ToString());
		Camera rig = null;
		foreach (Camera c3 in Camera.allCameras) if (c3.name == "RigCamera") rig = c3;
		if (rig != null)
		{
			Debug.Log("[DIAG] rig activeTexture=" + (rig.activeTexture != null ? rig.activeTexture.name + " " + rig.activeTexture.width + "x" + rig.activeTexture.height : "null") + " targetTexture=" + (rig.targetTexture != null) + " pixelRect=" + rig.pixelRect + " screen=" + Screen.width + "x" + Screen.height + " stackedOver=" + rig.depth + " opaque=" + rig.opaqueSortMode + " useOcc=" + rig.useOcclusionCulling + " layerCull=" + (rig.layerCullDistances != null));
			float[] lcd = rig.layerCullDistances;
			StringBuilder lb = new StringBuilder("[DIAG] layerCullDistances spherical=" + rig.layerCullSpherical + " nonzero:");
			for (int li = 0; li < lcd.Length; li++) if (lcd[li] != 0f) lb.Append(" L").Append(li).Append("=").Append(lcd[li]);
			Debug.Log(lb.ToString() + " proj=" + rig.projectionMatrix.m00.ToString("F3") + "," + rig.projectionMatrix.m11.ToString("F3") + "," + rig.projectionMatrix.m22.ToString("F3") + "," + rig.projectionMatrix.m23.ToString("F3") + " nearCullMatrixSet=" + rig.nonJitteredProjectionMatrix.m00.ToString("F3") + " worldToCamera.m23=" + rig.worldToCameraMatrix.m23.ToString("F1") + " sceneCull=" + rig.cameraType);
			Plane[] planes = GeometryUtility.CalculateFrustumPlanes(rig);
			int inMask = 0, inFrustum = 0, listed = 0;
			StringBuilder sb2 = new StringBuilder("[DIAG] visibility:");
			foreach (Renderer r in rs)
			{
				if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
				bool m2 = ((1 << r.gameObject.layer) & rig.cullingMask) != 0;
				bool f2 = GeometryUtility.TestPlanesAABB(planes, r.bounds);
				if (m2) inMask++;
				if (m2 && f2) inFrustum++;
				if (r.name == "GameboardBuilder_terrain")
				{
					Material tm = r.sharedMaterial;
					StringBuilder pb = new StringBuilder("[DIAG] terrain material=" + (tm != null ? tm.name : "null") + " passCount=" + (tm != null ? tm.passCount : -1) + " queue=" + (tm != null ? tm.renderQueue : -1) + " keywords=" + (tm != null ? string.Join(",", tm.shaderKeywords) : "") + " passes:");
					if (tm != null && tm.shader != null)
					{
						for (int pi = 0; pi < tm.shader.passCount && pi < 12; pi++)
						{
							string lm = "?";
							try { lm = tm.shader.FindPassTagValue(pi, new UnityEngine.Rendering.ShaderTagId("LightMode")).name; } catch (System.Exception) { }
							pb.Append(" [").Append(pi).Append(":").Append(tm.GetPassName(pi)).Append("/LM=").Append(lm).Append(" en=").Append(tm.GetShaderPassEnabled(lm)).Append("]");
						}
					}
					Debug.Log(pb.ToString());
				}
				if (listed < 8 && r.bounds.size.magnitude > 20f)
				{
					listed++;
					Material mt = r.sharedMaterial;
					MeshFilter mf = r.GetComponent<MeshFilter>();
					Mesh ms = mf != null ? mf.sharedMesh : null;
					sb2.Append(" {").Append(r.name).Append(" vis=").Append(r.isVisible).Append(" verts=").Append(ms != null ? ms.vertexCount : -1).Append(" subs=").Append(ms != null ? ms.subMeshCount : -1)
					   .Append(" scale=").Append(r.transform.lossyScale).Append(" L=").Append(r.gameObject.layer).Append(" mask=").Append(m2).Append(" frus=").Append(f2)
					   .Append(" c=").Append(r.bounds.center).Append(" sz=").Append(r.bounds.size.magnitude.ToString("F0"))
					   .Append(" q=").Append(mt != null ? mt.renderQueue : -1).Append(" sh=").Append(mt != null && mt.shader != null ? mt.shader.name.Substring(0, Mathf.Min(40, mt.shader.name.Length)) : "-")
					   .Append(" supp=").Append(mt != null && mt.shader != null ? mt.shader.isSupported : false).Append("}");
				}
			}
			sb2.Insert(0, "[DIAG] inMask=" + inMask + " inFrustum=" + inFrustum + " ");
			Debug.Log(sb2.ToString());
		}
		SkinnedMeshRenderer[] sms = Object.FindObjectsOfType<SkinnedMeshRenderer>();
		StringBuilder sk = new StringBuilder("[DIAG] skinned=" + sms.Length + ":");
		int skShown = 0;
		foreach (SkinnedMeshRenderer sm in sms)
		{
			if (skShown++ >= 14) break;
			Mesh sh = sm.sharedMesh;
			Material m0 = sm.sharedMaterial;
			sk.Append(" {").Append(sm.name).Append(" en=").Append(sm.enabled).Append(" act=").Append(sm.gameObject.activeInHierarchy)
			  .Append(" vis=").Append(sm.isVisible).Append(" verts=").Append(sh != null ? sh.vertexCount : -1)
			  .Append(" bones=").Append(sm.bones != null ? sm.bones.Length : -1).Append(" bindposes=").Append(sh != null ? sh.bindposes.Length : -1)
			  .Append(" bpv=").Append(sh != null ? sh.GetBonesPerVertex().Length : -1)
			  .Append(" mat=").Append(m0 != null ? m0.name : "null").Append(" sh=").Append(m0 != null && m0.shader != null ? m0.shader.name : "-")
			  .Append(" b=").Append(sm.bounds.size).Append(" ctr=").Append(sm.bounds.center).Append(" L=").Append(sm.gameObject.layer)
			  .Append(" rootpos=").Append(sm.rootBone != null ? sm.rootBone.position : sm.transform.position)
			  .Append(" tex=").Append(m0 != null && m0.mainTexture != null ? m0.mainTexture.name : "none").Append("}");
		}
		Debug.Log(sk.ToString());
		GameObject b = GameObject.Find("BaseRoot(Clone)");
		if (b == null) b = GameObject.Find("BaseRoot");
		Debug.Log("[DIAG] BaseRoot=" + (b != null) + (b != null ? " active=" + b.activeInHierarchy + " pos=" + b.transform.position + " childCount=" + b.transform.childCount : ""));
	}
}
""")
    replace_in(fp, "EB/Sparx/Hub.cs",
               "System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;\n",
               "System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;\n\t\t\tif (UnityEngine.GameObject.Find(\"TftfDiag\") == null) { UnityEngine.GameObject diag = new UnityEngine.GameObject(\"TftfDiag\"); UnityEngine.Object.DontDestroyOnLoad(diag); diag.AddComponent<TftfDiag>(); }\n",
               "D2 start diagnostics from Hub.Awake")
    # P27: the ripped GraphicsSettings tiers resolved to VertexLit, so PBR forward passes never draw (black board): force Forward
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs",
               "\t\tprivate void Start()\n\t\t{\n\t\t\tCameraCutAction = delegate",
               "\t\tprivate void Start()\n\t\t{\n\t\t\tGetComponent<Camera>().renderingPath = UnityEngine.RenderingPath.Forward;\n\t\t\tCameraCutAction = delegate",
               "P27a EBDeferredCamera.Start forces Forward rendering path")
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs",
               "\t\tpublic void Setup(bool forcePostFXReset = false)\n\t\t{\n",
               "\t\tpublic void Setup(bool forcePostFXReset = false)\n\t\t{\n\t\t\tGetComponent<Camera>().renderingPath = UnityEngine.RenderingPath.Forward;\n",
               "P27b EBDeferredCamera.Setup forces Forward rendering path")
    # D3: trace the shared-camera setup chain
    replace_in(fp, "EBSharedCameraController.cs", r"(protected virtual void OnDeferredReady\(\)\s*\{)",
               r'\1 UnityEngine.Debug.Log("[SCC] OnDeferredReady");', "D3a SCC.OnDeferredReady log", regex=True)
    replace_in(fp, "EBSharedCameraController.cs", r"(protected void CheckCameraUpdate\(\)\s*\{)",
               r'\1 UnityEngine.Debug.Log("[SCC] CheckCameraUpdate setup=" + _SetupComplete + " stack=" + _CameraStack.Count + " current=" + (_CurrentCamera == null ? "null" : _CurrentCamera.Requester));', "D3b SCC.CheckCameraUpdate log", regex=True)
    replace_in(fp, "EBSharedCameraController.cs", r"(private void SetCameraSettings\(SharedCameraSettings settings\)\s*\{)",
               r'\1 UnityEngine.Debug.Log("[SCC] SetCameraSettings req=" + (settings == null ? "null" : settings.Requester) + " far=" + (settings == null ? 0f : settings.Far) + " near=" + (settings == null ? 0f : settings.Near) + " parent=" + (settings == null || settings.Parent == null ? "null" : settings.Parent.name));', "D3c SCC.SetCameraSettings log", regex=True)
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs", "\t\t\tGetComponent<Camera>().renderingPath = UnityEngine.RenderingPath.Forward;\n\t\t\t_Setup = true;\n",
               "\t\t\tUnityEngine.Debug.Log(\"[EDC] Setup begin\");\n\t\t\tGetComponent<Camera>().renderingPath = UnityEngine.RenderingPath.Forward;\n\t\t\t_Setup = true;\n", "D3d EDC.Setup begin log")
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs", "\t\t\tif (OnSetupComplete != null)\n\t\t\t{\n\t\t\t\tOnSetupComplete();\n",
               "\t\t\tUnityEngine.Debug.Log(\"[EDC] Setup end, listeners=\" + (OnSetupComplete != null));\n\t\t\tif (OnSetupComplete != null)\n\t\t\t{\n\t\t\t\tOnSetupComplete();\n", "D3e EDC.Setup end log")
    # P28: missing localisation must not NRE the fight mode buttons
    replace_in(main, "QuestModeButton.cs", "string text = _buttonData.CategoryTitle;", "string text = _buttonData.CategoryTitle ?? string.Empty;", "P28a QuestModeButton title null-safe")
    replace_in(main, "QuestModeButton.cs", "string text2 = _buttonData.CategoryDescription;", "string text2 = _buttonData.CategoryDescription ?? string.Empty;", "P28b QuestModeButton description null-safe")
    # P33: Special 1/2 leave through the animator reaching Idle (PlayerController.OnAnimatorMove) and drop invulnerability
    # through a move-sequencer event; when either never fires the fighter stays in SpecialAttack, untouchable, and the
    # fight cannot end. Clear invulnerability on exit and fall back to Idle after a generous cap.
    replace_in(main, "PlayerSpecialAttackState.cs", "\tprivate int SpecialLevel;\n",
               "\tprivate int SpecialLevel;\n\n\tprivate float _elapsed;\n\n\tprivate const float MaxDuration = 6f;\n", "P33a special attack watchdog fields")
    replace_in(main, "PlayerSpecialAttackState.cs", "\t\tbase.StateMachine.UseSpecial(SpecialLevel);\n",
               "\t\t_elapsed = 0f;\n\t\tbase.StateMachine.UseSpecial(SpecialLevel);\n", "P33b special attack watchdog reset")
    replace_in(main, "PlayerSpecialAttackState.cs", "\t\tbase.OnExit();\n\t\tbase.StateMachine.RemoveActionFlag(",
               "\t\tbase.OnExit();\n\t\tbase.StateMachine.SetInvulnerability(invulnerable: false);\n\t\tbase.StateMachine.ToggleAttackWindow(value: true, PlayerController.AttackWindowFlags.All);\n\t\tbase.StateMachine.RemoveActionFlag(", "P33c special attack clears invulnerability on exit")
    replace_in(main, "PlayerSpecialAttackState.cs", "\tpublic override void Simulate(float deltaTime)\n\t{\n",
               "\tpublic override void Simulate(float deltaTime)\n\t{\n\t\t_elapsed += deltaTime;\n\t\tif (_elapsed > MaxDuration || (_elapsed > 0.5f && base.StateMachine.AnimatorNormalizedTime >= 1f))\n\t\t{\n\t\t\t_elapsed = 0f;\n\t\t\tbase.StateMachine.SwitchState(\"Idle\");\n\t\t\treturn;\n\t\t}\n", "P33d special attack watchdog")
    # P36: an attack closes the attack window and only a move-sequencer event reopens it; when that event never fires the
    # fighter idles with the window shut (no melee, no run, no dash) until a hit resets it. Idle means the move is over:
    # reopen the normal/special/dash windows (the heavy-hit-reaction lock is left alone).
    replace_in(main, "PlayerIdleState.cs", "\t\tbase.StateMachine.UpdateTargetVelocity(Vector3.zero);\n",
               "\t\tbase.StateMachine.UpdateTargetVelocity(Vector3.zero);\n\t\tbase.StateMachine.ToggleAttackWindow(value: true, PlayerController.AttackWindowFlags.NormalAttack | PlayerController.AttackWindowFlags.SpecialAttack | PlayerController.AttackWindowFlags.Dash);\n", "P36 idle reopens the attack window")
    # P38: fighters from the 9.2.0 character packs reference move files the 3.1.0 'moves' bundle lacks, so MoveInfo._asset
    # is null, no move events load and their attacks never hit. Fall back to the donor move files extracted into
    # Resources/movedata92 by extract_donor_moves.py.
    replace_in(main, "EB/MoveEditor/MoveInfo.cs", "\t\tpublic void DeserializeFromTextAsset()\n\t\t{\n",
               "\t\tpublic void DeserializeFromTextAsset()\n\t\t{\n\t\t\tif (_asset == null && !string.IsNullOrEmpty(_name))\n\t\t\t{\n\t\t\t\t_asset = Resources.Load<TextAsset>(\"movedata92/\" + _name);\n\t\t\t}\n", "P38 donor move data fallback")
    # P40 (shell only): the game's Resources live in the converted bundle, not in the player
    if os.environ.get("TFTF_SHELL") == "1":
        for p in list(fp.rglob("*.cs")) + list(main.rglob("*.cs")):
            if p.name == "TftfResources.cs":
                continue
            t = rd(p)
            t2, n = re.subn(r"(?<![\w])(?:UnityEngine\.)?Resources\.(Load|LoadAsync|LoadAll)\b", r"TftfResources.\1", t)
            t2 = re.sub(r"\bResourceRequest\b", "TftfRequest", t2)      # EB.Assets.LoadFromResources' request type
            if t2 != t:
                wr(p, t2); note("P40 Resources.Load -> TftfResources.Load", n)
    # P41 (shell only): the base game's assets/ folder is extracted from the user's APK into app storage by the launcher;
    # the game's file driver searches it where it would otherwise have read the APK itself.
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(fp, "EB/FileSystem/FileDriver.cs", "\t\t\tManager.Log(\"Datapath: \" + Application.dataPath);\n",
                   "\t\t\tManager.Log(\"Datapath: \" + Application.dataPath);\n\t\t\tstring tftfBase = Path.Combine(Application.persistentDataPath, \"tftf/base/assets\");\n\t\t\tAddSearchPath(tftfBase, SearchOrder.First);   // registered even if the first-run installer has not copied the files yet\n", "P41 file driver searches the extracted base assets")
    # P42 (shell only): the attribution SDK (MAT) has no Java side in the shell; its binding calls it only outside the
    # editor, so switch those branches off (the default return values already exist after each branch).
    if os.environ.get("TFTF_SHELL") == "1":
        mat = fp / "MATSDK/MATBinding.cs"
        if mat.exists():
            t = rd(mat)
            wr(mat, t.replace("!Application.isEditor", "false"))
            note("P42 MATBinding attribution calls disabled", t.count("!Application.isEditor"))
    # P45 (shell only): a shader the asset-bundle loader finds for a path (an old compiled shader from a 3.1.0 bundle, which Unity
    # 2020 cannot name or use) is replaced by the authored shader with the same declared name (see TftfResources.ResolveShader)
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(fp, "EB/Assets.cs", "\t\t\tobj = AssetBundleManager.Instance.Load<T>(path);\n",
                   "\t\t\tobj = AssetBundleManager.Instance.Load<T>(path);\n\t\t\tif (obj is Shader)\n\t\t\t{\n\t\t\t\tShader tftfShader = TftfResources.ResolveShader(path);\n\t\t\t\tif (tftfShader != null)\n\t\t\t\t{\n\t\t\t\t\tobj = tftfShader;\n\t\t\t\t}\n\t\t\t}\n",
                   "P45 shaders found in bundles are replaced by the authored ones")
    # P46 (shell only): the shader a widget was saved with is a stub object of the converted bundle whose Unity name is empty;
    # the draw call asks for the declared name instead
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(fp, "UIDrawCall.cs", "string text = ((mShader != null) ? mShader.name :", "string text = ((mShader != null) ? TftfResources.NameOf(mShader) :",
                   "P46 draw calls use the declared shader name")
    # P48 (shell only): the base's defender slots are not authored, so selecting a base node made DefenderSlot.Refresh throw inside the
    # edit popup's SetEnterParams; the popup never finished opening and the base UI stayed hidden ("frozen"). The refresh is guarded.
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(main, "DefenderSlot.cs",
                   "\tpublic void Refresh(BCGEntity entity, List<string> modes, BCGEntity secondEntity = null, bool isBossFinal = false, int possibleReplacements = 0, bool shouldScaleHeroRating = false)\n\t{\n",
                   "\tpublic void Refresh(BCGEntity entity, List<string> modes, BCGEntity secondEntity = null, bool isBossFinal = false, int possibleReplacements = 0, bool shouldScaleHeroRating = false)\n\t{\n\t\ttry\n\t\t{\n\t\t\tRefreshUnguarded(entity, modes, secondEntity, isBossFinal, possibleReplacements, shouldScaleHeroRating);\n\t\t}\n\t\tcatch (System.Exception ex)\n\t\t{\n\t\t\tUnityEngine.Debug.LogWarning(\"[TFTF] DefenderSlot.Refresh failed, slot left empty: \" + ex.Message);\n\t\t}\n\t}\n\n\tprivate void RefreshUnguarded(BCGEntity entity, List<string> modes, BCGEntity secondEntity, bool isBossFinal, int possibleReplacements, bool shouldScaleHeroRating)\n\t{\n",
                   "P48 DefenderSlot.Refresh guarded")
    # P49 (shell only): the board plays a tile's post-fight dialogue on every return to the board and never asks who won, so the
    # "after winning" messages also appeared after a loss. FightFlow records the result; the dialogue is skipped after a loss.
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(main, "FightFlow.cs", "activeQuest.ResolveMatch(_matchId, matchResult, enemyHealth,",
                   "TftfFightState.LastWon = _fightResultData != null && _fightResultData.IsWin;\n\t\t\t\tactiveQuest.ResolveMatch(_matchId, matchResult, enemyHealth,",
                   "P49a FightFlow records the quest fight result")
        replace_in(main, "Quests/Presentation/Gameboard.cs", "\t\t\t\tif (postFight)\n\t\t\t\t{\n\t\t\t\t\tif (Profile.Instance.SeenPostFightDialogueNodes != null",
                   "\t\t\t\tif (postFight && !TftfFightState.LastWon)\n\t\t\t\t{\n\t\t\t\t\treturn _displayingDialogue;\n\t\t\t\t}\n\t\t\t\tif (postFight)\n\t\t\t\t{\n\t\t\t\t\tif (Profile.Instance.SeenPostFightDialogueNodes != null",
                   "P49b the post-fight dialogue is skipped after a lost fight")
    # P50 (shell only, dev): tftf/fullmana.txt keeps player 0's special meter full (see TftfFightState.FullMana)
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(main, "PlayerController.cs", "\tprotected override void Update()\n\t{\n",
                   "\tprotected override void Update()\n\t{\n\t\tif (TftfFightState.FullMana && Id == 0 && Attributes != null)\n\t\t{\n\t\t\tAttributes.NormalizedMana = 1f;\n\t\t}\n",
                   "P50 dev switch: full special meter")
    # P47 (shell only): the prefab loader adds a widget's components one by one, so UIStretch.Awake runs before the UITexture/UISprite
    # exists and never finds it; the stretch then scales the transform instead of sizing the widget (the loading screen's background
    # became 770 times too tall: coloured streaks). Look the widget up again in Start.
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(fp, "UIStretch.cs", "\t\tmRoot = NGUITools.FindInParents<UIRoot>(base.gameObject);\n",
                   "\t\tmRoot = NGUITools.FindInParents<UIRoot>(base.gameObject);\n\t\tmWidget = GetComponent<UIWidget>();\n\t\tmSprite = GetComponent<UISprite>();\n\t\tmVectorArt = GetComponent<UIVectorArt>();\n\t\tmPanel = GetComponent<UIPanel>();\n",
                   "P47 UIStretch finds its widget again in Start")
    # P44 (shell only): the revival splash (big logo, "Unicron's Lair") between the Kabam screen and the title screen
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(main, "BootFlow.cs", "\t\tyield return Coroutines.Run(ReInitializeLoadingScreen());\n",
                   "\t\tyield return Coroutines.Run(ReInitializeLoadingScreen());\n\t\tyield return Coroutines.Run(TftfSplash.Show());\n",
                   "P44 revival splash after the Kabam screen")
    # P43 (shell only): the scene diagnostics log a dozen lines every 15 s; the shell logs them once, 20 s after start.
    if os.environ.get("TFTF_SHELL") == "1":
        replace_in(fp, "TftfDiag.cs", "_next = Time.realtimeSinceStartup + 15f;", "_next = Time.realtimeSinceStartup + 3600f;",
                   "P43 diagnostics logged once instead of every 15 s")
    # P29: GetRenderPath(kMain) always answers Forward (and records it in the static table the buffer setup reads)
    replace_in(fp, "EB/Rendering/EBGBuffer.cs",
               "\t\tpublic static eRENDER_PATH GetRenderPath(eMRT_BUFFER buffer, bool nativeResolution, bool hasPostFX)\n\t\t{\n",
               "\t\tpublic static eRENDER_PATH GetRenderPath(eMRT_BUFFER buffer, bool nativeResolution, bool hasPostFX)\n\t\t{\n\t\t\tif (buffer == eMRT_BUFFER.kMain)\n\t\t\t{\n\t\t\t\t_RenderPath[0] = eRENDER_PATH.Forward;\n\t\t\t\treturn eRENDER_PATH.Forward;\n\t\t\t}\n",
               "P29 EBGBuffer forward render path for the main buffer")
    # P30: Dictionary<T,..>(EnumComparer<T>) throws TypeLoadException under Unity 2020 Mono: use the default comparer
    for root in (fp, main):
        replace_in(root, "EnumMap.cs", "new Dictionary<T, string>(kComparer);", "new Dictionary<T, string>();", "P30 EnumMap default comparer")
    # P31: EnumComparer<T> used an explicit-layout generic union struct, which Unity 2020 Mono cannot load
    for root in (fp, main):
        if (root / "EnumComparer.cs").exists():
            wr(root / "EnumComparer.cs", """using System.Collections.Generic;

public class EnumComparer<T> : IEqualityComparer<T> where T : struct
{
	public bool Equals(T x, T y)
	{
		return EqualityComparer<T>.Default.Equals(x, y);
	}

	public int GetHashCode(T x)
	{
		return x.GetHashCode();
	}
}
""")
            note("P31 EnumComparer rewritten without a generic union")
    # P32: the ripped QualitySettings carry skinWeights=0 (invalid) which breaks skinned meshes (flat 'poster' fighters)
    replace_in(main, "PerformanceManager.cs",
               "Application.targetFrameRate = 30;\n\t\tQualitySettings.vSyncCount = 2;",
               "Application.targetFrameRate = 30;\n\t\tQualitySettings.vSyncCount = 2;\n\t\tQualitySettings.skinWeights = SkinWeights.FourBones;\n\t\tQualitySettings.lodBias = 1f;",
               "P32 PerformanceManager forces four-bone skinning and a sane lodBias")
    # P12: forward camera path (the deferred G-buffer/light pipeline cannot work with stub shaders)
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs",
               "_renderPath = EBGBuffer.GetRenderPath(EBGBuffer.eMRT_BUFFER.kMain, nativeResolution, HasPostFX());",
               "_renderPath = EBGBuffer.GetRenderPath(EBGBuffer.eMRT_BUFFER.kMain, nativeResolution, false);", "P12 EBDeferredCamera forced to Forward render path")
    replace_in(fp, "EB/Rendering/EBDeferredCamera.cs",
               "\t\t\tEBPlanarReflectionManager.Instance.Render(GetComponent<Camera>());",
               "\t\t\ttry { EBPlanarReflectionManager.Instance.Render(GetComponent<Camera>()); } catch (System.Exception) { }",
               "P13 planar reflection render guarded")
    # P14: a missing UIScreenCapture shader must not throw out of the window-stack callbacks
    for root in (fp, main):
        replace_in(root, "EB/Rendering/EBUIScreenCapture.cs",
                   "_SCBlitMaterial = new Material(shader);",
                   "if (shader != null) { _SCBlitMaterial = new Material(shader); }",
                   "P14 EBUIScreenCapture tolerates a missing shader")
        replace_in(root, "EB/Rendering/EBUIScreenCapture.cs",
                   "\tpublic bool Capture(ref UITextureRef uiTexture, UIScreenCaptureParams captureParams, Camera[] camerasToInclude = null)\n\t{\n",
                   "\tpublic bool Capture(ref UITextureRef uiTexture, UIScreenCaptureParams captureParams, Camera[] camerasToInclude = null)\n\t{\n\t\tSetup();\n\t\tif (_SCBlitMaterial == null)\n\t\t{\n\t\t\treturn false;\n\t\t}\n",
                   "P14 EBUIScreenCapture.Capture returns false without a material")
    # P15: Unity-2020 port noise (stub shaders etc.) must not turn every logged exception into the critical-error popup
    replace_in(fp, "EB/BugReport.cs", r"if \((!_sent && )?type == LogType\.Exception\)",
               "if (false && type == LogType.Exception)",
               "P15 exceptions no longer trigger the bug-report critical popup", regex=True)
    # P16: tolerate a null callback in InventoryAPI._Sync
    replace_in(fp, "EB/Sparx/InventoryAPI.cs", "\t\t\t\t\tcallback(result.id, null, result.hashtable);",
               "\t\t\t\t\tif (callback != null) callback(result.id, null, result.hashtable);", "P16 InventoryAPI callback null-check")
    # P17: log what AssetBundleManager actually hands back (bundle null? asset null? names)
    replace_in(fp, "EB/AssetBundleManager.cs",
               "\t\t\tAssetBundleRequest asyncOp = info.bundle.LoadAssetAsync(name, typeof(T));\n\t\t\tyield return asyncOp;\n\t\t\tT asset = (T)asyncOp.asset;\n",
               "\t\t\tif (info.bundle == null) { UnityEngine.Debug.LogError(\"[ABM] bundle null for \" + path + \" (info.isLoaded=\" + info.isLoaded + \")\"); cb(default(T)); yield break; }\n"
               "\t\t\tAssetBundleRequest asyncOp = info.bundle.LoadAssetAsync(name, typeof(T));\n\t\t\tyield return asyncOp;\n\t\t\tT asset = (T)asyncOp.asset;\n"
               "\t\t\tif (asset == null) { string[] names = info.bundle.GetAllAssetNames(); UnityEngine.Debug.LogError(\"[ABM] asset null: path=\" + path + \" name=\" + name + \" type=\" + typeof(T).Name + \" bundleAssets=\" + names.Length + \" first=\" + (names.Length > 0 ? names[0] : \"-\")); }\n"
               "\t\t\telse { UnityEngine.Debug.Log(\"[ABM] loaded \" + path + \" -> \" + asset.GetType().Name); }\n",
               "P17 AssetBundleManager load diagnostics")
    # P18: ILSpy lost the body of the requested-bundle match in ODRManager.OpenWad (same flags as the else-if branch beside it)
    replace_in(fp, "EB/Sparx/ODRManager.cs",
               r"if \(!string\.Equals\(assetBundleName, requestedBundle, StringComparison\.OrdinalIgnoreCase\)\)\s*\{\s*\}",
               "if (string.Equals(assetBundleName, requestedBundle, StringComparison.OrdinalIgnoreCase))\n"
               "\t\t\t\t\t\t{\n\t\t\t\t\t\t\tzipEntryOpenArchiveOptions |= ODRManager.OpenArchiveOptions.LoadInline;\n"
               "\t\t\t\t\t\t\tzipEntryOpenArchiveOptions |= ODRManager.OpenArchiveOptions.LoadAssetBundles;\n"
               "\t\t\t\t\t\t\tbreak;\n\t\t\t\t\t\t}",
               "P18 ODRManager.OpenWad requested-bundle flags restored", regex=True)
    # P19: ILSpy split iterator-closure captures into dead copies (val/flag/text) in Assets.DoLoadAsync: restore the originals
    F = "EB/Assets.cs"
    for old, new in (
        ("\t\t\t\tT val;\n\t\t\t\tif (ODRManager.Instance == null)\n\t\t\t\t{\n\t\t\t\t\tbool flag;\n",
         "\t\t\t\tif (ODRManager.Instance == null)\n\t\t\t\t{\n"),
        ("\t\t\t\t\t\tval = resourceObj;\n\t\t\t\t\t\tflag = true;\n", "\t\t\t\t\t\tobj = resourceObj;\n\t\t\t\t\t\tcheckedResources = true;\n"),
        ("\t\t\t\t\tstring text;\n\t\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(string err, T val2)\n\t\t\t\t\t{\n\t\t\t\t\t\tval = val2;\n\t\t\t\t\t\ttext = err;\n",
         "\t\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(string err, T val2)\n\t\t\t\t\t{\n\t\t\t\t\t\tobj = val2;\n\t\t\t\t\t\terrFromAssetBundleManager = err;\n"),
        ("\t\t\t\t\t\tyield return LoadFromResources(path, delegate(T resourceObj)\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tval = resourceObj;\n",
         "\t\t\t\t\t\tyield return LoadFromResources(path, delegate(T resourceObj)\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tobj = resourceObj;\n"),
    ):
        replace_in(fp, F, old, new, "P19 Assets.DoLoadAsync closure captures restored")
    replace_in(fp, F,
               "\t\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(T val2)\n"
               "\t\t\t\t\t{\n\t\t\t\t\t\tval = val2;\n",
               "\t\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(T val2)\n"
               "\t\t\t\t\t{\n\t\t\t\t\t\tobj = val2;\n",
               "P19 Assets.DoLoadAsync one-argument asset callback restored")
    # P20: bound the wait on the asset cleanup task (a stuck Resources.UnloadUnusedAssets froze every load behind it)
    replace_in(fp, "EB/Assets.cs",
               "\t\t\tT obj = (T)null;\n\t\t\twhile (_cleanupTask != null && !_cleanupTask.isDone)\n\t\t\t{\n\t\t\t\tyield return null;\n\t\t\t}\n",
               "\t\t\tT obj = (T)null;\n\t\t\tfloat cleanupWaitStart = UnityEngine.Time.realtimeSinceStartup;\n"
               "\t\t\twhile (_cleanupTask != null && !_cleanupTask.isDone && UnityEngine.Time.realtimeSinceStartup - cleanupWaitStart < 5f)\n\t\t\t{\n\t\t\t\tyield return null;\n\t\t\t}\n",
               "P20 Assets.DoLoadAsync cleanup wait bounded")
    # P21: library callbacks must always release the wait, even when the load returned null
    replace_in(main, "Quests/Presentation/GameboardBuilder.cs",
               r"if \(lib != null\)(\s*\{\s*_(?:Theme|Common|Building)Library = lib;)", r"if (true)\1",
               "P21 GameboardBuilder library callbacks always set the loaded flag", regex=True)
    # P22: DoLoadMultiAsync lost its per-iteration capture
    replace_in(fp, "EB/Assets.cs",
               "\t\t\tT[] array = default(T[]);\n\t\t\tint num = default(int);\n\t\t\tfor (int i = 0; i < paths.Length; i++)\n\t\t\t{\n\t\t\t\tyield return LoadAsync(paths[i], delegate(T asset)\n\t\t\t\t{\n\t\t\t\t\tarray[num] = asset;\n",
               "\t\t\tfor (int i = 0; i < paths.Length; i++)\n\t\t\t{\n\t\t\t\tint num = i;\n\t\t\t\tyield return LoadAsync(paths[i], delegate(T asset)\n\t\t\t\t{\n\t\t\t\t\tobjectList[num] = asset;\n",
               "P22 Assets.DoLoadMultiAsync per-iteration capture restored")
    # P23: alliance-quest config alias
    replace_in(main, "Quests/Presentation/GameboardBuilder.cs", "Config config2 = default(Config);", "Config config2 = config;",
               "P23 GameboardBuilder config alias")
    # P25: players without lvlB resources must not NRE the badge counters
    replace_in(main, "TutorialManagerHelper.cs",
               "if (Hub.Instance.ResourcesManager.GetResource(typeName).Amount > 0 && !IsTutorialComplete(text)",
               "if (Hub.Instance.ResourcesManager.GetResource(typeName) != null && Hub.Instance.ResourcesManager.GetResource(typeName).Amount > 0 && !IsTutorialComplete(text)",
               "P25 GetPendingBuildingUnlockTutorial null-safe")
    # ILSpy loses the synthetic string-switch value in this error callback. The
    # default branch should not match a named error case; initialize its value
    # to the non-matching default so the recovered source compiles.
    replace_in(main, "AllianceMembersState.cs",
               "int num;\n", "int num = 0;\n",
               "S29 AllianceMembersState recovered string switch initialized")
    # P26: honouring hold=true pins preloaded libraries and stalls the preload manager here; keep the previous behaviour
    replace_in(fp, "EB/AssetManager.cs", "bool hold2 = hold;", "bool hold2 = false;", "P26 AssetManager hold pinning disabled")
    p = fp / "EB/FileSystem/ZipDriver.cs"   # P7 (same fix as 2.0.2): SharpZipLib asks for OEM code page 850/437
    if p.exists():
        t = rd(p)
        if "AsciiCodePageProvider" not in t:
            t = t.replace("\tpublic class ZipDriver : Driver\n\t{",
                "\tinternal sealed class AsciiCodePageProvider : System.Text.EncodingProvider\n\t{\n"
                "\t\tpublic override System.Text.Encoding GetEncoding(int codepage) { return codepage == 850 || codepage == 437 ? System.Text.Encoding.ASCII : null; }\n"
                "\t\tpublic override System.Text.Encoding GetEncoding(string name) { return null; }\n\t}\n\n\tpublic class ZipDriver : Driver\n\t{", 1)
            t = t.replace("\t\tpublic ZipDriver()\n\t\t{\n", "\t\tpublic ZipDriver()\n\t\t{\n\t\t\tSystem.Text.Encoding.RegisterProvider(new AsciiCodePageProvider());\n", 1)
            wr(p, t); note("P7 ZipDriver code-page 850/437 provider (APK zip reads)")
    p = fp / "EB/Sparx/GachaManager.cs"
    if p.exists():
        t = rd(p)
        t2 = re.sub(r"default:\s*\{\s*int num;\s*if \(num == 1\)\s*\{\s*result = GetBox\(data\);\s*break;\s*\}\s*(Debug\.LogError\([^;]*\);)\s*break;\s*\}",
                    r'case "box":\n\t\t\t\t\tresult = GetBox(data);\n\t\t\t\t\tbreak;\n\t\t\t\tdefault:\n\t\t\t\t\t\1\n\t\t\t\t\tbreak;', t)
        if t2 != t: wr(p, t2); note("S16 ILSpy string-switch lost case (GachaManager 'box')")
    stub = fp / "EB/Replication/Unity2020NetworkEnums.cs"
    stub.parent.mkdir(parents=True, exist_ok=True)
    wr(stub, """namespace EB.Replication
{
\tpublic enum NetworkStateSynchronization { Off = 0, ReliableDeltaCompressed = 1, Unreliable = 2 }
\tpublic enum RPCMode { Server = 0, Others = 1, OthersBuffered = 2, All = 3, AllBuffered = 4 }
}
""")
    note("S6 stub legacy-networking enums (removed in 2018.2)")
    # S7 ISocialPlatform/ILocalUser gained Authenticate(Action<bool,string>) after Unity 5
    p = fp / "GooglePlayGames/PlayGamesPlatform.cs"
    if p.exists():
        t = rd(p)
        if "Action<bool, string> callback" not in t:
            t = re.sub(r"(?m)^(\t+)public void Authenticate\(ILocalUser (\w+), Action<bool> callback\)",
                       lambda m: (f"{m[1]}public void Authenticate(ILocalUser {m[2]}, Action<bool, string> callback)\n{m[1]}{{\n"
                                  f"{m[1]}\tAuthenticate({m[2]}, (Action<bool>)(ok => callback?.Invoke(ok, ok ? null : \"authentication failed\")));\n{m[1]}}}\n\n"
                                  f"{m[1]}public void Authenticate(ILocalUser {m[2]}, Action<bool> callback)"), t, count=1)
            t = re.sub(r"(?m)^(\t+)public void Authenticate\(Action<bool> callback\)",
                       lambda m: (f"{m[1]}public void Authenticate(Action<bool, string> callback)\n{m[1]}{{\n"
                                  f"{m[1]}\tAuthenticate((Action<bool>)(ok => callback?.Invoke(ok, ok ? null : \"authentication failed\")));\n{m[1]}}}\n\n"
                                  f"{m[1]}public void Authenticate(Action<bool> callback)"), t, count=1)
            wr(p, t); note("S7 Authenticate(Action<bool,string>) overload")
    p = fp / "GooglePlayGames/PlayGamesLocalUser.cs"
    if p.exists():
        t = rd(p)
        if "Action<bool, string> callback" not in t:
            t = re.sub(r"(?m)^(\t+)public void Authenticate\(Action<bool> callback\)",
                       lambda m: (f"{m[1]}public void Authenticate(Action<bool, string> callback)\n{m[1]}{{\n"
                                  f"{m[1]}\tmPlatform.Authenticate(callback);\n{m[1]}}}\n\n"
                                  f"{m[1]}public void Authenticate(Action<bool> callback)"), t, count=1)
            wr(p, t); note("S7 Authenticate(Action<bool,string>) overload (local user)")

REPAIRS = Path(__file__).resolve().parent / "repairs.json"  # accumulated compiler-log-driven repairs

def collect(log: Path):
    """Parse a Unity compile log; append line-preserving repair records to repairs.json."""
    import json
    recs = json.loads(REPAIRS.read_text()) if REPAIRS.exists() else []
    seen = {(r["file"], r["line"], r["kind"], r.get("var")) for r in recs}
    pat = re.compile(r"^(Assets/[^()]+\.cs)\((\d+),\d+\): error (CS\d+): (.*)$")
    added = 0
    for ln in log.read_text(errors="replace").splitlines():
        m = pat.match(ln)
        if not m: continue
        f, line, code, msg = m[1], int(m[2]), m[3], m[4]
        rec = None
        if code == "CS0165":
            v = re.search(r"'(\w+)'", msg)
            rec = {"kind": "unassigned-local", "var": v[1]}
        elif code == "CS0843":
            rec = {"kind": "struct-this"}
        elif code == "CS0304":
            rec = {"kind": "new-constraint"}
        if rec:
            rec.update(file=f, line=line)
            key = (f, line, rec["kind"], rec.get("var"))
            if key not in seen:
                seen.add(key); recs.append(rec); added += 1
    REPAIRS.write_text(json.dumps(recs, indent=1))
    print(f"collected {added} new log-driven repairs ({len(recs)} total)")

def log_driven(proj: Path):
    import json
    if not REPAIRS.exists(): return
    recs = json.loads(REPAIRS.read_text())
    by_file = {}
    for r in recs: by_file.setdefault(r["file"], []).append(r)
    for f, rs in by_file.items():
        p = proj / f
        if not p.exists(): continue
        lines = rd(p).split("\n")
        for r in rs:
            i = r["line"] - 1
            if r["kind"] == "unassigned-local":
                # nearest preceding declaration 'T name;' -> 'T name = default;'
                for j in range(i, max(i - 80, -1), -1):
                    m = re.match(r"^(\s*)([\w.<>\[\]?]+) " + re.escape(r["var"]) + r";\s*$", lines[j])
                    if m:
                        lines[j] = f"{m[1]}{m[2]} {r['var']} = default;"
                        note("L1 unassigned local -> '= default' (ILSpy lost string-switch map; SEMANTIC RISK)")
                        break
            elif r["kind"] == "struct-this":
                # ctors of the struct containing/at this line: add ': this()'
                for j in range(len(lines)):
                    if re.match(r"^\s*public \w+\([^)]*\)\s*$", lines[j]) and not lines[j].rstrip().endswith("this()") \
                       and j + 1 < len(lines) and lines[j + 1].strip() == "{" and abs(j - i) < 12:
                        lines[j] = lines[j].rstrip() + " : this()"
                        note("L2 struct ctor ': this()' for auto-properties")
            elif r["kind"] == "new-constraint":
                for j in range(i, max(i - 12, -1), -1):
                    if re.search(r"where T : [\w.<>, ]+$", lines[j]) and "new()" not in lines[j]:
                        lines[j] = lines[j].rstrip() + ", new()"
                        note("L3 add new() constraint")
                        break
        wr(p, "\n".join(lines))

def third_party(assets: Path):
    """Repairs for the ILSpy output of the bundled third-party assemblies (Fabric, Facebook, NBidi)."""
    sc = assets / "Scripts"
    # Unity 5 WWW.audioClip property -> GetAudioClip()
    replace_in(sc, "Fabric.Core/Fabric/AudioComponent.cs", "www.audioClip;", "www.GetAudioClip(false, false);",
               "T1 WWW.audioClip -> GetAudioClip")
    # Profiler moved to UnityEngine.Profiling; usedHeapSize is a uint property there too
    replace_in(sc, "Fabric.Core/Fabric/FabricManager.cs", r"(?<![\w.])Profiler\.usedHeapSize",
               "(uint)UnityEngine.Profiling.Profiler.usedHeapSize", "T2 Profiler namespace", regex=True)
    # setter-only properties whose backing field was lost by the decompiler
    replace_in(sc, "Facebook.Unity/Facebook.Unity/MethodCall.cs",
               "\t\tprotected FacebookBase FacebookImpl\n\t\t{\n\t\t\t[CompilerGenerated]\n\t\t\tset\n\t\t\t{\n\t\t\t\t_003CFacebookImpl_003Ek__BackingField = value;\n\t\t\t}\n\t\t}",
               "\t\tprotected FacebookBase FacebookImpl { get; set; }", "T3 MethodCall.FacebookImpl auto-property")
    replace_in(sc, "Facebook.Unity/Facebook.Unity/MethodCall.cs",
               "\t\tprotected MethodArguments Parameters\n\t\t{\n\t\t\t[CompilerGenerated]\n\t\t\tset\n\t\t\t{\n\t\t\t\t_003CParameters_003Ek__BackingField = value;\n\t\t\t}\n\t\t}",
               "\t\tprotected MethodArguments Parameters { get; set; }", "T4 MethodCall.Parameters auto-property")


def apply(assets: Path):
    fp = assets / "Plugins/Assembly-CSharp-firstpass"
    main = assets / "Scripts/Assembly-CSharp"
    generic(fp); generic(main)
    third_party(assets)
    specific(fp, main)
    log_driven(assets.parent)
    for k, v in LOG.items():
        print(f"  {v:5d}  {k}")

if __name__ == "__main__":
    if sys.argv[1] == "--collect":
        collect(Path(sys.argv[2]))
    else:
        apply(Path(sys.argv[1]))
