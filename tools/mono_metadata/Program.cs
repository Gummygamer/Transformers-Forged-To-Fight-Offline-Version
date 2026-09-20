// Reads ECMA metadata without loading or executing the inspected assembly.
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("Expected one managed PE path");
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();
var model = new Contracts(reader);
Console.WriteLine(JsonSerializer.Serialize(model.Read(pe), new JsonSerializerOptions { WriteIndented = true }));

sealed class Contracts(MetadataReader r) : ISignatureTypeProvider<string, object?>
{
    string S(StringHandle h) => r.GetString(h);
    string B(BlobHandle h) => Convert.ToHexString(r.GetBlobBytes(h));
    string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    string Definition(TypeDefinitionHandle h)
    {
        var t = r.GetTypeDefinition(h);
        return t.IsNested ? Definition(t.GetDeclaringType()) + "+" + S(t.Name)
                          : Qualified(S(t.Namespace), S(t.Name));
    }
    string Reference(TypeReferenceHandle h)
    {
        var t = r.GetTypeReference(h);
        var scope = t.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return Reference((TypeReferenceHandle)scope) + "+" + S(t.Name);
        string prefix = scope.Kind == HandleKind.AssemblyReference
            ? "[" + S(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) + "]" : "";
        return prefix + Qualified(S(t.Namespace), S(t.Name));
    }
    string Type(EntityHandle h) => h.IsNil ? "" : h.Kind switch
    {
        HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)h),
        HandleKind.TypeReference => Reference((TypeReferenceHandle)h),
        HandleKind.TypeSpecification => r.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null),
        _ => throw new BadImageFormatException("Unsupported type handle " + h.Kind)
    };
    string Signature(MethodSignature<string> s) =>
        $"{s.Header.RawValue}:{s.GenericParameterCount}:{s.RequiredParameterCount}:{s.ReturnType}({string.Join(",", s.ParameterTypes)})";
    string Method(EntityHandle h)
    {
        if (h.IsNil) return "";
        if (h.Kind == HandleKind.MethodDefinition)
        {
            var m = r.GetMethodDefinition((MethodDefinitionHandle)h);
            return Definition(m.GetDeclaringType()) + "::" + S(m.Name) + ":" + Signature(m.DecodeSignature(this, null));
        }
        var reference = r.GetMemberReference((MemberReferenceHandle)h);
        return Type(reference.Parent) + "::" + S(reference.Name) + ":" + Signature(reference.DecodeMethodSignature(this, null));
    }
    object[] Attributes(CustomAttributeHandleCollection hs) => hs.Select(h =>
    {
        var a = r.GetCustomAttribute(h);
        return (object)new { constructor = Method(a.Constructor), value = B(a.Value) };
    }).OrderBy(x => JsonSerializer.Serialize(x), StringComparer.Ordinal).ToArray();
    object? Constant(ConstantHandle h)
    {
        if (h.IsNil) return null;
        var c = r.GetConstant(h);
        return new { type = c.TypeCode.ToString(), value = B(c.Value) };
    }
    object[] Generics(GenericParameterHandleCollection hs) => hs.Select(h =>
    {
        var g = r.GetGenericParameter(h);
        return (object)new { name = S(g.Name), index = g.Index, flags = (int)g.Attributes,
            attributes = Attributes(g.GetCustomAttributes()), constraints = g.GetConstraints().Select(c =>
            {
                var constraint = r.GetGenericParameterConstraint(c);
                return new { type = Type(constraint.Type), attributes = Attributes(constraint.GetCustomAttributes()) };
            }).ToArray() };
    }).ToArray();
    public object Read(PEReader pe)
    {
        var a = r.GetAssemblyDefinition();
        var module = r.GetModuleDefinition();
        var types = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var h in r.TypeDefinitions)
        {
            var t = r.GetTypeDefinition(h);
            var layout = t.GetLayout();
            var fields = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var fh in t.GetFields())
            {
                var f = r.GetFieldDefinition(fh);
                fields.Add(S(f.Name), new { signature = f.DecodeSignature(this, null), flags = (int)f.Attributes,
                    offset = f.GetOffset(), has_rva = f.GetRelativeVirtualAddress() != 0,
                    marshal = B(f.GetMarshallingDescriptor()), constant = Constant(f.GetDefaultValue()),
                    attributes = Attributes(f.GetCustomAttributes()) });
            }
            var methods = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var mh in t.GetMethods())
            {
                var m = r.GetMethodDefinition(mh);
                var import = m.GetImport();
                methods.Add(S(m.Name) + ":" + Signature(m.DecodeSignature(this, null)), new {
                    flags = (int)m.Attributes, impl_flags = (int)m.ImplAttributes,
                    attributes = Attributes(m.GetCustomAttributes()), generics = Generics(m.GetGenericParameters()),
                    import = import.Module.IsNil ? null : new { name = S(import.Name), flags = (int)import.Attributes,
                        module = S(r.GetModuleReference(import.Module).Name) },
                    parameters = m.GetParameters().Select(ph => {
                        var p = r.GetParameter(ph);
                        return new { name = S(p.Name), sequence = p.SequenceNumber, flags = (int)p.Attributes,
                            constant = Constant(p.GetDefaultValue()), marshal = B(p.GetMarshallingDescriptor()),
                            attributes = Attributes(p.GetCustomAttributes()) };
                    }).ToArray() });
            }
            var properties = t.GetProperties().Select(ph => {
                var p = r.GetPropertyDefinition(ph);
                var access = p.GetAccessors();
                return new { name = S(p.Name), signature = Signature(p.DecodeSignature(this, null)), flags = (int)p.Attributes,
                    getter = Method(access.Getter), setter = Method(access.Setter), others = access.Others.Select(x => Method(x)).ToArray(),
                    constant = Constant(p.GetDefaultValue()), attributes = Attributes(p.GetCustomAttributes()) };
            }).OrderBy(x => x.name, StringComparer.Ordinal).ThenBy(x => x.signature, StringComparer.Ordinal).ToArray();
            var events = t.GetEvents().Select(eh => {
                var e = r.GetEventDefinition(eh);
                var access = e.GetAccessors();
                return new { name = S(e.Name), type = Type(e.Type), flags = (int)e.Attributes,
                    adder = Method(access.Adder), remover = Method(access.Remover), raiser = Method(access.Raiser),
                    others = access.Others.Select(x => Method(x)).ToArray(), attributes = Attributes(e.GetCustomAttributes()) };
            }).OrderBy(x => x.name, StringComparer.Ordinal).ToArray();
            types.Add(Definition(h), new { flags = (int)t.Attributes, base_type = Type(t.BaseType),
                layout = new { size = layout.Size, packing = layout.PackingSize },
                generics = Generics(t.GetGenericParameters()), attributes = Attributes(t.GetCustomAttributes()),
                interfaces = t.GetInterfaceImplementations().Select(ih => {
                    var i = r.GetInterfaceImplementation(ih);
                    return new { type = Type(i.Interface), attributes = Attributes(i.GetCustomAttributes()) };
                }).OrderBy(x => x.type, StringComparer.Ordinal).ToArray(),
                method_impls = t.GetMethodImplementations().Select(ih => {
                    var i = r.GetMethodImplementation(ih);
                    return new { body = Method(i.MethodBody), declaration = Method(i.MethodDeclaration) };
                }).OrderBy(x => x.declaration, StringComparer.Ordinal).ToArray(),
                field_order = t.GetFields().Select(f => S(r.GetFieldDefinition(f).Name)).ToArray(), fields, methods, properties, events });
        }
        return new { schema = 1, evidence = "PE metadata; inspected assembly is never loaded",
            identity = new { name = S(a.Name), version = a.Version.ToString(), culture = S(a.Culture),
                public_key = B(a.PublicKey), flags = (int)a.Flags, hash_algorithm = a.HashAlgorithm.ToString() },
            metadata_version = r.MetadataVersion, machine = pe.PEHeaders.CoffHeader.Machine.ToString(),
            cor_flags = pe.PEHeaders.CorHeader!.Flags.ToString(), module_name = S(module.Name),
            assembly_attributes = Attributes(a.GetCustomAttributes()), module_attributes = Attributes(module.GetCustomAttributes()),
            references = r.AssemblyReferences.Select(h => {
                var x = r.GetAssemblyReference(h);
                return new { name = S(x.Name), version = x.Version.ToString(), culture = S(x.Culture),
                    public_key_or_token = B(x.PublicKeyOrToken), flags = (int)x.Flags, hash = B(x.HashValue) };
            }).OrderBy(x => x.name, StringComparer.Ordinal).ToArray(),
            resources = r.ManifestResources.Select(h => {
                var x = r.GetManifestResource(h);
                return new { name = S(x.Name), flags = (int)x.Attributes, embedded = x.Implementation.IsNil };
            }).OrderBy(x => x.name, StringComparer.Ordinal).ToArray(), types,
            exclusions = new[] { "method bodies and behavioral equivalence", "resource contents and external resource identity",
                "FieldRVA data contents", "declarative security", "exported type forwarders", "Unity asset type trees and native binding resolution",
                "MVID, metadata tokens, PE timestamps and physical file offsets" } };
    }
    public string GetArrayType(string e, ArrayShape s) => $"{e}[rank={s.Rank};sizes={string.Join(",", s.Sizes)};lower={string.Join(",", s.LowerBounds)}]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fn:" + Signature(s);
    public string GetGenericInstantiation(string t, ImmutableArray<string> a) => t + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string t, bool required) => $"{t} mod{(required ? "req" : "opt")}({m})";
    public string GetPinnedType(string e) => e + " pinned";
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle h, byte k) => $"{k}:{Definition(h)}";
    public string GetTypeFromReference(MetadataReader _, TypeReferenceHandle h, byte k) => $"{k}:{Reference(h)}";
    public string GetTypeFromSpecification(MetadataReader _, object? c, TypeSpecificationHandle h, byte k) => Type(h);
}
