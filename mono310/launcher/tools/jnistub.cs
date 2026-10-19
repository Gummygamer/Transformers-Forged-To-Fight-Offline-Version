#:package Mono.Cecil@0.11.6
// Newly authored. Usage: dotnet run jnistub.cs -- <Managed dir> [--list]
// The 3.1.0 code was compiled for Android, so its Android SDK glue (AndroidJavaClass/AndroidJavaObject/AndroidJNI) runs
// unconditionally and throws on a PC player: EB.Version.GetNativeOSVersion aborts Setup.Awake and the game stays black.
// For every game assembly in the player's Managed folder, replace the body of each non-constructor method that touches
// UnityEngine.AndroidJava*/AndroidJNI* or calls an Android-only native library with a default return (a few start-up
// getters return sane values instead).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

string dir = args[0];
bool listOnly = args.Contains("--list");

// Return values that keep game start-up code on sane paths instead of null/0.
var overrides = new Dictionary<string, object>
{
    ["EB.Version::GetCountryCode"] = "US",
    ["EB.Version::GetLanguageCode"] = "en",
    ["EB.Version::GetPlatformVersion"] = "9",
    ["EB.Version::GetSDKVersion"] = 28,
    ["EB.Version::GetNativeOSVersion"] = 28,
    ["EB.Version::GetBuildNumber"] = "822754",
    ["APKSignature::get_IsValid"] = true,       // anti-tamper checks pass on a PC build
    ["JarExtractor::get_IsValid"] = true,
    ["EB.DriveInfo::GetAvailableSpaceInBytes"] = 100L << 30,   // 0 makes the ODR download refuse to unpack the WAD
};

static bool IsJni(TypeReference t)
{
    while (t is TypeSpecification s) t = s.ElementType;
    return t != null && t.Namespace == "UnityEngine" && (t.Name.StartsWith("AndroidJava") || t.Name.StartsWith("AndroidJNI"));
}

// Native libraries that exist only in the Android APK; methods calling into them are stubbed like the JNI glue.
var androidNative = new HashSet<string> { "android-signal", "apkprotect", "gpg" };

bool CallsAndroidNative(MethodReference r)
{
    MethodDefinition d;
    try { d = r.Resolve(); } catch (AssemblyResolutionException) { return false; }
    return d != null && d.IsPInvokeImpl && d.PInvokeInfo != null && androidNative.Contains(d.PInvokeInfo.Module.Name);
}

bool TouchesJni(MethodDefinition m) => m.HasBody && m.Body.Instructions.Any(i => i.Operand switch
{
    MethodReference r => IsJni(r.DeclaringType) || IsJni(r.ReturnType) || CallsAndroidNative(r),
    FieldReference f => IsJni(f.DeclaringType) || IsJni(f.FieldType),
    TypeReference t => IsJni(t),
    _ => false,
});

static MethodDefinition EmptyCollectionCtor(TypeReference t)
{
    if (t.IsGenericInstance || t.IsGenericParameter || !t.Namespace.StartsWith("System.Collections")) return null;
    var d = t.Resolve();
    return d == null || d.IsAbstract || d.IsInterface ? null
        : d.Methods.FirstOrDefault(c => c.IsConstructor && !c.IsStatic && c.IsPublic && c.Parameters.Count == 0);
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
    types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));

void Stub(MethodDefinition m, ModuleDefinition module)
{
    var body = m.Body;
    body.Instructions.Clear(); body.ExceptionHandlers.Clear(); body.Variables.Clear();
    var il = body.GetILProcessor();
    var ret = m.ReturnType;
    overrides.TryGetValue(m.DeclaringType.FullName + "::" + m.Name, out var v);
    foreach (var p in m.Parameters.Where(p => p.IsOut))    // out parameters get their default value
    {
        var et = ((ByReferenceType)p.ParameterType).ElementType;
        il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Initobj, et);
    }
    if (ret.MetadataType == MetadataType.Void) { }
    else if (v is string s) il.Emit(OpCodes.Ldstr, s);
    else if (v is int n) il.Emit(OpCodes.Ldc_I4, n);
    else if (v is bool b) il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
    else if (v is long l && ret.MetadataType == MetadataType.ValueType && ret.FullName == "System.Decimal")
    {   // new decimal(long), from the player's own mscorlib
        var ctor = ret.Resolve().Methods.First(c => c.IsConstructor && c.Parameters.Count == 1 && c.Parameters[0].ParameterType.MetadataType == MetadataType.Int64);
        il.Emit(OpCodes.Ldc_I8, l); il.Emit(OpCodes.Newobj, module.ImportReference(ctor));
    }
    else if (v is long l64) il.Emit(OpCodes.Ldc_I8, l64);
    else if (ret.MetadataType == MetadataType.String) il.Emit(OpCodes.Ldstr, "");
    else if (EmptyCollectionCtor(ret) is MethodDefinition ctor) il.Emit(OpCodes.Newobj, module.ImportReference(ctor));   // e.g. GetDeviceInfo: empty Hashtable
    else if (!ret.IsValueType && !ret.IsGenericParameter) il.Emit(OpCodes.Ldnull);
    else
    {
        var local = new VariableDefinition(ret);
        body.Variables.Add(local); body.InitLocals = true;
        il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Initobj, ret); il.Emit(OpCodes.Ldloc, local);
    }
    il.Emit(OpCodes.Ret);
}

int total = 0;
foreach (var path in Directory.GetFiles(dir, "*.dll"))
{
    string name = Path.GetFileName(path);
    if (name.StartsWith("UnityEngine") || name.StartsWith("Unity.") || name.StartsWith("System") || name.StartsWith("Mono.")
        || name == "mscorlib.dll" || name == "netstandard.dll") continue;
    // A resolver per assembly, disposed before writing: it keeps the referenced DLLs (also rewritten here) open.
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(dir);
    var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(path)), new ReaderParameters { AssemblyResolver = resolver });
    var targets = AllTypes(module.Types).SelectMany(t => t.Methods)
        .Where(m => !m.IsConstructor && !m.IsAbstract && TouchesJni(m)).ToList();
    if (targets.Count == 0) continue;
    foreach (var m in targets)
    {
        if (listOnly) Console.WriteLine(name + ": " + m.FullName);
        else Stub(m, module);
    }
    if (!listOnly)
    {
        var ms = new MemoryStream();
        module.Write(ms);
        resolver.Dispose();
        File.WriteAllBytes(path, ms.ToArray());
    }
    Console.WriteLine($"{name}: {targets.Count} methods stubbed{(listOnly ? " (list only)" : "")}");
    total += targets.Count;
}
Console.WriteLine("jnistub: " + total + " methods");
