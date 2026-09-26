#!/usr/bin/env python3
"""Adapt the original 2.0.2 firstpass sources to Unity 2020.3 APIs."""

from __future__ import annotations

import argparse
import re
from pathlib import Path


def adapt(root: Path) -> int:
    edits = 0
    edits += replace_count(root / "EB/DownloadHandlerStream.cs",
                           "UnityEngine.Experimental.Networking",
                           "UnityEngine.Networking")
    for relative in ("FontLocalizer.cs", "UIMaskMaterialManager.cs", "EB/Assets.cs"):
        edits += replace_count(root / relative, "Tuple<", "EB.Collections.Tuple<")
    for relative in ("GooglePlayGames.Native/ConversionUtils.cs",
                     "GooglePlayGames.Native/NativeClient.cs"):
        edits += replace_count(root / relative, "Types.",
                               "GooglePlayGames.Native.Cwrapper.Types.")

    platform = root / "GooglePlayGames/PlayGamesPlatform.cs"
    if "Authenticate(Action<bool, string> callback)" not in platform.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            platform,
            "\tpublic void Authenticate(Action<bool> callback)\n",
            "\tpublic void Authenticate(Action<bool, string> callback)\n\t{\n"
            "\t\tAuthenticate((Action<bool>)(success => callback?.Invoke(success, "
            "success ? null : \"Google Play authentication failed\")));\n\t}\n\n"
            "\tpublic void Authenticate(Action<bool> callback)\n",
        )
    if "Authenticate(ILocalUser unused, Action<bool, string> callback)" not in platform.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            platform,
            "\tpublic void Authenticate(ILocalUser unused, Action<bool> callback)\n",
            "\tpublic void Authenticate(ILocalUser unused, Action<bool, string> callback)\n"
            "\t{\n\t\tAuthenticate((Action<bool>)(success => callback?.Invoke(success, "
            "success ? null : \"Google Play authentication failed\")));\n\t}\n\n"
            "\tpublic void Authenticate(ILocalUser unused, Action<bool> callback)\n",
        )
    local_user = root / "GooglePlayGames/PlayGamesLocalUser.cs"
    if "Authenticate(Action<bool, string> callback)" not in local_user.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            local_user,
            "\tpublic void Authenticate(Action<bool> callback)\n",
            "\tpublic void Authenticate(Action<bool, string> callback)\n"
            "\t{\n\t\tmPlatform.Authenticate(callback);\n\t}\n\n"
            "\tpublic void Authenticate(Action<bool> callback)\n",
        )
    edits += replace_count(root / "NGUITools.cs",
        "Application.platform != RuntimePlatform.WindowsWebPlayer && Application.platform != RuntimePlatform.OSXWebPlayer",
        "true")
    edits += replace_count(root / "UIPanel.cs",
        " || Application.platform == RuntimePlatform.WindowsWebPlayer", "")
    edits += replace_count(root / "EB.Director.Runtime/GroupInstance.cs",
        "if (Application.platform == RuntimePlatform.OSXWebPlayer)", "if (false)")
    edits += replace_count(root / "TexturePoolManager.cs", "Cache.", "EB.Cache.")
    edits += replace_count(root / "EB.Sparx/LoginAPI.cs",
        "Application.bundleIdentifier", "Application.identifier")
    zip_driver = root / "EB.FileSystem/ZipDriver.cs"
    zip_source = zip_driver.read_text(encoding="utf-8-sig")
    if "AsciiCodePageProvider" not in zip_source:
        zip_source = zip_source.replace(
            "public class ZipDriver : Driver\n{",
            """internal sealed class AsciiCodePageProvider : System.Text.EncodingProvider
{
    public override System.Text.Encoding GetEncoding(int codepage)
    {
        return codepage == 850 || codepage == 437 ? System.Text.Encoding.ASCII : null;
    }

    public override System.Text.Encoding GetEncoding(string name)
    {
        return null;
    }
}

public class ZipDriver : Driver
{""",
        )
        zip_driver.write_text(zip_source, encoding="utf-8")
        edits += 1
    if "RegisterProvider(new AsciiCodePageProvider())" not in zip_source:
        edits += replace_count(
            zip_driver,
            "public ZipDriver()\n\t{\n\t\tif (Application.dataPath.EndsWith(\".apk\"))",
            "public ZipDriver()\n\t{\n\t\tSystem.Text.Encoding.RegisterProvider(new AsciiCodePageProvider());\n\t\tif (Application.dataPath.EndsWith(\".apk\"))",
        )
    manager = root / "EB.FileSystem/Manager.cs"
    manager_source = manager.read_text(encoding="utf-8-sig")
    local_pack_method = (
        '\tpublic Coroutine MountLocalPack(string name, string assetDirectory)\n'
        '\t{\n'
        '\t\tPackDriver packDriver = GetDriver<PackDriver>();\n'
        '\t\treturn packDriver.Mount(name, "zip://" + assetDirectory, SearchOrder.First, null, null);\n'
        '\t}\n\n'
    )
    if local_pack_method in manager_source:
        manager.write_text(manager_source.replace(local_pack_method, "", 1), encoding="utf-8")
        edits += 1
    pack_driver = root / "EB.FileSystem/PackDriver.cs"
    pack_source = pack_driver.read_text(encoding="utf-8-sig")
    original_pack_source = pack_source
    if "_localPortraitPackMounted" not in pack_source:
        pack_source = pack_source.replace(
            "\tprivate bool _verbose;\n",
            "\tprivate bool _verbose;\n\n\tprivate bool _localPortraitPackMounted;\n",
            1,
        )
    mount_log = '\t\t\tDebug.Log("Starting local portraits_odr mount");\n'
    if mount_log not in pack_source:
        pack_source = pack_source.replace(
            '\t\tUnityEngine.Debug.Log("MONO PackDriver mount " + tocFileName);\n',
            '\t\tUnityEngine.Debug.Log("MONO PackDriver mount " + tocFileName);\n'
            '\t\tif (tocFileName == "assets_quest_fte_odr/toc.txt" && !_localPortraitPackMounted)\n'
            '\t\t{\n'
            '\t\t\t_localPortraitPackMounted = true;\n'
            + mount_log +
            '\t\t\tCoroutines.Run(_Mount("portraits_odr", "zip://portraits_odr", SearchOrder.First, null, allowAutomount: false, null));\n'
            '\t\t}\n',
            1,
        )
        if mount_log not in pack_source:
            raise ValueError("could not start the portrait pack mount at ODR callback entry")
    post_yield_mount = (
        '\t\tif (tocFileName == "assets_quest_fte_odr/toc.txt" && !_localPortraitPackMounted)\n'
        '\t\t{\n'
        '\t\t\t_localPortraitPackMounted = true;\n'
        '\t\t\tDebug.Log("Mounting local portraits_odr pack after quest ODR startup");\n'
        '\t\t\tyield return Coroutines.Run(_Mount("portraits_odr", "zip://portraits_odr", SearchOrder.First, null, allowAutomount: false, null));\n'
        '\t\t}\n'
    )
    pack_source = pack_source.replace(post_yield_mount, "", 1)
    if pack_source != original_pack_source:
        pack_driver.write_text(pack_source, encoding="utf-8")
        edits += 1
    kabam_account = root / "EB.Sparx/KabamAccountManager.cs"
    if "skipping token migration" not in kabam_account.read_text(encoding="utf-8-sig"):
        edits += replace_body(kabam_account, "private string GetLegacyToken()", LEGACY_TOKEN_BODY)

    nanigans = root / "EB.Sparx/Nanigans.cs"
    edits += replace_count(
        nanigans,
        '''private static AndroidJavaClass _class = new AndroidJavaClass("com.explodingbarrel.nanigans.Manager");''',
        '''private static AndroidJavaClass _class;

	private static bool _bridgeChecked;

	private static AndroidJavaClass GetBridge()
	{
		if (!_bridgeChecked)
		{
			_bridgeChecked = true;
			try
			{
				_class = new AndroidJavaClass("com.explodingbarrel.nanigans.Manager");
			}
			catch (AndroidJavaException ex)
			{
				Debug.LogWarning("Nanigans Android bridge unavailable; skipping analytics: " + ex.Message);
			}
		}
		return _class;
	}''',
    )
    edits += replace_count(nanigans, "_class.CallStatic(", "GetBridge()?.CallStatic(")

    apk_signature = root / "APKSignature.cs"
    apk_source = apk_signature.read_text(encoding="utf-8-sig")
    apk_source, hmac_log_edits = re.subn(
        r"(?:UnityEngine\.)*Debug\.LogWarning\(\"APK protection library unavailable; skipping HMAC check:",
        "UnityEngine.Debug.LogWarning(\"APK protection library unavailable; skipping HMAC check:",
        apk_source,
    )
    if hmac_log_edits and apk_source != apk_signature.read_text(encoding="utf-8-sig"):
        apk_signature.write_text(apk_source, encoding="utf-8")
        edits += hmac_log_edits
    if "bool started = false;" not in apk_signature.read_text(encoding="utf-8-sig"):
        edits += replace_line(apk_signature, "if (APK_Check_Start_Salt(jarPath, salt))", APK_START_GUARD)
    apk_signature_text = apk_signature.read_text(encoding="utf-8-sig")
    signature_method = re.compile(
        r"\tpublic static string GetNativeLibrarySignature\(\)\n\t\{.*?\n\t\}",
        re.DOTALL,
    )
    safe_signature_method = '''\tpublic static string GetNativeLibrarySignature()
	{
		try
		{
			return APK_GetLibraries();
		}
		catch (DllNotFoundException ex)
		{
			UnityEngine.Debug.LogWarning("APK protection library unavailable; skipping native library check: " + ex.Message);
			return string.Empty;
		}
	}'''
    adapted_signature_text, signature_edits = signature_method.subn(
        safe_signature_method, apk_signature_text, count=1
    )
    if adapted_signature_text == apk_signature.read_text(encoding="utf-8-sig"):
        signature_edits = 0
    apk_signature_text = adapted_signature_text
    if signature_edits:
        apk_signature.write_text(apk_signature_text, encoding="utf-8")
        edits += signature_edits
    elif safe_signature_method not in apk_signature_text:
        raise ValueError("could not adapt APKSignature.GetNativeLibrarySignature")

    bug_report = root / "EB/BugReport.cs"
    edits += replace_count(
        bug_report,
        "\t\t\t\t_BR_Log(log);",
        "\t\t\t\ttry { _BR_Log(log); }\n"
        "\t\t\t\tcatch (DllNotFoundException) { "
        "/* Native signal logger is optional in the recompilation. */ }",
    )
    if "optional in this build" not in bug_report.read_text(encoding="utf-8-sig"):
        edits += replace_body(bug_report, "public static void InitNativeCrashReporting()",
                              "// The native crash-signal library is optional in this build.")
    edits += replace_count(bug_report,
        'CallStatic<AndroidJavaObject>("getInstance", null)',
        'CallStatic<AndroidJavaObject>("getInstance")')
    if "skipping previous-session crash import" not in bug_report.read_text(encoding="utf-8-sig"):
        # Hoist the data-path default out of the block, then guard the Java bridge
        # calls (through the end of the block that reads the data directory).
        edits += move_line_before(bug_report, "string project = Application.persistentDataPath;",
                                  'new AndroidJavaClass("com.explodingbarrel.android.CustomExceptionHandler");')
        edits += wrap_in_try(bug_report,
                             'new AndroidJavaClass("com.explodingbarrel.android.CustomExceptionHandler");',
                             '"getDataDirectory"', CRASH_IMPORT_CATCH, through_block=True)
    edits += replace_count(
        bug_report,
        '\t\tstring text2 = _BR_BugReport(_url, project, enableSessionLog: true);',
        '\t\tstring text2 = (_customExceptionHandlerObj != null) ? _BR_BugReport(_url, project, enableSessionLog: true) : null;')
    download = root / "EB/Download.cs"
    edits += replace_count(
        download,
        '\t\t_downloadManager = new AndroidJavaObject("com.explodingbarrel.android.Download", "__DownloadHandler");',
        '\t\t// The thin decompiled APK has no Android Download bridge; use the existing C# fallback.\n\t\t_downloadManager = null;')
    edits += replace_count(
        download,
        '\t\t_downloadManager.Call("RegisterReceivers");',
        '\t\tif (_downloadManager != null) _downloadManager.Call("RegisterReceivers");')
    edits += replace_count(
        download,
        '\t\t_downloadManager.Call("UnregisterReceivers");',
        '\t\tif (_downloadManager != null) _downloadManager.Call("UnregisterReceivers");')
    edits += replace_count(
        download,
        '\t\treturn _downloadManager.Call<float>("GetProgress", new object[1] { url });',
        '\t\treturn (_downloadManager != null) ? _downloadManager.Call<float>("GetProgress", new object[1] { url }) : (IsInCache(url) ? 1f : 0f);')
    edits += replace_count(
        download,
        '\t\t_downloadManager.Call("StopPrefetchDownloads");',
        '\t\tif (_downloadManager != null) _downloadManager.Call("StopPrefetchDownloads");')
    edits += replace_count(
        download,
        '\t\t_downloadManager.Call(func, url, expectedSize, cachePath);',
        '\t\tif (_downloadManager != null) _downloadManager.Call(func, url, expectedSize, cachePath);\n\t\telse StartCoroutine(_DownloadViaSparx(url, cachePath));')
    version = root / "EB/Version.cs"
    edits += replace_count(
        version,
        'new AndroidJavaClass("com.explodingbarrel.android.UnityAndroidDeviceInfo")',
        'TryGetAndroidDeviceInfo()')
    if "private static AndroidJavaClass TryGetAndroidDeviceInfo()" not in version.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            version,
            '\tprivate static string _buildNumber = string.Empty;\n',
            '\tprivate static string _buildNumber = string.Empty;\n'
            '\tprivate const string _androidDeviceInfoClassName = "com.explodingbarrel.android.UnityAndroidDeviceInfo";\n\n'
            '\tprivate static AndroidJavaClass TryGetAndroidDeviceInfo()\n'
            '\t{\n'
            '\t\ttry\n'
            '\t\t{\n'
            '\t\t\treturn new AndroidJavaClass(_androidDeviceInfoClassName);\n'
            '\t\t}\n'
            '\t\tcatch (AndroidJavaException)\n'
            '\t\t{\n'
            '\t\t\treturn null;\n'
            '\t\t}\n'
            '\t}\n')
    benchmark = root / "EB.Sparx/BenchmarkManager.cs"
    edits += replace_count(
        benchmark,
        'new AndroidJavaClass("com.explodingbarrel.android.AndroidBenchmark")',
        'TryGetAndroidBenchmark()')
    if "private static AndroidJavaClass TryGetAndroidBenchmark()" not in benchmark.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            benchmark,
            '\tprivate List<string> _benchmarkHooks = new List<string> { "settings" };\n',
            '\tprivate List<string> _benchmarkHooks = new List<string> { "settings" };\n'
            '\tprivate const string _androidBenchmarkClassName = "com.explodingbarrel.android.AndroidBenchmark";\n\n'
            '\tprivate static AndroidJavaClass TryGetAndroidBenchmark()\n'
            '\t{\n'
            '\t\ttry\n'
            '\t\t{\n'
            '\t\t\treturn new AndroidJavaClass(_androidBenchmarkClassName);\n'
            '\t\t}\n'
            '\t\tcatch (AndroidJavaException)\n'
            '\t\t{\n'
            '\t\t\treturn null;\n'
            '\t\t}\n'
            '\t}\n')
    google_provider = root / "EB.IAP.Internal/GoogleProvider.cs"
    if "in-app purchases are disabled" not in google_provider.read_text(encoding="utf-8-sig"):
        edits += wrap_in_try(google_provider, '_class = new AndroidJavaClass("com.explodingbarrel.iap.Manager");',
                             "});", IAP_INIT_CATCH)
        edits += insert_before_line(google_provider, '_class.CallStatic("CheckPromoEligible");', IAP_PROMO_GUARD)
        edits += insert_before_line(google_provider, "List<string> list = ArrayUtils.Map(items, (Item item) => item.productId);",
                                    IAP_ITEMS_GUARD)
    gcm = root / "GCM.cs"
    if "skipping registration" not in gcm.read_text(encoding="utf-8-sig"):
        edits += wrap_in_try(gcm, 'new AndroidJavaClass("com.unity3d.player.UnityPlayer");',
                             'CallStatic("Register", senderId', GCM_CATCH)
    notifications = root / "EB.Sparx/NotificationsManager.cs"
    if "private static AndroidJavaClass TryGetLocalNotificationManager()" not in notifications.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            notifications,
            'new AndroidJavaClass("com.explodingbarrel.notifications.LocalNotificationManager")',
            'TryGetLocalNotificationManager()')
        for call in (
            'androidJavaClass2.CallStatic("clearAllNotifications", args);',
            'string text3 = androidJavaClass2.CallStatic<string>("scheduleLocalNotification", args);',
            'androidJavaClass2.CallStatic("cancelNotification", args);',
            'androidJavaClass2.CallStatic("cancelNotifications", args);',
        ):
            edits += replace_count(notifications, call, 'if (androidJavaClass2 == null) return;\n\t\t' + call)
        edits += replace_count(
            notifications,
            '\tprivate void initializeGCM(Config config)\n',
            '\tprivate static AndroidJavaClass TryGetLocalNotificationManager()\n'
            '\t{\n'
            '\t\ttry\n'
            '\t\t{\n'
            '\t\t\treturn new AndroidJavaClass("com.explodingbarrel.notifications.LocalNotificationManager");\n'
            '\t\t}\n'
            '\t\tcatch (AndroidJavaException)\n'
            '\t\t{\n'
            '\t\t\treturn null;\n'
            '\t\t}\n'
            '\t}\n\n'
            '\tprivate void initializeGCM(Config config)\n')
    permissions = root / "EB/Permissions.cs"
    permissions_text = permissions.read_text(encoding="utf-8-sig")
    permissions_text, check_edits = re.subn(
        r'androidJavaObject\.Call<string>\("checkPermissionsArray", new object\[2\]\s*\{\s*(.+?),\s*(\w+)\s*\}\)',
        r"TryCheckPermissionsArray(androidJavaObject, \1, \2)", permissions_text, flags=re.DOTALL)
    if check_edits:
        permissions.write_text(permissions_text, encoding="utf-8")
        edits += check_edits
    if "private static string TryCheckPermissionsArray(" not in permissions.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            permissions,
            '\tpublic static void RequestPermission(string permissionKey, OnPermissionResult fnCallback, int requestCode)\n',
            '\tprivate static string TryCheckPermissionsArray(AndroidJavaObject activity, string permissions, int mode)\n'
            '\t{\n'
            '\t\ttry\n'
            '\t\t{\n'
            '\t\t\treturn activity.Call<string>("checkPermissionsArray", permissions, mode);\n'
            '\t\t}\n'
            '\t\tcatch (AndroidJavaException ex)\n'
            '\t\t{\n'
            '\t\t\tDebug.LogWarning("Legacy Android permission bridge unavailable; assuming manifest permissions are granted: " + ex.Message);\n'
            '\t\t\treturn string.Empty;\n'
            '\t\t}\n'
            '\t}\n\n'
            '\tpublic static void RequestPermission(string permissionKey, OnPermissionResult fnCallback, int requestCode)\n')
    if 'permissionKeys[0] == "write_external_storage"' not in permissions.read_text(encoding="utf-8-sig"):
        edits += insert_before_line(permissions, "if (permissionKeys.Length > 0 && !_requestMap.ContainsKey(requestCode))",
                                    STORAGE_PERMISSION_GRANT)
    if "const string unityPermission =" in permissions.read_text(encoding="utf-8-sig"):
        edits += replace_count(
            permissions,
            '''\t\tif (permissionKeys != null && permissionKeys.Length == 1 && permissionKeys[0] == "write_external_storage")
\t\t{
\t\t\tconst string unityPermission = "android.permission.WRITE_EXTERNAL_STORAGE";
\t\t\tif (UnityEngine.Android.Permission.HasUserAuthorizedPermission(unityPermission))
\t\t\t{
\t\t\t\tfnCallback?.Invoke(true, requestCode);
\t\t\t\treturn;
\t\t\t}
\t\t\tUnityEngine.Android.PermissionCallbacks callbacks = new UnityEngine.Android.PermissionCallbacks();
\t\t\tcallbacks.PermissionGranted += delegate { fnCallback?.Invoke(true, requestCode); };
\t\t\tcallbacks.PermissionDenied += delegate { fnCallback?.Invoke(false, requestCode); };
\t\t\tcallbacks.PermissionDeniedAndDontAskAgain += delegate { fnCallback?.Invoke(false, requestCode); };
\t\t\tUnityEngine.Android.Permission.RequestUserPermission(unityPermission, callbacks);
\t\t\treturn;
\t\t}''',
            '''\t\tif (permissionKeys != null && permissionKeys.Length == 1 && permissionKeys[0] == "write_external_storage")
\t\t{
\t\t\t// Unity stores extracted bundles under persistentDataPath; this is app-private storage.
\t\t\tfnCallback?.Invoke(true, requestCode);
\t\t\treturn;
\t\t}''')
    edits += replace_count(root / "EB.Net/TcpClientMono.cs",
        "new SslStream(_stream, true, RemoteCertificateValidationCallback, null)",
        "new SslStream(_stream, true)")
    edits += replace_count(root / "EB.Replication/Manager.cs",
        'methodInfo.GetCustomAttributes(typeof(RPC), inherit: true)',
        'methodInfo.GetCustomAttributes(inherit: true)')
    edits += replace_count(root / "EB.Replication/Manager.cs",
        'methodInfo.GetCustomAttributes(inherit: true).Length > 0',
        'Array.Exists(methodInfo.GetCustomAttributes(inherit: true), attribute => attribute.GetType().Name == "RPC")')
    postfx = root / "EB.Rendering/EBPostFXManager.cs"
    postfx_text = postfx.read_text(encoding="utf-8-sig")
    postfx_text, postfx_edits = re.subn(
        r'^(\s*)Debug\.LogError\("Could not load postfx shader " \+ text\);\n\s*return;$',
        lambda m: indent_block(POSTFX_FALLBACK, m.group(1)), postfx_text, count=1, flags=re.MULTILINE)
    if postfx_edits:
        postfx.write_text(postfx_text, encoding="utf-8")
        edits += 1
    elif "using the basic blit shader" not in postfx_text:
        raise ValueError(f"expected postfx shader error branch in {postfx}")
    eb_light = root / "EB.Rendering/EBLight.cs"
    if "pointLightShader" not in eb_light.read_text(encoding="utf-8-sig"):
        edits += replace_line(eb_light, '_Material = new Material(Shader.Find("EB/Light/PointLight"));', POINT_LIGHT_GUARD)
    for relative in ("Misc.cs", "EB/MemProfiler.cs", "EB.Sparx/BenchmarkManager.cs"):
        edits += replace_count(root / relative, "Profiler.", "UnityEngine.Profiling.Profiler.")
    for path in root.rglob("*.cs"):
        if "mipmap:" in path.read_text(encoding="utf-8-sig"):
            edits += replace_count(path, "mipmap:", "mipChain:")
        if "generateMips" in path.read_text(encoding="utf-8-sig"):
            edits += replace_count(path, "generateMips", "autoGenerateMips")

    enums = root / "EB.Replication/Unity2020NetworkEnums.cs"
    if not enums.exists():
        enums.write_text(
            """namespace EB.Replication
{
    // Unity removed these built-in networking enums after the original client
    // was released. The replication code uses them as its own protocol values.
    public enum NetworkStateSynchronization
    {
        Off = 0,
        ReliableDeltaCompressed = 1,
        Unreliable = 2
    }

    public enum RPCMode
    {
        Server = 0,
        Others = 1,
        OthersBuffered = 2,
        All = 3,
        AllBuffered = 4
    }
}
""",
            encoding="utf-8",
        )
        edits += 1

    # The old project's hidden post-processing shaders are not source assets in
    # the code-only APK. Keep the legacy camera and UI capture paths functional
    # in the Unity 2020 rebuild with a texture-copy pass for their required
    # shader names. These are deliberately small fallbacks; game content still
    # comes from the staged asset bundles.
    shader_root = root.parents[2] / "Assets/Resources/shaders"
    shader_root.mkdir(parents=True, exist_ok=True)
    fallback_pass = '''        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
'''
    fallback_shader = '''Shader "{shader_name}"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
{passes}    }
    Fallback Off
}
'''
    for filename, shader_name in (
        ("EBBlit.shader", "Hidden/EB/Blit"),
        ("EBUIScreenCapture.shader", "Hidden/EB/UIScreenCapture"),
        ("EBNoToneMapBlit.shader", "Hidden/EB/PostFX/NoToneMapBlit"),
        ("EBToneMapBlit.shader", "Hidden/EB/PostFX/ToneMapBlit"),
        ("EBPostFxDepthReplacement.shader", "Hidden/EB/PostFx/DepthReplacement"),
        ("EBPostFxLowResReplacement.shader", "Hidden/EB/PostFx/LowResCompReplacement"),
        ("EBPostFxParticleReplacement.shader", "Hidden/EB/PostFx/LowResParticleAlphaReplacement"),
        ("EBPostFxBloomReplacement.shader", "Hidden/EB/PostFx/BloomLowResParticleComp"),
        ("EBBlur.shader", "Hidden/EB/Blur"),
        ("EBDepthBlur.shader", "Hidden/EB/DepthBlur"),
        ("EBFxaa.shader", "Hidden/EB/PostFX/FXAA"),
    ):
        shader_path = shader_root / filename
        shader_text = fallback_shader.replace("{shader_name}", shader_name).replace(
            "{passes}", fallback_pass * 6
        )
        if not shader_path.exists() or shader_path.read_text(encoding="utf-8") != shader_text:
            shader_path.write_text(shader_text, encoding="utf-8")
            edits += 1
    return edits


