using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

// Dumps per-type field and base-type metadata from an assembly so the 2.0.2 ->
// 9.2 port can be audited against real metadata instead of regexes over C#.
//
// A whole-file regex cannot tell a serialized instance field from a static or
// [NonSerialized] one, cannot see which type DECLARED it (nested vs outer), cannot
// compare field types, and cannot compare base types -- and it matches word
// occurrences that are not declarations at all. This tool answers those questions
// from the assembly itself.
//
// Usage: ContractDump <assembly.dll> <out.json>
internal static class ContractDump
{
    private static readonly HashSet<string> Ignored = new HashSet<string>
    {
        "<Module>", "<PrivateImplementationDetails>",
    };

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: ContractDump <assembly.dll> <out.json>");
            return 2;
        }

        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var sb = new StringBuilder();
        var types = new List<TypeDefinition>();
        foreach (var t in asm.MainModule.Types)
        {
            Collect(t, types);
        }

        sb.Append("{\n");
        sb.Append($"  \"assembly\": {Quote(Path.GetFileName(args[0]))},\n");
        sb.Append("  \"types\": {\n");
        var first = true;
        foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append($"    {Quote(t.FullName)}: {{\n");
            sb.Append($"      \"base\": {Quote(t.BaseType?.FullName ?? "")},\n");
            sb.Append($"      \"kind\": {Quote(t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")},\n");
            sb.Append($"      \"declaring\": {Quote(t.DeclaringType?.FullName ?? "")},\n");
            // Every field is recorded, including compiler-generated ones. Backing
            // fields can be intentionally serialized via [field: SerializeField],
            // so discarding them here would drop real contract data; the decision
            // is made explicitly and visibly downstream instead.
            sb.Append("      \"fields\": [");
            var fields = t.Fields.ToList();
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (i > 0) sb.Append(",");
                sb.Append("\n        {");
                sb.Append($"\"name\": {Quote(f.Name)}, ");
                sb.Append($"\"type\": {Quote(f.FieldType.FullName)}, ");
                sb.Append($"\"static\": {(f.IsStatic ? "true" : "false")}, ");
                sb.Append($"\"readonly\": {(f.IsInitOnly ? "true" : "false")}, ");
                sb.Append($"\"const\": {(f.HasConstant ? "true" : "false")}, ");
                sb.Append($"\"public\": {(f.IsPublic ? "true" : "false")}, ");
                sb.Append($"\"serializefield\": {(HasAttr(f, "SerializeField") ? "true" : "false")}, ");
                // Cecil exposes [NonSerialized] as the NotSerialized field flag; it
                // is not reliably a CustomAttribute, and the CLR attribute type is
                // NonSerializedAttribute. Reading the flag is the correct source.
                sb.Append($"\"nonserialized\": {(f.IsNotSerialized ? "true" : "false")}, ");
                sb.Append($"\"hideininspector\": {(HasAttr(f, "HideInInspector") ? "true" : "false")}, ");
                sb.Append($"\"compilergenerated\": {(IsCompilerGenerated(f) ? "true" : "false")}, ");
                sb.Append($"\"formerlyserializedas\": {Quote(AttrArg(f, "FormerlySerializedAs") ?? "")}, ");
                // Enum members are const fields; the constant VALUE matters because
                // enums are serialized as integers, so a renamed member is not
                // interchangeable with a reordered one.
                // Always JSON-quoted: constants can be strings/chars, and an unquoted
                // string constant produces invalid JSON.
                sb.Append($"\"value\": {Quote(f.HasConstant && f.Constant != null ? Convert.ToString(f.Constant, System.Globalization.CultureInfo.InvariantCulture) : "")}");
                sb.Append("}");
            }
            sb.Append("\n      ]\n");
            sb.Append("    }");
        }
        sb.Append("\n  }\n}\n");

        File.WriteAllText(args[1], sb.ToString());
        Console.WriteLine($"{Path.GetFileName(args[0])}: {types.Count} types -> {args[1]}");
        return 0;
    }

    private static void Collect(TypeDefinition t, List<TypeDefinition> into)
    {
        if (Ignored.Contains(t.Name)) return;
        into.Add(t);
        foreach (var n in t.NestedTypes) Collect(n, into);
    }

    private static bool HasAttr(FieldDefinition f, string name) =>
        f.CustomAttributes.Any(a => a.AttributeType.Name == name ||
                                    a.AttributeType.FullName.EndsWith("." + name, StringComparison.Ordinal));

    private static string AttrArg(FieldDefinition f, string name)
    {
        var attr = f.CustomAttributes.FirstOrDefault(
            a => a.AttributeType.Name == name ||
                 a.AttributeType.FullName.EndsWith("." + name, StringComparison.Ordinal));
        if (attr == null || attr.ConstructorArguments.Count == 0) return null;
        return attr.ConstructorArguments[0].Value as string;
    }

    // Auto-property backing fields (<Name>k__BackingField) and other compiler
    // artifacts are not part of the serialization contract.
    private static bool IsCompilerGenerated(FieldDefinition f) =>
        f.Name.Contains("<") || f.Name.Contains(">") ||
        f.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}