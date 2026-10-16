// ApiGap: lists the external references of a Unity 5.x managed assembly that do not resolve against the
// assemblies of a newer Unity player. Newly authored tool; reads only the operator's own DLLs at run time.
// Usage: mono ApiGap.exe <assembly.dll> <search dir> [<search dir> ...]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

static class ApiGap
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ApiGap <assembly> <dir>..."); return 2; }
        var resolver = new DefaultAssemblyResolver();
        foreach (var d in args.Skip(1)) resolver.AddSearchDirectory(d);
        var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
        var missingTypes = new SortedDictionary<string, int>();
        var missingMembers = new SortedDictionary<string, int>();
        int checkedMembers = 0;

        foreach (var tr in module.GetTypeReferences())
        {
            if (tr.Scope is ModuleReference || tr.DeclaringType != null) continue;
            TypeDefinition td = null;
            try { td = tr.Resolve(); } catch (Exception) { }
            if (td == null) Bump(missingTypes, tr.FullName + " [" + tr.Scope.Name + "]");
        }
        foreach (var mr in module.GetMemberReferences())
        {
            var decl = mr.DeclaringType;
            if (decl == null || decl is GenericParameter || decl is ArrayType) continue;
            checkedMembers++;
            TypeDefinition td = null;
            try { td = decl.GetElementType().Resolve(); } catch (Exception) { }
            if (td == null) { Bump(missingMembers, "type?" + decl.FullName + "::" + mr.Name); continue; }
            if (!HasMember(td, mr)) Bump(missingMembers, decl.FullName + "::" + mr.Name + " " + Sig(mr));
        }
        Console.WriteLine("module " + module.Name + ": member refs checked " + checkedMembers);
        Console.WriteLine("unresolved types (" + missingTypes.Count + ")");
        foreach (var kv in missingTypes) Console.WriteLine("  T " + kv.Key + " x" + kv.Value);
        Console.WriteLine("unresolved members (" + missingMembers.Count + ")");
        foreach (var kv in missingMembers) Console.WriteLine("  M " + kv.Key + " x" + kv.Value);
        return 0;
    }

    static void Bump(SortedDictionary<string, int> d, string k) { int n; d.TryGetValue(k, out n); d[k] = n + 1; }

    static string Sig(MemberReference mr)
    {
        var m = mr as MethodReference;
        if (m != null) return "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        var f = mr as FieldReference;
        return f != null ? f.FieldType.FullName : "";
    }

    static bool HasMember(TypeDefinition td, MemberReference mr)
    {
        for (var t = td; t != null; )
        {
            var m = mr as MethodReference;
            if (m != null)
            {
                foreach (var cand in t.Methods)
                    if (cand.Name == m.Name && cand.Parameters.Count == m.Parameters.Count
                        && SameParams(cand, m)) return true;
            }
            else
            {
                if (mr is FieldReference) { if (t.Fields.Any(f => f.Name == mr.Name)) return true; }
            }
            // interface / base walk
            if (m != null) foreach (var it in t.Interfaces) { TypeDefinition itd = null; try { itd = it.InterfaceType.Resolve(); } catch (Exception) { } if (itd != null && HasMember(itd, mr)) return true; }
            TypeReference bt = t.BaseType;
            if (bt == null) break;
            try { t = bt.Resolve(); } catch (Exception) { t = null; }
        }
        return false;
    }

    static bool SameParams(MethodDefinition cand, MethodReference m)
    {
        for (int i = 0; i < cand.Parameters.Count; i++)
        {
            var a = cand.Parameters[i].ParameterType; var b = m.Parameters[i].ParameterType;
            if (a.GetElementType() is GenericParameter || b.GetElementType() is GenericParameter) continue;
            if (a.GetElementType().IsGenericInstance || b.GetElementType().IsGenericInstance) { if (a.GetElementType().Name != b.GetElementType().Name) return false; continue; }
            if (a.IsByReference != b.IsByReference) return false;
            if (StripName(a) != StripName(b)) return false;
        }
        return true;
    }

    static string StripName(TypeReference t) { return t.GetElementType().FullName + (t.IsArray ? "[]" : ""); }
}