# Code below is written for this project. Game sources are matched by method
# signatures and single-line anchors only; no decompiled blocks are stored here.
LEGACY_TOKEN_BODY = """try
{
\treturn new AndroidJavaClass("com.explodingbarrel.Helpers").CallStatic<string>("GetLegacyKabamAccountToken", new object[0]);
}
catch (AndroidJavaException ex)
{
\tDebug.LogWarning("Legacy Kabam account bridge unavailable; skipping token migration: " + ex.Message);
\treturn string.Empty;
}"""

APK_START_GUARD = """bool started = false;
try
{
\tstarted = APK_Check_Start_Salt(jarPath, salt);
}
catch (DllNotFoundException ex)
{
\tUnityEngine.Debug.LogWarning("APK protection library unavailable; skipping HMAC check: " + ex.Message);
}
if (started)"""

CRASH_IMPORT_CATCH = """catch (AndroidJavaException ex)
{
\t_customExceptionHandlerObj = null;
\tDebug.LogWarning("CustomExceptionHandler unavailable; skipping previous-session crash import: " + ex.Message);
}"""

IAP_INIT_CATCH = """catch (AndroidJavaException ex)
{
\t_class = null;
\t_supported = false;
\tDebug.LogWarning("Google Play billing bridge unavailable; in-app purchases are disabled: " + ex.Message);
\tif (_config.OnInitComplete != null) _config.OnInitComplete(false);
}"""

