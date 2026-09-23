// Strip compiler-generated types/methods and rewrite all method bodies for stub compilation.
// Usage: dotnet run --project StripCG.csproj -- <input.dll> <output.dll> <search-dir>
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static bool IsCompilerGeneratedType(string name) => name.StartsWith("<");

    static bool IsCompilerGeneratedMethod(MethodDefinition method)
    {
        if (method.HasOverrides) return false;
        return method.Name.StartsWith("<");
    }

    /// <summary>Build a GenericInstanceType from a type definition using its own generic parameters.</summary>
    static GenericInstanceType MakeSelfGenericInstance(TypeDefinition type)
    {
        var git = new GenericInstanceType(type);
        foreach (var gp in type.GenericParameters)
            git.GenericArguments.Add(gp);
        return git;
    }

    /// <summary>
    /// Substitute generic parameters of a base type definition with the actual
    /// arguments from a GenericInstanceType. Handles nested generics and arrays.
    /// </summary>
    /// <summary>
    /// Does this generic parameter belong to the given generic type?
    ///
    /// Reference equality alone is too strict: the element type of an
    /// interface reference obtained from TypeDefinition.Interfaces is often a
    /// different TypeReference object than the GenericParameter's owner, even
    /// when both describe the same definition. Only type-level parameters are
    /// substituted -- a method-level parameter has the method as its owner and
    /// must not be replaced positionally.
    /// </summary>
    static bool OwnsGenericParameter(GenericParameter gp, TypeReference elementType)
    {
        if (elementType == null) return false;
        // Only type-level parameters may be substituted positionally. A method
        // parameter (!!0) has the method as its owner and must never be replaced
        // by the enclosing type's argument at the same position.
        if (gp.Type != GenericParameterType.Type) return false;
        if (gp.Owner == elementType) return true;

        var ownerType = gp.Owner as TypeReference;
        if (ownerType == null) return false; // owned by a method, not the type
        if (ownerType.FullName == elementType.FullName) return true;

        var a = ownerType.Resolve();
        var b = elementType.Resolve();
        return a != null && b != null && a == b;
    }

    static TypeReference SubstituteGenericParams(TypeReference type, GenericInstanceType git)
    {
        if (type is GenericParameter gp)
        {
            if (gp.Position < git.GenericArguments.Count && OwnsGenericParameter(gp, git.ElementType))
                return git.GenericArguments[gp.Position];
            return type; // foreign generic param, leave as-is
        }
        if (type is GenericInstanceType innerGit)
        {
            var result = new GenericInstanceType(innerGit.ElementType);
            foreach (var arg in innerGit.GenericArguments)
                result.GenericArguments.Add(SubstituteGenericParams(arg, git));
            return result;
        }
        if (type is ArrayType at)
        {
            return new ArrayType(SubstituteGenericParams(at.ElementType, git), at.Rank);
        }
        if (type is ByReferenceType brt)
        {
            return new ByReferenceType(SubstituteGenericParams(brt.ElementType, git));
        }
        return type;
    }

    /// <summary>Emit a default value for a parameter type, avoiding locals when possible.</summary>
    static void EmitDefaultForParam(ILProcessor il, MethodBody body, ModuleDefinition module, TypeReference paramType)
    {
        var fullName = paramType.FullName;

        // Primitives - no import needed
        if (fullName == "System.Int32" || fullName == "System.Int16" || fullName == "System.Byte" ||
            fullName == "System.Boolean" || fullName == "System.Char" || fullName == "System.UInt32" ||
            fullName == "System.UInt16" || fullName == "System.SByte")
        {
            il.Append(il.Create(OpCodes.Ldc_I4_0));
        }
        else if (fullName == "System.Int64" || fullName == "System.UInt64")
        {
            il.Append(il.Create(OpCodes.Ldc_I8, 0L));
        }
        else if (fullName == "System.Single")
        {
            il.Append(il.Create(OpCodes.Ldc_R4, 0f));
        }
        else if (fullName == "System.Double")
        {
            il.Append(il.Create(OpCodes.Ldc_R8, 0.0));
        }
        else if (fullName == "System.IntPtr" || fullName == "System.UIntPtr")
        {
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Conv_I));
        }
        else if (paramType.IsGenericParameter)
        {
            // Generic parameter - ldnull (works for both ref and value type constraints at runtime)
            il.Append(il.Create(OpCodes.Ldnull));
        }
        else if (paramType.IsValueType)
        {
            // Value type needs initobj via local
            try
            {
                var imported = module.ImportReference(paramType);
                var local = new VariableDefinition(imported);
                body.Variables.Add(local);
                il.Append(il.Create(OpCodes.Ldloca, local));
                il.Append(il.Create(OpCodes.Initobj, imported));
                il.Append(il.Create(OpCodes.Ldloc, local));
            }
            catch
            {
                il.Append(il.Create(OpCodes.Ldnull));
            }
        }
        else
        {
            // Reference type
            il.Append(il.Create(OpCodes.Ldnull));
        }
    }

    /// <summary>Build a MethodReference for a ctor on a possibly-generic base type.</summary>
    static MethodReference MakeBaseCtorRef(ModuleDefinition module, TypeReference baseType, MethodDefinition ctorDef)
    {
        if (baseType.IsGenericInstance)
        {
            var git = (GenericInstanceType)baseType;
            var ctorRef = new MethodReference(".ctor", module.TypeSystem.Void, baseType);
            ctorRef.HasThis = true;
            // Substitute generic params in parameter types
            foreach (var p in ctorDef.Parameters)
            {
                var substituted = SubstituteGenericParams(p.ParameterType, git);
                ctorRef.Parameters.Add(new ParameterDefinition(substituted));
            }
            return module.ImportReference(ctorRef);
        }
        return module.ImportReference(ctorDef);
    }

    static void RewriteMethodBodies(ModuleDefinition module)
    {
        int rewritten = 0;
        foreach (var type in module.Types)
            RewriteTypeMethods(module, type, ref rewritten);
        Console.WriteLine($"  Rewrote {rewritten} method bodies");
    }

    static void RewriteTypeMethods(ModuleDefinition module, TypeDefinition type, ref int rewritten)
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            if (method.IsAbstract || method.IsPInvokeImpl || method.IsInternalCall) continue;

            var body = method.Body;
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            body.Instructions.Clear();

            if (method.IsConstructor)
            {
                if (method.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ret));
                }
                else if (type.IsValueType)
                {
                    if (type.HasGenericParameters)
                    {
                        var selfGit = MakeSelfGenericInstance(type);
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Initobj, selfGit));
                    }
                    else
                    {
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Initobj, type));
                    }
                    il.Append(il.Create(OpCodes.Ret));
                }
                else
                {
                    // Class ctor: ldarg.0; push defaults; call base..ctor; ret
                    il.Append(il.Create(OpCodes.Ldarg_0));

                    var baseType = type.BaseType;
                    bool calledBase = false;

                    if (baseType != null)
                    {
                        try
                        {
                            var baseTypeDef = baseType.Resolve();
                            if (baseTypeDef != null)
                            {
                                // Prefer parameterless ctor
                                MethodDefinition chosenCtor = baseTypeDef.Methods
                                    .FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);

                                // Else fewest-params ctor
                                if (chosenCtor == null)
                                {
                                    chosenCtor = baseTypeDef.Methods
                                        .Where(m => m.IsConstructor && !m.IsStatic)
                                        .OrderBy(m => m.Parameters.Count)
                                        .FirstOrDefault();
                                }

                                if (chosenCtor != null)
                                {
                                    var ctorRef = MakeBaseCtorRef(module, baseType, chosenCtor);
                                    // Push defaults for each parameter, substituting generic args
                                    var git = baseType as GenericInstanceType;
                                    foreach (var p in chosenCtor.Parameters)
                                    {
                                        var paramType = git != null
                                            ? SubstituteGenericParams(p.ParameterType, git)
                                            : p.ParameterType;
                                        EmitDefaultForParam(il, body, module, paramType);
                                    }
                                    il.Append(il.Create(OpCodes.Call, ctorRef));
                                    calledBase = true;
                                }
                            }
                        }
                        catch { /* resolve failed */ }
                    }

                    if (!calledBase)
                    {
                        var objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
                        il.Append(il.Create(OpCodes.Call, objectCtor));
                    }

                    il.Append(il.Create(OpCodes.Ret));
                }
            }
            else if (method.ReturnType.FullName == "System.Void" && !HasOutOrRefParams(method))
            {
                il.Append(il.Create(OpCodes.Ret));
            }
            else
            {
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Throw));
            }
            rewritten++;
        }

        foreach (var nested in type.NestedTypes)
            RewriteTypeMethods(module, nested, ref rewritten);
    }

    static bool HasOutOrRefParams(MethodDefinition method)
    {
        return method.Parameters.Any(p => p.IsOut || p.ParameterType.IsByReference);
    }

    /// <summary>
    /// Rebuild the MethodImpl (.override) records that IL2CPP's DummyDll omits.
    ///
    /// In the DummyDll an explicit interface implementation survives only as a
    /// method whose NAME is the dotted interface member, e.g.
    /// "Fabric.IEventListener.Process" or "EB.Dot.IDotGet&lt;T&gt;.Get". Without an
    /// override record, ilspy has no way to know it is an explicit
    /// implementation, so it emits a private mangled method and the type fails
    /// to compile (CS0535/CS0737). Delegating the reconstruction here means every
    /// DLL that goes through this pipeline -- the two game assemblies and any
    /// framework DLL converted to source -- is covered by one rule, and real
    /// explicit implementations are emitted for methods, properties and indexers
    /// alike.
    /// </summary>
    static void RestoreInterfaceOverrides(ModuleDefinition module)
    {
        int restored = 0;
        foreach (var type in module.Types)
            RestoreOverridesInType(module, type, ref restored);
        Console.WriteLine($"  Restored {restored} interface override(s)");
    }

    /// <summary>
    /// Decode IL2CPP's escape sequences in a metadata name: _002E is '.', _003C
    /// is '<' and _003E is '>'. Used only to recognise a dotted interface member;
    /// the decoded form is never written back as an identifier.
    /// </summary>
    static string DecodeIl2CppName(string name)
    {
        if (name.IndexOf("_002", StringComparison.Ordinal) < 0) return name;
        return name.Replace("_002E", ".").Replace("_003C", "<").Replace("_003E", ">");
    }

    static void RestoreOverridesInType(ModuleDefinition module, TypeDefinition type, ref int restored)
    {
        foreach (var method in type.Methods)
        {
            // Some DummyDll assemblies spell the dotted interface member with
            // IL2CPP's escape sequences (Fabric_002EGlobalSwitch_002EIListener_002EOnSwitch)
            // and others with literal dots. Decode only for MATCHING -- the method
            // keeps its original name; the override record is what tells ilspy
            // this is an explicit interface implementation.
            string decoded = DecodeIl2CppName(method.Name);
            int lastDot = decoded.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == decoded.Length - 1) continue;

            string ifaceName = decoded.Substring(0, lastDot);
            string memberName = decoded.Substring(lastDot + 1);

            var ifaceRef = FindInterface(type, ifaceName);
            if (ifaceRef == null) continue;

            var ifaceDef = ifaceRef.Resolve();
            if (ifaceDef == null) continue;

            var ifaceMethod = FindInterfaceMethod(ifaceDef, memberName, method);
            if (ifaceMethod == null) continue;


            var selfRef = MakeInterfaceMethodRef(module, ifaceRef, ifaceMethod, method);

            // A property or indexer explicit implementation only compiles when the
            // implementing type owns a PropertyDefinition for it. DummyDll usually
            // has the accessor methods but no owning property, so ilspy emits a
            // bare get_X()/set_X() method and C# rejects it (CS0683). Create the
            // property here so ilspy renders "T IFoo.Bar { get; set; }".
            if (ifaceMethod.IsGetter || ifaceMethod.IsSetter)
                EnsureExplicitProperty(module, type, ifaceRef, ifaceDef, ifaceMethod, method);

            // Avoid duplicating an override that is already recorded.
            bool exists = method.Overrides.Any(o =>
                o.Name == selfRef.Name && o.DeclaringType.FullName == selfRef.DeclaringType.FullName);
            if (!exists)
            {
                method.Overrides.Add(selfRef);
                restored++;
            }
        }

        foreach (var nested in type.NestedTypes)
            RestoreOverridesInType(module, nested, ref restored);
    }

    /// <summary>
    /// Locate the implemented interface whose full name matches the prefix of a
    /// dotted method name. Generic arguments in that prefix are written as
    /// "&lt;...&gt;" while Cecil reports them as a GenericInstanceType, so compare
    /// only the namespace-qualified element name, then search base types too
    /// (a class may implement the interface transitively).
    /// </summary>
    static TypeReference FindInterface(TypeDefinition type, string ifaceName)
    {
        // Match the FULL instantiation first. Stripping the generic arguments
        // makes every closed form of one open interface compare equal, so a type
        // implementing IDotGet<int>, IDotGet<bool> and IDotGet<string> would
        // bind every member to whichever instantiation happened to come first:
        // the rest then point at signatures the interface does not have (CS0539)
        // or collide on the same target (CS0111).
        string wantFull = NormalizeFullName(ifaceName);
        string wantOpen = StripGenericArgs(ifaceName);

        TypeReference openMatch = null;
        int openMatches = 0;

        for (var t = type; t != null; t = t.BaseType?.Resolve())
        {
            foreach (var impl in t.Interfaces)
            {
                var candidate = impl.InterfaceType;
                if (NormalizeFullName(candidate.FullName) == wantFull)
                    return candidate;

                if (StripGenericArgs(candidate.FullName) == wantOpen ||
                    StripGenericArgs(candidate.Namespace + "." + candidate.Name) == wantOpen)
                {
                    openMatch = candidate;
                    openMatches++;
                }
            }
        }

        // An open-name match is only usable when exactly one interface has that
        // name; otherwise the instantiation is ambiguous and guessing is worse
        // than leaving the member to the compiler's normal resolution.
        return openMatches == 1 ? openMatch : null;
    }

    /// <summary>
    /// Normalise a type name for comparison, keeping generic ARGUMENTS.
    ///
    /// Cecil writes nested types with '/' and generic arity with a backtick
    /// (IDotGet`1&lt;System.Boolean&gt;); a decoded IL2CPP method name writes both
    /// with '.' and no arity (IDotGet&lt;System.Boolean&gt;). Argument names are CLR
    /// names on both sides (System.Boolean).
    /// </summary>
    static string NormalizeFullName(string name)
    {
        name = System.Text.RegularExpressions.Regex.Replace(name, @"`\d+", "");
        return name.Replace('/', '.');
    }

    static string StripGenericArgs(string name)
    {
        int lt = name.IndexOf('<');
        if (lt >= 0) name = name.Substring(0, lt);
        int bt = name.IndexOf('`');
        if (bt >= 0) name = name.Substring(0, bt);
        return name.Replace('/', '.');
    }

    /// <summary>
    /// Find the interface member the dotted method corresponds to. Matches on
    /// name and parameter count/type so overloads and indexers resolve correctly.
    /// </summary>
    static MethodDefinition FindInterfaceMethod(TypeDefinition ifaceDef, string memberName,
                                                MethodDefinition impl)
    {
        // Accessors (get_/set_/add_/remove_) are unique by name within an
        // interface, and the DummyDll strips their parameters altogether, so
        // parameter matching against the interface would never succeed.
        bool accessorName = memberName.StartsWith("get_") || memberName.StartsWith("set_")
                         || memberName.StartsWith("add_") || memberName.StartsWith("remove_");

        MethodDefinition fallback = null;
        foreach (var candidate in ifaceDef.Methods)
        {
            if (candidate.Name != memberName) continue;
            if (accessorName) return candidate;
            if (candidate.Parameters.Count != impl.Parameters.Count) continue;
            if (fallback == null) fallback = candidate;

            bool allMatch = true;
            for (int i = 0; i < candidate.Parameters.Count; i++)
            {
                string a = TypeKey(candidate.Parameters[i].ParameterType);
                string b = TypeKey(impl.Parameters[i].ParameterType);
                if (a != b) { allMatch = false; break; }
            }
            if (allMatch) return candidate;
        }
        return fallback;
    }

    static string TypeKey(TypeReference t)
    {
        if (t is ByReferenceType b) return TypeKey(b.ElementType) + "&";
        if (t is ArrayType a) return TypeKey(a.ElementType) + "[]";
        if (t is GenericParameter gp) return "!" + gp.Name;
        return StripGenericArgs(t.Namespace + "." + t.Name);
    }

    /// <summary>
    /// Build the override target for an interface member.
    ///
    /// Two imports and a retarget, in this order:
    ///  1. Import the OPEN interface method. That brings its return and parameter
    ///     types into this module (including ones declared in another module,
    ///     such as mscorlib's IEnumerator) while leaving the interface's own
    ///     generic parameters untouched as VAR n. Skipping this step is what
    ///     makes MetadataBuilder report "declared in another module".
    ///  2. Import the interface reference with the implementing TYPE as the
    ///     generic context, so an instantiated interface such as IDotGet&lt;!0&gt;
    ///     is valid in this module.
    ///  3. Retarget the imported method onto that host, which is what binds the
    ///     VAR n parameters. The parameter types are deliberately NOT
    ///     hand-substituted or hand-imported.
    /// </summary>
    static MethodReference MakeInterfaceMethodRef(ModuleDefinition module, TypeReference ifaceRef,
                                                  MethodDefinition ifaceMethod, MethodDefinition impl)
    {
        var openImported = module.ImportReference(ifaceMethod);
        var hostImported = module.ImportReference(ifaceRef, impl.DeclaringType);
        return MakeHostInstanceGeneric(openImported, hostImported);
    }

    static MethodReference MakeHostInstanceGeneric(MethodReference self, TypeReference host)
    {
        var r = new MethodReference(self.Name, self.ReturnType, host)
        {
            HasThis = self.HasThis,
            ExplicitThis = self.ExplicitThis,
            CallingConvention = self.CallingConvention,
        };
        foreach (var p in self.Parameters)
            r.Parameters.Add(new ParameterDefinition(p.ParameterType));
        foreach (var g in self.GenericParameters)
            r.GenericParameters.Add(new GenericParameter(g.Name, r));
        return r;
    }

    /// <summary>
    /// Make sure the implementing type owns the explicit PropertyDefinition that
    /// ilspy needs in order to render an explicitly implemented property.
    /// The property's name is the interface name plus the property name, which
    /// is the form ilspy recognises as an explicit implementation.
    /// </summary>
    static void EnsureExplicitProperty(ModuleDefinition module, TypeDefinition type, TypeReference ifaceRef,
                                       TypeDefinition ifaceDef, MethodDefinition ifaceMethod,
                                       MethodDefinition implMethod)
    {
        PropertyDefinition ifaceProp = null;
        foreach (var candidate in ifaceDef.Properties)
        {
            if (candidate.GetMethod == ifaceMethod || candidate.SetMethod == ifaceMethod)
            {
                ifaceProp = candidate;
                break;
            }
        }
        if (ifaceProp == null) return;

        // Normalised, because the DummyDll records these property names without
        // generic arity and with '/' between nested types. Using FullName raw
        // creates a SECOND property next to the existing one (CS0102/CS8646).
        string propName = NormalizeFullName(ifaceRef.FullName) + "." + ifaceProp.Name;

        PropertyDefinition prop = null;
        foreach (var existing in type.Properties)
        {
            if (existing.Name == propName) { prop = existing; break; }
        }
        if (prop == null)
        {
            var valueType = ifaceMethod.IsGetter
                ? ifaceMethod.ReturnType
                : ifaceMethod.Parameters[ifaceMethod.Parameters.Count - 1].ParameterType;

            prop = new PropertyDefinition(propName, PropertyAttributes.None,
                                          ImportMemberType(module, valueType, implMethod));
            type.Properties.Add(prop);
        }

        // Indexers ("Item") must carry their index parameters, or C# sees a plain
        // property and reports the interface indexer as unimplemented. The
        // parameters are usually gone from the interface property too, so fall
        // back to the accessor's own signature (a getter's parameters, or a
        // setter's minus the trailing value parameter).
        if (prop.Parameters.Count == 0)
        {
            var indexTypes = ifaceProp.Parameters.Count > 0
                ? AccessorIndexTypes(ifaceProp.GetMethod ?? ifaceProp.SetMethod, fallback: true)
                : AccessorIndexTypes(ifaceMethod, fallback: false);

            foreach (var indexType in indexTypes)
                prop.Parameters.Add(new ParameterDefinition(
                    ImportMemberType(module, indexType, implMethod)));
        }

        if (ifaceMethod.IsGetter)
        {
            implMethod.SemanticsAttributes |= MethodSemanticsAttributes.Getter;
            if (prop.GetMethod == null || prop.GetMethod == implMethod) prop.GetMethod = implMethod;
        }
        else
        {
            implMethod.SemanticsAttributes |= MethodSemanticsAttributes.Setter;
            if (prop.SetMethod == null || prop.SetMethod == implMethod) prop.SetMethod = implMethod;
        }
        implMethod.IsSpecialName = true;
        implMethod.IsHideBySig = true;
    }

    /// <summary>
    /// Give every indexer property the parameter list its accessors already have.
    ///
    /// The IL2CPP dump loses the parameters of explicit indexers, so the property
    /// looks like a normal property ("object IList.Item" instead of
    /// "object IList.this[int]") and the interface indexer counts as unimplemented.
    /// </summary>
    static void FixIndexerProperties(ModuleDefinition module)
    {
        int fixedCount = 0;
        foreach (var type in module.Types)
            fixedCount += FixIndexerPropertiesInType(type);
        Console.WriteLine($"  Fixed {fixedCount} indexer property parameter list(s)");
    }

    static int FixIndexerPropertiesInType(TypeDefinition type)
    {
        int fixedCount = 0;
        foreach (var prop in type.Properties)
        {
            int lastDot = prop.Name.LastIndexOf('.');
            if (lastDot <= 0) continue;

            string prefix = prop.Name.Substring(0, lastDot);
            string member = prop.Name.Substring(lastDot + 1);

            // The DummyDll leaves these properties without linked accessors, so
            // pair them up by name before anything can read the signature.
            var getter = FindMethodByName(type, prefix + ".get_" + member) ?? prop.GetMethod;
            var setter = FindMethodByName(type, prefix + ".set_" + member) ?? prop.SetMethod;
            if (getter == null && setter == null) continue;

            if (prop.GetMethod == null && getter != null) prop.GetMethod = getter;
            if (prop.SetMethod == null && setter != null) prop.SetMethod = setter;

            int indexCount = getter != null
                ? getter.Parameters.Count
                : setter.Parameters.Count - 1;

            if (indexCount <= 0 || prop.Parameters.Count == indexCount) continue;

            // Indexers must carry their index parameters or C# sees a plain
            // property and reports the interface indexer as unimplemented.
            var source = getter ?? setter;
            prop.Parameters.Clear();
            for (int i = 0; i < indexCount; i++)
            {
                var src = source.Parameters[i];
                var pd = new ParameterDefinition(src.ParameterType);
                if (!string.IsNullOrEmpty(src.Name)) pd.Name = src.Name;
                prop.Parameters.Add(pd);
            }
            fixedCount++;
        }

        foreach (var nested in type.NestedTypes)
            fixedCount += FixIndexerPropertiesInType(nested);
        return fixedCount;
    }

    /// <summary>
    /// The types used to index this accessor's property: everything a getter
    /// takes, or everything a setter takes except its trailing value parameter.
    /// </summary>
    static TypeReference[] AccessorIndexTypes(MethodDefinition accessor, bool fallback)
    {
        if (accessor == null) return new TypeReference[0];
        int count = accessor.IsGetter ? accessor.Parameters.Count
                                      : accessor.Parameters.Count - 1;
        if (count <= 0) return new TypeReference[0];
        var types = new TypeReference[count];
        for (int i = 0; i < count; i++) types[i] = accessor.Parameters[i].ParameterType;
        return types;
    }

    static MethodDefinition FindMethodByName(TypeDefinition type, string name)
    {
        foreach (var m in type.Methods)
            if (m.Name == name) return m;
        return null;
    }

    /// <summary>
    /// Import a member's type when it genuinely comes from another module, and
    /// leave it alone when it carries generic parameters (owned by the interface
    /// definition) or already lives in this module.
    /// </summary>
    static TypeReference ImportMemberType(ModuleDefinition module, TypeReference type, MethodDefinition context)
    {
        if (type == null) return null;
        if (ContainsGenericParameter(type)) return type;
        if (type.Module == module) return type;
        return module.ImportReference(type, context);
    }

    static bool ContainsGenericParameter(TypeReference t)
    {
        if (t is GenericParameter) return true;
        if (t is GenericInstanceType git) return git.GenericArguments.Any(ContainsGenericParameter);
        if (t is ArrayType at) return ContainsGenericParameter(at.ElementType);
        if (t is ByReferenceType b) return ContainsGenericParameter(b.ElementType);
        return false;
    }

    static void StripCGTypes(string inputPath, string outputPath, string searchDir, string bclDir)
    {
        var resolver = new DefaultAssemblyResolver();
        // Unity's real reference assemblies come FIRST. The IL2CPP DummyDll's
        // BCL copies have degraded signatures -- for instance IList.Item there has
        // no index parameter -- and resolving System.* from them makes the
        // reconstructed explicit indexers look parameterless.
        if (!string.IsNullOrEmpty(bclDir) && Directory.Exists(bclDir))
            resolver.AddSearchDirectory(bclDir);
        resolver.AddSearchDirectory(searchDir);
        var readerParams = new ReaderParameters { AssemblyResolver = resolver };

        var asm = AssemblyDefinition.ReadAssembly(inputPath, readerParams);
        int removedTypes = 0;
        int removedMethods = 0;

        foreach (var module in asm.Modules)
        {
            foreach (var type in module.Types.ToList())
                StripFromType(type, ref removedTypes, ref removedMethods);

            var cgTopLevel = module.Types.Where(t => IsCompilerGeneratedType(t.Name)).ToList();
            foreach (var t in cgTopLevel)
            {
                module.Types.Remove(t);
                removedTypes++;
            }

            RewriteMethodBodies(module);
            RestoreInterfaceOverrides(module);
            FixIndexerProperties(module);
        }

        asm.Write(outputPath);
        Console.WriteLine($"Stripped {removedTypes} CG types, {removedMethods} CG methods from {Path.GetFileName(inputPath)}");
    }

    static void StripFromType(TypeDefinition type, ref int removedTypes, ref int removedMethods)
    {
        var cgNested = type.NestedTypes.Where(t => IsCompilerGeneratedType(t.Name)).ToList();
        foreach (var nested in cgNested)
        {
            type.NestedTypes.Remove(nested);
            removedTypes++;
        }

        var cgMethods = type.Methods.Where(m => IsCompilerGeneratedMethod(m)).ToList();
        foreach (var m in cgMethods)
        {
            type.Methods.Remove(m);
            removedMethods++;
        }

        foreach (var nested in type.NestedTypes.ToList())
            StripFromType(nested, ref removedTypes, ref removedMethods);
    }

    static void Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 4)
        {
            Console.Error.WriteLine("Usage: StripCG <input.dll> <output.dll> <search-dir> [bcl-dir]");
            Environment.Exit(1);
        }
        StripCGTypes(args[0], args[1], args[2], args.Length > 3 ? args[3] : null);
    }
}