IAP_PROMO_GUARD = """if (_class == null)
{
\tif (_checkPromoCallback != null) _checkPromoCallback("false");
\treturn;
}"""

IAP_ITEMS_GUARD = """if (_class == null)
{
\tOnIAPUnsupported();
\treturn;
}"""

GCM_CATCH = """catch (AndroidJavaException ex)
{
\tDebug.LogWarning("Push registration bridge unavailable; skipping registration: " + ex.Message);
}"""

STORAGE_PERMISSION_GRANT = """if (permissionKeys != null && permissionKeys.Length == 1 && permissionKeys[0] == "write_external_storage")
{
\t// Unity stores extracted bundles under persistentDataPath; this is app-private storage.
\tfnCallback?.Invoke(true, requestCode);
\treturn;
}"""

POSTFX_FALLBACK = """// Older game assets may not contain this generated effect shader.
// Keep rendering through the basic blit shader and skip the effects.
Debug.LogWarning("Could not load postfx shader " + text + "; using the basic blit shader");
_CompositeMaterial = _BlitMaterial;
_PostFXComposite.Clear();
return;"""

POINT_LIGHT_GUARD = """Shader pointLightShader = Shader.Find("EB/Light/PointLight");
if (pointLightShader == null)
{
\t// The old helper shader is absent from the code-only 2.0.2 APK.
\t// Skip this optional light mesh instead of aborting world creation.
\tDebug.LogWarning("EB/Light/PointLight shader unavailable; disabling this legacy light component");
\t_MeshRenderer.enabled = false;
\tenabled = false;
\treturn;
}
_Material = new Material(pointLightShader);"""


def indent_block(block: str, indent: str) -> str:
    return "\n".join(indent + line if line else line for line in block.split("\n"))


def _lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8-sig").split("\n")


def _find(lines: list[str], anchor: str, start: int = 0) -> int:
    for index in range(start, len(lines)):
        if anchor in lines[index]:
            return index
    raise ValueError(f"anchor not found: {anchor!r}")


def _indent_of(line: str) -> str:
    return line[: len(line) - len(line.lstrip())]


def replace_line(path: Path, anchor: str, block: str) -> int:
    """Replace the single line containing anchor with an indented block."""
    lines = _lines(path)
    index = _find(lines, anchor)
    lines[index : index + 1] = indent_block(block, _indent_of(lines[index])).split("\n")
    path.write_text("\n".join(lines), encoding="utf-8")
    return 1


def insert_before_line(path: Path, anchor: str, block: str) -> int:
    lines = _lines(path)
    index = _find(lines, anchor)
    lines[index:index] = indent_block(block, _indent_of(lines[index])).split("\n")
    path.write_text("\n".join(lines), encoding="utf-8")
    return 1


def move_line_before(path: Path, moved: str, anchor: str) -> int:
    lines = _lines(path)
    target = _find(lines, anchor)
    index = _find(lines, moved, target)
    line = lines.pop(index).strip()
    lines.insert(target, _indent_of(lines[target]) + line)
    path.write_text("\n".join(lines), encoding="utf-8")
    return 1


def wrap_in_try(path: Path, start_anchor: str, end_anchor: str, catch_block: str, through_block: bool = False) -> int:
    """Wrap the lines from start_anchor through end_anchor in try/catch.

    With through_block, the span continues to the brace closing the block the
    end anchor sits in, at the start line's indentation.
    """
    lines = _lines(path)
    start = _find(lines, start_anchor)
    end = _find(lines, end_anchor, start)
    indent = _indent_of(lines[start])
    if through_block:
        end = next(i for i in range(end + 1, len(lines)) if lines[i].strip() == "}" and _indent_of(lines[i]) == indent)
    body = ["\t" + line if line else line for line in lines[start : end + 1]]
    wrapped = [indent + "try", indent + "{"] + body + [indent + "}"] + indent_block(catch_block, indent).split("\n")
    lines[start : end + 1] = wrapped
    path.write_text("\n".join(lines), encoding="utf-8")
    return 1


def replace_body(path: Path, signature: str, body: str) -> int:
    """Replace a method body, located by its signature, with body."""
    text = path.read_text(encoding="utf-8-sig")
    start = text.find(signature)
    if start < 0:
        raise ValueError(f"method not found in {path}: {signature}")
    open_brace = text.index("{", start + len(signature))
    depth = 0
    for index in range(open_brace, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                close_brace = index
                break
    line_start = text.rfind("\n", 0, start) + 1
    indent = text[line_start:start]
    new_body = "\n" + indent_block(body, indent + "\t") + "\n" + indent
    path.write_text(text[: open_brace + 1] + new_body + text[close_brace:], encoding="utf-8")
    return 1


def replace_count(path: Path, old: str, new: str) -> int:
    text = path.read_text(encoding="utf-8-sig")
    if old in {"Tuple<", "Types.", "Profiler.", "Cache."}:
        pattern = re.compile(rf"(?<![\w.]){re.escape(old)}")
        text, count = pattern.subn(new, text)
    else:
        count = text.count(old)
        if count:
            text = text.replace(old, new)
    if count == 0 and new not in text:
        raise ValueError(f"expected source text not found in {path}: {old!r}")
    if count:
        path.write_text(text, encoding="utf-8")
    return count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="decompiled Firstpass202 source directory")
    args = parser.parse_args()
    if not args.source.is_dir():
        parser.error(f"source directory does not exist: {args.source}")
    print(f"Unity 2020 firstpass compatibility: {adapt(args.source)} source edit(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
