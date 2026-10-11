
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

class RepairRecoveredConstructor {
    static string NativeModuleFor(string assemblyName, TypeDefinition type) {
        if (assemblyName == "Assembly-CSharp-firstpass.dll") {
            if (type.FullName == "APKSignature") return "apkprotect";
            if (type.FullName == "EB.BugReport") return "android-signal";
            if (type.FullName == "EB.ENet.Plugin") return "enetlib";
            if (type.FullName.StartsWith("GooglePlayGames.Native.", StringComparison.Ordinal)) return "gpg";
        }
        if (assemblyName == "Firebase.App.dll" && type.FullName.StartsWith("Firebase.AppUtilPINVOKE", StringComparison.Ordinal))
            return "FirebaseCppApp-5.6.1";
        if (assemblyName == "Firebase.Messaging.dll" && type.FullName.StartsWith("Firebase.Messaging.FirebaseMessagingPINVOKE", StringComparison.Ordinal))
            return "FirebaseCppMessaging";
        if (assemblyName == "Firebase.Platform.dll" && type.FullName == "Firebase.Unity.InstallRootCerts")
            return "FirebaseCppApp-5.6.1";
        if (assemblyName == "Kabam.Krash.Native.dll" && type.FullName == "EB.Krash.NativeInterface")
            return "krash";
        return null;
    }

    static int RepairMissingPInvokeMetadata(AssemblyDefinition assembly, TypeDefinition type,
        string assemblyName) {
        MethodDefinition[] imports = type.Methods.Where(method => method.IsPInvokeImpl).ToArray();
        if (imports.Length == 0) return 0;
        string moduleName = NativeModuleFor(assemblyName, type);
        if (moduleName == null)
            throw new InvalidDataException("no evidence-based native module mapping for " +
                assemblyName + ":" + type.FullName);

        // The module/entry-point pairs are linkage identifiers corroborated by
        // local 9.2 native-library names, an earlier recovered firstpass import
        // table, and the matching plugin API types. Restore declarations only.
        ModuleReference nativeModule = assembly.MainModule.ModuleReferences
            .SingleOrDefault(reference => reference.Name == moduleName);
        int changes = 0;
        foreach (MethodDefinition method in imports) {
            if (!method.IsStatic)
                throw new InvalidDataException("unexpected instance P/Invoke declaration: " + method.FullName);
            if (nativeModule == null) {
                nativeModule = new ModuleReference(moduleName);
                assembly.MainModule.ModuleReferences.Add(nativeModule);
            }
            if (method.PInvokeInfo == null) {
                method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvWinapi,
                    method.Name, nativeModule);
                changes++;
            } else if (method.PInvokeInfo.Module == null || method.PInvokeInfo.Module.Name != moduleName ||
                method.PInvokeInfo.EntryPoint != method.Name ||
                method.PInvokeInfo.Attributes != PInvokeAttributes.CallConvWinapi) {
                throw new InvalidDataException("unexpected native mapping for " + method.FullName);
            }
        }
        return changes;
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots) {
        foreach (TypeDefinition type in roots) {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    static int RepairPackedColorConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.Color")
            return 0;

        ModuleDefinition module = type.Module;
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        TypeDefinition vector4 = module.GetType("EB.Math.Vector4");
        FieldDefinition packed = type.Fields.SingleOrDefault(field => field.Name == "packed");
        if (vector3 == null || vector4 == null || packed == null || packed.IsStatic ||
            packed.FieldType.MetadataType != MetadataType.UInt32)
            throw new InvalidDataException("unexpected EB.Math.Color packed-field metadata");

        int repaired = 0;
        foreach (TypeDefinition vector in new[] { vector3, vector4 }) {
            FieldDefinition[] components = (vector == vector3
                ? new[] { "x", "y", "z" }
                : new[] { "x", "y", "z", "w" })
                .Select(name => vector.Fields.SingleOrDefault(field => field.Name == name)).ToArray();
            MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
                !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
                method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == vector.FullName);
            if (components.Length != (vector == vector3 ? 3 : 4) ||
                components.Any(field => field == null || field.IsStatic ||
                    field.FieldType.MetadataType != MetadataType.Single) || constructor == null)
                throw new InvalidDataException("unexpected EB.Math.Color vector constructor metadata: " + vector.FullName);

            // Native 9.2 at 0x1553240 / 0x1553348 clamps channels to [0,1],
            // scales by 255, truncates to bytes, then packs RGBA little-endian.
            // Cpp2IL's emitted bodies compare Vector3/Vector4 values directly
            // and contain invalid object locals; rebuild only these two overloads.
            constructor.Body.ExceptionHandlers.Clear();
            constructor.Body.Variables.Clear();
            constructor.Body.Instructions.Clear();
            constructor.Body.InitLocals = true;
            ILProcessor il = constructor.Body.GetILProcessor();
            VariableDefinition[] bytes = Enumerable.Range(0, components.Length)
                .Select(_ => new VariableDefinition(module.TypeSystem.Int32)).ToArray();
            VariableDefinition[] values = Enumerable.Range(0, components.Length)
                .Select(_ => new VariableDefinition(module.TypeSystem.Single)).ToArray();
            foreach (VariableDefinition local in values.Concat(bytes))
                constructor.Body.Variables.Add(local);

            for (int index = 0; index < components.Length; index++) {
                Instruction lower = Instruction.Create(OpCodes.Nop);
                Instruction upper = Instruction.Create(OpCodes.Nop);
                Instruction done = Instruction.Create(OpCodes.Nop);
                il.Append(Instruction.Create(OpCodes.Ldarg_1));
                il.Append(Instruction.Create(OpCodes.Ldfld, components[index]));
                il.Append(Instruction.Create(OpCodes.Stloc, values[index]));
                il.Append(Instruction.Create(OpCodes.Ldloc, values[index]));
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 0.0f));
                il.Append(Instruction.Create(OpCodes.Blt_S, lower));
                il.Append(Instruction.Create(OpCodes.Ldloc, values[index]));
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f));
                il.Append(Instruction.Create(OpCodes.Bgt_S, upper));
                il.Append(Instruction.Create(OpCodes.Br_S, done));
                il.Append(lower);
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 0.0f));
                il.Append(Instruction.Create(OpCodes.Stloc, values[index]));
                il.Append(Instruction.Create(OpCodes.Br_S, done));
                il.Append(upper);
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f));
                il.Append(Instruction.Create(OpCodes.Stloc, values[index]));
                il.Append(done);
                il.Append(Instruction.Create(OpCodes.Ldloc, values[index]));
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 255.0f));
                il.Append(Instruction.Create(OpCodes.Mul));
                il.Append(Instruction.Create(OpCodes.Conv_I4));
                il.Append(Instruction.Create(OpCodes.Stloc, bytes[index]));
            }

            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, bytes[0]));
            for (int index = 1; index < bytes.Length; index++) {
                il.Append(Instruction.Create(OpCodes.Ldloc, bytes[index]));
                il.Append(Instruction.Create(OpCodes.Ldc_I4, index * 8));
                il.Append(Instruction.Create(OpCodes.Shl));
                il.Append(Instruction.Create(OpCodes.Or));
            }
            if (bytes.Length == 3) {
                il.Append(Instruction.Create(OpCodes.Ldc_I4, unchecked((int)0xff000000)));
                il.Append(Instruction.Create(OpCodes.Or));
            }
            il.Append(Instruction.Create(OpCodes.Conv_U4));
            il.Append(Instruction.Create(OpCodes.Stfld, packed));
            il.Append(Instruction.Create(OpCodes.Ret));
            constructor.Body.MaxStackSize = 3;
            repaired++;
        }
        return repaired;
    }

    static int RepairPackedColorEquality(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.Color")
            return 0;

        FieldDefinition packed = type.Fields.SingleOrDefault(field => field.Name == "packed");
        MethodDefinition typedEquals = type.Methods.SingleOrDefault(method => method.Name == "Equals" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == type.FullName);
        MethodDefinition equality = type.Methods.SingleOrDefault(method => method.Name == "op_Equality" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == type.FullName));
        MethodDefinition inequality = type.Methods.SingleOrDefault(method => method.Name == "op_Inequality" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == type.FullName));
        if (packed == null || packed.IsStatic || packed.FieldType.MetadataType != MetadataType.UInt32 ||
            typedEquals == null || !typedEquals.HasBody || equality == null || !equality.HasBody ||
            inequality == null || !inequality.HasBody)
            throw new InvalidDataException("unexpected EB.Math.Color equality metadata");

        typedEquals.Body.ExceptionHandlers.Clear();
        typedEquals.Body.Variables.Clear();
        typedEquals.Body.Instructions.Clear();
        typedEquals.Body.InitLocals = true;
        ILProcessor typedIl = typedEquals.Body.GetILProcessor();
        typedIl.Append(typedIl.Create(OpCodes.Ldarg_0));
        typedIl.Append(typedIl.Create(OpCodes.Ldfld, packed));
        typedIl.Append(typedIl.Create(OpCodes.Ldarga_S, typedEquals.Parameters[0]));
        typedIl.Append(typedIl.Create(OpCodes.Ldfld, packed));
        typedIl.Append(typedIl.Create(OpCodes.Ceq));
        typedIl.Append(typedIl.Create(OpCodes.Ret));

        equality.Body.ExceptionHandlers.Clear();
        equality.Body.Variables.Clear();
        equality.Body.Instructions.Clear();
        equality.Body.InitLocals = true;
        ILProcessor il = equality.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarga_S, equality.Parameters[0]));
        il.Append(il.Create(OpCodes.Ldfld, packed));
        il.Append(il.Create(OpCodes.Ldarga_S, equality.Parameters[1]));
        il.Append(il.Create(OpCodes.Ldfld, packed));
        il.Append(il.Create(OpCodes.Ceq));
        il.Append(il.Create(OpCodes.Ret));

        inequality.Body.ExceptionHandlers.Clear();
        inequality.Body.Variables.Clear();
        inequality.Body.Instructions.Clear();
        inequality.Body.InitLocals = true;
        ILProcessor inequalityIl = inequality.Body.GetILProcessor();
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ldarga_S, inequality.Parameters[0]));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ldfld, packed));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ldarga_S, inequality.Parameters[1]));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ldfld, packed));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ceq));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ldc_I4_0));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ceq));
        inequalityIl.Append(inequalityIl.Create(OpCodes.Ret));
        return 3;
    }

    static void AppendPackedColorChannelLerp(ILProcessor il, VariableDefinition first,
        VariableDefinition second, ParameterDefinition amount, MethodReference channelLerp,
        VariableDefinition result, int mask, int shift) {
        il.Append(Instruction.Create(OpCodes.Ldloc, first));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, mask));
        il.Append(Instruction.Create(OpCodes.And));
        if (shift != 0) {
            il.Append(Instruction.Create(OpCodes.Ldc_I4, shift));
            il.Append(Instruction.Create(OpCodes.Shr_Un));
        }
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Ldloc, second));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, mask));
        il.Append(Instruction.Create(OpCodes.And));
        if (shift != 0) {
            il.Append(Instruction.Create(OpCodes.Ldc_I4, shift));
            il.Append(Instruction.Create(OpCodes.Shr_Un));
        }
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Ldarg, amount));
        il.Append(Instruction.Create(OpCodes.Call, channelLerp));
        il.Append(Instruction.Create(OpCodes.Stloc, result));
    }

    static int RepairPackedColorLerp(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.Color")
            return 0;

        FieldDefinition packed = type.Fields.SingleOrDefault(field => field.Name == "packed");
        MethodDefinition lerp = type.Methods.SingleOrDefault(method => method.Name == "Lerp" &&
            method.IsStatic && method.ReturnType.FullName == type.FullName &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == type.FullName &&
            method.Parameters[1].ParameterType.FullName == type.FullName &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Single);
        MethodReference channelLerp = type.Methods.SingleOrDefault(method => method.Name == "Lerp" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Int32 &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Int32 &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Single);
        if (packed == null || packed.IsStatic || packed.FieldType.MetadataType != MetadataType.UInt32 ||
            lerp == null || !lerp.HasBody || channelLerp == null)
            throw new InvalidDataException("unexpected EB.Math.Color packed Lerp metadata");

        ModuleDefinition module = type.Module;
        lerp.Body.ExceptionHandlers.Clear();
        lerp.Body.Variables.Clear();
        lerp.Body.Instructions.Clear();
        lerp.Body.InitLocals = true;
        lerp.Body.MaxStackSize = 4;
        VariableDefinition first = new VariableDefinition(module.TypeSystem.UInt32);
        VariableDefinition second = new VariableDefinition(module.TypeSystem.UInt32);
        VariableDefinition red = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition green = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition blue = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition alpha = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition result = new VariableDefinition(type);
        foreach (VariableDefinition local in new[] { first, second, red, green, blue, alpha, result })
            lerp.Body.Variables.Add(local);

        ILProcessor il = lerp.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarga, lerp.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, packed));
        il.Append(Instruction.Create(OpCodes.Stloc, first));
        il.Append(Instruction.Create(OpCodes.Ldarga, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, packed));
        il.Append(Instruction.Create(OpCodes.Stloc, second));
        AppendPackedColorChannelLerp(il, first, second, lerp.Parameters[2], channelLerp,
            red, 0x000000ff, 0);
        AppendPackedColorChannelLerp(il, first, second, lerp.Parameters[2], channelLerp,
            green, 0x0000ff00, 8);
        AppendPackedColorChannelLerp(il, first, second, lerp.Parameters[2], channelLerp,
            blue, 0x00ff0000, 16);
        AppendPackedColorChannelLerp(il, first, second, lerp.Parameters[2], channelLerp,
            alpha, unchecked((int)0xff000000), 24);
        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldloc, red));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
        il.Append(Instruction.Create(OpCodes.And));
        il.Append(Instruction.Create(OpCodes.Ldloc, green));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
        il.Append(Instruction.Create(OpCodes.And));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 8));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Ldloc, blue));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
        il.Append(Instruction.Create(OpCodes.And));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 16));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Ldloc, alpha));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
        il.Append(Instruction.Create(OpCodes.And));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 24));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Conv_U4));
        il.Append(Instruction.Create(OpCodes.Stfld, packed));
        il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        return 1;
    }

    static void ResetMethodBody(MethodDefinition method) {
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = true;
    }

    static MethodDefinition FindInstanceConstructor(TypeDefinition type, params string[] parameters) =>
        type.Methods.SingleOrDefault(method => method.Name == ".ctor" && !method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == parameters.Length &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));

    static int RepairPackedColorConversions(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.Color")
            return 0;

        ModuleDefinition module = type.Module;
        FieldDefinition packed = type.Fields.SingleOrDefault(field => field.Name == "packed" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.UInt32);
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        TypeDefinition vector4 = module.GetType("EB.Math.Vector4");
        FieldDefinition[] rgb = vector3 == null ? new FieldDefinition[0] : new[] { "x", "y", "z" }
            .Select(name => vector3.Fields.SingleOrDefault(field => field.Name == name &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        FieldDefinition[] rgba = vector4 == null ? new FieldDefinition[0] : new[] { "x", "y", "z", "w" }
            .Select(name => vector4.Fields.SingleOrDefault(field => field.Name == name &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        MethodDefinition vector3Constructor = vector3 == null ? null : FindInstanceConstructor(vector3,
            "System.Single", "System.Single", "System.Single");
        MethodDefinition vector4Constructor = vector4 == null ? null : FindInstanceConstructor(vector4,
            "System.Single", "System.Single", "System.Single", "System.Single");
        MethodDefinition floatConstructor = FindInstanceConstructor(type,
            "System.Single", "System.Single", "System.Single", "System.Single");
        MethodDefinition multiply = type.Methods.SingleOrDefault(method => method.Name == "Multiply" &&
            method.IsStatic && method.ReturnType.FullName == type.FullName && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == type.FullName &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Single);
        MethodDefinition multiplyOperator = type.Methods.SingleOrDefault(method => method.Name == "op_Multiply" &&
            method.IsStatic && method.ReturnType.FullName == type.FullName && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == type.FullName &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Single);
        MethodDefinition fromIntegers = type.Methods.SingleOrDefault(method => method.Name == "FromNonPremultiplied" &&
            method.IsStatic && method.ReturnType.FullName == type.FullName && method.Parameters.Count == 4 &&
            method.Parameters.All(parameter => parameter.ParameterType.MetadataType == MetadataType.Int32));
        MethodDefinition fromVector = type.Methods.SingleOrDefault(method => method.Name == "FromNonPremultiplied" &&
            method.IsStatic && method.ReturnType.FullName == type.FullName && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Vector4");
        MethodDefinition toVector3 = type.Methods.SingleOrDefault(method => method.Name == "ToVector3" &&
            !method.IsStatic && method.ReturnType.FullName == "EB.Math.Vector3" && method.Parameters.Count == 0);
        MethodDefinition toVector4 = type.Methods.SingleOrDefault(method => method.Name == "ToVector4" &&
            !method.IsStatic && method.ReturnType.FullName == "EB.Math.Vector4" && method.Parameters.Count == 0);
        if (packed == null || rgb.Length != 3 || rgb.Any(field => field == null) ||
            rgba.Length != 4 || rgba.Any(field => field == null) || vector3Constructor == null ||
            vector4Constructor == null || floatConstructor == null || multiply == null ||
            multiplyOperator == null || fromIntegers == null || fromVector == null ||
            toVector3 == null || toVector4 == null)
            throw new InvalidDataException("unexpected EB.Math.Color conversion metadata");

        VariableDefinition result = new VariableDefinition(type);
        VariableDefinition alphaScale = new VariableDefinition(module.TypeSystem.Single);
        VariableDefinition[] channels = Enumerable.Range(0, 3)
            .Select(_ => new VariableDefinition(module.TypeSystem.Int32)).ToArray();
        ResetMethodBody(fromIntegers);
        fromIntegers.Body.Variables.Add(result);
        fromIntegers.Body.Variables.Add(alphaScale);
        foreach (VariableDefinition channel in channels) fromIntegers.Body.Variables.Add(channel);
        // 9.2 computes alpha/255 as a float, multiplies each input RGB channel by it,
        // truncates each product to int, and packs ARGB bytes without a second clamp.
        ILProcessor il = fromIntegers.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Conv_R4));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f / 255.0f));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Stloc, alphaScale));
        for (int i = 0; i < 3; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg, fromIntegers.Parameters[i]));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldloc, alphaScale));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Stloc, channels[i]));
        }
        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldloc, channels[0]));
        il.Append(Instruction.Create(OpCodes.Ldloc, channels[1]));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_8));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Ldloc, channels[2]));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)16));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)24));
        il.Append(Instruction.Create(OpCodes.Shl));
        il.Append(Instruction.Create(OpCodes.Or));
        il.Append(Instruction.Create(OpCodes.Conv_U4));
        il.Append(Instruction.Create(OpCodes.Stfld, packed));
        il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        fromIntegers.Body.MaxStackSize = 4;

        ResetMethodBody(fromVector);
        VariableDefinition vectorResult = new VariableDefinition(type);
        VariableDefinition alpha = new VariableDefinition(module.TypeSystem.Single);
        VariableDefinition[] premultiplied = Enumerable.Range(0, 3)
            .Select(_ => new VariableDefinition(module.TypeSystem.Single)).ToArray();
        fromVector.Body.Variables.Add(vectorResult);
        fromVector.Body.Variables.Add(alpha);
        foreach (VariableDefinition channel in premultiplied) fromVector.Body.Variables.Add(channel);
        il = fromVector.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarga_S, fromVector.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, rgba[3]));
        il.Append(Instruction.Create(OpCodes.Stloc, alpha));
        for (int i = 0; i < 3; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarga_S, fromVector.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Ldfld, rgba[i]));
            il.Append(Instruction.Create(OpCodes.Ldloc, alpha));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Stloc, premultiplied[i]));
        }
        il.Append(Instruction.Create(OpCodes.Ldloca, vectorResult));
        il.Append(Instruction.Create(OpCodes.Ldloc, premultiplied[0]));
        il.Append(Instruction.Create(OpCodes.Ldloc, premultiplied[1]));
        il.Append(Instruction.Create(OpCodes.Ldloc, premultiplied[2]));
        il.Append(Instruction.Create(OpCodes.Ldloc, alpha));
        il.Append(Instruction.Create(OpCodes.Call, floatConstructor));
        il.Append(Instruction.Create(OpCodes.Ldloc, vectorResult));
        il.Append(Instruction.Create(OpCodes.Ret));
        fromVector.Body.MaxStackSize = 5;

        ResetMethodBody(multiply);
        VariableDefinition[] multiplied = Enumerable.Range(0, 4)
            .Select(_ => new VariableDefinition(module.TypeSystem.Int32)).ToArray();
        foreach (VariableDefinition channel in multiplied) multiply.Body.Variables.Add(channel);
        il = multiply.Body.GetILProcessor();
        for (int i = 0; i < 4; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarga_S, multiply.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Ldfld, packed));
            if (i != 0) {
                il.Append(Instruction.Create(OpCodes.Ldc_I4, i * 8));
                il.Append(Instruction.Create(OpCodes.Shr_Un));
            }
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
            il.Append(Instruction.Create(OpCodes.And));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Stloc, multiplied[i]));
        }
        VariableDefinition multipliedResult = new VariableDefinition(type);
        multiply.Body.Variables.Add(multipliedResult);
        il.Append(Instruction.Create(OpCodes.Ldloca, multipliedResult));
        il.Append(Instruction.Create(OpCodes.Ldloc, multiplied[0]));
        for (int i = 1; i < 4; i++) {
            il.Append(Instruction.Create(OpCodes.Ldloc, multiplied[i]));
            il.Append(Instruction.Create(OpCodes.Ldc_I4, i * 8));
            il.Append(Instruction.Create(OpCodes.Shl));
            il.Append(Instruction.Create(OpCodes.Or));
        }
        il.Append(Instruction.Create(OpCodes.Conv_U4));
        il.Append(Instruction.Create(OpCodes.Stfld, packed));
        il.Append(Instruction.Create(OpCodes.Ldloc, multipliedResult));
        il.Append(Instruction.Create(OpCodes.Ret));
        multiply.Body.MaxStackSize = 4;

        ResetMethodBody(multiplyOperator);
        il = multiplyOperator.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, multiply));
        il.Append(Instruction.Create(OpCodes.Ret));
        multiplyOperator.Body.MaxStackSize = 2;

        ResetMethodBody(toVector3);
        VariableDefinition vector3Result = new VariableDefinition(vector3);
        toVector3.Body.Variables.Add(vector3Result);
        il = toVector3.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldloca, vector3Result));
        for (int i = 0; i < 3; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, packed));
            if (i != 0) {
                il.Append(Instruction.Create(OpCodes.Ldc_I4, i * 8));
                il.Append(Instruction.Create(OpCodes.Shr_Un));
            }
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
            il.Append(Instruction.Create(OpCodes.And));
            il.Append(Instruction.Create(OpCodes.Conv_R_Un));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f / 255.0f));
            il.Append(Instruction.Create(OpCodes.Mul));
        }
        il.Append(Instruction.Create(OpCodes.Call, vector3Constructor));
        il.Append(Instruction.Create(OpCodes.Ldloc, vector3Result));
        il.Append(Instruction.Create(OpCodes.Ret));
        toVector3.Body.MaxStackSize = 4;

        ResetMethodBody(toVector4);
        VariableDefinition vector4Result = new VariableDefinition(vector4);
        toVector4.Body.Variables.Add(vector4Result);
        il = toVector4.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldloca, vector4Result));
        for (int i = 0; i < 4; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, packed));
            if (i != 0) {
                il.Append(Instruction.Create(OpCodes.Ldc_I4, i * 8));
                il.Append(Instruction.Create(OpCodes.Shr_Un));
            }
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xff));
            il.Append(Instruction.Create(OpCodes.And));
            il.Append(Instruction.Create(OpCodes.Conv_R_Un));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f / 255.0f));
            il.Append(Instruction.Create(OpCodes.Mul));
        }
        il.Append(Instruction.Create(OpCodes.Call, vector4Constructor));
        il.Append(Instruction.Create(OpCodes.Ldloc, vector4Result));
        il.Append(Instruction.Create(OpCodes.Ret));
        toVector4.Body.MaxStackSize = 5;
        return 6;
    }

    static bool IsByReferenceTo(TypeReference type, string elementType) {
        ByReferenceType byReference = type as ByReferenceType;
        return byReference != null && byReference.ElementType.FullName == elementType;
    }

    static int RequiredEnumValue(TypeDefinition enumType, string name) {
        FieldDefinition field = enumType.Fields.SingleOrDefault(candidate => candidate.Name == name &&
            candidate.IsStatic && candidate.HasConstant);
        if (field == null || field.Constant == null)
            throw new InvalidDataException("unexpected enum constant metadata: " + enumType.FullName + "." + name);
        return Convert.ToInt32(field.Constant);
    }

    static void AppendBoundsComponent(ILProcessor il, bool useThis, FieldDefinition minOrMax,
        FieldDefinition component) {
        il.Append(Instruction.Create(useThis ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldflda, minOrMax));
        il.Append(Instruction.Create(OpCodes.Ldfld, component));
    }

    static void AppendPointComponent(ILProcessor il, bool useThis, FieldDefinition bounds,
        FieldDefinition component) {
        il.Append(Instruction.Create(useThis ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1));
        if (useThis)
            il.Append(Instruction.Create(OpCodes.Ldflda, bounds));
        il.Append(Instruction.Create(OpCodes.Ldfld, component));
    }

    static void AppendContainmentStore(ILProcessor il, int value) {
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, value));
        il.Append(Instruction.Create(OpCodes.Stind_I4));
    }

    static void AppendAabbComparison(ILProcessor il, bool leftIsThis, FieldDefinition leftSide,
        bool rightIsThis, FieldDefinition rightSide, FieldDefinition component, OpCode comparison,
        Instruction target) {
        AppendBoundsComponent(il, leftIsThis, leftSide, component);
        AppendBoundsComponent(il, rightIsThis, rightSide, component);
        il.Append(Instruction.Create(comparison));
        il.Append(Instruction.Create(OpCodes.Brtrue, target));
    }

    static MethodDefinition FindContainsByReference(TypeDefinition type, string argumentType) =>
        type.Methods.SingleOrDefault(method => method.Name == "Contains" && !method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 2 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, argumentType) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, "EB.Math.ContainmentType"));

    static MethodDefinition FindContainsValue(TypeDefinition type, string argumentType) =>
        type.Methods.SingleOrDefault(method => method.Name == "Contains" && !method.IsStatic &&
            method.ReturnType.FullName == "EB.Math.ContainmentType" && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == argumentType);

    static void RebuildContainsValueWrapper(MethodDefinition wrapper, MethodDefinition byReferenceMethod,
        TypeReference containmentType) {
        wrapper.Body.ExceptionHandlers.Clear();
        wrapper.Body.Variables.Clear();
        wrapper.Body.Instructions.Clear();
        wrapper.Body.InitLocals = true;
        VariableDefinition result = new VariableDefinition(containmentType);
        wrapper.Body.Variables.Add(result);
        ILProcessor il = wrapper.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, wrapper.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, result));
        il.Append(Instruction.Create(OpCodes.Call, byReferenceMethod));
        il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        wrapper.Body.MaxStackSize = 3;
    }

    static int RepairBoundingBoxContains(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.BoundingBox")
            return 0;

        FieldDefinition min = type.Fields.SingleOrDefault(field => field.Name == "Min");
        FieldDefinition max = type.Fields.SingleOrDefault(field => field.Name == "Max");
        TypeDefinition vector3 = type.Module.GetType("EB.Math.Vector3");
        TypeDefinition containment = type.Module.GetType("EB.Math.ContainmentType");
        if (min == null || max == null || min.FieldType.FullName != "EB.Math.Vector3" ||
            max.FieldType.FullName != "EB.Math.Vector3" || vector3 == null || containment == null || !containment.IsEnum)
            throw new InvalidDataException("unexpected EB.Math.BoundingBox field or enum metadata");

        FieldDefinition[] components = new[] { "x", "y", "z" }.Select(name =>
            vector3.Fields.SingleOrDefault(field => field.Name == name && field.FieldType.MetadataType == MetadataType.Single))
            .ToArray();
        if (components.Any(field => field == null))
            throw new InvalidDataException("unexpected EB.Math.Vector3 component metadata");

        int containsValue = RequiredEnumValue(containment, "Contains");
        int disjointValue = RequiredEnumValue(containment, "Disjoint");
        int intersectsValue = RequiredEnumValue(containment, "Intersects");
        MethodDefinition boxByRef = FindContainsByReference(type, "EB.Math.BoundingBox");
        MethodDefinition pointByRef = FindContainsByReference(type, "EB.Math.Vector3");
        MethodDefinition sphereByRef = FindContainsByReference(type, "EB.Math.BoundingSphere");
        MethodDefinition boxValue = FindContainsValue(type, "EB.Math.BoundingBox");
        MethodDefinition pointValue = FindContainsValue(type, "EB.Math.Vector3");
        MethodDefinition sphereValue = FindContainsValue(type, "EB.Math.BoundingSphere");
        if (boxByRef == null || pointByRef == null || sphereByRef == null ||
            boxValue == null || pointValue == null || sphereValue == null)
            throw new InvalidDataException("unexpected EB.Math.BoundingBox.Contains overload metadata");

        foreach (MethodDefinition method in new[] { boxByRef, pointByRef, sphereByRef,
            boxValue, pointValue, sphereValue }) {
            if (!method.HasBody)
                throw new InvalidDataException("EB.Math.BoundingBox.Contains method has no body: " + method.FullName);
            method.Body.ExceptionHandlers.Clear();
            method.Body.Variables.Clear();
            method.Body.Instructions.Clear();
            method.Body.InitLocals = true;
        }

        Instruction boxDisjoint = Instruction.Create(OpCodes.Nop);
        Instruction boxIntersects = Instruction.Create(OpCodes.Nop);
        ILProcessor boxIl = boxByRef.Body.GetILProcessor();
        for (int i = 0; i < components.Length; i++) {
            AppendAabbComparison(boxIl, true, max, false, min, components[i], OpCodes.Clt, boxDisjoint);
            AppendAabbComparison(boxIl, true, min, false, max, components[i], OpCodes.Cgt, boxDisjoint);
        }
        for (int i = 0; i < components.Length; i++) {
            AppendAabbComparison(boxIl, false, min, true, min, components[i], OpCodes.Clt, boxIntersects);
            AppendAabbComparison(boxIl, false, max, true, max, components[i], OpCodes.Cgt, boxIntersects);
        }
        AppendContainmentStore(boxIl, containsValue);
        boxIl.Append(Instruction.Create(OpCodes.Ret));
        boxIl.Append(boxIntersects);
        AppendContainmentStore(boxIl, intersectsValue);
        boxIl.Append(Instruction.Create(OpCodes.Ret));
        boxIl.Append(boxDisjoint);
        AppendContainmentStore(boxIl, disjointValue);
        boxIl.Append(Instruction.Create(OpCodes.Ret));
        boxByRef.Body.MaxStackSize = 2;

        Instruction pointDisjoint = Instruction.Create(OpCodes.Nop);
        ILProcessor pointIl = pointByRef.Body.GetILProcessor();
        for (int i = 0; i < components.Length; i++) {
            AppendPointComponent(pointIl, false, null, components[i]);
            AppendPointComponent(pointIl, true, min, components[i]);
            pointIl.Append(Instruction.Create(OpCodes.Clt));
            pointIl.Append(Instruction.Create(OpCodes.Brtrue, pointDisjoint));
            AppendPointComponent(pointIl, false, null, components[i]);
            AppendPointComponent(pointIl, true, max, components[i]);
            pointIl.Append(Instruction.Create(OpCodes.Cgt));
            pointIl.Append(Instruction.Create(OpCodes.Brtrue, pointDisjoint));
        }
        AppendContainmentStore(pointIl, containsValue);
        pointIl.Append(Instruction.Create(OpCodes.Ret));
        pointIl.Append(pointDisjoint);
        AppendContainmentStore(pointIl, disjointValue);
        pointIl.Append(Instruction.Create(OpCodes.Ret));
        pointByRef.Body.MaxStackSize = 2;

        RebuildContainsValueWrapper(boxValue, boxByRef, containment);
        RebuildContainsValueWrapper(pointValue, pointByRef, containment);
        RebuildContainsValueWrapper(sphereValue, sphereByRef, containment);

        TypeDefinition sphereType = type.Module.GetType("EB.Math.BoundingSphere");
        if (sphereType == null)
            throw new InvalidDataException("missing EB.Math.BoundingSphere metadata");
        FieldDefinition center = sphereType.Fields.SingleOrDefault(field => field.Name == "Center" &&
            field.FieldType.FullName == vector3.FullName);
        FieldDefinition radius = sphereType.Fields.SingleOrDefault(field => field.Name == "Radius" &&
            field.FieldType.MetadataType == MetadataType.Single);
        MethodDefinition clamp = vector3.Methods.SingleOrDefault(method => method.Name == "Clamp" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 4 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[2].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[3].ParameterType, vector3.FullName));
        MethodDefinition distanceSquared = vector3.Methods.SingleOrDefault(method =>
            method.Name == "DistanceSquared" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[2].ParameterType, "System.Single"));
        if (center == null || radius == null || clamp == null || distanceSquared == null)
            throw new InvalidDataException("unexpected BoundingSphere or Vector3 helper metadata");

        VariableDefinition sphereCenter = new VariableDefinition(vector3);
        VariableDefinition closest = new VariableDefinition(vector3);
        VariableDefinition distance = new VariableDefinition(type.Module.TypeSystem.Single);
        sphereByRef.Body.Variables.Add(sphereCenter);
        sphereByRef.Body.Variables.Add(closest);
        sphereByRef.Body.Variables.Add(distance);
        ILProcessor sphereIl = sphereByRef.Body.GetILProcessor();
        Instruction sphereDisjoint = Instruction.Create(OpCodes.Nop);
        Instruction sphereIntersects = Instruction.Create(OpCodes.Nop);
        sphereIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        sphereIl.Append(Instruction.Create(OpCodes.Ldfld, center));
        sphereIl.Append(Instruction.Create(OpCodes.Stloc, sphereCenter));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, sphereCenter));
        sphereIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        sphereIl.Append(Instruction.Create(OpCodes.Ldflda, min));
        sphereIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        sphereIl.Append(Instruction.Create(OpCodes.Ldflda, max));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, closest));
        sphereIl.Append(Instruction.Create(OpCodes.Call, clamp));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, sphereCenter));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, closest));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, distance));
        sphereIl.Append(Instruction.Create(OpCodes.Call, distanceSquared));
        sphereIl.Append(Instruction.Create(OpCodes.Ldloc, distance));
        sphereIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        sphereIl.Append(Instruction.Create(OpCodes.Ldfld, radius));
        sphereIl.Append(Instruction.Create(OpCodes.Bgt, sphereDisjoint));
        foreach (FieldDefinition component in components) {
            sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, sphereCenter));
            sphereIl.Append(Instruction.Create(OpCodes.Ldfld, component));
            sphereIl.Append(Instruction.Create(OpCodes.Ldarg_1));
            sphereIl.Append(Instruction.Create(OpCodes.Ldfld, radius));
            sphereIl.Append(Instruction.Create(OpCodes.Sub));
            AppendBoundsComponent(sphereIl, true, min, component);
            sphereIl.Append(Instruction.Create(OpCodes.Clt));
            sphereIl.Append(Instruction.Create(OpCodes.Brtrue, sphereIntersects));
            sphereIl.Append(Instruction.Create(OpCodes.Ldloca_S, sphereCenter));
            sphereIl.Append(Instruction.Create(OpCodes.Ldfld, component));
            sphereIl.Append(Instruction.Create(OpCodes.Ldarg_1));
            sphereIl.Append(Instruction.Create(OpCodes.Ldfld, radius));
            sphereIl.Append(Instruction.Create(OpCodes.Add));
            AppendBoundsComponent(sphereIl, true, max, component);
            sphereIl.Append(Instruction.Create(OpCodes.Cgt));
            sphereIl.Append(Instruction.Create(OpCodes.Brtrue, sphereIntersects));
        }
        AppendContainmentStore(sphereIl, containsValue);
        sphereIl.Append(Instruction.Create(OpCodes.Ret));
        sphereIl.Append(sphereIntersects);
        AppendContainmentStore(sphereIl, intersectsValue);
        sphereIl.Append(Instruction.Create(OpCodes.Ret));
        sphereIl.Append(sphereDisjoint);
        AppendContainmentStore(sphereIl, disjointValue);
        sphereIl.Append(Instruction.Create(OpCodes.Ret));
        sphereByRef.Body.MaxStackSize = 4;
        return 6;
    }

    static MethodDefinition FindOutMethod(TypeDefinition type, string name, string returnType,
        string argumentType, string outputType) => type.Methods.SingleOrDefault(method =>
            method.Name == name && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 2 && IsByReferenceTo(method.Parameters[0].ParameterType, argumentType) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, outputType));

    static MethodDefinition FindValueMethod(TypeDefinition type, string name, string returnType,
        string argumentType) => type.Methods.SingleOrDefault(method => method.Name == name && !method.IsStatic &&
            method.ReturnType.FullName == returnType && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == argumentType);

    static void RebuildOutValueWrapper(MethodDefinition wrapper, MethodDefinition outMethod,
        TypeReference outputType) {
        ResetMethodBody(wrapper);
        VariableDefinition result = new VariableDefinition(outputType);
        wrapper.Body.Variables.Add(result);
        ILProcessor il = wrapper.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, wrapper.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, result));
        il.Append(Instruction.Create(OpCodes.Call, outMethod));
        il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        wrapper.Body.MaxStackSize = 3;
    }

    static int RepairBoundingSphereGeometry(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Math.BoundingSphere")
            return 0;

        ModuleDefinition module = type.Module;
        FieldDefinition center = type.Fields.SingleOrDefault(field => field.Name == "Center" &&
            field.FieldType.FullName == "EB.Math.Vector3");
        FieldDefinition radius = type.Fields.SingleOrDefault(field => field.Name == "Radius" &&
            field.FieldType.MetadataType == MetadataType.Single);
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        TypeDefinition plane = module.GetType("EB.Math.Plane");
        TypeDefinition containment = module.GetType("EB.Math.ContainmentType");
        TypeDefinition planeResult = module.GetType("EB.Math.PlaneIntersectionType");
        if (center == null || radius == null || vector3 == null || plane == null ||
            containment == null || !containment.IsEnum || planeResult == null || !planeResult.IsEnum)
            throw new InvalidDataException("unexpected EB.Math.BoundingSphere field or enum metadata");
        FieldDefinition[] components = new[] { "x", "y", "z" }.Select(name =>
            vector3.Fields.SingleOrDefault(field => field.Name == name &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        FieldDefinition planeNormal = plane.Fields.SingleOrDefault(field => field.Name == "Normal" &&
            field.FieldType.FullName == vector3.FullName);
        FieldDefinition planeD = plane.Fields.SingleOrDefault(field => field.Name == "D" &&
            field.FieldType.MetadataType == MetadataType.Single);
        if (components.Any(field => field == null) || planeNormal == null || planeD == null)
            throw new InvalidDataException("unexpected Vector3 or Plane field metadata");

        int containsValue = RequiredEnumValue(containment, "Contains");
        int disjointValue = RequiredEnumValue(containment, "Disjoint");
        int intersectsValue = RequiredEnumValue(containment, "Intersects");
        int frontValue = RequiredEnumValue(planeResult, "Front");
        int backValue = RequiredEnumValue(planeResult, "Back");
        int planeIntersectsValue = RequiredEnumValue(planeResult, "Intersecting");
        MethodDefinition containsPointRef = FindContainsByReference(type, vector3.FullName);
        MethodDefinition containsSphereRef = FindContainsByReference(type, type.FullName);
        MethodDefinition containsPoint = FindContainsValue(type, vector3.FullName);
        MethodDefinition containsSphere = FindContainsValue(type, type.FullName);
        MethodDefinition intersectsPlaneRef = FindOutMethod(type, "Intersects", "System.Void",
            plane.FullName, planeResult.FullName);
        MethodDefinition intersectsPlane = FindValueMethod(type, "Intersects", planeResult.FullName,
            plane.FullName);
        MethodDefinition intersectsSphereRef = FindOutMethod(type, "Intersects", "System.Void",
            type.FullName, "System.Boolean");
        MethodDefinition intersectsSphere = FindValueMethod(type, "Intersects", "System.Boolean",
            type.FullName);
        MethodDefinition distance = vector3.Methods.SingleOrDefault(method => method.Name == "Distance" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[2].ParameterType, "System.Single"));
        MethodDefinition distanceSquared = vector3.Methods.SingleOrDefault(method =>
            method.Name == "DistanceSquared" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[1].ParameterType, vector3.FullName) &&
            IsByReferenceTo(method.Parameters[2].ParameterType, "System.Single"));
        if (containsPointRef == null || containsSphereRef == null || containsPoint == null ||
            containsSphere == null || intersectsPlaneRef == null || intersectsPlane == null ||
            intersectsSphereRef == null || intersectsSphere == null || distance == null || distanceSquared == null)
            throw new InvalidDataException("unexpected EB.Math.BoundingSphere overload or Vector3 helper metadata");

        foreach (MethodDefinition method in new[] { containsPointRef, containsSphereRef, containsPoint,
            containsSphere, intersectsPlaneRef, intersectsPlane, intersectsSphereRef, intersectsSphere })
            ResetMethodBody(method);

        VariableDefinition pointDistanceSquared = new VariableDefinition(module.TypeSystem.Single);
        containsPointRef.Body.Variables.Add(pointDistanceSquared);
        ILProcessor il = containsPointRef.Body.GetILProcessor();
        Instruction pointDisjoint = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldflda, center));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, pointDistanceSquared));
        il.Append(Instruction.Create(OpCodes.Call, distanceSquared));
        il.Append(Instruction.Create(OpCodes.Ldloc, pointDistanceSquared));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Bge, pointDisjoint));
        AppendContainmentStore(il, containsValue);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(pointDisjoint);
        AppendContainmentStore(il, disjointValue);
        il.Append(Instruction.Create(OpCodes.Ret));
        containsPointRef.Body.MaxStackSize = 3;
        RebuildContainsValueWrapper(containsPoint, containsPointRef, containment);

        VariableDefinition centerDistance = new VariableDefinition(module.TypeSystem.Single);
        containsSphereRef.Body.Variables.Add(centerDistance);
        il = containsSphereRef.Body.GetILProcessor();
        Instruction sphereDisjoint = Instruction.Create(OpCodes.Nop);
        Instruction sphereIntersects = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldflda, center));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldflda, center));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, centerDistance));
        il.Append(Instruction.Create(OpCodes.Call, distance));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Ldloc, centerDistance));
        il.Append(Instruction.Create(OpCodes.Blt, sphereDisjoint));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Ldloc, centerDistance));
        il.Append(Instruction.Create(OpCodes.Blt, sphereIntersects));
        AppendContainmentStore(il, containsValue);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(sphereIntersects);
        AppendContainmentStore(il, intersectsValue);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(sphereDisjoint);
        AppendContainmentStore(il, disjointValue);
        il.Append(Instruction.Create(OpCodes.Ret));
        containsSphereRef.Body.MaxStackSize = 3;
        RebuildContainsValueWrapper(containsSphere, containsSphereRef, containment);

        VariableDefinition signedDistance = new VariableDefinition(module.TypeSystem.Single);
        intersectsPlaneRef.Body.Variables.Add(signedDistance);
        il = intersectsPlaneRef.Body.GetILProcessor();
        Instruction planeBack = Instruction.Create(OpCodes.Nop);
        Instruction planeIntersects = Instruction.Create(OpCodes.Nop);
        for (int i = 0; i < components.Length; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldflda, planeNormal));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, center));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Mul));
            if (i != 0) il.Append(Instruction.Create(OpCodes.Add));
        }
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldfld, planeD));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Stloc, signedDistance));
        il.Append(Instruction.Create(OpCodes.Ldloc, signedDistance));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Bgt, planeBack));
        il.Append(Instruction.Create(OpCodes.Ldloc, signedDistance));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, radius));
        il.Append(Instruction.Create(OpCodes.Neg));
        il.Append(Instruction.Create(OpCodes.Blt, planeIntersects));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, planeIntersectsValue));
        il.Append(Instruction.Create(OpCodes.Stind_I4));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(planeIntersects);
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, backValue));
        il.Append(Instruction.Create(OpCodes.Stind_I4));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(planeBack);
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, frontValue));
        il.Append(Instruction.Create(OpCodes.Stind_I4));
        il.Append(Instruction.Create(OpCodes.Ret));
        intersectsPlaneRef.Body.MaxStackSize = 3;
        RebuildOutValueWrapper(intersectsPlane, intersectsPlaneRef, planeResult);

        il = intersectsSphereRef.Body.GetILProcessor();
        VariableDefinition sphereContainment = new VariableDefinition(containment);
        intersectsSphereRef.Body.Variables.Add(sphereContainment);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, sphereContainment));
        il.Append(Instruction.Create(OpCodes.Call, containsSphereRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldloc, sphereContainment));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, intersectsValue));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Stind_I1));
        il.Append(Instruction.Create(OpCodes.Ret));
        intersectsSphereRef.Body.MaxStackSize = 3;
        RebuildOutValueWrapper(intersectsSphere, intersectsSphereRef, module.TypeSystem.Boolean);
        return 8;
    }

    static int RepairCachePurge(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Cache")
            return 0;

        MethodDefinition purge = type.Methods.SingleOrDefault(method => method.Name == "PurgeCache" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.TimeSpan");
        if (purge == null || !purge.HasBody)
            throw new InvalidDataException("missing EB.Cache.PurgeCache(TimeSpan) method");

        PropertyDefinition cacheFolderProperty = type.Properties.SingleOrDefault(property =>
            property.Name == "CacheFolder" && property.PropertyType.MetadataType == MetadataType.String &&
            property.GetMethod != null && property.GetMethod.IsStatic && property.GetMethod.Parameters.Count == 0);
        FieldDefinition diskCache = type.Fields.SingleOrDefault(field => field.Name == "DiskCache" &&
            field.IsStatic && field.FieldType.MetadataType == MetadataType.Boolean);
        TypeDefinition debugType = type.Module.GetType("EB.Debug");
        MethodDefinition logError = debugType == null ? null : debugType.Methods.SingleOrDefault(method =>
            method.Name == "LogError" && method.IsStatic && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "System.Object" &&
            method.Parameters[1].ParameterType.FullName == "System.Object[]");
        if (cacheFolderProperty == null || diskCache == null || logError == null)
            throw new InvalidDataException("missing EB.Cache purge dependencies");

        ModuleDefinition module = type.Module;
        MethodReference utcNow = module.ImportReference(typeof(DateTime).GetProperty("UtcNow").GetGetMethod());
        MethodReference getFiles = module.ImportReference(typeof(System.IO.Directory).GetMethod("GetFiles",
            new[] { typeof(string) }));
        MethodReference lastWriteTimeUtc = module.ImportReference(typeof(System.IO.File).GetMethod(
            "GetLastWriteTimeUtc", new[] { typeof(string) }));
        MethodReference deleteFile = module.ImportReference(typeof(System.IO.File).GetMethod("Delete",
            new[] { typeof(string) }));
        MethodReference subtractDates = module.ImportReference(typeof(DateTime).GetMethod("op_Subtraction",
            new[] { typeof(DateTime), typeof(DateTime) }));
        MethodReference greaterThan = module.ImportReference(typeof(TimeSpan).GetMethod("op_GreaterThan",
            new[] { typeof(TimeSpan), typeof(TimeSpan) }));
        MethodReference exceptionMessage = module.ImportReference(typeof(Exception).GetProperty("Message").GetGetMethod());
        MethodReference concatenate = module.ImportReference(typeof(string).GetMethod("Concat",
            new[] { typeof(string), typeof(string) }));
        TypeReference exceptionType = module.ImportReference(typeof(Exception));
        TypeReference objectType = module.TypeSystem.Object;

        purge.Body.ExceptionHandlers.Clear();
        purge.Body.Variables.Clear();
        purge.Body.Instructions.Clear();
        purge.Body.InitLocals = true;
        VariableDefinition now = new VariableDefinition(module.ImportReference(typeof(DateTime)));
        VariableDefinition files = new VariableDefinition(new ArrayType(module.TypeSystem.String));
        VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition file = new VariableDefinition(module.TypeSystem.String);
        VariableDefinition modified = new VariableDefinition(module.ImportReference(typeof(DateTime)));
        VariableDefinition error = new VariableDefinition(exceptionType);
        foreach (VariableDefinition local in new[] { now, files, index, file, modified, error })
            purge.Body.Variables.Add(local);

        ILProcessor il = purge.Body.GetILProcessor();
        Instruction tryStart = Instruction.Create(OpCodes.Call, utcNow);
        Instruction loopTest = Instruction.Create(OpCodes.Nop);
        Instruction loopBody = Instruction.Create(OpCodes.Nop);
        Instruction skipDelete = Instruction.Create(OpCodes.Nop);
        Instruction catchStart = Instruction.Create(OpCodes.Stloc, error);
        Instruction skipLog = Instruction.Create(OpCodes.Nop);
        Instruction returnPoint = Instruction.Create(OpCodes.Ret);
        il.Append(tryStart);
        il.Append(Instruction.Create(OpCodes.Stloc, now));
        il.Append(Instruction.Create(OpCodes.Call, cacheFolderProperty.GetMethod));
        il.Append(Instruction.Create(OpCodes.Call, getFiles));
        il.Append(Instruction.Create(OpCodes.Stloc, files));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(Instruction.Create(OpCodes.Br, loopTest));
        il.Append(loopBody);
        il.Append(Instruction.Create(OpCodes.Ldloc, files));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Ldelem_Ref));
        il.Append(Instruction.Create(OpCodes.Stloc, file));
        il.Append(Instruction.Create(OpCodes.Ldloc, file));
        il.Append(Instruction.Create(OpCodes.Call, lastWriteTimeUtc));
        il.Append(Instruction.Create(OpCodes.Stloc, modified));
        il.Append(Instruction.Create(OpCodes.Ldloc, now));
        il.Append(Instruction.Create(OpCodes.Ldloc, modified));
        il.Append(Instruction.Create(OpCodes.Call, subtractDates));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, greaterThan));
        il.Append(Instruction.Create(OpCodes.Brfalse, skipDelete));
        il.Append(Instruction.Create(OpCodes.Ldloc, file));
        il.Append(Instruction.Create(OpCodes.Call, deleteFile));
        il.Append(skipDelete);
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(loopTest);
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Ldloc, files));
        il.Append(Instruction.Create(OpCodes.Ldlen));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Blt, loopBody));
        il.Append(Instruction.Create(OpCodes.Leave, returnPoint));
        il.Append(catchStart);
        il.Append(Instruction.Create(OpCodes.Ldsfld, diskCache));
        il.Append(Instruction.Create(OpCodes.Brfalse, skipLog));
        il.Append(Instruction.Create(OpCodes.Ldstr, "[CoreCache::Purge] Failed to purge cache..."));
        il.Append(Instruction.Create(OpCodes.Ldloc, error));
        il.Append(Instruction.Create(OpCodes.Callvirt, exceptionMessage));
        il.Append(Instruction.Create(OpCodes.Call, concatenate));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Newarr, objectType));
        il.Append(Instruction.Create(OpCodes.Call, logError));
        il.Append(skipLog);
        il.Append(Instruction.Create(OpCodes.Leave, returnPoint));
        il.Append(returnPoint);

        purge.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = returnPoint,
        });
        purge.Body.MaxStackSize = 2;
        return 1;
    }

    static MethodDefinition FindBufferMethod(TypeDefinition bufferType, string name, string returnType,
        params string[] parameters) => bufferType.Methods.SingleOrDefault(method => method.Name == name &&
            !method.IsStatic && method.ReturnType.FullName == returnType &&
            method.Parameters.Count == parameters.Length &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));

    static int RepairBitStreamByteArray(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.BitStream")
            return 0;

        MethodDefinition serialize = type.Methods.SingleOrDefault(method => method.Name == "Serialize" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 1 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, "System.Byte[]"));
        TypeDefinition bufferType = type.Module.GetType("EB.Buffer");
        FieldDefinition bufferField = type.Fields.SingleOrDefault(field => field.Name == "_buffer" &&
            field.FieldType.FullName == "EB.Buffer");
        FieldDefinition isReading = type.Fields.SingleOrDefault(field =>
            field.Name == "<isReading>k__BackingField" && field.FieldType.MetadataType == MetadataType.Boolean);
        if (serialize == null || !serialize.HasBody || bufferType == null || bufferField == null || isReading == null)
            throw new InvalidDataException("missing EB.BitStream byte-array serialization metadata");

        MethodDefinition readByte = FindBufferMethod(bufferType, "ReadByte", "System.Byte");
        MethodDefinition readUInt16 = FindBufferMethod(bufferType, "ReadUInt16LE", "System.UInt16");
        MethodDefinition readBytes = FindBufferMethod(bufferType, "ReadBytes",
            "System.ArraySegment`1<System.Byte>", "System.Int32");
        MethodDefinition writeByte = FindBufferMethod(bufferType, "WriteByte", "System.Void", "System.Byte");
        MethodDefinition writeUInt16 = FindBufferMethod(bufferType, "WriteUInt16LE", "System.Void", "System.UInt16");
        MethodDefinition writeBytes = FindBufferMethod(bufferType, "WriteBytes", "System.Void", "System.Byte[]");
        if (readByte == null || readUInt16 == null || readBytes == null || writeByte == null ||
            writeUInt16 == null || writeBytes == null)
            throw new InvalidDataException("missing EB.Buffer byte-array serialization helpers");

        ModuleDefinition module = type.Module;
        TypeReference byteType = module.TypeSystem.Byte;
        TypeReference byteArrayType = new ArrayType(byteType);
        TypeReference segmentType = module.ImportReference(typeof(ArraySegment<byte>));
        MethodReference segmentArray = module.ImportReference(typeof(ArraySegment<byte>)
            .GetProperty("Array").GetGetMethod());
        MethodReference segmentOffset = module.ImportReference(typeof(ArraySegment<byte>)
            .GetProperty("Offset").GetGetMethod());
        MethodReference arrayCopy = module.ImportReference(typeof(Array).GetMethod("Copy",
            new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) }));
        MethodReference intToString = module.ImportReference(typeof(Convert).GetMethod("ToString", new[] { typeof(int) }));
        MethodReference concatenate = module.ImportReference(typeof(string).GetMethod("Concat",
            new[] { typeof(string), typeof(string) }));
        MethodReference argumentExceptionConstructor = module.ImportReference(typeof(ArgumentException)
            .GetConstructor(new[] { typeof(string) }));
        if (arrayCopy == null || intToString == null || concatenate == null || argumentExceptionConstructor == null)
            throw new InvalidDataException("missing byte-array serializer runtime methods");

        serialize.Body.ExceptionHandlers.Clear();
        serialize.Body.Variables.Clear();
        serialize.Body.Instructions.Clear();
        serialize.Body.InitLocals = true;
        VariableDefinition data = new VariableDefinition(byteArrayType);
        VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition segment = new VariableDefinition(segmentType);
        serialize.Body.Variables.Add(data);
        serialize.Body.Variables.Add(length);
        serialize.Body.Variables.Add(segment);

        ILProcessor il = serialize.Body.GetILProcessor();
        Instruction write = Instruction.Create(OpCodes.Nop);
        Instruction extendedLength = Instruction.Create(OpCodes.Nop);
        Instruction writeArray = Instruction.Create(OpCodes.Nop);
        Instruction writeExtendedLength = Instruction.Create(OpCodes.Nop);
        Instruction writePayload = Instruction.Create(OpCodes.Nop);
        Instruction finish = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, isReading));
        il.Append(Instruction.Create(OpCodes.Brfalse, write));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Callvirt, readByte));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Beq, extendedLength));
        Instruction readPayload = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Br, readPayload));
        il.Append(extendedLength);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Callvirt, readUInt16));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(readPayload);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Callvirt, readBytes));
        il.Append(Instruction.Create(OpCodes.Stloc, segment));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Newarr, byteType));
        il.Append(Instruction.Create(OpCodes.Stloc, data));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, segment));
        il.Append(Instruction.Create(OpCodes.Call, segmentArray));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, segment));
        il.Append(Instruction.Create(OpCodes.Call, segmentOffset));
        il.Append(Instruction.Create(OpCodes.Ldloc, data));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Call, arrayCopy));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldloc, data));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Br, finish));

        il.Append(write);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Stloc, data));
        il.Append(Instruction.Create(OpCodes.Ldloc, data));
        il.Append(Instruction.Create(OpCodes.Ldlen));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 65535));
        il.Append(Instruction.Create(OpCodes.Blt, writeArray));
        il.Append(Instruction.Create(OpCodes.Ldstr, "Byte array too large for bitstream "));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, length));
        il.Append(Instruction.Create(OpCodes.Call, intToString));
        il.Append(Instruction.Create(OpCodes.Call, concatenate));
        il.Append(Instruction.Create(OpCodes.Newobj, argumentExceptionConstructor));
        il.Append(Instruction.Create(OpCodes.Throw));
        il.Append(writeArray);
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Bge, writeExtendedLength));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Conv_U1));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeByte));
        il.Append(Instruction.Create(OpCodes.Br, writePayload));
        il.Append(writeExtendedLength);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Conv_U1));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeByte));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Conv_U2));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeUInt16));
        il.Append(writePayload);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, data));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeBytes));
        il.Append(finish);
        serialize.Body.MaxStackSize = 5;
        return 1;
    }

    static int RepairBitStreamBuffer(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.BitStream")
            return 0;

        MethodDefinition serialize = type.Methods.SingleOrDefault(method => method.Name == "Serialize" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 1 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, "EB.Buffer"));
        TypeDefinition bufferType = type.Module.GetType("EB.Buffer");
        FieldDefinition bufferField = type.Fields.SingleOrDefault(field => field.Name == "_buffer" &&
            field.FieldType.FullName == "EB.Buffer");
        FieldDefinition isReading = type.Fields.SingleOrDefault(field =>
            field.Name == "<isReading>k__BackingField" && field.FieldType.MetadataType == MetadataType.Boolean);
        if (serialize == null || !serialize.HasBody || bufferType == null || bufferField == null || isReading == null)
            throw new InvalidDataException("missing EB.BitStream Buffer serialization metadata");

        MethodDefinition readByte = FindBufferMethod(bufferType, "ReadByte", "System.Byte");
        MethodDefinition readUInt16 = FindBufferMethod(bufferType, "ReadUInt16LE", "System.UInt16");
        MethodDefinition readBytes = FindBufferMethod(bufferType, "ReadBytes",
            "System.ArraySegment`1<System.Byte>", "System.Int32");
        MethodDefinition writeByte = FindBufferMethod(bufferType, "WriteByte", "System.Void", "System.Byte");
        MethodDefinition writeUInt16 = FindBufferMethod(bufferType, "WriteUInt16LE", "System.Void", "System.UInt16");
        MethodDefinition writeBuffer = FindBufferMethod(bufferType, "WriteBuffer", "System.Void", "EB.Buffer");
        MethodDefinition lengthGetter = bufferType.Methods.SingleOrDefault(method => method.Name == "get_Length" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Int32 && method.Parameters.Count == 0);
        MethodDefinition segmentConstructor = bufferType.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "System.ArraySegment`1<System.Byte>" &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean);
        if (readByte == null || readUInt16 == null || readBytes == null || writeByte == null ||
            writeUInt16 == null || writeBuffer == null || lengthGetter == null || segmentConstructor == null)
            throw new InvalidDataException("missing EB.Buffer helpers or ArraySegment constructor for serialization");

        ModuleDefinition module = type.Module;
        TypeReference segmentType = module.ImportReference(typeof(ArraySegment<byte>));
        MethodReference intToString = module.ImportReference(typeof(int).GetMethod("ToString", Type.EmptyTypes));
        MethodReference concatenate = module.ImportReference(typeof(string).GetMethod("Concat",
            new[] { typeof(string), typeof(string) }));
        MethodReference argumentExceptionConstructor = module.ImportReference(typeof(ArgumentException)
            .GetConstructor(new[] { typeof(string) }));
        if (intToString == null || concatenate == null || argumentExceptionConstructor == null)
            throw new InvalidDataException("missing Buffer serializer runtime methods");

        serialize.Body.ExceptionHandlers.Clear();
        serialize.Body.Variables.Clear();
        serialize.Body.Instructions.Clear();
        serialize.Body.InitLocals = true;
        VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
        VariableDefinition segment = new VariableDefinition(segmentType);
        serialize.Body.Variables.Add(length);
        serialize.Body.Variables.Add(segment);

        ILProcessor il = serialize.Body.GetILProcessor();
        Instruction write = Instruction.Create(OpCodes.Nop);
        Instruction extendedLength = Instruction.Create(OpCodes.Nop);
        Instruction readPayload = Instruction.Create(OpCodes.Nop);
        Instruction writeShortLength = Instruction.Create(OpCodes.Nop);
        Instruction writeExtendedLength = Instruction.Create(OpCodes.Nop);
        Instruction writePayload = Instruction.Create(OpCodes.Nop);
        Instruction finish = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, isReading));
        il.Append(Instruction.Create(OpCodes.Brfalse, write));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Callvirt, readByte));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Beq, extendedLength));
        il.Append(Instruction.Create(OpCodes.Br, readPayload));
        il.Append(extendedLength);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Callvirt, readUInt16));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(readPayload);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Callvirt, readBytes));
        il.Append(Instruction.Create(OpCodes.Stloc, segment));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldloc, segment));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Newobj, segmentConstructor));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Br, finish));

        il.Append(write);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Callvirt, lengthGetter));
        il.Append(Instruction.Create(OpCodes.Stloc, length));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 65535));
        il.Append(Instruction.Create(OpCodes.Blt, writeShortLength));
        il.Append(Instruction.Create(OpCodes.Ldstr, "Byte array too large for bitstream "));
        il.Append(Instruction.Create(OpCodes.Ldloca_S, length));
        il.Append(Instruction.Create(OpCodes.Call, intToString));
        il.Append(Instruction.Create(OpCodes.Call, concatenate));
        il.Append(Instruction.Create(OpCodes.Newobj, argumentExceptionConstructor));
        il.Append(Instruction.Create(OpCodes.Throw));
        il.Append(writeShortLength);
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Bge, writeExtendedLength));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Conv_U1));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeByte));
        il.Append(Instruction.Create(OpCodes.Br, writePayload));
        il.Append(writeExtendedLength);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 255));
        il.Append(Instruction.Create(OpCodes.Conv_U1));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeByte));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldloc, length));
        il.Append(Instruction.Create(OpCodes.Conv_U2));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeUInt16));
        il.Append(writePayload);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeBuffer));
        il.Append(finish);
        serialize.Body.MaxStackSize = 3;
        return 1;
    }

    static int RepairBitStreamString(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.BitStream")
            return 0;

        MethodDefinition serialize = type.Methods.SingleOrDefault(method => method.Name == "Serialize" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 1 &&
            IsByReferenceTo(method.Parameters[0].ParameterType, "System.String"));
        TypeDefinition bufferType = type.Module.GetType("EB.Buffer");
        FieldDefinition bufferField = type.Fields.SingleOrDefault(field => field.Name == "_buffer" &&
            field.FieldType.FullName == "EB.Buffer");
        FieldDefinition isReading = type.Fields.SingleOrDefault(field =>
            field.Name == "<isReading>k__BackingField" && field.FieldType.MetadataType == MetadataType.Boolean);
        MethodDefinition readString = bufferType == null ? null : FindBufferMethod(bufferType,
            "ReadString", "System.String");
        MethodDefinition writeString = bufferType == null ? null : FindBufferMethod(bufferType,
            "WriteString", "System.Void", "System.String");
        if (serialize == null || !serialize.HasBody || bufferField == null || isReading == null ||
            readString == null || writeString == null)
            throw new InvalidDataException("missing EB.BitStream string serialization metadata");

        serialize.Body.ExceptionHandlers.Clear();
        serialize.Body.Variables.Clear();
        serialize.Body.Instructions.Clear();
        serialize.Body.InitLocals = true;
        ILProcessor il = serialize.Body.GetILProcessor();
        Instruction write = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, isReading));
        il.Append(Instruction.Create(OpCodes.Brfalse, write));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Callvirt, readString));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(write);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, bufferField));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Callvirt, writeString));
        il.Append(Instruction.Create(OpCodes.Ret));
        serialize.Body.MaxStackSize = 2;
        return 1;
    }

    static int RepairBeamFloatEvaluation(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.BeamRenderer")
            return 0;

        MethodDefinition evaluate = type.Methods.SingleOrDefault(method => method.Name == "FloatEvlautation" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Single &&
            method.Parameters.Count == 4 &&
            method.Parameters[0].ParameterType.FullName == "UnityEngine.AnimationCurve" &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Single &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Int32 &&
            IsByReferenceTo(method.Parameters[3].ParameterType,
                "System.Collections.Generic.List`1<System.Single>"));
        FieldDefinition addedEvaluations = type.Fields.SingleOrDefault(field =>
            field.Name == "addedEvaluaions" && field.FieldType.MetadataType == MetadataType.Boolean && !field.IsStatic);
        if (evaluate == null || !evaluate.HasBody || addedEvaluations == null)
            throw new InvalidDataException("missing BeamRenderer FloatEvlautation metadata");

        ModuleDefinition module = type.Module;
        TypeReference listType = ((ByReferenceType)evaluate.Parameters[3].ParameterType).ElementType;
        MethodReference listGetItem = new MethodReference("get_Item", module.TypeSystem.Single, listType) {
            HasThis = true,
        };
        listGetItem.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        MethodReference listAdd = new MethodReference("Add", module.TypeSystem.Void, listType) {
            HasThis = true,
        };
        listAdd.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        MethodReference curveEvaluate = new MethodReference("Evaluate", module.TypeSystem.Single,
            evaluate.Parameters[0].ParameterType) {
            HasThis = true,
        };
        curveEvaluate.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));

        evaluate.Body.ExceptionHandlers.Clear();
        evaluate.Body.Variables.Clear();
        evaluate.Body.Instructions.Clear();
        evaluate.Body.InitLocals = true;
        VariableDefinition value = new VariableDefinition(module.TypeSystem.Single);
        evaluate.Body.Variables.Add(value);
        ILProcessor il = evaluate.Body.GetILProcessor();
        Instruction sampleCurve = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, addedEvaluations));
        il.Append(Instruction.Create(OpCodes.Brfalse, sampleCurve));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, evaluate.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Callvirt, listGetItem));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(sampleCurve);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, curveEvaluate));
        il.Append(Instruction.Create(OpCodes.Stloc, value));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, evaluate.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, value));
        il.Append(Instruction.Create(OpCodes.Callvirt, listAdd));
        il.Append(Instruction.Create(OpCodes.Ldloc, value));
        il.Append(Instruction.Create(OpCodes.Ret));
        evaluate.Body.MaxStackSize = 2;
        return 1;
    }

    static int RepairBeamRendererUpdate(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.BeamRenderer")
            return 0;

        MethodDefinition update = type.Methods.SingleOrDefault(method => method.Name == "Update" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 0);
        MethodDefinition updateMesh = type.Methods.SingleOrDefault(method => method.Name == "UpdateMesh" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 0);
        TypeDefinition instanceType = type.Module.GetType("EB.Rendering.BeamRendererInstance");
        FieldDefinition instance = type.Fields.SingleOrDefault(field => field.Name == "_Instance" &&
            field.FieldType.FullName == "EB.Rendering.BeamRendererInstance" && !field.IsStatic);
        FieldDefinition startTime = type.Fields.SingleOrDefault(field => field.Name == "_StartTime" &&
            field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition elapsed = type.Fields.SingleOrDefault(field => field.Name == "_DurationSinceStartTime" &&
            field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition startVector = type.Fields.SingleOrDefault(field => field.Name == "_StartVector" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition endVector = type.Fields.SingleOrDefault(field => field.Name == "_EndVector" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition currentStartup = type.Fields.SingleOrDefault(field =>
            field.Name == "_CurrentStartupDuration" && field.FieldType.MetadataType == MetadataType.Single &&
            !field.IsStatic);
        FieldDefinition startObject = instanceType == null ? null : instanceType.Fields.SingleOrDefault(field =>
            field.Name == "StartObject" && field.FieldType.FullName == "UnityEngine.GameObject" && !field.IsStatic);
        FieldDefinition endObject = instanceType == null ? null : instanceType.Fields.SingleOrDefault(field =>
            field.Name == "EndObject" && field.FieldType.FullName == "UnityEngine.GameObject" && !field.IsStatic);
        FieldDefinition startupDuration = instanceType == null ? null : instanceType.Fields.SingleOrDefault(field =>
            field.Name == "StartUpDuration" && field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition duration = instanceType == null ? null : instanceType.Fields.SingleOrDefault(field =>
            field.Name == "Duration" && field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition looping = instanceType == null ? null : instanceType.Fields.SingleOrDefault(field =>
            field.Name == "Looping" && field.FieldType.MetadataType == MetadataType.Boolean && !field.IsStatic);
        if (update == null || !update.HasBody || updateMesh == null || instance == null || startTime == null ||
            elapsed == null || startVector == null || endVector == null || currentStartup == null ||
            startObject == null || endObject == null || startupDuration == null || duration == null || looping == null)
            throw new InvalidDataException("missing BeamRenderer.Update fields or method metadata");

        ModuleDefinition module = type.Module;
        TypeReference gameObjectType = startObject.FieldType;
        TypeReference transformType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Transform");
        TypeReference unityObjectType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Object");
        TypeReference vectorType = startVector.FieldType;
        TypeReference timeType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Time");
        TypeReference mathfType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Mathf");
        if (transformType == null || unityObjectType == null || timeType == null || mathfType == null)
            throw new InvalidDataException("missing Unity type references for BeamRenderer.Update");

        FieldReference[] vectorComponents = new[] { "x", "y", "z" }.Select(name =>
            new FieldReference(name, module.TypeSystem.Single, vectorType)).ToArray();
        MethodReference objectEquality = new MethodReference("op_Equality", module.TypeSystem.Boolean,
            unityObjectType) { HasThis = false };
        objectEquality.Parameters.Add(new ParameterDefinition(unityObjectType));
        objectEquality.Parameters.Add(new ParameterDefinition(unityObjectType));
        MethodReference getTransform = new MethodReference("get_transform", transformType, gameObjectType) {
            HasThis = true,
        };
        MethodReference getPosition = new MethodReference("get_position", vectorType, transformType) {
            HasThis = true,
        };
        MethodReference timeNow = new MethodReference("get_time", module.TypeSystem.Single, timeType) {
            HasThis = false,
        };
        MethodReference clamp01 = new MethodReference("Clamp01", module.TypeSystem.Single, mathfType) {
            HasThis = false,
        };
        clamp01.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));

        VariableDefinition active = new VariableDefinition(instance.FieldType);
        VariableDefinition startTransform = new VariableDefinition(transformType);
        VariableDefinition endTransform = new VariableDefinition(transformType);
        VariableDefinition startup = new VariableDefinition(module.TypeSystem.Single);
        update.Body.ExceptionHandlers.Clear();
        update.Body.Variables.Clear();
        update.Body.Variables.Add(active);
        update.Body.Variables.Add(startTransform);
        update.Body.Variables.Add(endTransform);
        update.Body.Variables.Add(startup);
        update.Body.Instructions.Clear();
        update.Body.InitLocals = true;
        ILProcessor il = update.Body.GetILProcessor();
        Instruction noUpdate = Instruction.Create(OpCodes.Ldc_I4_0);
        Instruction skipStartup = Instruction.Create(OpCodes.Nop);
        Instruction finishStartup = Instruction.Create(OpCodes.Nop);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, instance));
        il.Append(Instruction.Create(OpCodes.Stloc, active));
        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, startObject));
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Call, objectEquality));
        il.Append(Instruction.Create(OpCodes.Brtrue, noUpdate));
        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, endObject));
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Call, objectEquality));
        il.Append(Instruction.Create(OpCodes.Brtrue, noUpdate));

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, timeNow));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, startTime));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stfld, elapsed));

        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, startObject));
        il.Append(Instruction.Create(OpCodes.Callvirt, getTransform));
        il.Append(Instruction.Create(OpCodes.Stloc, startTransform));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, startTransform));
        il.Append(Instruction.Create(OpCodes.Callvirt, getPosition));
        il.Append(Instruction.Create(OpCodes.Stfld, startVector));

        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, endObject));
        il.Append(Instruction.Create(OpCodes.Callvirt, getTransform));
        il.Append(Instruction.Create(OpCodes.Stloc, endTransform));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, endTransform));
        il.Append(Instruction.Create(OpCodes.Callvirt, getPosition));
        il.Append(Instruction.Create(OpCodes.Stfld, endVector));

        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, startupDuration));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 0f));
        il.Append(Instruction.Create(OpCodes.Ble, skipStartup));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, elapsed));
        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, startupDuration));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Call, clamp01));
        il.Append(Instruction.Create(OpCodes.Stloc, startup));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, startup));
        il.Append(Instruction.Create(OpCodes.Stfld, currentStartup));
        il.Append(Instruction.Create(OpCodes.Ldloc, startup));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 1f));
        il.Append(Instruction.Create(OpCodes.Clt));
        il.Append(Instruction.Create(OpCodes.Brfalse, skipStartup));

        // Vector3.Lerp(start, end, t) clamps t, then performs start + (end-start)*t.
        il.Append(Instruction.Create(OpCodes.Ldloc, startup));
        il.Append(Instruction.Create(OpCodes.Call, clamp01));
        il.Append(Instruction.Create(OpCodes.Stloc, startup));
        foreach (FieldReference component in vectorComponents) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, endVector));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, startVector));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, endVector));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, startVector));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Sub));
            il.Append(Instruction.Create(OpCodes.Ldloc, startup));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, component));
        }
        il.Append(skipStartup);
        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, looping));
        il.Append(Instruction.Create(OpCodes.Brtrue, finishStartup));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, elapsed));
        il.Append(Instruction.Create(OpCodes.Ldloc, active));
        il.Append(Instruction.Create(OpCodes.Ldfld, duration));
        il.Append(Instruction.Create(OpCodes.Clt));
        il.Append(Instruction.Create(OpCodes.Brfalse, noUpdate));
        il.Append(finishStartup);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, updateMesh));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(noUpdate);
        il.Append(Instruction.Create(OpCodes.Ret));
        update.Body.MaxStackSize = 4;
        return 1;
    }

    static int RepairCrashDoAnim(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "Crash")
            return 0;

        MethodDefinition doAnim = type.Methods.SingleOrDefault(method => method.Name == "DoAnim" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 0);
        TypeDefinition animationType = type.Module.GetType("EZAnimation");
        FieldDefinition start = type.Fields.SingleOrDefault(field => field.Name == "start" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition magnitude = type.Fields.SingleOrDefault(field => field.Name == "magnitude" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition subTransform = type.Fields.SingleOrDefault(field => field.Name == "subTrans" &&
            field.FieldType.FullName == "UnityEngine.Transform" && !field.IsStatic);
        FieldDefinition tempMagnitude = type.Fields.SingleOrDefault(field => field.Name == "tempMag" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition temp = type.Fields.SingleOrDefault(field => field.Name == "temp" &&
            field.FieldType.FullName == "UnityEngine.Vector3" && !field.IsStatic);
        FieldDefinition factor = type.Fields.SingleOrDefault(field => field.Name == "factor" &&
            field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition elapsed = animationType == null ? null : animationType.Fields.SingleOrDefault(field =>
            field.Name == "timeElapsed" && field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        FieldDefinition interval = animationType == null ? null : animationType.Fields.SingleOrDefault(field =>
            field.Name == "interval" && field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
        MethodDefinition stop = animationType == null ? null : animationType.Methods.SingleOrDefault(method =>
            method.Name == "_stop" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 0);
        if (doAnim == null || !doAnim.HasBody || start == null || magnitude == null || subTransform == null ||
            tempMagnitude == null || temp == null || factor == null || elapsed == null || interval == null || stop == null)
            throw new InvalidDataException("missing Crash.DoAnim field or base animation metadata");

        ModuleDefinition module = type.Module;
        TypeReference vectorType = magnitude.FieldType;
        TypeReference objectType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Object");
        TypeReference randomType = module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Random");
        if (objectType == null || randomType == null)
            throw new InvalidDataException("missing UnityEngine.Object or Random type reference for Crash.DoAnim");

        FieldReference[] components = new[] { "x", "y", "z" }.Select(name =>
            new FieldReference(name, module.TypeSystem.Single, vectorType)).ToArray();
        MethodReference objectEquality = new MethodReference("op_Equality", module.TypeSystem.Boolean, objectType) {
            HasThis = false,
        };
        objectEquality.Parameters.Add(new ParameterDefinition(objectType));
        objectEquality.Parameters.Add(new ParameterDefinition(objectType));
        MethodReference randomRange = new MethodReference("Range", module.TypeSystem.Single, randomType) {
            HasThis = false,
        };
        randomRange.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        randomRange.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        MethodReference setLocalPosition = new MethodReference("set_localPosition", module.TypeSystem.Void,
            subTransform.FieldType) {
            HasThis = true,
        };
        setLocalPosition.Parameters.Add(new ParameterDefinition(vectorType));

        doAnim.Body.ExceptionHandlers.Clear();
        doAnim.Body.Variables.Clear();
        doAnim.Body.Instructions.Clear();
        doAnim.Body.InitLocals = false;
        ILProcessor il = doAnim.Body.GetILProcessor();
        Instruction animate = Instruction.Create(OpCodes.Nop);
        Instruction finish = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, subTransform));
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Call, objectEquality));
        il.Append(Instruction.Create(OpCodes.Brfalse, animate));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, stop));
        il.Append(Instruction.Create(OpCodes.Br, finish));

        il.Append(animate);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, elapsed));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, interval));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Stfld, factor));

        foreach (FieldReference component in components) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, tempMagnitude));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, magnitude));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, factor));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, magnitude));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Sub));
            il.Append(Instruction.Create(OpCodes.Stfld, component));
        }

        foreach (FieldReference component in components) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, temp));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, start));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, tempMagnitude));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Neg));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, tempMagnitude));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Call, randomRange));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, component));
        }

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, subTransform));
        il.Append(Instruction.Create(OpCodes.Brfalse, finish));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, subTransform));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, temp));
        il.Append(Instruction.Create(OpCodes.Callvirt, setLocalPosition));
        il.Append(finish);
        doAnim.Body.MaxStackSize = 4;
        return 1;
    }

    static int RepairBaseApiPlaceEntity(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Base.BaseAPI")
            return 0;

        MethodDefinition place = type.Methods.SingleOrDefault(method => method.Name == "PlaceEntity" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 4 && method.Parameters[0].ParameterType.FullName == "EB.Base.Base" &&
            method.Parameters[1].ParameterType.FullName == "EB.Missions.Socket" &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[3].ParameterType.FullName ==
                "EB.Action`2<System.String,System.Collections.IDictionary>");
        TypeDefinition baseType = type.Module.GetType("EB.Base.Base");
        TypeDefinition socketType = type.Module.GetType("EB.Missions.Socket");
        TypeDefinition missionType = type.Module.GetType("EB.Missions.ActiveMission");
        MethodDefinition baseTypeGetter = baseType == null ? null : baseType.Methods.SingleOrDefault(method =>
            method.Name == "get_type" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.String &&
            method.Parameters.Count == 0);
        MethodDefinition missionGetter = baseType == null ? null : baseType.Methods.SingleOrDefault(method =>
            method.Name == "get_mission" && !method.IsStatic && method.Parameters.Count == 0 &&
            method.ReturnType.FullName == "EB.Missions.ActiveMission");
        MethodDefinition missionIdGetter = missionType == null ? null : missionType.Methods.SingleOrDefault(method =>
            method.Name == "get_id" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.String &&
            method.Parameters.Count == 0);
        MethodDefinition socketPositionGetter = socketType == null ? null : socketType.Methods.SingleOrDefault(method =>
            method.Name == "get_position" && !method.IsStatic && method.ReturnType.FullName == "UnityEngine.Vector2" &&
            method.Parameters.Count == 0);
        MethodDefinition socketTypeGetter = socketType == null ? null : socketType.Methods.SingleOrDefault(method =>
            method.Name == "get_type" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.String &&
            method.Parameters.Count == 0);
        MethodDefinition socketIdGetter = socketType == null ? null : socketType.Methods.SingleOrDefault(method =>
            method.Name == "get_id" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.String &&
            method.Parameters.Count == 0);
        MethodDefinition post = type.Methods.SingleOrDefault(method => method.Name == "Post" && !method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.FullName == "EB.Sparx.Request");
        MethodDefinition service = type.Methods.SingleOrDefault(method => method.Name == "Service" && !method.IsStatic &&
            method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == "EB.Sparx.Request" &&
            place != null && method.Parameters[1].ParameterType.FullName == place.Parameters[3].ParameterType.FullName &&
            method.ReturnType.MetadataType == MetadataType.Void);
        TypeReference vectorType = socketPositionGetter == null ? null : socketPositionGetter.ReturnType;
        FieldReference vectorX = vectorType == null ? null : new FieldReference("x",
            type.Module.TypeSystem.Single, vectorType);
        FieldReference vectorY = vectorType == null ? null : new FieldReference("y",
            type.Module.TypeSystem.Single, vectorType);
        if (place == null || !place.HasBody || baseTypeGetter == null || missionGetter == null ||
            missionIdGetter == null || socketPositionGetter == null || socketTypeGetter == null ||
            socketIdGetter == null || post == null || service == null || vectorX == null || vectorY == null)
            throw new InvalidDataException("missing EB.Base.BaseAPI.PlaceEntity metadata");

        ModuleDefinition module = type.Module;
        VariableDefinition position = new VariableDefinition(vectorType);
        VariableDefinition coordinate = new VariableDefinition(module.TypeSystem.Single);
        VariableDefinition arguments = new VariableDefinition(new ArrayType(module.TypeSystem.Object));
        VariableDefinition request = new VariableDefinition(post.ReturnType);
        place.Body.ExceptionHandlers.Clear();
        place.Body.Variables.Clear();
        place.Body.Variables.Add(position);
        place.Body.Variables.Add(coordinate);
        place.Body.Variables.Add(arguments);
        place.Body.Variables.Add(request);
        place.Body.Instructions.Clear();
        place.Body.InitLocals = true;
        ILProcessor il = place.Body.GetILProcessor();
        Instruction xReady = Instruction.Create(OpCodes.Nop);
        Instruction yReady = Instruction.Create(OpCodes.Nop);

        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, socketPositionGetter));
        il.Append(Instruction.Create(OpCodes.Stloc, position));

        // Rebuild the exact seven-slot route arguments visible in the native trace.
        il.Append(Instruction.Create(OpCodes.Ldc_I4_7));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(Instruction.Create(OpCodes.Stloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, baseTypeGetter));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, socketTypeGetter));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, missionGetter));
        il.Append(Instruction.Create(OpCodes.Callvirt, missionIdGetter));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_4));
        il.Append(Instruction.Create(OpCodes.Ldloca, position));
        il.Append(Instruction.Create(OpCodes.Ldfld, vectorX));
        il.Append(Instruction.Create(OpCodes.Stloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, float.PositiveInfinity));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Brfalse, xReady));
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Neg));
        il.Append(Instruction.Create(OpCodes.Stloc, coordinate));
        il.Append(xReady);
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_5));
        il.Append(Instruction.Create(OpCodes.Ldloca, position));
        il.Append(Instruction.Create(OpCodes.Ldfld, vectorY));
        il.Append(Instruction.Create(OpCodes.Stloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, float.PositiveInfinity));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Brfalse, yReady));
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Neg));
        il.Append(Instruction.Create(OpCodes.Stloc, coordinate));
        il.Append(yReady);
        il.Append(Instruction.Create(OpCodes.Ldloc, coordinate));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_6));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, socketIdGetter));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "/base/place/{0}/{1}/{2}/{3}/{4}/{5}/{6}"));
        il.Append(Instruction.Create(OpCodes.Ldloc, arguments));
        MethodReference format = StaticMethodReference(module, module.TypeSystem.String, "Format",
            module.TypeSystem.String, module.TypeSystem.String, new ArrayType(module.TypeSystem.Object));
        il.Append(Instruction.Create(OpCodes.Call, format));
        il.Append(Instruction.Create(OpCodes.Call, post));
        il.Append(Instruction.Create(OpCodes.Stloc, request));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, request));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, place.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Call, service));
        il.Append(Instruction.Create(OpCodes.Ret));
        place.Body.MaxStackSize = 4;
        return 1;
    }

    static int RepairCopyMemberBindingEnsureTypeMatches(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.UI.DataBinding.CopyMemberBinding")
            return 0;

        MethodDefinition ensure = type.Methods.SingleOrDefault(method => method.Name == "EnsureTypeMatches" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "System.Type" &&
            IsByReferenceTo(method.Parameters[1].ParameterType, "System.Object"));
        if (ensure == null || !ensure.HasBody)
            throw new InvalidDataException("missing CopyMemberBinding.EnsureTypeMatches method");

        ModuleDefinition module = type.Module;
        TypeReference typeObject = ensure.Parameters[0].ParameterType;
        MethodReference typeIsValueType = module.ImportReference(typeof(Type).GetProperty("IsValueType").GetGetMethod());
        MethodReference typeIsAssignableFrom = module.ImportReference(typeof(Type).GetMethod(
            "IsAssignableFrom", new[] { typeof(Type) }));
        MethodReference nullableUnderlyingType = module.ImportReference(typeof(Nullable).GetMethod(
            "GetUnderlyingType", new[] { typeof(Type) }));
        MethodReference typeFromHandle = module.ImportReference(typeof(Type).GetMethod(
            "GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
        MethodReference typeEquality = module.ImportReference(typeof(Type).GetMethod(
            "op_Equality", new[] { typeof(Type), typeof(Type) }));
        MethodReference objectGetType = module.ImportReference(typeof(object).GetMethod("GetType", Type.EmptyTypes));
        MethodReference typeName = module.ImportReference(typeof(Type).GetProperty("Name").GetGetMethod());
        MethodReference createInstance = module.ImportReference(typeof(Activator).GetMethod(
            "CreateInstance", new[] { typeof(Type) }));
        FieldReference stringEmpty = module.ImportReference(typeof(string).GetField("Empty"));
        TypeReference exceptionType = module.ImportReference(typeof(Exception));
        TypeDefinition debugType = module.GetType("EB.Debug");
        MethodDefinition logError = debugType == null ? null : debugType.Methods.SingleOrDefault(method =>
            method.Name == "LogError" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 2 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
            method.Parameters[1].ParameterType.FullName == "System.Object[]");
        if (typeIsValueType == null || typeIsAssignableFrom == null || nullableUnderlyingType == null ||
            typeFromHandle == null || typeEquality == null || objectGetType == null || typeName == null ||
            createInstance == null || stringEmpty == null || logError == null)
            throw new InvalidDataException("missing CopyMemberBinding.EnsureTypeMatches runtime metadata");

        VariableDefinition actualType = new VariableDefinition(typeObject);
        VariableDefinition exception = new VariableDefinition(exceptionType);
        ensure.Body.ExceptionHandlers.Clear();
        ensure.Body.Variables.Clear();
        ensure.Body.Variables.Add(actualType);
        ensure.Body.Variables.Add(exception);
        ensure.Body.Instructions.Clear();
        ensure.Body.InitLocals = true;
        ILProcessor il = ensure.Body.GetILProcessor();
        Instruction hasValue = Instruction.Create(OpCodes.Nop);
        Instruction initialize = Instruction.Create(OpCodes.Nop);
        Instruction create = Instruction.Create(OpCodes.Nop);
        Instruction tryStart = Instruction.Create(OpCodes.Nop);
        Instruction catchStart = Instruction.Create(OpCodes.Stloc, exception);
        Instruction done = Instruction.Create(OpCodes.Ret);

        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Brtrue, hasValue));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, typeIsValueType));
        il.Append(Instruction.Create(OpCodes.Brfalse, done));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, nullableUnderlyingType));
        il.Append(Instruction.Create(OpCodes.Brtrue, done));
        il.Append(Instruction.Create(OpCodes.Br, initialize));

        il.Append(hasValue);
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Callvirt, objectGetType));
        il.Append(Instruction.Create(OpCodes.Stloc, actualType));
        il.Append(Instruction.Create(OpCodes.Ldloc, actualType));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, typeIsAssignableFrom));
        il.Append(Instruction.Create(OpCodes.Brtrue, done));

        il.Append(initialize);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldtoken, module.TypeSystem.String));
        il.Append(Instruction.Create(OpCodes.Call, typeFromHandle));
        il.Append(Instruction.Create(OpCodes.Call, typeEquality));
        il.Append(Instruction.Create(OpCodes.Brfalse, create));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldsfld, stringEmpty));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Br, done));

        il.Append(create);
        il.Append(tryStart);
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, createInstance));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        Instruction leaveTry = Instruction.Create(OpCodes.Leave, done);
        il.Append(leaveTry);
        il.Append(catchStart);
        il.Append(Instruction.Create(OpCodes.Ldstr, "{0}: Can't create an instance of type '{1}'"));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, objectGetType));
        il.Append(Instruction.Create(OpCodes.Callvirt, typeName));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Call, logError));
        il.Append(Instruction.Create(OpCodes.Leave, done));
        il.Append(done);

        ensure.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = done,
        });
        ensure.Body.MaxStackSize = 4;
        return 1;
    }

    static MethodReference StaticMethodReference(ModuleDefinition module, TypeReference owner,
        string name, TypeReference returnType, params TypeReference[] parameterTypes) {
        MethodReference method = new MethodReference(name, returnType, owner) { HasThis = false };
        foreach (TypeReference parameterType in parameterTypes)
            method.Parameters.Add(new ParameterDefinition(parameterType));
        return module.ImportReference(method);
    }

    static int RepairCameraDataLerp(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "EB.Director.CameraData")
            return 0;

        FieldDefinition projection = type.Fields.SingleOrDefault(field => field.Name == "projection");
        FieldDefinition position = type.Fields.SingleOrDefault(field => field.Name == "position");
        FieldDefinition rotation = type.Fields.SingleOrDefault(field => field.Name == "rotation");
        FieldDefinition orthographic = type.Fields.SingleOrDefault(field => field.Name == "orthographic");
        FieldDefinition orthographicSize = type.Fields.SingleOrDefault(field => field.Name == "orthographicSize");
        MethodDefinition lerp = type.Methods.SingleOrDefault(method => method.Name == "Lerp" && method.IsStatic &&
            method.ReturnType.FullName == type.FullName && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == type.FullName &&
            method.Parameters[1].ParameterType.FullName == type.FullName &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Single);
        MethodDefinition matrixLerp = type.Methods.SingleOrDefault(method => method.Name == "Lerp" && method.IsStatic &&
            projection != null && method.ReturnType.FullName == projection.FieldType.FullName && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == projection.FieldType.FullName &&
            method.Parameters[1].ParameterType.FullName == projection.FieldType.FullName &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Single);
        if (projection == null || projection.FieldType.FullName != "UnityEngine.Matrix4x4" ||
            position == null || position.FieldType.FullName != "UnityEngine.Vector3" ||
            rotation == null || rotation.FieldType.FullName != "UnityEngine.Quaternion" ||
            orthographic == null || orthographic.FieldType.MetadataType != MetadataType.Boolean ||
            orthographicSize == null || orthographicSize.FieldType.MetadataType != MetadataType.Single ||
            lerp == null || !lerp.HasBody || matrixLerp == null || !matrixLerp.HasBody)
            throw new InvalidDataException("unexpected EB.Director.CameraData.Lerp metadata");

        ModuleDefinition module = type.Module;
        TypeReference mathf = module.GetTypeReferences().SingleOrDefault(reference => reference.FullName == "UnityEngine.Mathf");
        if (mathf == null)
            throw new InvalidDataException("UnityEngine.Mathf reference is missing from CameraData assembly");
        MethodReference vectorLerp = StaticMethodReference(module, position.FieldType, "Lerp", position.FieldType,
            position.FieldType, position.FieldType, module.TypeSystem.Single);
        MethodReference quaternionLerp = StaticMethodReference(module, rotation.FieldType, "Lerp", rotation.FieldType,
            rotation.FieldType, rotation.FieldType, module.TypeSystem.Single);
        MethodReference clamp01 = StaticMethodReference(module, mathf, "Clamp01", module.TypeSystem.Single,
            module.TypeSystem.Single);
        MethodReference scalarLerp = StaticMethodReference(module, mathf, "Lerp", module.TypeSystem.Single,
            module.TypeSystem.Single, module.TypeSystem.Single, module.TypeSystem.Single);

        lerp.Body.ExceptionHandlers.Clear();
        lerp.Body.Variables.Clear();
        lerp.Body.Instructions.Clear();
        lerp.Body.InitLocals = true;
        lerp.Body.MaxStackSize = 5;
        VariableDefinition result = new VariableDefinition(type);
        lerp.Body.Variables.Add(result);
        ILProcessor il = lerp.Body.GetILProcessor();

        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, projection));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, projection));
        il.Append(Instruction.Create(OpCodes.Ldarg, lerp.Parameters[2]));
        il.Append(Instruction.Create(OpCodes.Call, matrixLerp));
        il.Append(Instruction.Create(OpCodes.Stfld, projection));

        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, position));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, position));
        il.Append(Instruction.Create(OpCodes.Ldarg, lerp.Parameters[2]));
        il.Append(Instruction.Create(OpCodes.Call, clamp01));
        il.Append(Instruction.Create(OpCodes.Call, vectorLerp));
        il.Append(Instruction.Create(OpCodes.Stfld, position));

        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, rotation));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, rotation));
        il.Append(Instruction.Create(OpCodes.Ldarg, lerp.Parameters[2]));
        il.Append(Instruction.Create(OpCodes.Call, quaternionLerp));
        il.Append(Instruction.Create(OpCodes.Stfld, rotation));

        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, orthographic));
        il.Append(Instruction.Create(OpCodes.Stfld, orthographic));

        il.Append(Instruction.Create(OpCodes.Ldloca, result));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, orthographicSize));
        il.Append(Instruction.Create(OpCodes.Ldarga_S, lerp.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, orthographicSize));
        il.Append(Instruction.Create(OpCodes.Ldarg, lerp.Parameters[2]));
        il.Append(Instruction.Create(OpCodes.Call, scalarLerp));
        il.Append(Instruction.Create(OpCodes.Stfld, orthographicSize));
        il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        return 1;
    }

    static FieldDefinition RequireUILabelField(TypeDefinition type, string name, string expectedType) {
        FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == name);
        if (field == null || field.FieldType.FullName != expectedType)
            throw new InvalidDataException("unexpected UILabel field metadata: " + name);
        return field;
    }

    static MethodReference UILabelGetter(TypeReference owner, string name) =>
        new MethodReference(name, owner, owner) { HasThis = false };

    static void StoreUILabelInt(ILProcessor il, TypeDefinition type,
        string name, int value, string expectedType) {
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldc_I4, value));
        il.Append(il.Create(OpCodes.Stfld, RequireUILabelField(type, name, expectedType)));
    }

    static void StoreUILabelFloat(ILProcessor il, TypeDefinition type, string name, float value) {
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldc_R4, value));
        il.Append(il.Create(OpCodes.Stfld, RequireUILabelField(type, name, "System.Single")));
    }

    static int RepairUILabelConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "UILabel")
            return 0;

        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            !method.IsStatic && method.Parameters.Count == 0);
        if (constructor == null) throw new InvalidDataException("missing UILabel parameterless constructor");

        ModuleDefinition module = type.Module;
        FieldDefinition colorField = RequireUILabelField(type, "mEffectColor", "UnityEngine.Color");
        FieldDefinition effectDistance = RequireUILabelField(type, "mEffectDistance", "UnityEngine.Vector2");
        FieldDefinition gradientTop = RequireUILabelField(type, "mGradientTop", "UnityEngine.Color");
        FieldDefinition gradientBottom = RequireUILabelField(type, "mGradientBottom", "UnityEngine.Color");
        FieldDefinition calculatedSize = RequireUILabelField(type, "mCalculatedSize", "UnityEngine.Vector2");
        RequireUILabelField(type, "enableTextPreProcessing", "System.Boolean");

        TypeReference colorType = colorField.FieldType;
        TypeReference vector2Type = effectDistance.FieldType;
        MethodReference colorCtor = new MethodReference(".ctor", module.TypeSystem.Void, colorType) { HasThis = true };
        colorCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        colorCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        colorCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        TypeDefinition baseType = module.GetType("UIWidget");
        MethodDefinition baseConstructor = baseType?.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            !method.IsStatic && method.Parameters.Count == 0);
        if (baseConstructor == null) throw new InvalidDataException("missing UIWidget base constructor");

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = true;
        ILProcessor il = constructor.Body.GetILProcessor();
        StoreUILabelInt(il, type, "keepCrispWhenShrunk", 1, "UILabel/Crispness");
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldstr, ""));
        il.Append(il.Create(OpCodes.Stfld, RequireUILabelField(type, "mText", "System.String")));
        StoreUILabelInt(il, type, "mFontSize", 16, "System.Int32");
        StoreUILabelInt(il, type, "mEncoding", 1, "System.Boolean");
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, UILabelGetter(colorType, "get_black")));
        il.Append(il.Create(OpCodes.Stfld, colorField));
        StoreUILabelInt(il, type, "mSymbols", 1, "NGUIText/SymbolStyle");
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, UILabelGetter(vector2Type, "get_one")));
        il.Append(il.Create(OpCodes.Stfld, effectDistance));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, UILabelGetter(colorType, "get_white")));
        il.Append(il.Create(OpCodes.Stfld, gradientTop));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, 0.7f)); il.Append(il.Create(OpCodes.Ldc_R4, 0.7f));
        il.Append(il.Create(OpCodes.Ldc_R4, 0.7f)); il.Append(il.Create(OpCodes.Newobj, colorCtor));
        il.Append(il.Create(OpCodes.Stfld, gradientBottom));
        StoreUILabelInt(il, type, "mEnableLocalization", 1, "System.Boolean");
        StoreUILabelInt(il, type, "mRtLLabelAllowsCameraFlip", 1, "System.Boolean");
        StoreUILabelInt(il, type, "mLabelForcesResizeToShrink", 1, "System.Boolean");
        StoreUILabelInt(il, type, "mMultiline", 1, "System.Boolean");
        StoreUILabelFloat(il, type, "mDensity", 1.0f);
        StoreUILabelInt(il, type, "mShouldBeProcessed", 1, "System.Boolean");
        StoreUILabelInt(il, type, "enableTextPreProcessing", 1, "System.Boolean");
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, UILabelGetter(vector2Type, "get_zero")));
        il.Append(il.Create(OpCodes.Stfld, calculatedSize));
        StoreUILabelFloat(il, type, "mScale", 1.0f);
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldstr, ""));
        il.Append(il.Create(OpCodes.Stfld, RequireUILabelField(type, "_id", "System.String")));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, baseConstructor));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static FieldDefinition RequireNGUIField(TypeDefinition type, string name, string expectedType) {
        FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == name);
        if (field == null || field.FieldType.FullName != expectedType || field.IsStatic)
            throw new InvalidDataException("unexpected NGUI field metadata: " + type.FullName + "." + name);
        return field;
    }

    static MethodDefinition RequireNGUIConstructor(TypeDefinition type) {
        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            !method.IsStatic && method.Parameters.Count == 0);
        if (constructor == null) throw new InvalidDataException("missing parameterless constructor: " + type.FullName);
        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = true;
        return constructor;
    }

    static void AppendNGUIBaseCall(ILProcessor il, TypeDefinition type) {
        MethodReference baseConstructor = new MethodReference(".ctor", type.Module.TypeSystem.Void,
            type.BaseType) { HasThis = true };
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, type.Module.ImportReference(baseConstructor)));
        il.Append(il.Create(OpCodes.Ret));
    }

    static int RepairEBRBSimulationChunkConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.EBRBSimulationChunk") return 0;
        if (type.BaseType?.FullName != "System.Object")
            throw new InvalidDataException("unexpected EBRBSimulationChunk base type");

        FieldDefinition mapping = RequireNGUIField(type, "Mapping", "System.Int32");
        FieldDefinition activateMapping = RequireNGUIField(type, "ActivateMapping", "System.Int32");
        RequireNGUIField(type, "COM", "UnityEngine.Vector3");
        RequireNGUIField(type, "IBody", "UnityEngine.Vector4");
        RequireNGUIField(type, "Size", "UnityEngine.Vector3");
        RequireNGUIField(type, "Hit", "UnityEngine.Vector3");

        MethodDefinition constructor = RequireNGUIConstructor(type);
        ModuleDefinition module = type.Module;
        MethodReference baseConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            type.BaseType) { HasThis = true };
        ILProcessor il = constructor.Body.GetILProcessor();

        // Managed C# equivalent: COM, IBody, Size, and Hit are zero-valued;
        // Mapping and ActivateMapping are -1. The zero-valued fields need no
        // stores because CLR object allocation initializes them before .ctor.
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(baseConstructor)));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Stfld, mapping));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Stfld, activateMapping));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairEBRBParticleSimulationReferenceAllocations(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.EBRBParticleSimulation/Simulation") return 0;

        FieldDefinition[] serializedReferenceFields = {
            type.Fields.SingleOrDefault(field => field.Name == "Properties"),
            type.Fields.SingleOrDefault(field => field.Name == "RenderProperties")
        };
        if (serializedReferenceFields.Any(field => field == null || field.IsStatic ||
            field.FieldType.MetadataType != MetadataType.Class))
            throw new InvalidDataException("unexpected EBRBParticleSimulation.Simulation serialized-reference fields");

        int repairs = 0;
        MethodDefinition[] constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic &&
            (method.Parameters.Count == 0 || (method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName == type.FullName))).ToArray();
        foreach (MethodDefinition constructor in constructors) {
            if (!constructor.HasBody)
                throw new InvalidDataException("missing Simulation constructor body: " + constructor.FullName);

            int constructorRepairs = 0;
            foreach (FieldDefinition field in serializedReferenceFields) {
                TypeDefinition valueType = field.FieldType.Resolve();
                MethodDefinition valueConstructor = valueType?.Methods.SingleOrDefault(method =>
                    method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
                if (valueType == null || valueConstructor == null || valueType.BaseType?.FullName != "System.Object")
                    throw new InvalidDataException("missing serialized nested-object constructor: " + field.FullName);

                foreach (Instruction store in constructor.Body.Instructions.Where(instruction =>
                    instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference target &&
                    target.Name == field.Name && target.DeclaringType.FullName == type.FullName).ToArray()) {
                    Instruction load = store.Previous;
                    VariableDefinition local = load != null &&
                        (load.OpCode == OpCodes.Ldloc || load.OpCode == OpCodes.Ldloc_S) ?
                        load.Operand as VariableDefinition : null;
                    if (local == null) continue;

                    Instruction save = load.Previous;
                    while (save != null && !((save.OpCode == OpCodes.Stloc || save.OpCode == OpCodes.Stloc_S) &&
                        Object.Equals(save.Operand, local))) save = save.Previous;
                    Instruction allocation = save?.Previous;
                    if (allocation == null || allocation.OpCode != OpCodes.Newobj ||
                        !(allocation.Operand is MethodReference allocatedConstructor)) continue;

                    if (allocatedConstructor.DeclaringType.FullName == field.FieldType.FullName &&
                        (allocatedConstructor.Parameters.Count == 0 ||
                            (allocatedConstructor.Parameters.Count == 1 &&
                                allocatedConstructor.Parameters[0].ParameterType.FullName == field.FieldType.FullName)) &&
                        local.VariableType.FullName == field.FieldType.FullName)
                        continue;
                    if (allocatedConstructor.DeclaringType.FullName != "System.Object" ||
                        allocatedConstructor.Parameters.Count != 0 || local.VariableType.FullName != "System.Object")
                        throw new InvalidDataException("unexpected Simulation " + field.Name +
                            " allocation in " + constructor.FullName + ": " + allocatedConstructor.FullName);

                    // Cpp2IL lowered the native `new Properties()`/`new RenderProperties()`
                    // calls as `new System.Object()` while retaining the typed stfld. This
                    // gives Unity's prefab deserializer a 0x10-byte object for a larger nested
                    // type. Restore the constructor and local type together so the CIL stack
                    // carries the declared serialized type all the way into stfld.
                    local.VariableType = field.FieldType;
                    allocation.Operand = type.Module.ImportReference(valueConstructor);
                    constructorRepairs++;
                    repairs++;
                }
            }
            if (constructorRepairs != 0 && constructorRepairs != serializedReferenceFields.Length)
                throw new InvalidDataException("incomplete Simulation reference-allocation repair in " +
                    constructor.FullName + ": " + constructorRepairs);
        }
        if (constructors.Length == 0)
            throw new InvalidDataException("missing EBRBParticleSimulation.Simulation constructors");
        return repairs;
    }

    static int RepairTrailConfigConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.TrailRenderer/TrailConfig") return 0;
        if (type.BaseType?.FullName != "System.Object")
            throw new InvalidDataException("unexpected TrailConfig base type");

        RequireNGUIField(type, "DistanceThreshold", "System.Single");
        RequireNGUIField(type, "CurveTension", "System.Single");
        RequireNGUIField(type, "TextureRepeat", "System.Single");
        RequireNGUIField(type, "FadeStartTime", "System.Single");
        RequireNGUIField(type, "FadeDuration", "System.Single");
        RequireNGUIField(type, "Exponent", "System.Single");
        RequireNGUIField(type, "TextureYSplit", "System.Int32");
        FieldDefinition widthCurve = RequireNGUIField(type, "WidthCurve", "UnityEngine.AnimationCurve");
        RequireNGUIField(type, "Color", "UnityEngine.Color");

        MethodDefinition constructor = RequireNGUIConstructor(type);
        ModuleDefinition module = type.Module;
        TypeReference keyframeType = module.ImportReference(
            module.GetTypeReferences().SingleOrDefault(reference => reference.FullName == "UnityEngine.Keyframe")
                ?? throw new InvalidDataException("missing UnityEngine.Keyframe reference"));
        TypeReference keyframeArrayType = new ArrayType(keyframeType);
        TypeReference animationCurveType = widthCurve.FieldType;
        MethodReference baseConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            type.BaseType) { HasThis = true };
        MethodReference keyframeConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            keyframeType) { HasThis = true };
        keyframeConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        keyframeConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        MethodReference curveConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            animationCurveType) { HasThis = true };
        curveConstructor.Parameters.Add(new ParameterDefinition(keyframeArrayType));

        VariableDefinition keyframe = new VariableDefinition(keyframeType);
        constructor.Body.Variables.Add(keyframe);
        ILProcessor il = constructor.Body.GetILProcessor();
        // C# equivalent, transcribed from the native field writes and constants:
        // DistanceThreshold=.05f; CurveTension=.75f; TextureRepeat=1f;
        // FadeStartTime=.5f; FadeDuration=.5f; Exponent=1f; TextureYSplit=1;
        // WidthCurve=new AnimationCurve([Keyframe(0,1), Keyframe(1,1)]);
        // Color=Color.white. All other fields retain CLR zero/null defaults.
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(baseConstructor)));
        StoreNGUIFloat(il, type, "DistanceThreshold", 0.05f);
        StoreNGUIFloat(il, type, "CurveTension", 0.75f);
        StoreNGUIFloat(il, type, "TextureRepeat", 1f);
        StoreNGUIFloat(il, type, "FadeStartTime", 0.5f);
        StoreNGUIFloat(il, type, "FadeDuration", 0.5f);
        StoreNGUIFloat(il, type, "Exponent", 1f);
        StoreNGUIInt(il, type, "TextureYSplit", 1, "System.Int32");

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Newarr, keyframeType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldloca, keyframe));
        il.Append(il.Create(OpCodes.Ldc_R4, 0f));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(keyframeConstructor)));
        il.Append(il.Create(OpCodes.Ldloc, keyframe));
        il.Append(il.Create(OpCodes.Stelem_Any, keyframeType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ldloca, keyframe));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(keyframeConstructor)));
        il.Append(il.Create(OpCodes.Ldloc, keyframe));
        il.Append(il.Create(OpCodes.Stelem_Any, keyframeType));
        il.Append(il.Create(OpCodes.Newobj, module.ImportReference(curveConstructor)));
        il.Append(il.Create(OpCodes.Stfld, widthCurve));
        StoreNGUIGetter(il, type, "Color", "get_white", "UnityEngine.Color");
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static FieldDefinition RequireMoveSequencerStaticField(TypeDefinition type, string name,
        string expectedType) {
        FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == name);
        if (field == null || !field.IsStatic || field.FieldType.FullName != expectedType)
            throw new InvalidDataException("unexpected MoveSequencer static field: " + name);
        return field;
    }

    static TypeReference MoveSequencerExternalType(ModuleDefinition module, string ns, string name) {
        AssemblyNameReference scope = module.AssemblyReferences.SingleOrDefault(reference =>
            reference.Name == "Assembly-CSharp-firstpass");
        if (scope == null)
            throw new InvalidDataException("missing Assembly-CSharp-firstpass reference");
        return module.ImportReference(new TypeReference(ns, name, module, scope));
    }

    static GenericInstanceType MoveSequencerClosedType(TypeReference openType,
        params TypeReference[] arguments) {
        GenericInstanceType closedType = new GenericInstanceType(openType);
        foreach (TypeReference argument in arguments) closedType.GenericArguments.Add(argument);
        return closedType;
    }

    static void StoreMoveSequencerIntArray(ILProcessor il, FieldDefinition field, params int[] values) {
        il.Append(il.Create(OpCodes.Ldc_I4, values.Length));
        il.Append(il.Create(OpCodes.Newarr, field.FieldType.Module.TypeSystem.Int32));
        for (int index = 0; index < values.Length; index++) {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            il.Append(il.Create(OpCodes.Ldc_I4, values[index]));
            il.Append(il.Create(OpCodes.Stelem_I4));
        }
        il.Append(il.Create(OpCodes.Stsfld, field));
    }

    static int RepairMoveSequencerInitializer(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "EB.MoveEditor.MoveSequencer")
            return 0;
        FieldDefinition poolField = RequireMoveSequencerStaticField(type, "kActiveMoveEventPool",
            "EB.Collections.Pool`1<EB.MoveEditor.MoveSequencer/ActiveMoveEvent>");
        FieldDefinition priorityCount = RequireMoveSequencerStaticField(type, "kNumEventPriorities",
            "System.Int32");
        FieldDefinition normalNodes = RequireMoveSequencerStaticField(type,
            "kNumNodesPerEventPriority_Normal", "System.Int32[]");
        FieldDefinition lightNodes = RequireMoveSequencerStaticField(type,
            "kNumNodesPerEventPriority_Light", "System.Int32[]");

        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".cctor" &&
            method.IsStatic && method.Parameters.Count == 0);
        TypeDefinition activeMoveEvent = type.NestedTypes.SingleOrDefault(nested => nested.Name == "ActiveMoveEvent");
        MethodDefinition factory = type.Methods.SingleOrDefault(method =>
            method.Name == "OnCreateActiveMoveEvent" && method.IsStatic && method.Parameters.Count == 0);
        if (constructor == null || activeMoveEvent == null || factory == null ||
            factory.ReturnType.FullName != activeMoveEvent.FullName)
            throw new InvalidDataException("unexpected MoveSequencer initializer metadata");

        ModuleDefinition module = type.Module;
        TypeReference functionOpen = MoveSequencerExternalType(module, "EB", "Function`1");
        TypeReference poolOpen = MoveSequencerExternalType(module, "EB.Collections", "Pool`1");
        TypeReference actionOpen = MoveSequencerExternalType(module, "EB", "Action`1");
        GenericInstanceType functionType = MoveSequencerClosedType(functionOpen, activeMoveEvent);
        GenericInstanceType poolType = MoveSequencerClosedType(poolOpen, activeMoveEvent);
        // A MemberRef on a closed generic declaring type encodes its parameter
        // types in terms of the declaring type's generic parameter. The runtime
        // substitutes ActiveMoveEvent from Pool<ActiveMoveEvent> at the callsite.
        GenericParameter poolItem = new GenericParameter("T", poolOpen);
        poolOpen.GenericParameters.Add(poolItem);
        GenericInstanceType poolFunctionParameter = MoveSequencerClosedType(functionOpen, poolItem);
        GenericInstanceType poolActionParameter = MoveSequencerClosedType(actionOpen, poolItem);

        MethodReference functionConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            functionType) { HasThis = true };
        functionConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        functionConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        MethodReference poolConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
            poolType) { HasThis = true };
        poolConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        poolConstructor.Parameters.Add(new ParameterDefinition(poolFunctionParameter));
        poolConstructor.Parameters.Add(new ParameterDefinition(poolActionParameter));

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        constructor.Body.MaxStackSize = 4;
        ILProcessor il = constructor.Body.GetILProcessor();

        // C# equivalent from the native trace and the preserved RVA data:
        // new Pool<ActiveMoveEvent>(64, OnCreateActiveMoveEvent);
        // priorities are Normal/High/Urgent (three values);
        // normal capacities are [32,16,8], light capacities are [8,2,2].
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)64));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldftn, module.ImportReference(factory)));
        il.Append(il.Create(OpCodes.Newobj, module.ImportReference(functionConstructor)));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Newobj, module.ImportReference(poolConstructor)));
        il.Append(il.Create(OpCodes.Stsfld, poolField));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Stsfld, priorityCount));
        StoreMoveSequencerIntArray(il, normalNodes, 32, 16, 8);
        StoreMoveSequencerIntArray(il, lightNodes, 8, 2, 2);
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairDynamicScrollViewConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "DynamicScrollView")
            return 0;
        FieldDefinition itemPool = type.Fields.SingleOrDefault(field => field.Name == "itemPool");
        FieldDefinition waitForPresentations = type.Fields.SingleOrDefault(field =>
            field.Name == "waitForCreatedItemPresentations");
        FieldDefinition transitionTime = type.Fields.SingleOrDefault(field => field.Name == "TransitionTime");
        FieldDefinition isEnabled = type.Fields.SingleOrDefault(field => field.Name == "isEnabled");
        FieldDefinition initialPosition = type.Fields.SingleOrDefault(field => field.Name == "initialPosition");
        FieldDefinition initialClipOffset = type.Fields.SingleOrDefault(field => field.Name == "initialClipOffset");
        FieldDefinition cachedItems = type.Fields.SingleOrDefault(field => field.Name == "_cachedItemsDict");
        FieldDefinition cachedWidgets = type.Fields.SingleOrDefault(field => field.Name == "_cachedWidgets");
        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            method.Parameters.Count == 0);
        if (itemPool == null || itemPool.FieldType.FullName != "GameObjectItemPool" ||
            waitForPresentations == null || waitForPresentations.FieldType.FullName != "System.Boolean" ||
            transitionTime == null || transitionTime.FieldType.FullName != "System.Single" ||
            isEnabled == null || isEnabled.FieldType.FullName != "System.Boolean" ||
            initialPosition == null || initialPosition.FieldType.FullName != "UnityEngine.Vector3" ||
            initialClipOffset == null || initialClipOffset.FieldType.FullName != "UnityEngine.Vector2" ||
            cachedItems == null || cachedWidgets == null || constructor == null ||
            !constructor.HasBody || type.BaseType.FullName != "UnityEngine.MonoBehaviour")
            throw new InvalidDataException("unexpected DynamicScrollView constructor metadata");

        MethodReference itemPoolConstructor = FindConstructor(constructor, "GameObjectItemPool");
        MethodReference itemsDictionaryConstructor = FindConstructor(constructor, cachedItems.FieldType.FullName);
        MethodReference widgetsDictionaryConstructor = FindConstructor(constructor, cachedWidgets.FieldType.FullName);
        MethodReference vector3Zero = constructor.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .SingleOrDefault(method => method != null && method.FullName ==
                "UnityEngine.Vector3 UnityEngine.Vector3::get_zero()");
        MethodReference vector2Zero = constructor.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .SingleOrDefault(method => method != null && method.FullName ==
                "UnityEngine.Vector2 UnityEngine.Vector2::get_zero()");
        MethodReference baseConstructor = constructor.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .SingleOrDefault(method => method != null && method.Name == ".ctor" &&
                method.DeclaringType.FullName == "UnityEngine.MonoBehaviour");
        if (itemPoolConstructor == null || itemsDictionaryConstructor == null ||
            widgetsDictionaryConstructor == null || vector3Zero == null || vector2Zero == null ||
            baseConstructor == null)
            throw new InvalidDataException("missing DynamicScrollView constructor call metadata");

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        constructor.Body.MaxStackSize = 2;
        ILProcessor il = constructor.Body.GetILProcessor();
        // Recovered C# semantics from the native 9.2 constructor trace:
        // construct the pool and caches, set the three defaults, and zero both offsets.
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, baseConstructor)));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, itemPoolConstructor)));
        il.Append(il.Create(OpCodes.Stfld, itemPool));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stfld, waitForPresentations));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, 1.0f));
        il.Append(il.Create(OpCodes.Stfld, transitionTime));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stfld, isEnabled));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, vector3Zero)));
        il.Append(il.Create(OpCodes.Stfld, initialPosition));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, vector2Zero)));
        il.Append(il.Create(OpCodes.Stfld, initialClipOffset));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, itemsDictionaryConstructor)));
        il.Append(il.Create(OpCodes.Stfld, cachedItems));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, widgetsDictionaryConstructor)));
        il.Append(il.Create(OpCodes.Stfld, cachedWidgets));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static MethodReference moduleImport(TypeDefinition owner, MethodReference method) =>
        owner.Module.ImportReference(method);

    static MethodReference FindConstructor(MethodDefinition method, string declaringType) {
        MethodReference[] constructors = method.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Newobj)
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(reference => reference != null && reference.Name == ".ctor" &&
                reference.DeclaringType.FullName == declaringType && reference.Parameters.Count == 0)
            .ToArray();
        // Recovered constructors may also allocate a capacity-sized collection
        // of the same generic type. These repairs initialize serialized fields
        // with the parameterless collection constructor; calls are repeated for
        // separate fields, so only distinct signatures are ambiguous.
        string[] signatures = constructors.Select(reference => reference.FullName)
            .Distinct(StringComparer.Ordinal).ToArray();
        return signatures.Length == 1 ? constructors[0] : null;
    }

    static MethodReference FindCall(MethodDefinition method, string name, string declaringType) {
        return method.Body.Instructions.Select(instruction => instruction.Operand as MethodReference)
            .FirstOrDefault(reference => reference != null && reference.Name == name &&
                reference.DeclaringType.FullName == declaringType);
    }

    static int RepairAveHistoryItemConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "AveHistoryItem") return 0;

        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            !method.IsStatic && method.Parameters.Count == 0);
        FieldDefinition winColor = type.Fields.SingleOrDefault(field => field.Name == "WinColor");
        FieldDefinition loseColor = type.Fields.SingleOrDefault(field => field.Name == "LoseColor");
        if (constructor == null || !constructor.HasBody || type.BaseType == null ||
            type.BaseType.FullName != "UnityEngine.MonoBehaviour" ||
            winColor == null || loseColor == null || winColor.IsStatic || loseColor.IsStatic ||
            winColor.FieldType.FullName != "UnityEngine.Color32" ||
            loseColor.FieldType.FullName != "UnityEngine.Color32")
            throw new InvalidDataException("unexpected AveHistoryItem constructor or color-field metadata");

        MethodReference colorConstructor = FindCall(constructor, ".ctor", "UnityEngine.Color32");
        MethodReference baseConstructor = FindCall(constructor, ".ctor", "UnityEngine.MonoBehaviour");
        if (colorConstructor == null || colorConstructor.Parameters.Count != 4 ||
            colorConstructor.Parameters.Any(parameter => parameter.ParameterType.MetadataType != MetadataType.Byte) ||
            baseConstructor == null || baseConstructor.Parameters.Count != 0)
            throw new InvalidDataException("missing AveHistoryItem Color32 or base constructor metadata");

        // Cpp2IL retained both 9.2 color tuples in this body, but corrupted the
        // receiver/local flow around them. The fields' use in Init identifies
        // the blue tuple as the win tint and the red tuple as the loss tint.
        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        constructor.Body.MaxStackSize = 5;
        ILProcessor il = constructor.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, baseConstructor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        foreach (int channel in new[] { 88, 200, 255, 255 })
            il.Append(Instruction.Create(OpCodes.Ldc_I4, channel));
        il.Append(Instruction.Create(OpCodes.Newobj, colorConstructor));
        il.Append(Instruction.Create(OpCodes.Stfld, winColor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        foreach (int channel in new[] { 186, 12, 47, 255 })
            il.Append(Instruction.Create(OpCodes.Ldc_I4, channel));
        il.Append(Instruction.Create(OpCodes.Newobj, colorConstructor));
        il.Append(Instruction.Create(OpCodes.Stfld, loseColor));
        il.Append(Instruction.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairHeroPortraitConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "HeroPortrait") return 0;
        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            method.Parameters.Count == 0);
        string[] requiredFields = {
            "rarityFrameSpritePrefix", "onPressScale", "textBackingDefaultPadding", "_blueprintColor",
            "_selectedColour", "_unselectedColour", "_boostColour", "_knockedOutColour", "OverlayBitMap",
            "largeBottomBorderHeight", "medBottomBorderHeight", "smallBottomBorderHeight",
            "xSmallBottomBorderHeight", "_isRendering", "_healthPercentage", "_showBoostedColours",
            "showInfoButton", "_setSizeBeforeInit", "_existingOverlays"
        };
        foreach (string name in requiredFields)
            if (!type.Fields.Any(field => field.Name == name))
                throw new InvalidDataException("missing HeroPortrait constructor field: " + name);
        FieldDefinition overlayMap = type.Fields.Single(field => field.Name == "OverlayBitMap");
        if (overlayMap.FieldType.FullName != "System.Collections.Generic.Dictionary`2<System.Int32,System.String>" ||
            constructor == null || !constructor.HasBody || type.BaseType.FullName != "UnityEngine.MonoBehaviour")
            throw new InvalidDataException("unexpected HeroPortrait constructor metadata");

        MethodReference dictionaryConstructor = FindConstructor(constructor, overlayMap.FieldType.FullName);
        MethodReference dictionaryAdd = FindCall(constructor, "Add", overlayMap.FieldType.FullName);
        MethodReference overlayListConstructor = FindConstructor(constructor,
            "System.Collections.Generic.List`1<HeroPortraitOverlay>");
        MethodReference colorConstructor = FindCall(constructor, ".ctor", "UnityEngine.Color");
        MethodReference selectedColor = FindCall(constructor, "get_white", "UnityEngine.Color");
        MethodReference boostColor = FindCall(constructor, "get_green", "UnityEngine.Color");
        MethodReference knockedOutColor = FindCall(constructor, "get_red", "UnityEngine.Color");
        MethodReference zeroOffset = FindCall(constructor, "get_zero", "UnityEngine.Vector2");
        MethodReference baseConstructor = FindCall(constructor, ".ctor", "UnityEngine.MonoBehaviour");
        if (dictionaryConstructor == null || dictionaryAdd == null || overlayListConstructor == null ||
            colorConstructor == null || selectedColor == null || boostColor == null ||
            knockedOutColor == null || zeroOffset == null || baseConstructor == null)
            throw new InvalidDataException("missing HeroPortrait constructor call metadata: " +
                String.Join(",", new[] {
                    dictionaryConstructor == null ? "dictionary .ctor" : null,
                    dictionaryAdd == null ? "dictionary Add" : null,
                    overlayListConstructor == null ? "overlay list .ctor" : null,
                    colorConstructor == null ? "Color .ctor" : null,
                    selectedColor == null ? "Color.white" : null,
                    boostColor == null ? "Color.green" : null,
                    knockedOutColor == null ? "Color.red" : null,
                    zeroOffset == null ? "Vector2.zero" : null,
                    baseConstructor == null ? "MonoBehaviour .ctor" : null
                }.Where(name => name != null).ToArray()));

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        constructor.Body.MaxStackSize = 4;
        VariableDefinition map = new VariableDefinition(overlayMap.FieldType);
        constructor.Body.Variables.Add(map);
        ILProcessor il = constructor.Body.GetILProcessor();
        // C# equivalent checked against the native 9.2 field-write trace. Values from the
        // older build are used only as a cross-check where the 9.2 trace shows packed stores.
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, baseConstructor)));
        StoreStringField(il, type, "rarityFrameSpritePrefix", "frame_portrait_rarity_");
        StoreFloatField(il, type, "onPressScale", 1.1f);
        StoreIntField(il, type, "textBackingDefaultPadding", 20);
        StoreColorField(il, type, "_blueprintColor", colorConstructor, 0.26f, 0.78f, 1f, 1f);
        StoreColorField(il, type, "_selectedColour", selectedColor);
        StoreColorField(il, type, "_unselectedColour", selectedColor);
        StoreColorField(il, type, "_boostColour", boostColor);
        StoreColorField(il, type, "_knockedOutColour", knockedOutColor);
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, dictionaryConstructor)));
        il.Append(il.Create(OpCodes.Stloc, map));
        AddOverlayMapValue(il, map, dictionaryAdd, 1, "BotName");
        AddOverlayMapValue(il, map, dictionaryAdd, 2, "Rating");
        AddOverlayMapValue(il, map, dictionaryAdd, 8, "Rarity");
        AddOverlayMapValue(il, map, dictionaryAdd, 4, "HealthBar");
        AddOverlayMapValue(il, map, dictionaryAdd, 16, "StatusIcon");
        AddOverlayMapValue(il, map, dictionaryAdd, 32, "KO");
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, map));
        il.Append(il.Create(OpCodes.Stfld, overlayMap));
        StoreIntField(il, type, "largeBottomBorderHeight", 70);
        StoreIntField(il, type, "medBottomBorderHeight", 50);
        StoreIntField(il, type, "smallBottomBorderHeight", 40);
        StoreIntField(il, type, "xSmallBottomBorderHeight", 1);
        StoreBoolField(il, type, "_isRendering", true);
        StoreFloatField(il, type, "_healthPercentage", 1f);
        StoreBoolField(il, type, "_showBoostedColours", true);
        StoreBoolField(il, type, "showInfoButton", true);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, zeroOffset)));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == "_setSizeBeforeInit")));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, overlayListConstructor)));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == "_existingOverlays")));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static void StoreStringField(ILProcessor il, TypeDefinition type, string name, string value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldstr, value));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void StoreIntField(ILProcessor il, TypeDefinition type, string name, int value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, value));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void StoreFloatField(ILProcessor il, TypeDefinition type, string name, float value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, value));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void StoreBoolField(ILProcessor il, TypeDefinition type, string name, bool value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void StoreColorField(ILProcessor il, TypeDefinition type, string name, MethodReference colorGetter) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, colorGetter)));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void StoreColorField(ILProcessor il, TypeDefinition type, string name,
        MethodReference colorConstructor, float red, float green, float blue, float alpha) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, red));
        il.Append(il.Create(OpCodes.Ldc_R4, green));
        il.Append(il.Create(OpCodes.Ldc_R4, blue));
        il.Append(il.Create(OpCodes.Ldc_R4, alpha));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, colorConstructor)));
        il.Append(il.Create(OpCodes.Stfld, type.Fields.Single(field => field.Name == name)));
    }

    static void AddOverlayMapValue(ILProcessor il, VariableDefinition map, MethodReference add,
        int key, string value) {
        il.Append(il.Create(OpCodes.Ldloc, map));
        il.Append(il.Create(OpCodes.Ldc_I4, key));
        il.Append(il.Create(OpCodes.Ldstr, value));
        il.Append(il.Create(OpCodes.Callvirt, add));
    }

    static int RepairDefaultRedeemerDisplayConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" ||
            type.FullName != "CustomRedeemerDisplays.DefaultRedeemerDisplay") return 0;
        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".ctor" &&
            method.Parameters.Count == 0);
        FieldDefinition overlayMap = type.Fields.SingleOrDefault(field => field.Name == "OverlayBitMap");
        FieldDefinition existingOverlays = type.Fields.SingleOrDefault(field => field.Name == "_existingOverlays");
        string[] requiredFields = {
            "_redeemerTag:System.String", "_customFormat:System.String", "_colorLimitText:System.Boolean",
            "_baseWidth:System.Int32", "_baseHeight:System.Int32", "_showToolTips:System.Boolean",
            "_animationWaitTime:System.Single"
        };
        foreach (string fieldSignature in requiredFields) {
            string[] parts = fieldSignature.Split(':');
            FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == parts[0]);
            if (field == null || field.FieldType.FullName != parts[1])
                throw new InvalidDataException("unexpected DefaultRedeemerDisplay field: " + fieldSignature);
        }
        if (constructor == null || !constructor.HasBody || overlayMap == null || existingOverlays == null ||
            overlayMap.FieldType.FullName != "System.Collections.Generic.Dictionary`2<System.Int32,System.String>" ||
            existingOverlays.FieldType.FullName !=
                "System.Collections.Generic.List`1<CustomRedeemerDisplays.RedeemerOverlay>" ||
            type.BaseType.FullName != "EB.Sparx.RedeemerDisplay")
            throw new InvalidDataException("unexpected DefaultRedeemerDisplay constructor metadata");

        MethodReference dictionaryConstructor = FindConstructor(constructor, overlayMap.FieldType.FullName);
        MethodReference dictionaryAdd = FindCall(constructor, "Add", overlayMap.FieldType.FullName);
        MethodReference listConstructor = FindConstructor(constructor, existingOverlays.FieldType.FullName);
        MethodReference baseConstructor = FindCall(constructor, ".ctor", "EB.Sparx.RedeemerDisplay");
        if (dictionaryConstructor == null || dictionaryAdd == null || listConstructor == null ||
            baseConstructor == null)
            throw new InvalidDataException("missing DefaultRedeemerDisplay constructor call metadata");

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        constructor.Body.MaxStackSize = 4;
        VariableDefinition map = new VariableDefinition(overlayMap.FieldType);
        constructor.Body.Variables.Add(map);
        ILProcessor il = constructor.Body.GetILProcessor();
        // C# equivalent from the 9.2 native trace: serialized defaults, the redeemer
        // overlay-key table, and the mutable overlay list.
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, baseConstructor)));
        StoreStringField(il, type, "_redeemerTag", "LRG");
        StoreStringField(il, type, "_customFormat", "");
        StoreBoolField(il, type, "_colorLimitText", true);
        StoreIntField(il, type, "_baseWidth", 90);
        StoreIntField(il, type, "_baseHeight", 90);
        StoreBoolField(il, type, "_showToolTips", true);
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, dictionaryConstructor)));
        il.Append(il.Create(OpCodes.Stloc, map));
        AddOverlayMapValue(il, map, dictionaryAdd, 1, "Name");
        AddOverlayMapValue(il, map, dictionaryAdd, 2, "Rating");
        AddOverlayMapValue(il, map, dictionaryAdd, 4, "Rarity");
        AddOverlayMapValue(il, map, dictionaryAdd, 8, "Class");
        AddOverlayMapValue(il, map, dictionaryAdd, 16, "Quantity");
        AddOverlayMapValue(il, map, dictionaryAdd, 32, "BoostText");
        AddOverlayMapValue(il, map, dictionaryAdd, 64, "TextBacking");
        AddOverlayMapValue(il, map, dictionaryAdd, 128, "IconQuantity");
        AddOverlayMapValue(il, map, dictionaryAdd, 256, "Limit");
        AddOverlayMapValue(il, map, dictionaryAdd, 512, "Glow");
        AddOverlayMapValue(il, map, dictionaryAdd, 1024, "Description");
        AddOverlayMapValue(il, map, dictionaryAdd, 2048, "Value");
        AddOverlayMapValue(il, map, dictionaryAdd, 4096, "InfoButton");
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, map));
        il.Append(il.Create(OpCodes.Stfld, overlayMap));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, moduleImport(type, listConstructor)));
        il.Append(il.Create(OpCodes.Stfld, existingOverlays));
        StoreFloatField(il, type, "_animationWaitTime", 0.2f);
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static void StoreNGUIFloat(ILProcessor il, TypeDefinition type, string name, float value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, value));
        il.Append(il.Create(OpCodes.Stfld, RequireNGUIField(type, name, "System.Single")));
    }

    static void StoreNGUIInt(ILProcessor il, TypeDefinition type, string name, int value,
        string expectedType) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, value));
        il.Append(il.Create(OpCodes.Stfld, RequireNGUIField(type, name, expectedType)));
    }

    static void StoreNGUIString(ILProcessor il, TypeDefinition type, string name, string value) {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldstr, value));
        il.Append(il.Create(OpCodes.Stfld, RequireNGUIField(type, name, "System.String")));
    }

    static MethodReference NGUIValueConstructor(TypeReference owner, params TypeReference[] parameters) {
        MethodReference constructor = new MethodReference(".ctor", owner.Module.TypeSystem.Void, owner) {
            HasThis = true
        };
        foreach (TypeReference parameter in parameters)
            constructor.Parameters.Add(new ParameterDefinition(parameter));
        return owner.Module.ImportReference(constructor);
    }

    static void StoreNGUIGetter(ILProcessor il, TypeDefinition type, string name,
        string getter, string expectedType) {
        FieldDefinition field = RequireNGUIField(type, name, expectedType);
        MethodReference method = new MethodReference(getter, field.FieldType, field.FieldType) {
            HasThis = false
        };
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, type.Module.ImportReference(method)));
        il.Append(il.Create(OpCodes.Stfld, field));
    }

    static void StoreNGUIVector(ILProcessor il, TypeDefinition type, string name,
        string expectedType, params float[] values) {
        FieldDefinition field = RequireNGUIField(type, name, expectedType);
        TypeReference vectorType = field.FieldType;
        TypeReference[] parameters = values.Select(_ => (TypeReference)type.Module.TypeSystem.Single).ToArray();
        il.Append(il.Create(OpCodes.Ldarg_0));
        foreach (float value in values) il.Append(il.Create(OpCodes.Ldc_R4, value));
        il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(vectorType, parameters)));
        il.Append(il.Create(OpCodes.Stfld, field));
    }

    // NGUI's serialized field layout and constructor defaults in 2.0.2 match
    // the retained 9.2 metadata for these controls. Rebuild the initializers
    // as authored field assignments because Cpp2IL emits invalid stack types
    // for their stripped constructor bodies; serialized 9.2 asset data remains
    // authoritative after Unity deserializes each component.
    static int RepairNGUIConstructors(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll") return 0;
        ModuleDefinition module = type.Module;
        if (type.FullName == "UIBasicSprite") {
            if (type.BaseType.FullName != "UIWidget")
                throw new InvalidDataException("unexpected UIBasicSprite base type: " + type.BaseType.FullName);
            FieldDefinition topType = RequireNGUIField(type, "topType", "UIBasicSprite/AdvancedType");
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            MethodReference baseConstructor = new MethodReference(".ctor", module.TypeSystem.Void,
                type.BaseType) { HasThis = true };
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(baseConstructor)));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, topType));
            il.Append(il.Create(OpCodes.Ret));
            return 1;
        }
        if (type.FullName == "UIParticleEmitter/EmissionSettings") {
            FieldDefinition color = RequireNGUIField(type, "ColorOverLifeTime", "UnityEngine.Gradient");
            FieldDefinition size = RequireNGUIField(type, "SizeOverLifeTime", "UnityEngine.AnimationCurve");
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            StoreNGUIString(il, type, "Name", "New Emission Settings");
            StoreNGUIInt(il, type, "MaxParticles", 25, "System.Int32");
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(color.FieldType)));
            il.Append(il.Create(OpCodes.Stfld, color));
            StoreNGUIFloat(il, type, "SpawnRate", 0.04f);
            StoreNGUIInt(il, type, "ConstrainDimensions", 1, "System.Boolean");
            StoreNGUIVector(il, type, "SizeMin", "UnityEngine.Vector2", 20f, 20f);
            StoreNGUIVector(il, type, "SizeMax", "UnityEngine.Vector2", 20f, 20f);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(size.FieldType)));
            il.Append(il.Create(OpCodes.Stfld, size));
            StoreNGUIVector(il, type, "EmissionAreaSize", "UnityEngine.Vector2", 10f, 10f);
            StoreNGUIVector(il, type, "VelMin", "UnityEngine.Vector2", -1f, -1f);
            StoreNGUIVector(il, type, "VelMax", "UnityEngine.Vector2", 1f, 1f);
            StoreNGUIFloat(il, type, "LifeTimeMin", 1f);
            StoreNGUIFloat(il, type, "LifeTimeMax", 1f);
            AppendNGUIBaseCall(il, type);
            return 1;
        }
        if (type.FullName == "UIButtonColor") {
            RequireNGUIField(type, "hover", "UnityEngine.Color");
            RequireNGUIField(type, "pressed", "UnityEngine.Color");
            RequireNGUIField(type, "disabledColor", "UnityEngine.Color");
            RequireNGUIField(type, "duration", "System.Single");
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            StoreNGUIVector(il, type, "hover", "UnityEngine.Color", 0.8823529f, 0.7843137f, 0.5882353f, 1f);
            StoreNGUIVector(il, type, "pressed", "UnityEngine.Color", 0.7176471f, 0.6392157f, 0.4823529f, 1f);
            StoreNGUIGetter(il, type, "disabledColor", "get_grey", "UnityEngine.Color");
            StoreNGUIFloat(il, type, "duration", 0.2f);
            AppendNGUIBaseCall(il, type);
            return 1;
        }
        if (type.FullName == "UIButtonScale" || type.FullName == "UIButtonOffset") {
            RequireNGUIField(type, "hover", "UnityEngine.Vector3");
            RequireNGUIField(type, "pressed", "UnityEngine.Vector3");
            RequireNGUIField(type, "duration", "System.Single");
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            if (type.FullName == "UIButtonScale") {
                StoreNGUIVector(il, type, "hover", "UnityEngine.Vector3", 1.1f, 1.1f, 1.1f);
                StoreNGUIVector(il, type, "pressed", "UnityEngine.Vector3", 1.05f, 1.05f, 1.05f);
            } else {
                StoreNGUIGetter(il, type, "hover", "get_zero", "UnityEngine.Vector3");
                StoreNGUIVector(il, type, "pressed", "UnityEngine.Vector3", 2f, -2f, 0f);
            }
            StoreNGUIFloat(il, type, "duration", 0.2f);
            AppendNGUIBaseCall(il, type);
            return 1;
        }
        if (type.FullName == "UITweener") {
            FieldDefinition curveField = RequireNGUIField(type, "animationCurve", "UnityEngine.AnimationCurve");
            RequireNGUIField(type, "ignoreTimeScale", "System.Boolean");
            RequireNGUIField(type, "duration", "System.Single");
            FieldDefinition eventField = RequireNGUIField(type, "onFinished",
                "System.Collections.Generic.List`1<EventDelegate>");
            RequireNGUIField(type, "mAmountPerDelta", "System.Single");
            TypeReference keyframe = new TypeReference("UnityEngine", "Keyframe", module,
                curveField.FieldType.Scope) { IsValueType = true };
            MethodReference keyframeConstructor = NGUIValueConstructor(keyframe,
                module.TypeSystem.Single, module.TypeSystem.Single,
                module.TypeSystem.Single, module.TypeSystem.Single);
            ArrayType keyframeArray = new ArrayType(keyframe);
            MethodReference curveConstructor = NGUIValueConstructor(curveField.FieldType, keyframeArray);
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_2));
            il.Append(il.Create(OpCodes.Newarr, keyframe));
            float[][] keys = { new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f, 0f } };
            for (int index = 0; index < keys.Length; index++) {
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Ldc_I4, index));
                il.Append(il.Create(OpCodes.Ldelema, keyframe));
                foreach (float value in keys[index]) il.Append(il.Create(OpCodes.Ldc_R4, value));
                il.Append(il.Create(OpCodes.Newobj, keyframeConstructor));
                il.Append(il.Create(OpCodes.Stobj, keyframe));
            }
            il.Append(il.Create(OpCodes.Newobj, curveConstructor));
            il.Append(il.Create(OpCodes.Stfld, curveField));
            StoreNGUIInt(il, type, "ignoreTimeScale", 1, "System.Boolean");
            StoreNGUIFloat(il, type, "duration", 1f);
            TypeReference listType = eventField.FieldType;
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(listType)));
            il.Append(il.Create(OpCodes.Stfld, eventField));
            StoreNGUIFloat(il, type, "mAmountPerDelta", 1000f);
            AppendNGUIBaseCall(il, type);
            return 1;
        }
        if (type.FullName == "UIPanel") {
            RequireNGUIField(type, "showInPanelTool", "System.Boolean");
            RequireNGUIField(type, "cullWhileDragging", "System.Boolean");
            RequireNGUIField(type, "softBorderPadding", "System.Boolean");
            RequireNGUIField(type, "startingRenderQueue", "System.Int32");
            FieldDefinition widgets = RequireNGUIField(type, "widgets", "BetterList`1<UIWidget>");
            FieldDefinition drawCalls = RequireNGUIField(type, "drawCalls", "BetterList`1<UIDrawCall>");
            RequireNGUIField(type, "worldToLocal", "UnityEngine.Matrix4x4");
            RequireNGUIField(type, "drawCallClipRange", "UnityEngine.Vector4");
            RequireNGUIField(type, "mAlpha", "System.Single");
            RequireNGUIField(type, "mClipRange", "UnityEngine.Vector4");
            RequireNGUIField(type, "mClipSoftness", "UnityEngine.Vector2");
            RequireNGUIField(type, "mClipOffset", "UnityEngine.Vector2");
            RequireNGUIField(type, "mMatrixFrame", "System.Int32");
            RequireNGUIField(type, "mLayer", "System.Int32");
            RequireNGUIField(type, "mMin", "UnityEngine.Vector2");
            RequireNGUIField(type, "mMax", "UnityEngine.Vector2");
            RequireNGUIField(type, "viewSize", "UnityEngine.Vector2");
            ILProcessor il = RequireNGUIConstructor(type).Body.GetILProcessor();
            StoreNGUIInt(il, type, "showInPanelTool", 1, "System.Boolean");
            StoreNGUIInt(il, type, "cullWhileDragging", 1, "System.Boolean");
            StoreNGUIInt(il, type, "softBorderPadding", 1, "System.Boolean");
            StoreNGUIInt(il, type, "startingRenderQueue", 3000, "System.Int32");
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(widgets.FieldType)));
            il.Append(il.Create(OpCodes.Stfld, widgets));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(drawCalls.FieldType)));
            il.Append(il.Create(OpCodes.Stfld, drawCalls));
            StoreNGUIGetter(il, type, "worldToLocal", "get_identity", "UnityEngine.Matrix4x4");
            StoreNGUIVector(il, type, "drawCallClipRange", "UnityEngine.Vector4", 0f, 0f, 1f, 1f);
            StoreNGUIFloat(il, type, "mAlpha", 1f);
            StoreNGUIVector(il, type, "mClipRange", "UnityEngine.Vector4", 0f, 0f, 300f, 200f);
            StoreNGUIVector(il, type, "mClipSoftness", "UnityEngine.Vector2", 4f, 4f);
            StoreNGUIGetter(il, type, "mClipOffset", "get_zero", "UnityEngine.Vector2");
            StoreNGUIInt(il, type, "mMatrixFrame", -1, "System.Int32");
            StoreNGUIInt(il, type, "mLayer", -1, "System.Int32");
            StoreNGUIGetter(il, type, "mMin", "get_zero", "UnityEngine.Vector2");
            StoreNGUIGetter(il, type, "mMax", "get_zero", "UnityEngine.Vector2");
            StoreNGUIGetter(il, type, "viewSize", "get_zero", "UnityEngine.Vector2");
            AppendNGUIBaseCall(il, type);
            return 1;
        }
        return 0;
    }

    static TypeReference UnityEngineType(ModuleDefinition module, string name) {
        AssemblyNameReference scope = module.AssemblyReferences.SingleOrDefault(reference =>
            reference.Name == "UnityEngine.CoreModule" || reference.Name == "UnityEngine");
        if (scope == null) throw new InvalidDataException("missing UnityEngine reference for " + name);
        return new TypeReference("UnityEngine", name, module, scope);
    }

    static MethodReference UnityStaticMethod(ModuleDefinition module, string ownerName,
        string name, TypeReference result, params TypeReference[] parameters) {
        TypeReference owner = UnityEngineType(module, ownerName);
        MethodReference method = new MethodReference(name, result, owner) { HasThis = false };
        foreach (TypeReference parameter in parameters)
            method.Parameters.Add(new ParameterDefinition(parameter));
        return module.ImportReference(method);
    }

    static MethodReference UnityInstanceGetter(ModuleDefinition module, string ownerName,
        string name, TypeReference result) {
        TypeReference owner = UnityEngineType(module, ownerName);
        return module.ImportReference(new MethodReference(name, result, owner) { HasThis = true });
    }

    static FieldDefinition RequireNGUIStaticField(TypeDefinition type, string name, string expectedType) {
        FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == name);
        if (field == null || field.FieldType.FullName != expectedType || !field.IsStatic)
            throw new InvalidDataException("unexpected NGUI static field metadata: " + type.FullName + "." + name);
        return field;
    }

    static int RepairNGUIToolsInitializer(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" || type.FullName != "NGUITools") return 0;
        ModuleDefinition module = type.Module;
        MethodDefinition constructor = type.Methods.SingleOrDefault(method => method.Name == ".cctor" &&
            method.IsStatic && method.Parameters.Count == 0);
        FieldDefinition loaded = RequireNGUIStaticField(type, "mLoaded", "System.Boolean");
        FieldDefinition volume = RequireNGUIStaticField(type, "mGlobalVolume", "System.Single");
        FieldDefinition timestamp = RequireNGUIStaticField(type, "mLastTimestamp", "System.Single");
        FieldDefinition cameras = RequireNGUIStaticField(type, "camerasByLayer",
            "System.Collections.Generic.Dictionary`2<System.Int32,UnityEngine.Camera>");
        FieldDefinition sides = RequireNGUIStaticField(type, "mSides", "UnityEngine.Vector3[]");
        FieldDefinition keys = RequireNGUIStaticField(type, "keys", "UnityEngine.KeyCode[]");
        if (constructor == null || !(sides.FieldType is ArrayType sidesArray) ||
            !(keys.FieldType is ArrayType keysArray) || keysArray.ElementType.FullName != "UnityEngine.KeyCode")
            throw new InvalidDataException("unexpected NGUITools static initializer metadata");

        constructor.Body.ExceptionHandlers.Clear();
        constructor.Body.Variables.Clear();
        constructor.Body.Instructions.Clear();
        constructor.Body.InitLocals = false;
        ILProcessor il = constructor.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stsfld, loaded));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Stsfld, volume));
        il.Append(il.Create(OpCodes.Ldc_R4, 0f));
        il.Append(il.Create(OpCodes.Stsfld, timestamp));
        il.Append(il.Create(OpCodes.Newobj, NGUIValueConstructor(cameras.FieldType)));
        il.Append(il.Create(OpCodes.Stsfld, cameras));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Newarr, sidesArray.ElementType));
        il.Append(il.Create(OpCodes.Stsfld, sides));

        // NGUI's verified defaults map the physical key codes to captions.
        int[] keyCodes = {
            8, 9, 12, 13, 19, 27, 32, 33, 34, 35, 36, 38, 39, 40, 41, 42,
            43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
            59, 60, 61, 62, 63, 64, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100,
            101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116,
            117, 118, 119, 120, 121, 122, 127, 256, 257, 258, 259, 260, 261, 262, 263, 264,
            265, 266, 267, 268, 269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279, 280,
            281, 282, 283, 284, 285, 286, 287, 288, 289, 290, 291, 292, 293, 294, 295, 296,
            300, 301, 302, 303, 304, 305, 306, 307, 308, 326, 327, 328, 329, 330, 331, 332,
            333, 334, 335, 336, 337, 338, 339, 340, 341, 342, 343, 344, 345, 346, 347, 348, 349
        };
        il.Append(il.Create(OpCodes.Ldc_I4, keyCodes.Length));
        il.Append(il.Create(OpCodes.Newarr, keysArray.ElementType));
        for (int index = 0; index < keyCodes.Length; index++) {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            il.Append(il.Create(OpCodes.Ldc_I4, keyCodes[index]));
            il.Append(il.Create(OpCodes.Stelem_I4));
        }
        il.Append(il.Create(OpCodes.Stsfld, keys));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairNGUIValidation(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll") return 0;
        ModuleDefinition module = type.Module;
        if (type.FullName == "NGUITools") {
            int repaired = 0;
            foreach (MethodDefinition callback in type.Methods.Where(method => method.Name == "GetActive" &&
                method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
                method.Parameters.Count == 1).ToArray()) {
                string argumentType = callback.Parameters[0].ParameterType.FullName;
                if (argumentType != "UnityEngine.Behaviour" && argumentType != "UnityEngine.GameObject")
                    continue;

                callback.Body.ExceptionHandlers.Clear();
                callback.Body.Variables.Clear();
                callback.Body.Instructions.Clear();
                callback.Body.InitLocals = false;
                ILProcessor il = callback.Body.GetILProcessor();
                Instruction inactive = il.Create(OpCodes.Ldc_I4_0);
                Instruction done = il.Create(OpCodes.Ret);
                TypeReference unityObject = UnityEngineType(module, "Object");
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, UnityStaticMethod(module, "Object", "op_Implicit",
                    module.TypeSystem.Boolean, unityObject)));
                il.Append(il.Create(OpCodes.Brfalse, inactive));
                if (argumentType == "UnityEngine.Behaviour") {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Callvirt, UnityInstanceGetter(module, "Behaviour",
                        "get_enabled", module.TypeSystem.Boolean)));
                    il.Append(il.Create(OpCodes.Brfalse, inactive));
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Callvirt, UnityInstanceGetter(module, "Component",
                        "get_gameObject", UnityEngineType(module, "GameObject"))));
                } else {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }
                il.Append(il.Create(OpCodes.Callvirt, UnityInstanceGetter(module, "GameObject",
                    "get_activeInHierarchy", module.TypeSystem.Boolean)));
                il.Append(il.Create(OpCodes.Br, done));
                il.Append(inactive);
                il.Append(done);
                repaired++;
            }
            if (repaired != 2)
                throw new InvalidDataException("expected both NGUI GetActive overloads, repaired " + repaired);
            return repaired;
        }
        if (type.FullName == "UIGrid" || type.FullName == "UITable") {
            MethodDefinition callback = type.Methods.SingleOrDefault(method => method.Name == "OnValidate" &&
                !method.IsStatic && method.Parameters.Count == 0);
            MethodDefinition reposition = type.Methods.SingleOrDefault(method => method.Name == "Reposition" &&
                !method.IsStatic && method.Parameters.Count == 0);
            TypeDefinition nguiTools = module.GetType("NGUITools");
            MethodDefinition getActive = nguiTools?.Methods.SingleOrDefault(method => method.Name == "GetActive" &&
                method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
                method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "UnityEngine.Behaviour");
            if (callback == null || reposition == null || getActive == null)
                throw new InvalidDataException("unexpected NGUI validation metadata: " + type.FullName);

            callback.Body.ExceptionHandlers.Clear();
            callback.Body.Variables.Clear();
            callback.Body.Instructions.Clear();
            callback.Body.InitLocals = true;
            ILProcessor il = callback.Body.GetILProcessor();
            Instruction ret = il.Create(OpCodes.Ret);
            TypeReference application = UnityEngineType(module, "Application");
            MethodReference isPlaying = module.ImportReference(new MethodReference("get_isPlaying",
                module.TypeSystem.Boolean, application) { HasThis = false });
            il.Append(il.Create(OpCodes.Call, isPlaying));
            il.Append(il.Create(OpCodes.Brtrue, ret));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, getActive));
            il.Append(il.Create(OpCodes.Brfalse, ret));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, reposition));
            il.Append(ret);
            return 1;
        }

        if (type.FullName == "UIProgressBar") {
            MethodDefinition callback = type.Methods.SingleOrDefault(method => method.Name == "OnValidate" &&
                !method.IsStatic && method.Parameters.Count == 0);
            MethodDefinition upgrade = type.Methods.SingleOrDefault(method => method.Name == "Upgrade" &&
                !method.IsStatic && method.Parameters.Count == 0);
            MethodDefinition forceUpdate = type.Methods.SingleOrDefault(method => method.Name == "ForceUpdate" &&
                !method.IsStatic && method.Parameters.Count == 0);
            TypeDefinition nguiTools = module.GetType("NGUITools");
            MethodDefinition getActive = nguiTools?.Methods.SingleOrDefault(method => method.Name == "GetActive" &&
                method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
                method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "UnityEngine.Behaviour");
            FieldDefinition value = type.Fields.SingleOrDefault(field => field.Name == "mValue" &&
                field.FieldType.MetadataType == MetadataType.Single && !field.IsStatic);
            FieldDefinition dirty = type.Fields.SingleOrDefault(field => field.Name == "mIsDirty" &&
                field.FieldType.MetadataType == MetadataType.Boolean && !field.IsStatic);
            FieldDefinition steps = type.Fields.SingleOrDefault(field => field.Name == "numberOfSteps" &&
                field.FieldType.MetadataType == MetadataType.Int32 && !field.IsStatic);
            if (callback == null || upgrade == null || forceUpdate == null || getActive == null ||
                value == null || dirty == null || steps == null)
                throw new InvalidDataException("unexpected UIProgressBar validation metadata");

            callback.Body.ExceptionHandlers.Clear();
            callback.Body.Variables.Clear();
            callback.Body.Instructions.Clear();
            callback.Body.InitLocals = true;
            ILProcessor il = callback.Body.GetILProcessor();
            Instruction inactive = il.Create(OpCodes.Nop);
            Instruction done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, getActive));
            il.Append(il.Create(OpCodes.Brfalse, inactive));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, upgrade));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, dirty));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, value));
            il.Append(il.Create(OpCodes.Call, UnityStaticMethod(module, "Mathf", "Clamp01",
                module.TypeSystem.Single, module.TypeSystem.Single)));
            il.Append(il.Create(OpCodes.Stfld, value));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, steps));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)20));
            il.Append(il.Create(OpCodes.Call, UnityStaticMethod(module, "Mathf", "Clamp",
                module.TypeSystem.Int32, module.TypeSystem.Int32, module.TypeSystem.Int32,
                module.TypeSystem.Int32)));
            il.Append(il.Create(OpCodes.Stfld, steps));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, forceUpdate));
            il.Append(il.Create(OpCodes.Br, done));
            il.Append(inactive);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, value));
            il.Append(il.Create(OpCodes.Call, UnityStaticMethod(module, "Mathf", "Clamp01",
                module.TypeSystem.Single, module.TypeSystem.Single)));
            il.Append(il.Create(OpCodes.Stfld, value));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, steps));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)20));
            il.Append(il.Create(OpCodes.Call, UnityStaticMethod(module, "Mathf", "Clamp",
                module.TypeSystem.Int32, module.TypeSystem.Int32, module.TypeSystem.Int32,
                module.TypeSystem.Int32)));
            il.Append(il.Create(OpCodes.Stfld, steps));
            il.Append(done);
            return 1;
        }
        return 0;
    }

    static bool ContainsOwnerlessGenericParameter(TypeReference reference) {
        if (reference == null) return false;
        if (reference is GenericParameter parameter) return parameter.Owner == null;
        if (reference is GenericInstanceType instance)
            return ContainsOwnerlessGenericParameter(instance.ElementType) ||
                instance.GenericArguments.Any(ContainsOwnerlessGenericParameter);
        if (reference is TypeSpecification specification)
            return ContainsOwnerlessGenericParameter(specification.ElementType);
        return false;
    }

    static TypeReference RebindOwnerlessILType(TypeReference reference, MethodDefinition method) {
        if (reference == null) return null;
        if (reference is GenericParameter parameter && parameter.Owner == null) {
            if (parameter.Type == GenericParameterType.Type &&
                parameter.Position >= 0 && parameter.Position < method.DeclaringType.GenericParameters.Count)
                return method.DeclaringType.GenericParameters[parameter.Position];
            if (parameter.Type == GenericParameterType.Method &&
                parameter.Position >= 0 && parameter.Position < method.GenericParameters.Count)
                return method.GenericParameters[parameter.Position];
            // Cpp2IL emits some free generic tokens in nongeneric methods. Such
            // tokens have no valid runtime owner; object is the only safe
            // concrete token type that lets IL2CPP name the remaining method.
            return method.Module.TypeSystem.Object;
        }
        if (reference is GenericInstanceType instance) {
            GenericInstanceType rebound = new GenericInstanceType(
                RebindOwnerlessILType(instance.ElementType, method));
            foreach (TypeReference argument in instance.GenericArguments)
                rebound.GenericArguments.Add(RebindOwnerlessILType(argument, method));
            return rebound;
        }
        if (reference is ArrayType array)
            return new ArrayType(RebindOwnerlessILType(array.ElementType, method), array.Rank);
        if (reference is ByReferenceType byReference)
            return new ByReferenceType(RebindOwnerlessILType(byReference.ElementType, method));
        if (reference is PointerType pointer)
            return new PointerType(RebindOwnerlessILType(pointer.ElementType, method));
        if (reference is PinnedType pinned)
            return new PinnedType(RebindOwnerlessILType(pinned.ElementType, method));
        if (reference is SentinelType sentinel)
            return new SentinelType(RebindOwnerlessILType(sentinel.ElementType, method));
        if (reference is OptionalModifierType optional)
            return new OptionalModifierType(RebindOwnerlessILType(optional.ModifierType, method),
                RebindOwnerlessILType(optional.ElementType, method));
        if (reference is RequiredModifierType required)
            return new RequiredModifierType(RebindOwnerlessILType(required.ModifierType, method),
                RebindOwnerlessILType(required.ElementType, method));
        return reference;
    }

    static int RepairOwnerlessILTypeReferences(MethodDefinition method) {
        if (!method.HasBody) return 0;
        int repaired = 0;
        foreach (Instruction instruction in method.Body.Instructions) {
            if (!(instruction.Operand is TypeReference reference) ||
                !ContainsOwnerlessGenericParameter(reference)) continue;
            instruction.Operand = method.Module.ImportReference(RebindOwnerlessILType(reference, method));
            repaired++;
        }
        return repaired;
    }

    static FieldDefinition FindInstanceField(TypeDefinition type, string name) {
        for (TypeDefinition current = type; current != null; current = current.BaseType == null
            ? null : current.BaseType.Resolve()) {
            FieldDefinition field = current.Fields.SingleOrDefault(candidate => candidate.Name == name);
            if (field != null) return field;
        }
        return null;
    }

    static int RepairVectorAnimationLoopReset(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            !new[] { "AnimatePosition", "AnimateRotation", "AnimateScale" }.Contains(type.FullName))
            return 0;

        FieldDefinition mode = FindInstanceField(type, "m_mode");
        FieldDefinition restart = FindInstanceField(type, "restartOnRepeat");
        FieldDefinition start = type.Fields.SingleOrDefault(field => field.Name == "start");
        FieldDefinition end = type.Fields.SingleOrDefault(field => field.Name == "end");
        FieldDefinition delta = type.Fields.SingleOrDefault(field => field.Name == "delta");
        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == "LoopReset" &&
            !candidate.IsStatic && candidate.Parameters.Count == 0 &&
            candidate.ReturnType.MetadataType == MetadataType.Void);
        if (mode == null || mode.FieldType.FullName != "EZAnimation/ANIM_MODE" || mode.IsStatic ||
            restart == null || restart.FieldType.MetadataType != MetadataType.Boolean || restart.IsStatic ||
            new[] { start, end, delta }.Any(field => field == null || field.IsStatic ||
                field.FieldType.FullName != "UnityEngine.Vector3") || method == null)
            throw new InvalidDataException("unexpected vector animation LoopReset metadata: " + type.FullName);

        TypeReference vector = end.FieldType;
        MethodReference add = new MethodReference("op_Addition", vector, vector) { HasThis = false };
        add.Parameters.Add(new ParameterDefinition(vector));
        add.Parameters.Add(new ParameterDefinition(vector));

        // Native 9.2 ARM64 traces show: return unless mode is Once and restartOnRepeat
        // is false; then copy end to start and add delta to end. The generated CIL
        // instead emitted struct `add` plus invalid unmanaged-memory placeholders.
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = true;
        ILProcessor il = method.Body.GetILProcessor();
        Instruction done = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, mode));
        il.Append(il.Create(OpCodes.Brtrue, done));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, restart));
        il.Append(il.Create(OpCodes.Brtrue, done));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, end));
        il.Append(il.Create(OpCodes.Stfld, start));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, end));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, delta));
        il.Append(il.Create(OpCodes.Call, add));
        il.Append(il.Create(OpCodes.Stfld, end));
        il.Append(done);
        method.Body.MaxStackSize = 3;
        return 1;
    }

    static bool RepairResolvedValueType(TypeReference reference) {
        if (reference == null) return false;
        bool changed = false;
        if (reference is TypeSpecification specification)
            changed |= RepairResolvedValueType(specification.ElementType);
        if (reference is GenericInstanceType instance)
            foreach (TypeReference argument in instance.GenericArguments)
                changed |= RepairResolvedValueType(argument);
        if (reference is OptionalModifierType optional)
            changed |= RepairResolvedValueType(optional.ModifierType);
        if (reference is RequiredModifierType required)
            changed |= RepairResolvedValueType(required.ModifierType);

        if (reference is TypeDefinition) return changed;
        try {
            TypeDefinition definition = reference.Resolve();
            if (!(reference is TypeSpecification) && definition != null &&
                definition.IsValueType && !reference.IsValueType) {
                reference.IsValueType = true;
                changed = true;
            }
        } catch (AssemblyResolutionException) { }
          catch (ResolutionException) { }
        return changed;
    }

    static bool RepairMethodReferenceValueTypes(MethodReference method) {
        if (method == null) return false;
        bool changed = RepairResolvedValueType(method.DeclaringType) |
            RepairResolvedValueType(method.ReturnType);
        foreach (ParameterDefinition parameter in method.Parameters)
            changed |= RepairResolvedValueType(parameter.ParameterType);
        if (method is GenericInstanceMethod generic)
            foreach (TypeReference argument in generic.GenericArguments)
                changed |= RepairResolvedValueType(argument);
        return changed;
    }

    static bool RepairFieldReferenceValueTypes(FieldReference field) {
        if (field == null) return false;
        return RepairResolvedValueType(field.DeclaringType) |
            RepairResolvedValueType(field.FieldType);
    }

    static int RepairAssemblyValueTypeFlags(AssemblyDefinition assembly) {
        int changed = 0;
        foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types)) {
            if (RepairResolvedValueType(type.BaseType)) changed++;
            foreach (InterfaceImplementation implementation in type.Interfaces)
                if (RepairResolvedValueType(implementation.InterfaceType)) changed++;
            foreach (FieldDefinition field in type.Fields)
                if (RepairResolvedValueType(field.FieldType)) changed++;
            foreach (PropertyDefinition property in type.Properties) {
                if (RepairResolvedValueType(property.PropertyType)) changed++;
                foreach (ParameterDefinition parameter in property.Parameters)
                    if (RepairResolvedValueType(parameter.ParameterType)) changed++;
            }
            foreach (EventDefinition @event in type.Events)
                if (RepairResolvedValueType(@event.EventType)) changed++;
            foreach (MethodDefinition method in type.Methods) {
                if (RepairResolvedValueType(method.ReturnType)) changed++;
                foreach (ParameterDefinition parameter in method.Parameters)
                    if (RepairResolvedValueType(parameter.ParameterType)) changed++;
                if (!method.HasBody) continue;
                foreach (VariableDefinition variable in method.Body.Variables)
                    if (RepairResolvedValueType(variable.VariableType)) changed++;
                foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
                    if (RepairResolvedValueType(handler.CatchType)) changed++;
                foreach (Instruction instruction in method.Body.Instructions) {
                    if (instruction.Operand is TypeReference reference && RepairResolvedValueType(reference)) changed++;
                    else if (instruction.Operand is MethodReference called && RepairMethodReferenceValueTypes(called)) changed++;
                    else if (instruction.Operand is FieldReference referenced && RepairFieldReferenceValueTypes(referenced)) changed++;
                    else if (instruction.Operand is CallSite callSite) {
                        if (RepairResolvedValueType(callSite.ReturnType)) changed++;
                        foreach (ParameterDefinition parameter in callSite.Parameters)
                            if (RepairResolvedValueType(parameter.ParameterType)) changed++;
                    }
                }
            }
        }
        return changed;
    }

    static void SetKnownOwnerlessLocal(MethodDefinition method, int index,
        TypeReference expected, ref int repaired) {
        if (!method.HasBody || index < 0 || index >= method.Body.Variables.Count)
            throw new InvalidDataException("unexpected local layout in " + method.FullName);
        VariableDefinition local = method.Body.Variables[index];
        if (ContainsOwnerlessGenericParameter(local.VariableType)) {
            local.VariableType = method.Module.ImportReference(expected);
            repaired++;
        } else if (local.VariableType.FullName != expected.FullName) {
            throw new InvalidDataException("expected ownerless generic local in " + method.FullName + " V_" + index);
        }
    }

    static void ReplaceKnownOwnerlessCast(MethodDefinition method, int offset,
        TypeReference expected, ref int repaired) {
        Instruction instruction = method.Body.Instructions.SingleOrDefault(item => item.Offset == offset);
        if (instruction == null || instruction.OpCode != OpCodes.Castclass ||
            !(instruction.Operand is TypeReference)) {
            Instruction[] ownerlessCasts = method.Body.Instructions.Where(item =>
                item.OpCode == OpCodes.Castclass && item.Operand is TypeReference candidateType &&
                ContainsOwnerlessGenericParameter(candidateType)).ToArray();
            if (ownerlessCasts.Length == 0) return; // The updated Cpp2IL output may have eliminated this stale offset/cast.
            if (ownerlessCasts.Length != 1)
                throw new InvalidDataException("ambiguous ownerless casts in " + method.FullName);
            instruction = ownerlessCasts[0];
        }
        TypeReference actual = instruction.Operand as TypeReference;
        if (actual == null)
            throw new InvalidDataException("expected ownerless cast in " + method.FullName + " IL_" + offset.ToString("x4"));
        if (ContainsOwnerlessGenericParameter(actual)) {
            instruction.Operand = method.Module.ImportReference(expected);
            repaired++;
        } else if (actual.FullName != expected.FullName) {
            throw new InvalidDataException("unexpected cast type in " + method.FullName + " IL_" + offset.ToString("x4"));
        }
    }

    static MethodDefinition RequireUniqueBody(TypeDefinition type, string name) {
        MethodDefinition[] matches = type.Methods.Where(method => method.Name == name && method.HasBody).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("expected one method " + type.FullName + "::" + name);
        return matches[0];
    }

    static int RepairKnownOwnerless9_2References(TypeDefinition type) {
        ModuleDefinition module = type.Module;
        int repaired = 0;
        if (type.FullName == "BCGManager/<>c__DisplayClass7_0") {
            MethodDefinition method = RequireUniqueBody(type, "<GetHeroBaseAttributes>b__0");
            TypeDefinition heroDetails = module.GetType("BCGHeroDetails");
            if (heroDetails == null)
                throw new InvalidDataException("missing BCGHeroDetails type for GetHeroBaseAttributes");
            // The clean Cpp2IL output leaves both of these locals as !0. V_55
            // receives List<BCGHeroDetails>.get_Item(), and V_44 receives V_55.
            SetKnownOwnerlessLocal(method, 44, heroDetails, ref repaired);
            SetKnownOwnerlessLocal(method, 55, heroDetails, ref repaired);
            ReplaceKnownOwnerlessCast(method, 0x04d8, module.TypeSystem.String, ref repaired);
        } else if (type.FullName == "OldObjectPool") {
            MethodDefinition method = RequireUniqueBody(type, "Release");
            if (method.Parameters.Count != 1 || method.Parameters[0].ParameterType.FullName != "UnityEngine.Object")
                throw new InvalidDataException("unexpected OldObjectPool.Release signature");
            ReplaceKnownOwnerlessCast(method, 0x009e, method.Parameters[0].ParameterType, ref repaired);
        }
        return repaired;
    }

    static int RepairKnownEmpty9_2Finalizer(string assemblyName, TypeDefinition type) {
        bool isRecoveredEmptyFinalizer =
            (assemblyName == "Assembly-CSharp.dll" &&
                (type.FullName == "LightWeightObjectPool`1" || type.FullName == "UpgradeScreenData")) ||
            (assemblyName == "Assembly-CSharp-firstpass.dll" &&
                type.FullName == "EB.LightWeightDictionary`2");
        if (!isRecoveredEmptyFinalizer) return 0;

        MethodDefinition method = RequireUniqueBody(type, "Finalize");
        if (!method.IsVirtual || method.IsStatic || method.Parameters.Count != 0 ||
            method.ReturnType.MetadataType != MetadataType.Void)
            throw new InvalidDataException("unexpected recovered empty finalizer signature: " + method.FullName);
        MethodReference baseFinalizer = method.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Call)
            .Select(instruction => instruction.Operand as MethodReference)
            .FirstOrDefault(reference => reference != null &&
                reference.FullName == "System.Void System.Object::Finalize()");
        if (baseFinalizer == null)
            throw new InvalidDataException("could not identify System.Object.Finalize call in " + method.FullName);

        // The recovered 9.2 source has empty destructors for these three types.
        // Their emitted bodies instead contain unresolved native calls and
        // unrelated method instructions, which crash Mono's finalizer thread.
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        ILProcessor il = method.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, method.Module.ImportReference(baseFinalizer)));
        il.Append(Instruction.Create(OpCodes.Ret));
        method.Body.MaxStackSize = 1;
        return 1;
    }

    static bool Repair(MethodDefinition method, TypeDefinition type) {
        if (!method.HasBody) throw new InvalidDataException("method has no body: " + method.FullName);
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        ILProcessor il = method.Body.GetILProcessor();
        ModuleDefinition module = method.Module;
        if (method.Name == ".cctor" && type.FullName == "EB.Hash") {
            var constants = new[] {
                new { Name = "HASH_PRIME_64", Type = MetadataType.UInt64, Value = 0x00000100000001b3L },
                new { Name = "HASH_INIT_64", Type = MetadataType.UInt64, Value = unchecked((long)0xcbf29ce484222325UL) },
            };
            foreach (var constant in constants) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == constant.Name);
                if (field == null || !field.IsStatic || field.FieldType.MetadataType != constant.Type)
                    throw new InvalidDataException("missing EB.Hash static field: " + constant.Name);
                il.Append(Instruction.Create(OpCodes.Ldc_I8, constant.Value));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }
            var wordConstants = new[] {
                new { Name = "HASH_PRIME_32", Value = 0x01000193 },
                new { Name = "HASH_INIT_32", Value = unchecked((int)0x811c9dc5) },
            };
            foreach (var constant in wordConstants) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == constant.Name);
                if (field == null || !field.IsStatic || field.FieldType.MetadataType != MetadataType.UInt32)
                    throw new InvalidDataException("missing EB.Hash static field: " + constant.Name);
                il.Append(Instruction.Create(OpCodes.Ldc_I4, constant.Value));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }
            il.Append(Instruction.Create(OpCodes.Ret));
        }
        if (method.Name == "FNV64" && type.FullName == "EB.Hash") {
            FieldDefinition prime = type.Fields.SingleOrDefault(f => f.Name == "HASH_PRIME_64");
            if (!method.IsStatic || method.Parameters.Count != 2 ||
                method.Parameters[0].ParameterType.FullName != "System.Byte[]" ||
                method.Parameters[1].ParameterType.MetadataType != MetadataType.Int64 ||
                method.ReturnType.MetadataType != MetadataType.Int64 || prime == null ||
                !prime.IsStatic || prime.FieldType.MetadataType != MetadataType.UInt64)
                throw new InvalidDataException("unexpected EB.Hash.FNV64 metadata");

            // Native 9.2 implements the byte loop as (hash * HASH_PRIME_64) ^ byte,
            // returning the input seed for an empty array. Keep null input's
            // native NullReferenceException through ldlen on the array.
            VariableDefinition hash = new VariableDefinition(module.TypeSystem.Int64);
            VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(hash);
            method.Body.Variables.Add(length);
            method.Body.Variables.Add(index);
            method.Body.InitLocals = true;
            Instruction loop = Instruction.Create(OpCodes.Ldloc, index);
            Instruction done = Instruction.Create(OpCodes.Ldloc, hash);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldlen));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Stloc, length));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Stloc, hash));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(loop);
            il.Append(Instruction.Create(OpCodes.Ldloc, length));
            il.Append(Instruction.Create(OpCodes.Bge_S, done));
            il.Append(Instruction.Create(OpCodes.Ldloc, hash));
            il.Append(Instruction.Create(OpCodes.Ldsfld, prime));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldelem_U1));
            il.Append(Instruction.Create(OpCodes.Conv_I8));
            il.Append(Instruction.Create(OpCodes.Xor));
            il.Append(Instruction.Create(OpCodes.Stloc, hash));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(Instruction.Create(OpCodes.Br_S, loop));
            il.Append(done);
            il.Append(Instruction.Create(OpCodes.Ret));
            method.Body.MaxStackSize = 3;
            // This emitter owns the complete body. The generic tail below
            // appends a ret for one-instruction repairs; doing that here
            // leaves an unreachable empty-stack ret that IL2CPP rejects.
            return true;
        }
        if (method.Name == "get_Randomizer" && type.FullName == "EB.Core.ThreadSafeRandom") {
            FieldDefinition randomizer = type.Fields.SingleOrDefault(f => f.Name == "_kRandomizer");
            MethodReference utcNow = module.ImportReference(typeof(DateTime).GetProperty("UtcNow").GetGetMethod());
            MethodReference ticks = module.ImportReference(typeof(DateTime).GetProperty("Ticks").GetGetMethod());
            MethodReference randomConstructor = module.ImportReference(typeof(Random).GetConstructor(new[] { typeof(int) }));
            if (!method.IsStatic || method.Parameters.Count != 0 ||
                method.ReturnType.FullName != "System.Random" || randomizer == null || !randomizer.IsStatic ||
                randomizer.FieldType.FullName != "System.Random" ||
                utcNow == null || ticks == null || randomConstructor == null)
                throw new InvalidDataException("unexpected ThreadSafeRandom.Randomizer metadata");

            if (!randomizer.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ThreadStaticAttribute")) {
                var threadStaticConstructor = typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes);
                if (threadStaticConstructor == null)
                    throw new InvalidDataException("ThreadStaticAttribute constructor is unavailable");
                randomizer.CustomAttributes.Add(new CustomAttribute(module.ImportReference(threadStaticConstructor)));
            }

            // The native getter reuses its thread-local Random or seeds a new
            // instance from (int)(DateTime.UtcNow.Ticks << 4). Cpp2IL's body
            // has a bad local type at IL_00cc; rebuild the observed sequence.
            VariableDefinition now = new VariableDefinition(module.ImportReference(typeof(DateTime)));
            method.Body.Variables.Add(now);
            method.Body.InitLocals = true;
            Instruction returnRandomizer = Instruction.Create(OpCodes.Ret);
            il.Append(Instruction.Create(OpCodes.Ldsfld, randomizer));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Brtrue_S, returnRandomizer));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(Instruction.Create(OpCodes.Call, utcNow));
            il.Append(Instruction.Create(OpCodes.Stloc, now));
            il.Append(Instruction.Create(OpCodes.Ldloca, now));
            il.Append(Instruction.Create(OpCodes.Call, ticks));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_4));
            il.Append(Instruction.Create(OpCodes.Shl));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Newobj, randomConstructor));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Stsfld, randomizer));
            il.Append(returnRandomizer);
            method.Body.MaxStackSize = 2;
        }
        if (method.Name == "Init" && type.FullName == "EB.SafeValue") {
            FieldDefinition value = type.Fields.SingleOrDefault(f => f.Name == "_v");
            FieldDefinition seed = type.Fields.SingleOrDefault(f => f.Name == "_r");
            FieldDefinition hashValue = type.Fields.SingleOrDefault(f => f.Name == "_h");
            TypeDefinition randomType = module.GetType("EB.Core.ThreadSafeRandom");
            TypeDefinition hashType = module.GetType("EB.Hash");
            MethodDefinition randomGetter = randomType == null ? null : randomType.Methods.SingleOrDefault(m =>
                m.Name == "get_value" && m.IsStatic && m.Parameters.Count == 0 &&
                m.ReturnType.MetadataType == MetadataType.Single);
            MethodDefinition hash = hashType == null ? null : hashType.Methods.SingleOrDefault(m =>
                m.Name == "FNV64" && m.IsStatic && m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == "System.Byte[]" &&
                m.Parameters[1].ParameterType.MetadataType == MetadataType.Int64 &&
                m.ReturnType.MetadataType == MetadataType.Int64);
            if (method.IsStatic || method.Parameters.Count != 1 ||
                method.Parameters[0].ParameterType.FullName != "System.Byte[]" ||
                value == null || value.IsStatic || value.FieldType.FullName != "System.Byte[]" ||
                seed == null || seed.IsStatic || seed.FieldType.MetadataType != MetadataType.Int64 ||
                hashValue == null || hashValue.IsStatic || hashValue.FieldType.MetadataType != MetadataType.Int64 ||
                randomGetter == null || hash == null)
                throw new InvalidDataException("unexpected EB.SafeValue.Init metadata");

            // Cpp2IL's translated random-value comparison has invalid stack
            // types at IL_00a9. Native 9.2 code confirms Init stores the byte
            // array, converts ThreadSafeRandom.value to the Int64 _r seed,
            // then stores EB.Hash.FNV64(_v, _r) in _h.
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Stfld, value));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, randomGetter));
            il.Append(Instruction.Create(OpCodes.Conv_I8));
            il.Append(Instruction.Create(OpCodes.Stfld, seed));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, value));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, seed));
            il.Append(Instruction.Create(OpCodes.Call, hash));
            il.Append(Instruction.Create(OpCodes.Stfld, hashValue));
            il.Append(Instruction.Create(OpCodes.Ret));
            method.Body.MaxStackSize = 3;
        }
        if (method.Name == ".ctor" && (
            type.FullName == "EB.UI.Social.FuseSocialHub/FuseSocialHubPresentationConfig" ||
            type.FullName == "EB.UI.SystemMessage.FuseSystemMessageOverlay/SystemMessagePresentationConfig")) {
            if (method.IsStatic || method.Parameters.Count != 0 || type.BaseType == null ||
                type.BaseType.FullName != "System.Object")
                throw new InvalidDataException("unexpected presentation config constructor metadata: " + type.FullName);
            var objectConstructor = new MethodReference(".ctor", module.TypeSystem.Void, type.BaseType) {
                HasThis = true
            };
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, objectConstructor));
        }
        if (method.Name == ".ctor" &&
            type.FullName == "Facebook.Unity.Settings.FacebookSettings/UrlSchemes") {
            FieldDefinition list = type.Fields.SingleOrDefault(f => f.Name == "list");
            if (method.IsStatic || method.Parameters.Count != 1 || list == null || list.IsStatic ||
                list.FieldType.FullName != method.Parameters[0].ParameterType.FullName)
                throw new InvalidDataException("unexpected Facebook URL schemes constructor metadata");
            var objectConstructor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) {
                HasThis = true
            };
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, objectConstructor));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Stfld, list));
        }
        if (method.Name == ".cctor" && type.FullName == "Quests.Presentation.GameboardBuilder") {
            // The malformed Cpp2IL translation retains these initializer
            // constants: 1101004800 is the float bit pattern for 20.0, the
            // nearby 0.5 operation derives HALF_TILE_SIZE, and 10 is the
            // integer initializer for PARTITION_SIZE. RAID_START_POS has no
            // recoverable initializer in the translation, so it remains the
            // CLR default until its native initializer can be recovered.
            var scalarInitializers = new[] {
                new { Name = "TILE_SIZE", Value = 20.0f },
                new { Name = "HALF_TILE_SIZE", Value = 10.0f },
            };
            foreach (var initializer in scalarInitializers) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == initializer.Name);
                if (field == null || !field.IsStatic || field.FieldType.MetadataType != MetadataType.Single)
                    throw new InvalidDataException("missing GameboardBuilder static float: " + initializer.Name);
                il.Append(Instruction.Create(OpCodes.Ldc_R4, initializer.Value));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }
            FieldDefinition partitionSize = type.Fields.SingleOrDefault(f => f.Name == "PARTITION_SIZE");
            if (partitionSize == null || !partitionSize.IsStatic || partitionSize.FieldType.MetadataType != MetadataType.Int32)
                throw new InvalidDataException("missing GameboardBuilder static int: PARTITION_SIZE");
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 10));
            il.Append(Instruction.Create(OpCodes.Stsfld, partitionSize));
        }
        if (method.Name == ".cctor" && type.FullName == "AudioPal") {
            FieldDefinition emitters = type.Fields.SingleOrDefault(f => f.Name == "MaxEnvironmentEmitters");
            TypeDefinition details = module.Types.SingleOrDefault(t => t.Name == "<PrivateImplementationDetails>");
            FieldDefinition values = details == null ? null : details.Fields.SingleOrDefault(f =>
                f.Name == "AFC9AA9020B3869AAFAE1D357C80E629287DD014C51D2B7AFAF2B3E1BA79BE33");
            if (emitters == null || !emitters.IsStatic ||
                emitters.FieldType.MetadataType != MetadataType.Array || values == null ||
                !values.Attributes.HasFlag(FieldAttributes.HasFieldRVA) || values.InitialValue.Length != 16)
                throw new InvalidDataException("missing AudioPal environment emitter RVA data");
            MethodReference initializeArray = module.ImportReference(typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                .GetMethod("InitializeArray", new[] { typeof(Array), typeof(RuntimeFieldHandle) }));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_4));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldtoken, values));
            il.Append(Instruction.Create(OpCodes.Call, initializeArray));
            il.Append(Instruction.Create(OpCodes.Stsfld, emitters));
        }
        if (method.Name == ".cctor" && type.FullName == "EBWorldPainterData") {
            TypeDefinition quality = type.NestedTypes.SingleOrDefault(t => t.Name == "eVISIBILITY_QUALITY");
            FieldDefinition count = type.Fields.SingleOrDefault(f => f.Name == "eVISIBILITY_QUALITY_COUNT");
            int values = quality == null ? 0 : quality.Fields.Count(f => f.IsLiteral);
            if (values == 0 || count == null || !count.IsStatic ||
                count.FieldType.MetadataType != MetadataType.Int32)
                throw new InvalidDataException("missing EBWorldPainterData visibility quality metadata");
            // The native initializer is Enum.GetValues(eVISIBILITY_QUALITY).Length.
            il.Append(Instruction.Create(OpCodes.Ldc_I4, values));
            il.Append(Instruction.Create(OpCodes.Stsfld, count));
        }
        if (method.Name == ".cctor" && type.FullName == "BuffsController") {
            TypeDefinition comparer = module.GetType("BuffTargetComparer");
            FieldDefinition targetComparer = type.Fields.SingleOrDefault(f => f.Name == "kTargetComparer");
            TypeDefinition modTypes = module.GetType("BuffModTypes");
            FieldDefinition count = type.Fields.SingleOrDefault(f => f.Name == "kNum_BuffModTypes");
            MethodDefinition constructor = comparer == null ? null : comparer.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            int values = modTypes == null ? 0 : modTypes.Fields.Count(f => f.IsLiteral);
            if (targetComparer == null || !targetComparer.IsStatic || constructor == null ||
                count == null || !count.IsStatic || count.FieldType.MetadataType != MetadataType.Int32 || values == 0)
                throw new InvalidDataException("missing BuffsController static initializer metadata");
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, targetComparer));
            il.Append(Instruction.Create(OpCodes.Ldc_I4, values));
            il.Append(Instruction.Create(OpCodes.Stsfld, count));
        }
        if (method.Name == ".cctor" && type.FullName == "EB.MoveEditor.PrefabLib") {
            FieldDefinition map = type.Fields.SingleOrDefault(f => f.Name == "RegisterActionsMap");
            GenericInstanceType dictionary = map == null ? null : map.FieldType as GenericInstanceType;
            TypeDefinition action = type.NestedTypes.SingleOrDefault(t => t.Name == "RegisterAction");
            if (map == null || !map.IsStatic || dictionary == null || dictionary.GenericArguments.Count != 2 || action == null)
                throw new InvalidDataException("missing PrefabLib registration dictionary metadata");
            TypeDefinition dictionaryDefinition = dictionary.ElementType.Resolve();
            MethodDefinition dictionaryConstructorDefinition = dictionaryDefinition == null ? null :
                dictionaryDefinition.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            MethodDefinition addDefinition = dictionaryDefinition == null ? null :
                dictionaryDefinition.Methods.SingleOrDefault(m => m.Name == "Add" && !m.IsStatic && m.Parameters.Count == 2);
            MethodDefinition actionConstructorDefinition = action.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
            if (dictionaryConstructorDefinition == null || addDefinition == null || actionConstructorDefinition == null)
                throw new InvalidDataException("could not resolve PrefabLib dictionary/delegate constructors");
            MethodReference dictionaryConstructor = module.ImportReference(dictionaryConstructorDefinition);
            dictionaryConstructor.DeclaringType = dictionary;
            MethodReference add = module.ImportReference(addDefinition);
            add.DeclaringType = dictionary;
            MethodReference actionConstructor = module.ImportReference(actionConstructorDefinition);
            MethodReference getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod(
                "GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
            il.Append(Instruction.Create(OpCodes.Newobj, dictionaryConstructor));
            var registrations = new[] {
                new { Key = "UnityEngine.ParticleSystem", Method = "RegisterParticle" },
                new { Key = "EB.Rendering.GenericTrailRendererInstance", Method = "RegisterTrail" },
                new { Key = "Prop", Method = "RegisterProp" },
            };
            foreach (var registration in registrations) {
                TypeReference keyType = module.GetTypeReferences().SingleOrDefault(t => t.FullName == registration.Key);
                if (keyType == null) keyType = AllTypes(module.Types).SingleOrDefault(t => t.FullName == registration.Key);
                MethodDefinition callback = type.Methods.SingleOrDefault(m => m.Name == registration.Method &&
                    m.IsStatic && m.Parameters.Count == 3);
                if (keyType == null || callback == null)
                    throw new InvalidDataException("missing PrefabLib registration target: " + registration.Key);
                il.Append(Instruction.Create(OpCodes.Dup));
                il.Append(Instruction.Create(OpCodes.Ldtoken, keyType));
                il.Append(Instruction.Create(OpCodes.Call, getTypeFromHandle));
                il.Append(Instruction.Create(OpCodes.Ldnull));
                il.Append(Instruction.Create(OpCodes.Ldftn, callback));
                il.Append(Instruction.Create(OpCodes.Newobj, actionConstructor));
                il.Append(Instruction.Create(OpCodes.Callvirt, add));
            }
            il.Append(Instruction.Create(OpCodes.Stsfld, map));
        }
        if (method.Name == ".cctor" && type.FullName == "CriticalError") {
            FieldDefinition config = type.Fields.SingleOrDefault(f => f.Name == "CriticalErrorConfig");
            FieldDefinition closeButton = type.Fields.SingleOrDefault(f => f.Name == "CloseButtonIndex");
            TypeDefinition configType = config == null ? null : config.FieldType.Resolve();
            MethodDefinition constructor = configType == null ? null : configType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            if (config == null || !config.IsStatic || closeButton == null || !closeButton.IsStatic || constructor == null)
                throw new InvalidDataException("missing CriticalError static initializer metadata");
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, config));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_M1));
            il.Append(Instruction.Create(OpCodes.Stsfld, closeButton));
        }
        if (method.Name == ".cctor" && type.FullName == "CriticalError/ConfigData/<>c") {
            FieldDefinition singleton = type.Fields.SingleOrDefault(f => f.Name == "<>9");
            MethodDefinition constructor = type.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            if (singleton == null || !singleton.IsStatic || constructor == null)
                throw new InvalidDataException("missing CriticalError.ConfigData lambda singleton metadata");
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, singleton));
        }
        if (method.Name == ".ctor" && type.FullName == "CriticalError/ConfigData") {
            TypeDefinition closure = module.GetType("CriticalError/ConfigData/<>c");
            FieldDefinition singleton = closure == null ? null : closure.Fields.SingleOrDefault(f => f.Name == "<>9");
            FieldDefinition cachedGetter = closure == null ? null : closure.Fields.SingleOrDefault(f => f.Name == "<>9__2_0");
            FieldDefinition getter = type.Fields.SingleOrDefault(f => f.Name == "PrefabNameGetter");
            FieldDefinition layerName = type.Fields.SingleOrDefault(f => f.Name == "LayerName");
            MethodDefinition callback = closure == null ? null : closure.Methods.SingleOrDefault(m =>
                m.Name == "<.ctor>b__2_0" && !m.IsStatic && m.Parameters.Count == 0);
            TypeDefinition delegateType = cachedGetter == null ? null : cachedGetter.FieldType.Resolve();
            MethodDefinition delegateConstructor = delegateType == null ? null : delegateType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
            if (singleton == null || cachedGetter == null || !cachedGetter.IsStatic || getter == null ||
                !getter.IsPublic || layerName == null || callback == null || delegateConstructor == null)
                throw new InvalidDataException("missing CriticalError.ConfigData lambda metadata");
            var ready = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldsfld, cachedGetter));
            il.Append(Instruction.Create(OpCodes.Brtrue, ready));
            il.Append(Instruction.Create(OpCodes.Ldsfld, singleton));
            il.Append(Instruction.Create(OpCodes.Ldftn, callback));
            var closedDelegateConstructor = new MethodReference(".ctor", module.TypeSystem.Void, cachedGetter.FieldType) {
                HasThis = true
            };
            closedDelegateConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            closedDelegateConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            il.Append(Instruction.Create(OpCodes.Newobj, closedDelegateConstructor));
            il.Append(Instruction.Create(OpCodes.Stsfld, cachedGetter));
            il.Append(ready);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldsfld, cachedGetter));
            il.Append(Instruction.Create(OpCodes.Stfld, getter));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldstr, "Critical"));
            il.Append(Instruction.Create(OpCodes.Stfld, layerName));
        }
        if (method.Name == "OnAfterDeserialize" && type.FullName == "AITuneables") {
            FieldDefinition modifiers = type.Fields.SingleOrDefault(f => f.Name == "_modifiersToDef");
            FieldDefinition tuneables = type.Fields.SingleOrDefault(f => f.Name == "Tuneables");
            TypeDefinition definition = module.GetType("AITuneableDefinition");
            if (modifiers == null || tuneables == null || definition == null || method.IsStatic ||
                method.Parameters.Count != 0 || method.ReturnType.MetadataType != MetadataType.Void)
                throw new InvalidDataException("missing AITuneables deserialization metadata");
            TypeReference listType = tuneables.FieldType;
            TypeReference mapType = modifiers.FieldType;
            MethodReference clear = new MethodReference("Clear", module.TypeSystem.Void, mapType) { HasThis = true };
            MethodReference getType = module.ImportReference(typeof(object).GetMethod("GetType", Type.EmptyTypes));
            MethodReference getMethod = module.ImportReference(typeof(Type).GetMethod("GetMethod", new[] { typeof(string) }));
            MethodReference invoke = module.ImportReference(typeof(System.Reflection.MethodInfo).GetMethod(
                "Invoke", new[] { typeof(object), typeof(object[]) }));
            var count = new MethodReference("get_Count", module.TypeSystem.Int32, listType) { HasThis = true };
            TypeReference nonGenericList = module.ImportReference(typeof(System.Collections.IList));
            MethodReference item = module.ImportReference(nonGenericList.Resolve().Methods.Single(m =>
                m.Name == "get_Item" && m.Parameters.Count == 1));
            MethodReference hasFlat = module.ImportReference(definition.Methods.Single(m => m.Name == "HasFlatModifier"));
            MethodReference hasPercentage = module.ImportReference(definition.Methods.Single(m => m.Name == "HasPercentageModifier"));
            MethodReference hasOverride = module.ImportReference(definition.Methods.Single(m => m.Name == "HasOverrideModifier"));
            FieldDefinition hasModifiers = definition.Fields.SingleOrDefault(f => f.Name == "HasModifiers");
            FieldDefinition flat = definition.Fields.SingleOrDefault(f => f.Name == "FlatModifier");
            FieldDefinition percentage = definition.Fields.SingleOrDefault(f => f.Name == "PercentageModifier");
            FieldDefinition over = definition.Fields.SingleOrDefault(f => f.Name == "OverrideModifier");
            if (hasModifiers == null || flat == null || percentage == null || over == null)
                throw new InvalidDataException("missing AITuneableDefinition modifier fields");

            VariableDefinition list = new VariableDefinition(listType);
            VariableDefinition definitionLocal = new VariableDefinition(definition);
            VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(list);
            method.Body.Variables.Add(definitionLocal);
            method.Body.Variables.Add(index);
            method.Body.Variables.Add(length);
            method.Body.InitLocals = true;
            il = method.Body.GetILProcessor();
            Instruction done = Instruction.Create(OpCodes.Ret);
            Instruction loop = Instruction.Create(OpCodes.Nop);
            Instruction checkPercentage = Instruction.Create(OpCodes.Nop);
            Instruction checkOverride = Instruction.Create(OpCodes.Nop);
            Instruction next = Instruction.Create(OpCodes.Nop);
            Instruction test = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Brfalse, done));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Callvirt, clear));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, tuneables));
            il.Append(Instruction.Create(OpCodes.Stloc, list));
            il.Append(Instruction.Create(OpCodes.Ldloc, list));
            il.Append(Instruction.Create(OpCodes.Brfalse, done));
            il.Append(Instruction.Create(OpCodes.Ldloc, list));
            il.Append(Instruction.Create(OpCodes.Callvirt, count));
            il.Append(Instruction.Create(OpCodes.Stloc, length));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(Instruction.Create(OpCodes.Br, test));
            il.Append(loop);
            il.Append(Instruction.Create(OpCodes.Ldloc, list));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Callvirt, item));
            il.Append(Instruction.Create(OpCodes.Castclass, definition));
            il.Append(Instruction.Create(OpCodes.Stloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Ldfld, hasModifiers));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Callvirt, hasFlat));
            il.Append(Instruction.Create(OpCodes.Brfalse, checkPercentage));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Callvirt, getType));
            il.Append(Instruction.Create(OpCodes.Ldstr, "Add"));
            il.Append(Instruction.Create(OpCodes.Callvirt, getMethod));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Ldfld, flat));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Callvirt, invoke));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(checkPercentage);
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Callvirt, hasPercentage));
            il.Append(Instruction.Create(OpCodes.Brfalse, checkOverride));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Callvirt, getType));
            il.Append(Instruction.Create(OpCodes.Ldstr, "Add"));
            il.Append(Instruction.Create(OpCodes.Callvirt, getMethod));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Ldfld, percentage));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Callvirt, invoke));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(checkOverride);
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Callvirt, hasOverride));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Callvirt, getType));
            il.Append(Instruction.Create(OpCodes.Ldstr, "Add"));
            il.Append(Instruction.Create(OpCodes.Callvirt, getMethod));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Ldfld, over));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Ldloc, definitionLocal));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            il.Append(Instruction.Create(OpCodes.Callvirt, invoke));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(next);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(test);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldloc, length));
            il.Append(Instruction.Create(OpCodes.Blt, loop));
            il.Append(done);
            method.Body.MaxStackSize = 3;
        }
        if (method.Name == "SetupTuneables_Internal" && type.FullName == "AITuneablesSet") {
            if (method.IsStatic || method.Parameters.Count != 3 ||
                method.Parameters[0].ParameterType.FullName != "System.Collections.Generic.List`1<AITuneableDefinition>" ||
                method.Parameters[1].ParameterType.FullName != "System.Collections.Generic.List`1<AITuneableOverride>" ||
                method.Parameters[2].ParameterType.MetadataType != MetadataType.Boolean ||
                method.ReturnType.MetadataType != MetadataType.Void)
                throw new InvalidDataException("unexpected AITuneablesSet.SetupTuneables_Internal signature");
            FieldDefinition dictionary = type.Fields.SingleOrDefault(f => f.Name == "_tuneablesDictionary");
            TypeDefinition definition = module.GetType("AITuneableDefinition");
            TypeDefinition tuneableOverride = module.GetType("AITuneableOverride");
            MethodDefinition find = type.Methods.SingleOrDefault(m => m.Name == "FindTuneableOverride" &&
                !m.IsStatic && m.Parameters.Count == 2);
            MethodDefinition add = type.Methods.SingleOrDefault(m => m.Name == "AddTuneableOverride" &&
                !m.IsStatic && m.Parameters.Count == 3);
            MethodDefinition setDefinition = tuneableOverride == null ? null : tuneableOverride.Methods.SingleOrDefault(m =>
                m.Name == "SetDefinition" && !m.IsStatic && m.Parameters.Count == 1);
            FieldDefinition name = definition == null ? null : definition.Fields.SingleOrDefault(f => f.Name == "Name");
            GenericInstanceType dictionaryType = dictionary == null ? null : dictionary.FieldType as GenericInstanceType;
            if (dictionary == null || dictionaryType == null ||
                definition == null || tuneableOverride == null || find == null || add == null ||
                setDefinition == null || name == null)
                throw new InvalidDataException("missing AITuneablesSet tuneable assignment metadata");
            var count = new MethodReference("get_Count", module.TypeSystem.Int32,
                method.Parameters[0].ParameterType) { HasThis = true };
            TypeReference nonGenericList = module.ImportReference(typeof(System.Collections.IList));
            MethodReference item = module.ImportReference(nonGenericList.Resolve().Methods.Single(m =>
                m.Name == "get_Item" && m.Parameters.Count == 1));
            var findRef = module.ImportReference(find);
            var addRef = module.ImportReference(add);
            var setDefinitionRef = module.ImportReference(setDefinition);
            TypeReference nonGenericDictionary = module.ImportReference(typeof(System.Collections.IDictionary));
            MethodReference setItem = module.ImportReference(nonGenericDictionary.Resolve().Methods.Single(m =>
                m.Name == "set_Item" && m.Parameters.Count == 2));
            VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition def = new VariableDefinition(definition);
            VariableDefinition entry = new VariableDefinition(tuneableOverride);
            method.Body.Variables.Add(length);
            method.Body.Variables.Add(index);
            method.Body.Variables.Add(def);
            method.Body.Variables.Add(entry);
            method.Body.InitLocals = true;
            il = method.Body.GetILProcessor();
            Instruction done = Instruction.Create(OpCodes.Ret);
            Instruction loop = Instruction.Create(OpCodes.Nop);
            Instruction haveEntry = Instruction.Create(OpCodes.Nop);
            Instruction next = Instruction.Create(OpCodes.Nop);
            Instruction test = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Brfalse, done));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, dictionary));
            il.Append(Instruction.Create(OpCodes.Brfalse, done));
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Callvirt, count));
            il.Append(Instruction.Create(OpCodes.Stloc, length));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(Instruction.Create(OpCodes.Br, test));
            il.Append(loop);
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Callvirt, item));
            il.Append(Instruction.Create(OpCodes.Castclass, definition));
            il.Append(Instruction.Create(OpCodes.Stloc, def));
            il.Append(Instruction.Create(OpCodes.Ldloc, def));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[1]));
            il.Append(Instruction.Create(OpCodes.Ldloc, def));
            il.Append(Instruction.Create(OpCodes.Ldfld, name));
            il.Append(Instruction.Create(OpCodes.Call, findRef));
            il.Append(Instruction.Create(OpCodes.Stloc, entry));
            il.Append(Instruction.Create(OpCodes.Ldloc, entry));
            il.Append(Instruction.Create(OpCodes.Brtrue, haveEntry));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldloc, def));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[2]));
            il.Append(Instruction.Create(OpCodes.Call, addRef));
            il.Append(Instruction.Create(OpCodes.Stloc, entry));
            il.Append(haveEntry);
            il.Append(Instruction.Create(OpCodes.Ldloc, entry));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldloc, entry));
            il.Append(Instruction.Create(OpCodes.Ldloc, def));
            il.Append(Instruction.Create(OpCodes.Callvirt, setDefinitionRef));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, dictionary));
            il.Append(Instruction.Create(OpCodes.Ldloc, def));
            il.Append(Instruction.Create(OpCodes.Ldfld, name));
            il.Append(Instruction.Create(OpCodes.Ldloc, entry));
            il.Append(Instruction.Create(OpCodes.Callvirt, setItem));
            il.Append(next);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(test);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldloc, length));
            il.Append(Instruction.Create(OpCodes.Blt, loop));
            il.Append(done);
            method.Body.MaxStackSize = 4;
        }
        if (method.Name == "Init" && type.FullName == "AIRageSettings" &&
            !method.IsStatic && method.Parameters.Count == 1) {
            FieldDefinition levels = type.Fields.SingleOrDefault(f => f.Name == "_levels");
            TypeDefinition levelType = module.GetType("AIRageLevel");
            MethodDefinition initializeLevel = levelType == null ? null : levelType.Methods.SingleOrDefault(m =>
                m.Name == "Init" && !m.IsStatic && m.Parameters.Count == 3 &&
                m.Parameters[0].ParameterType.MetadataType == MetadataType.Single &&
                m.Parameters[1].ParameterType.MetadataType == MetadataType.Single &&
                m.Parameters[2].ParameterType.FullName == "AITuneables");
            if (levels == null || levels.FieldType.MetadataType != MetadataType.Array ||
                levelType == null || initializeLevel == null || method.Parameters[0].ParameterType.FullName != "AITuneables")
                throw new InvalidDataException("missing AIRageSettings native Init metadata");
            VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition length = new VariableDefinition(module.TypeSystem.Int32);
            VariableDefinition level = new VariableDefinition(levelType);
            method.Body.Variables.Add(index);
            method.Body.Variables.Add(length);
            method.Body.Variables.Add(level);
            method.Body.InitLocals = true;
            il = method.Body.GetILProcessor();
            Instruction done = Instruction.Create(OpCodes.Ret);
            Instruction loop = Instruction.Create(OpCodes.Nop);
            Instruction lengthReady = Instruction.Create(OpCodes.Nop);
            Instruction test = Instruction.Create(OpCodes.Nop);
            Instruction next = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, levels));
            il.Append(Instruction.Create(OpCodes.Brfalse, done));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, levels));
            il.Append(Instruction.Create(OpCodes.Ldlen));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Stloc, length));
            il.Append(Instruction.Create(OpCodes.Ldloc, length));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
            il.Append(Instruction.Create(OpCodes.Ble, lengthReady));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
            il.Append(Instruction.Create(OpCodes.Stloc, length));
            il.Append(lengthReady);
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(Instruction.Create(OpCodes.Br, test));
            il.Append(loop);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, levels));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldelem_Ref));
            il.Append(Instruction.Create(OpCodes.Stloc, level));
            il.Append(Instruction.Create(OpCodes.Ldloc, level));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            il.Append(Instruction.Create(OpCodes.Ldloc, level));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Conv_R4));
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(initializeLevel)));
            il.Append(next);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stloc, index));
            il.Append(test);
            il.Append(Instruction.Create(OpCodes.Ldloc, index));
            il.Append(Instruction.Create(OpCodes.Ldloc, length));
            il.Append(Instruction.Create(OpCodes.Blt, loop));
            il.Append(done);
            method.Body.MaxStackSize = 4;
        }
        if (method.Name == "OnBeforeSerialize" && type.FullName == "Fabric.SerializableDictionary`2") {
            FieldDefinition keys = type.Fields.SingleOrDefault(f => f.Name == "keys");
            FieldDefinition values = type.Fields.SingleOrDefault(f => f.Name == "values");
            if (keys == null || values == null || method.IsStatic || method.Parameters.Count != 0 ||
                method.ReturnType.MetadataType != MetadataType.Void ||
                (keys.FieldType as GenericInstanceType) == null ||
                (values.FieldType as GenericInstanceType) == null)
                throw new InvalidDataException("missing Fabric.SerializableDictionary serialization metadata");
            TypeReference listInterface = module.ImportReference(typeof(System.Collections.IList));
            TypeReference dictionaryInterface = module.ImportReference(typeof(System.Collections.IDictionary));
            MethodReference listClear = module.ImportReference(listInterface.Resolve().Methods.Single(m =>
                m.Name == "Clear" && m.Parameters.Count == 0));
            MethodReference listAdd = module.ImportReference(listInterface.Resolve().Methods.Single(m =>
                m.Name == "Add" && m.Parameters.Count == 1));
            MethodReference dictionaryGetEnumerator = module.ImportReference(dictionaryInterface.Resolve().Methods.Single(m =>
                m.Name == "GetEnumerator" && m.Parameters.Count == 0));
            TypeReference enumeratorInterface = module.ImportReference(typeof(System.Collections.IDictionaryEnumerator));
            MethodReference currentEntry = module.ImportReference(enumeratorInterface.Resolve().Methods.Single(m =>
                m.Name == "get_Entry" && m.Parameters.Count == 0));
            MethodReference moveNext = module.ImportReference(typeof(System.Collections.IEnumerator)
                .GetMethod("MoveNext", Type.EmptyTypes));
            TypeReference entryType = module.ImportReference(typeof(System.Collections.DictionaryEntry));
            MethodReference entryKey = module.ImportReference(typeof(System.Collections.DictionaryEntry)
                .GetProperty("Key").GetGetMethod());
            MethodReference entryValue = module.ImportReference(typeof(System.Collections.DictionaryEntry)
                .GetProperty("Value").GetGetMethod());
            MethodReference dispose = module.ImportReference(typeof(IDisposable).GetMethod("Dispose", Type.EmptyTypes));
            VariableDefinition enumerator = new VariableDefinition(enumeratorInterface);
            VariableDefinition entry = new VariableDefinition(entryType);
            method.Body.Variables.Add(enumerator);
            method.Body.Variables.Add(entry);
            method.Body.InitLocals = true;
            il = method.Body.GetILProcessor();
            Instruction createKeys = Instruction.Create(OpCodes.Nop);
            Instruction createValues = Instruction.Create(OpCodes.Nop);
            Instruction loop = Instruction.Create(OpCodes.Nop);
            Instruction skipDispose = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, keys));
            il.Append(Instruction.Create(OpCodes.Brfalse, createKeys));
            Instruction keysReady = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Br, keysReady));
            il.Append(createKeys);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldtoken, keys.FieldType));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Type)
                .GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }))));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Activator)
                .GetMethod("CreateInstance", new[] { typeof(Type) }))));
            il.Append(Instruction.Create(OpCodes.Castclass, keys.FieldType));
            il.Append(Instruction.Create(OpCodes.Stfld, keys));
            il.Append(keysReady);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, keys));
            il.Append(Instruction.Create(OpCodes.Castclass, listInterface));
            il.Append(Instruction.Create(OpCodes.Callvirt, listClear));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, values));
            il.Append(Instruction.Create(OpCodes.Brfalse, createValues));
            Instruction valuesReady = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Br, valuesReady));
            il.Append(createValues);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldtoken, values.FieldType));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Type)
                .GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }))));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Activator)
                .GetMethod("CreateInstance", new[] { typeof(Type) }))));
            il.Append(Instruction.Create(OpCodes.Castclass, values.FieldType));
            il.Append(Instruction.Create(OpCodes.Stfld, values));
            il.Append(valuesReady);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, values));
            il.Append(Instruction.Create(OpCodes.Castclass, listInterface));
            il.Append(Instruction.Create(OpCodes.Callvirt, listClear));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Castclass, dictionaryInterface));
            il.Append(Instruction.Create(OpCodes.Callvirt, dictionaryGetEnumerator));
            il.Append(Instruction.Create(OpCodes.Stloc, enumerator));
            il.Append(Instruction.Create(OpCodes.Br, loop));
            Instruction body = Instruction.Create(OpCodes.Nop);
            il.Append(body);
            il.Append(Instruction.Create(OpCodes.Ldloc, enumerator));
            il.Append(Instruction.Create(OpCodes.Callvirt, currentEntry));
            il.Append(Instruction.Create(OpCodes.Stloc, entry));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, keys));
            il.Append(Instruction.Create(OpCodes.Castclass, listInterface));
            il.Append(Instruction.Create(OpCodes.Ldloca, entry));
            il.Append(Instruction.Create(OpCodes.Call, entryKey));
            il.Append(Instruction.Create(OpCodes.Callvirt, listAdd));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, values));
            il.Append(Instruction.Create(OpCodes.Castclass, listInterface));
            il.Append(Instruction.Create(OpCodes.Ldloca, entry));
            il.Append(Instruction.Create(OpCodes.Call, entryValue));
            il.Append(Instruction.Create(OpCodes.Callvirt, listAdd));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(loop);
            il.Append(Instruction.Create(OpCodes.Ldloc, enumerator));
            il.Append(Instruction.Create(OpCodes.Callvirt, moveNext));
            il.Append(Instruction.Create(OpCodes.Brtrue, body));
            il.Append(Instruction.Create(OpCodes.Ldloc, enumerator));
            il.Append(Instruction.Create(OpCodes.Isinst, module.ImportReference(typeof(IDisposable))));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Brfalse, skipDispose));
            il.Append(Instruction.Create(OpCodes.Callvirt, dispose));
            il.Append(skipDispose);
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(Instruction.Create(OpCodes.Ret));
            method.Body.MaxStackSize = 4;
        }
        if (method.Name == "GetInterpolator" && type.FullName == "EZAnimation") {
            string[] fieldNames = {
                "_linear",
                "_backIn", "_backOut", "_backInOut", "_backOutIn",
                "_bounceIn", "_bounceOut", "_bounceInOut", "_bounceOutIn",
                "_circIn", "_circOut", "_circInOut", "_circOutIn",
                "_cubicIn", "_cubicOut", "_cubicInOut", "_cubicOutIn",
                "_elasticIn", "_elasticOut", "_elasticInOut", "_elasticOutIn",
                "_expIn", "_expOut", "_expInOut", "_expOutIn",
                "_quadraticIn", "_quadraticOut", "_quadraticInOut", "_quadraticOutIn",
                "_quarticIn", "_quarticOut", "_quarticInOut", "_quarticOutIn",
                "_quinticIn", "_quinticOut", "_quinticInOut", "_quinticOutIn",
                "_sinusIn", "_sinusOut", "_sinusInOut", "_sinusOutIn", "_spring",
            };
            var fields = fieldNames.Select(name => type.Fields.SingleOrDefault(f => f.Name == name)).ToArray();
            if (fields.Any(f => f == null || !f.IsStatic) || method.IsStatic == false || method.Parameters.Count != 1 ||
                method.ReturnType.FullName != "EZAnimation/Interpolator")
                throw new InvalidDataException("missing EZAnimation interpolator table metadata");
            Instruction[] cases = fields.Select(f => Instruction.Create(OpCodes.Ldsfld, f)).ToArray();
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Switch, cases));
            il.Append(Instruction.Create(OpCodes.Ldsfld, fields[0]));
            il.Append(Instruction.Create(OpCodes.Ret));
            for (int i = 0; i < cases.Length; ++i) {
                il.Append(cases[i]);
                il.Append(Instruction.Create(OpCodes.Ret));
            }
        }
        if (method.Name == ".cctor" && type.FullName == "EZAnimation") {
            var delegates = new[] {
                new { Field = "_backIn", Method = "backIn" },
                new { Field = "_backInOut", Method = "backInOut" },
                new { Field = "_backOut", Method = "backOut" },
                new { Field = "_backOutIn", Method = "backOutIn" },
                new { Field = "_bounceInOut", Method = "bounceInOut" },
                new { Field = "_bounceOut", Method = "bounceOut" },
                new { Field = "_bounceIn", Method = "bounceIn" },
                new { Field = "_bounceOutIn", Method = "bounceOutIn" },
                new { Field = "_circIn", Method = "circIn" },
                new { Field = "_circInOut", Method = "circInOut" },
                new { Field = "_linear", Method = "linear" },
                new { Field = "_circOut", Method = "circOut" },
                new { Field = "_circOutIn", Method = "circOutIn" },
                new { Field = "_cubicIn", Method = "cubicIn" },
                new { Field = "_cubicInOut", Method = "cubicInOut" },
                new { Field = "_cubicOut", Method = "cubicOut" },
                new { Field = "_cubicOutIn", Method = "cubicOutIn" },
                new { Field = "_elasticIn", Method = "elasticIn" },
                new { Field = "_elasticInOut", Method = "elasticInOut" },
                new { Field = "_elasticOut", Method = "elasticOut" },
                new { Field = "_elasticOutIn", Method = "elasticOutIn" },
                new { Field = "_expIn", Method = "expIn" },
                new { Field = "_expInOut", Method = "expInOut" },
                new { Field = "_expOut", Method = "expOut" },
                new { Field = "_expOutIn", Method = "expOutIn" },
                new { Field = "_quadraticIn", Method = "quadraticIn" },
                new { Field = "_quadraticInOut", Method = "quadraticInOut" },
                new { Field = "_quadraticOut", Method = "quadraticOut" },
                new { Field = "_quadraticOutIn", Method = "quadraticOutIn" },
                new { Field = "_quarticIn", Method = "quarticIn" },
                new { Field = "_quarticInOut", Method = "quarticInOut" },
                new { Field = "_quarticOut", Method = "quarticOut" },
                new { Field = "_quarticOutIn", Method = "quarticOutIn" },
                new { Field = "_quinticIn", Method = "quinticIn" },
                new { Field = "_quinticInOut", Method = "quinticInOut" },
                new { Field = "_quinticOut", Method = "quinticOut" },
                new { Field = "_quinticOutIn", Method = "quinticOutIn" },
                new { Field = "_sinusIn", Method = "sinusIn" },
                new { Field = "_sinusInOut", Method = "sinusInOut" },
                new { Field = "_sinusOut", Method = "sinusOut" },
                new { Field = "_sinusOutIn", Method = "sinusOutIn" },
                new { Field = "_spring", Method = "spring" },
            };
            TypeDefinition delegateType = module.GetType("EZAnimation/Interpolator");
            MethodDefinition delegateConstructor = delegateType == null ? null : delegateType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
            if (delegateConstructor == null || delegates.Length != 42)
                throw new InvalidDataException("missing EZAnimation delegate metadata");
            MethodReference constructor = module.ImportReference(delegateConstructor);
            foreach (var entry in delegates) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == entry.Field);
                MethodDefinition callback = type.Methods.SingleOrDefault(m => m.Name == entry.Method &&
                    m.IsStatic && m.Parameters.Count == 4);
                if (field == null || !field.IsStatic || callback == null)
                    throw new InvalidDataException("missing EZAnimation delegate target: " + entry.Method);
                il.Append(Instruction.Create(OpCodes.Ldnull));
                il.Append(Instruction.Create(OpCodes.Ldftn, callback));
                il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }
        }
        if (method.Name == ".cctor" && type.FullName == "EB.Rendering.EBParticlePal") {
            // Cpp2IL retained four Enum.GetValues(...).Length calculations in
            // this initializer but lost the type tokens and static field stores.
            // The enum definitions remain intact; eQUALITY's Off=-1 member is
            // the single sentinel excluded by the recovered final decrement.
            var counts = new[] {
                new { Field = "ePARAMETER_COUNT", Enum = "ePARAMETER", Excluded = 0 },
                new { Field = "eTRIGGER_COUNT", Enum = "eTRIGGER", Excluded = 0 },
                new { Field = "eTUNING_COUNT", Enum = "eTUNING", Excluded = 0 },
                new { Field = "eQUALITY_COUNT", Enum = "eQUALITY", Excluded = 1 },
            };
            foreach (var count in counts) {
                TypeDefinition enumType = type.NestedTypes.SingleOrDefault(t => t.Name == count.Enum);
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == count.Field);
                if (enumType == null || field == null || !field.IsStatic ||
                    field.FieldType.MetadataType != MetadataType.Int32)
                    throw new InvalidDataException("missing EBParticlePal enum count: " + count.Field);
                int values = enumType.Fields.Count(f => f.IsLiteral);
                if (values == 0 || values <= count.Excluded)
                    throw new InvalidDataException("invalid EBParticlePal enum definition: " + count.Enum);
                il.Append(Instruction.Create(OpCodes.Ldc_I4, values - count.Excluded));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }
        }
        if (method.Name == ".cctor" && type.FullName == "UILabel") {
            FieldDefinition config = type.Fields.SingleOrDefault(f => f.Name == "Config");
            TypeDefinition configType = config == null ? null : config.FieldType.Resolve();
            MethodDefinition configConstructor = configType == null ? null : configType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            if (config == null || !config.IsStatic || configConstructor == null)
                throw new InvalidDataException("missing UILabel.Config constructor");
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(configConstructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, config));

            foreach (string fieldName in new[] { "mList", "mFontUsage", "mTempVerts", "mTempIndices" }) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                if (field == null || !field.IsStatic || !(field.FieldType is GenericInstanceType))
                    throw new InvalidDataException("missing UILabel static collection: " + fieldName);
                var constructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                    HasThis = true
                };
                il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }

            FieldDefinition rebuildAdded = type.Fields.SingleOrDefault(f => f.Name == "mTexRebuildAdded");
            if (rebuildAdded == null || !rebuildAdded.IsStatic ||
                rebuildAdded.FieldType.MetadataType != MetadataType.Boolean)
                throw new InvalidDataException("missing UILabel texture rebuild flag");
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stsfld, rebuildAdded));

            MethodDefinition localizationChanged = type.Methods.SingleOrDefault(m =>
                m.Name == "HandleLocalizationChanged" && m.IsStatic && m.Parameters.Count == 0);
            TypeDefinition actionType = module.GetType("EB.Action");
            TypeDefinition localizerType = module.GetType("EB.Localizer");
            if (actionType == null) {
                TypeReference reference = module.GetTypeReferences().SingleOrDefault(t => t.FullName == "EB.Action");
                actionType = reference == null ? null : reference.Resolve();
            }
            if (localizerType == null) {
                TypeReference reference = module.GetTypeReferences().SingleOrDefault(t => t.FullName == "EB.Localizer");
                localizerType = reference == null ? null : reference.Resolve();
            }
            MethodDefinition actionConstructor = actionType == null ? null : actionType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
            MethodDefinition addLocalizationChanged = localizerType == null ? null : localizerType.Methods.SingleOrDefault(m =>
                m.Name == "add_OnLocalizationChanged" && m.IsStatic && m.Parameters.Count == 1);
            if (localizationChanged == null || actionConstructor == null || addLocalizationChanged == null)
                throw new InvalidDataException("missing UILabel localization event metadata");
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Ldftn, localizationChanged));
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(actionConstructor)));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(addLocalizationChanged)));
        }
        if (method.Name == ".cctor" && type.FullName == "EB.UI.PrefabDiff.PrefabDiffTracker") {
            FieldDefinition modifiers = type.Fields.SingleOrDefault(f => f.Name == "_customModifiers");
            FieldDefinition timeslice = type.Fields.SingleOrDefault(f => f.Name == "TimesliceMaxWaitFrames");
            if (modifiers == null || !modifiers.IsStatic || !(modifiers.FieldType is GenericInstanceType) ||
                timeslice == null || !timeslice.IsStatic || timeslice.FieldType.MetadataType != MetadataType.Int32)
                throw new InvalidDataException("missing PrefabDiffTracker static initializer fields");
            var dictionaryConstructor = new MethodReference(".ctor", module.TypeSystem.Void, modifiers.FieldType) {
                HasThis = true
            };
            il.Append(Instruction.Create(OpCodes.Newobj, dictionaryConstructor));
            il.Append(Instruction.Create(OpCodes.Stsfld, modifiers));
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 17));
            il.Append(Instruction.Create(OpCodes.Stsfld, timeslice));
        }
        if (method.Name == ".cctor" && type.FullName == "EB.Localizer") {
            foreach (string fieldName in new[] { "_strings", "_status" }) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                if (field == null || !field.IsStatic || !(field.FieldType is GenericInstanceType))
                    throw new InvalidDataException("missing Localizer collection: " + fieldName);
                var constructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                    HasThis = true
                };
                il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }

            TypeDefinition providerType = type.NestedTypes.SingleOrDefault(t => t.Name == "FormatProvider");
            MethodDefinition providerConstructor = providerType == null ? null : providerType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            FieldDefinition provider = type.Fields.SingleOrDefault(f => f.Name == "_provider");
            if (provider == null || !provider.IsStatic || providerConstructor == null)
                throw new InvalidDataException("missing Localizer format provider");
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(providerConstructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, provider));

            foreach (var flag in new[] {
                new { Name = "ShowStatuses", Value = false },
                new { Name = "ShowStringIds", Value = false },
                new { Name = "HidePlaceHolder", Value = true },
                new { Name = "ShowClientPrefix", Value = false },
            }) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == flag.Name);
                if (field == null || !field.IsStatic || field.FieldType.MetadataType != MetadataType.Boolean)
                    throw new InvalidDataException("missing Localizer display flag: " + flag.Name);
                il.Append(Instruction.Create(flag.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                il.Append(Instruction.Create(OpCodes.Stsfld, field));
            }

            FieldDefinition regexField = type.Fields.SingleOrDefault(f => f.Name == "locRegex");
            TypeDefinition regexType = regexField == null ? null : regexField.FieldType.Resolve();
            MethodDefinition regexConstructor = regexType == null ? null : regexType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.MetadataType == MetadataType.String);
            if (regexField == null || !regexField.IsStatic || regexConstructor == null)
                throw new InvalidDataException("missing Localizer regex metadata");
            // The source regex literal was lost by Cpp2IL. Keep the recovered
            // formatting operation harmless until that literal is recovered.
            il.Append(Instruction.Create(OpCodes.Ldstr, "(?!)"));
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(regexConstructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, regexField));
        }
        if (method.Name == ".cctor" && (type.FullName == "EB.UI.Social.FuseSocialHub" ||
            type.FullName == "EB.UI.SystemMessage.FuseSystemMessageOverlay")) {
            // Both recovered initializers contain one parameterless config
            // construction followed by a store to the corresponding static
            // PresentationConfig field. The constructor call target was lost,
            // but both field types and their constructors remain in metadata.
            FieldDefinition config = type.Fields.SingleOrDefault(f => f.Name == "PresentationConfig");
            TypeDefinition configType = config == null ? null : config.FieldType.Resolve();
            MethodDefinition configConstructor = configType == null ? null : configType.Methods.SingleOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            if (config == null || !config.IsStatic || configConstructor == null)
                throw new InvalidDataException("missing presentation config initializer metadata: " + type.FullName);
            il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(configConstructor)));
            il.Append(Instruction.Create(OpCodes.Stsfld, config));
        }
        if (method.Name == "add_OnLocalizationChanged" && type.FullName == "EB.Localizer") {
            FieldDefinition handlers = type.Fields.FirstOrDefault(f => f.Name == "OnLocalizationChanged");
            if (handlers == null || !handlers.IsStatic || handlers.FieldType.FullName != "EB.Action" ||
                !method.IsStatic || method.Parameters.Count != 1 ||
                method.Parameters[0].ParameterType.FullName != "EB.Action")
                throw new InvalidDataException("missing Localizer localization event backing field");
            AssemblyNameReference core = module.AssemblyReferences.FirstOrDefault(a => a.Name == "mscorlib");
            if (core == null) throw new InvalidDataException("mscorlib reference is missing");
            var delegateType = new TypeReference("System", "Delegate", module, core);
            var combine = new MethodReference("Combine", delegateType, delegateType);
            combine.Parameters.Add(new ParameterDefinition(delegateType));
            combine.Parameters.Add(new ParameterDefinition(delegateType));
            il.Append(Instruction.Create(OpCodes.Ldsfld, handlers));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, combine));
            il.Append(Instruction.Create(OpCodes.Castclass, handlers.FieldType));
            il.Append(Instruction.Create(OpCodes.Stsfld, handlers));
        }
        if (method.Name == ".ctor") {
            if (type.BaseType == null) throw new InvalidDataException("constructor type has no base: " + type.FullName);
            if (type.BaseType.FullName != "System.Object") {
                TypeDefinition baseDefinition = type.BaseType.Resolve();
                MethodDefinition baseConstructor = baseDefinition.Methods.FirstOrDefault(m =>
                    m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
                if (baseConstructor == null)
                    throw new InvalidDataException("base has no parameterless constructor: " + type.BaseType.FullName);
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Call, method.Module.ImportReference(baseConstructor)));
            }
            // The Cpp2IL body for this constructor contains invalid branches and
            // unresolved operands, but its field initializers are unambiguous.
            // Rebuild those five collection initializers rather than dropping
            // the GameboardBuilder runtime state entirely.
            if (type.FullName == "Quests.Presentation.GameboardBuilder") {
                string[] initializedFields = {
                    "TileToNodeController", "_NodeNumbers", "_Towers", "_Relics", "BaseNodes"
                };
                foreach (string fieldName in initializedFields) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                    if (field == null || field.IsStatic)
                        throw new InvalidDataException("missing GameboardBuilder field: " + fieldName);
                    var collectionConstructor = new MethodReference(".ctor", moduleVoid(field), field.FieldType) {
                        HasThis = true
                    };
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Newobj, collectionConstructor));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }
            }
            if (type.FullName == "AVEBattlegroupSelectionPanel") {
                FieldDefinition colors = type.Fields.SingleOrDefault(f => f.Name == "BattlegroupColours");
                ArrayType colorArray = colors == null ? null : colors.FieldType as ArrayType;
                if (colors == null || colors.IsStatic || colorArray == null)
                    throw new InvalidDataException("missing AVEBattlegroupSelectionPanel color array");
                AssemblyNameReference core = module.AssemblyReferences.SingleOrDefault(a => a.Name == "UnityEngine.CoreModule");
                if (core == null) throw new InvalidDataException("UnityEngine.CoreModule reference is missing");
                var color = new TypeReference("UnityEngine", "Color", module, core) { IsValueType = true };
                var color32 = new TypeReference("UnityEngine", "Color32", module, core) { IsValueType = true };
                var color32Constructor = new MethodReference(".ctor", module.TypeSystem.Void, color32) { HasThis = true };
                for (int i = 0; i < 4; i++) color32Constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Byte));
                var colorConversion = new MethodReference("op_Implicit", color, color32);
                colorConversion.Parameters.Add(new ParameterDefinition(color32));
                int[][] rgba = { new[] { 0, 195, 255, 255 }, new[] { 196, 93, 255, 255 }, new[] { 255, 166, 0, 255 } };
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
                il.Append(Instruction.Create(OpCodes.Newarr, colorArray.ElementType));
                for (int index = 0; index < rgba.Length; index++) {
                    il.Append(Instruction.Create(OpCodes.Dup));
                    il.Append(Instruction.Create(OpCodes.Ldc_I4, index));
                    foreach (int channel in rgba[index]) il.Append(Instruction.Create(OpCodes.Ldc_I4, channel));
                    il.Append(Instruction.Create(OpCodes.Newobj, color32Constructor));
                    il.Append(Instruction.Create(OpCodes.Call, colorConversion));
                    il.Append(Instruction.Create(OpCodes.Stelem_Any, colorArray.ElementType));
                }
                il.Append(Instruction.Create(OpCodes.Stfld, colors));
            }
            if (type.FullName == "WindowStateHelper") {
                FieldDefinition states = type.Fields.SingleOrDefault(f => f.Name == "WindowStates");
                FieldDefinition stateStack = type.Fields.SingleOrDefault(f => f.Name == "_stateStack");
                FieldDefinition strategy = type.Fields.SingleOrDefault(f => f.Name == "InstantiationStrategy");
                MethodDefinition defaultStrategy = type.Methods.SingleOrDefault(m =>
                    m.Name == "DefaultInstantiationStrategy" && m.IsStatic && m.Parameters.Count == 2);
                MethodDefinition delegateConstructor = strategy == null ? null : strategy.FieldType.Resolve().Methods.SingleOrDefault(m =>
                    m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
                    m.Parameters[1].ParameterType.MetadataType == MetadataType.IntPtr);
                if (states == null || stateStack == null || strategy == null ||
                    defaultStrategy == null || delegateConstructor == null)
                    throw new InvalidDataException("missing WindowStateHelper constructor metadata");
                MethodReference statesConstructor = new MethodReference(".ctor", module.TypeSystem.Void, states.FieldType) {
                    HasThis = true
                };
                MethodReference stackConstructor = new MethodReference(".ctor", module.TypeSystem.Void, stateStack.FieldType) {
                    HasThis = true
                };
                MethodReference strategyConstructor = new MethodReference(".ctor", module.TypeSystem.Void, strategy.FieldType) {
                    HasThis = true
                };
                strategyConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                strategyConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Newobj, statesConstructor));
                il.Append(Instruction.Create(OpCodes.Stfld, states));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Newobj, stackConstructor));
                il.Append(Instruction.Create(OpCodes.Stfld, stateStack));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldnull));
                il.Append(Instruction.Create(OpCodes.Ldftn, module.ImportReference(defaultStrategy)));
                il.Append(Instruction.Create(OpCodes.Newobj, strategyConstructor));
                il.Append(Instruction.Create(OpCodes.Stfld, strategy));
            }
            if (type.FullName == "BadgeManager") {
                // Rebuild the field initializers from the native constructor.
                // Cpp2IL's translated constructor has malformed branches and
                // null operands; the ARM64 body shows precisely which fields
                // are initialized, including the two compiler-emitted enum
                // arrays and six one-item tag lists.
                TypeDefinition details = module.Types.SingleOrDefault(t => t.Name == "<PrivateImplementationDetails>");
                FieldDefinition gameStoreData = details == null ? null : details.Fields.SingleOrDefault(f =>
                    f.Name == "8507D52074E89CEA466A9D7177C3137C7B140EC512EF393EBE02EA24C9541081");
                FieldDefinition gachaData = details == null ? null : details.Fields.SingleOrDefault(f =>
                    f.Name == "3EE179651D67B1186F407C310DB8C5EDC56790AAA6E925879E4548358D7F5D10");
                FieldDefinition gameStoreTags = type.Fields.SingleOrDefault(f => f.Name == "_tagGameStore");
                FieldDefinition gachaTags = type.Fields.SingleOrDefault(f => f.Name == "_tagGachaNew");
                if (gameStoreTags == null || gachaTags == null ||
                    !(gameStoreTags.FieldType is ArrayType) || !(gachaTags.FieldType is ArrayType) ||
                    gameStoreData == null || gachaData == null ||
                    !gameStoreData.Attributes.HasFlag(FieldAttributes.HasFieldRVA) || gameStoreData.InitialValue.Length != 28 ||
                    !gachaData.Attributes.HasFlag(FieldAttributes.HasFieldRVA) || gachaData.InitialValue.Length != 12)
                    throw new InvalidDataException("missing BadgeManager enum array RVA initializers");
                MethodReference initializeArray = module.ImportReference(typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                    .GetMethod("InitializeArray", new[] { typeof(Array), typeof(RuntimeFieldHandle) }));
                foreach (var initializer in new[] {
                    new { Field = gameStoreTags, Data = gameStoreData, Count = 7 },
                    new { Field = gachaTags, Data = gachaData, Count = 3 }
                }) {
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Ldc_I4, initializer.Count));
                    il.Append(Instruction.Create(OpCodes.Newarr, ((ArrayType)initializer.Field.FieldType).ElementType));
                    il.Append(Instruction.Create(OpCodes.Dup));
                    il.Append(Instruction.Create(OpCodes.Ldtoken, initializer.Data));
                    il.Append(Instruction.Create(OpCodes.Call, initializeArray));
                    il.Append(Instruction.Create(OpCodes.Stfld, initializer.Field));
                }

                string[] emptyListFields = {
                    "seenTags", "_badges", "_priorityQueues", "_cachedCounts",
                    "_previouslyCachedCounts"
                };
                foreach (string fieldName in emptyListFields) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                    if (field == null || !(field.FieldType is GenericInstanceType))
                        throw new InvalidDataException("missing BadgeManager list field: " + fieldName);
                    var constructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                        HasThis = true
                    };
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }

                foreach (var initializer in new[] {
                    new { Field = "_featuredTags", Value = "featured" },
                    new { Field = "_potionTags", Value = "potion" },
                    new { Field = "_boostTags", Value = "boost" },
                    new { Field = "_vsTags", Value = "vs" },
                    new { Field = "_shldTags", Value = "shld" },
                    new { Field = "_masteryTags", Value = "mastery" }
                }) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == initializer.Field);
                    if (field == null || !(field.FieldType is GenericInstanceType) ||
                        ((GenericInstanceType)field.FieldType).GenericArguments[0].FullName != "System.String")
                        throw new InvalidDataException("missing BadgeManager string tag list: " + initializer.Field);
                    var constructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                        HasThis = true
                    };
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Ldfld, field));
                    il.Append(Instruction.Create(OpCodes.Castclass, module.ImportReference(typeof(System.Collections.IList))));
                    il.Append(Instruction.Create(OpCodes.Ldstr, initializer.Value));
                    MethodReference add = module.ImportReference(typeof(System.Collections.IList).GetMethod("Add"));
                    il.Append(Instruction.Create(OpCodes.Callvirt, add));
                    il.Append(Instruction.Create(OpCodes.Pop));
                }

                foreach (string fieldName in new[] { "screenTagBadgeLookup", "_gameStoreItemCache" }) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                    if (field == null || !(field.FieldType is GenericInstanceType))
                        throw new InvalidDataException("missing BadgeManager dictionary field: " + fieldName);
                    var constructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                        HasThis = true
                    };
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }
            }
            if (type.FullName == "BuildingPortrait") {
                // The native constructor initializes only these values after
                // MonoBehaviour::.ctor: its large portrait default, a zero
                // pending size, and the two serialized visibility flags.
                FieldDefinition largeSize = type.Fields.SingleOrDefault(f => f.Name == "PortraitSizeLarge");
                FieldDefinition defaultSize = type.Fields.SingleOrDefault(f => f.Name == "defaultSize");
                FieldDefinition pendingSize = type.Fields.SingleOrDefault(f => f.Name == "_setSizeBeforeInit");
                if (largeSize == null || !largeSize.IsStatic || defaultSize == null || defaultSize.IsStatic ||
                    pendingSize == null || pendingSize.IsStatic || largeSize.FieldType.FullName != defaultSize.FieldType.FullName ||
                    defaultSize.FieldType.FullName != pendingSize.FieldType.FullName)
                    throw new InvalidDataException("missing BuildingPortrait size initializer fields");
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldsfld, largeSize));
                il.Append(Instruction.Create(OpCodes.Stfld, defaultSize));
                VariableDefinition zeroSize = new VariableDefinition(pendingSize.FieldType);
                method.Body.Variables.Add(zeroSize);
                il.Append(Instruction.Create(OpCodes.Ldloca, zeroSize));
                il.Append(Instruction.Create(OpCodes.Initobj, pendingSize.FieldType));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldloc, zeroSize));
                il.Append(Instruction.Create(OpCodes.Stfld, pendingSize));
                foreach (string fieldName in new[] { "_showBoostDetails", "_isUserBuilding" }) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                    if (field == null || field.IsStatic || field.FieldType.MetadataType != MetadataType.Boolean)
                        throw new InvalidDataException("missing BuildingPortrait constructor flag: " + fieldName);
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }
            }
            if (type.FullName == "AllianceStatsPopup") {
                // The translated dictionary initializer lost all of its key
                // and value operands, so it currently throws on the first
                // Add(null, null). Preserve the constructor's collection
                // contract; the presentation fills this cache as it loads.
                FieldDefinition stats = type.Fields.SingleOrDefault(f => f.Name == "mStats");
                if (stats == null || stats.IsStatic || !(stats.FieldType is GenericInstanceType))
                    throw new InvalidDataException("missing AllianceStatsPopup stats dictionary");
                var constructor = new MethodReference(".ctor", module.TypeSystem.Void, stats.FieldType) {
                    HasThis = true
                };
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                il.Append(Instruction.Create(OpCodes.Stfld, stats));
            }
            if (type.FullName == "GachaRevealPresentation") {
                // Cpp2IL loses the operands for every placeholder mapping
                // entry, producing Dictionary.Add(null, null). Keep the
                // mapping available as a valid empty cache until those
                // source strings can be matched to their native literals.
                FieldDefinition mapping = type.Fields.SingleOrDefault(f => f.Name == "placeholderMapping");
                FieldDefinition portraitScale = type.Fields.SingleOrDefault(f => f.Name == "_portraitScale");
                if (mapping == null || mapping.IsStatic || !(mapping.FieldType is GenericInstanceType) ||
                    portraitScale == null || portraitScale.IsStatic ||
                    portraitScale.FieldType.MetadataType != MetadataType.Single)
                    throw new InvalidDataException("missing GachaRevealPresentation constructor fields");
                var constructor = new MethodReference(".ctor", module.TypeSystem.Void, mapping.FieldType) {
                    HasThis = true
                };
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Newobj, constructor));
                il.Append(Instruction.Create(OpCodes.Stfld, mapping));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 1.0f));
                il.Append(Instruction.Create(OpCodes.Stfld, portraitScale));
            }
            if (type.FullName == "EB.Rendering.EBParticlePal") {
                string[] initializedFields = { "conditions", "isEnabled" };
                foreach (string fieldName in initializedFields) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                    if (field == null || field.IsStatic || !(field.FieldType is GenericInstanceType))
                        throw new InvalidDataException("missing EBParticlePal collection: " + fieldName);
                    var collectionConstructor = new MethodReference(".ctor", module.TypeSystem.Void, field.FieldType) {
                        HasThis = true
                    };
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Newobj, collectionConstructor));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }
                FieldDefinition damping = type.Fields.SingleOrDefault(f => f.Name == "VelocityDamping");
                FieldDefinition position = type.Fields.SingleOrDefault(f => f.Name == "lastPosition");
                FieldDefinition quality = type.Fields.SingleOrDefault(f => f.Name == "quality");
                if (damping == null || position == null || quality == null || quality.IsStatic)
                    throw new InvalidDataException("missing EBParticlePal instance defaults");
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldc_R4, 0.5f));
                il.Append(Instruction.Create(OpCodes.Stfld, damping));
                VariableDefinition zeroPosition = new VariableDefinition(position.FieldType);
                method.Body.Variables.Add(zeroPosition);
                il.Append(Instruction.Create(OpCodes.Ldloca, zeroPosition));
                il.Append(Instruction.Create(OpCodes.Initobj, position.FieldType));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldloc, zeroPosition));
                il.Append(Instruction.Create(OpCodes.Stfld, position));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
                il.Append(Instruction.Create(OpCodes.Stfld, quality));
            }
            if (type.FullName == "EB.Rendering.EBParticlePal/Condition") {
                var objectConstructor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) {
                    HasThis = true
                };
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Call, objectConstructor));
                foreach (string enumFieldName in new[] { "Parameter", "Trigger" }) {
                    FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == enumFieldName);
                    if (field == null || field.IsStatic)
                        throw new InvalidDataException("missing EBParticlePal Condition field: " + enumFieldName);
                    il.Append(Instruction.Create(OpCodes.Ldarg_0));
                    il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
                    il.Append(Instruction.Create(OpCodes.Stfld, field));
                }
                FieldDefinition expanded = type.Fields.SingleOrDefault(f => f.Name == "Expanded");
                FieldDefinition tunings = type.Fields.SingleOrDefault(f => f.Name == "Tunings");
                ArrayType tuningArray = tunings == null ? null : tunings.FieldType as ArrayType;
                TypeDefinition parent = module.GetType("EB.Rendering.EBParticlePal");
                FieldDefinition tuningCount = parent == null ? null : parent.Fields.SingleOrDefault(f => f.Name == "eTUNING_COUNT");
                if (expanded == null || tunings == null || tuningArray == null ||
                    tuningCount == null || !tuningCount.IsStatic)
                    throw new InvalidDataException("missing EBParticlePal Condition initializer fields");
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
                il.Append(Instruction.Create(OpCodes.Stfld, expanded));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldsfld, tuningCount));
                il.Append(Instruction.Create(OpCodes.Newarr, tuningArray.ElementType));
                il.Append(Instruction.Create(OpCodes.Stfld, tunings));
                VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
                method.Body.Variables.Add(index);
                Instruction loop = Instruction.Create(OpCodes.Ldloc, index);
                Instruction done = Instruction.Create(OpCodes.Ret);
                il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
                il.Append(Instruction.Create(OpCodes.Stloc, index));
                il.Append(loop);
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldfld, tunings));
                il.Append(Instruction.Create(OpCodes.Ldlen));
                il.Append(Instruction.Create(OpCodes.Conv_I4));
                il.Append(Instruction.Create(OpCodes.Bge, done));
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldfld, tunings));
                il.Append(Instruction.Create(OpCodes.Ldloc, index));
                MethodDefinition tuningConstructor = tuningArray.ElementType.Resolve().Methods.Single(m =>
                    m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
                il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(tuningConstructor)));
                il.Append(Instruction.Create(OpCodes.Stelem_Ref));
                il.Append(Instruction.Create(OpCodes.Ldloc, index));
                il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
                il.Append(Instruction.Create(OpCodes.Add));
                il.Append(Instruction.Create(OpCodes.Stloc, index));
                il.Append(Instruction.Create(OpCodes.Br, loop));
                il.Append(done);
            }
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        method.Body.InitLocals = false;
        if (method.Name == ".cctor")
            method.Body.MaxStackSize = type.FullName == "Quests.Presentation.GameboardBuilder" ||
                type.FullName == "EB.Rendering.EBParticlePal" ||
            type.FullName == "EB.UI.PrefabDiff.PrefabDiffTracker" ||
                type.FullName == "EB.Localizer" ||
                type.FullName == "EB.UI.Social.FuseSocialHub" ||
                type.FullName == "EB.UI.SystemMessage.FuseSystemMessageOverlay" ||
                type.FullName == "EBWorldPainterData" ? 1 :
                type.FullName == "EB.MoveEditor.PrefabLib" ? 4 :
                type.FullName == "EZAnimation" ? 2 :
                type.FullName == "CriticalError" ? 1 :
                type.FullName == "CriticalError/ConfigData/<>c" ? 1 :
                type.FullName == "BuffsController" ? 1 :
                type.FullName == "UILabel" ? 2 : 0;
        else if (method.Name == ".ctor" && type.FullName == "EB.Rendering.EBParticlePal/Condition")
            method.Body.MaxStackSize = 3;
        else if (method.Name == ".ctor" && type.FullName == "AVEBattlegroupSelectionPanel")
            method.Body.MaxStackSize = 7;
        else if (method.Name == "GetInterpolator" && type.FullName == "EZAnimation")
            method.Body.MaxStackSize = 1;
        else if (method.Name == ".ctor" && type.FullName == "CriticalError/ConfigData")
            method.Body.MaxStackSize = 2;
        else if (method.Name == ".ctor" && (type.FullName == "EB.UI.Social.FuseSocialHub/FuseSocialHubPresentationConfig" ||
            type.FullName == "EB.UI.SystemMessage.FuseSystemMessageOverlay/SystemMessagePresentationConfig"))
            method.Body.MaxStackSize = 1;
        else if (method.Name == ".ctor" && (type.FullName == "EB.Rendering.EBParticlePal" ||
            type.FullName == "Quests.Presentation.GameboardBuilder"))
            method.Body.MaxStackSize = 2;
        else if (method.Name == ".ctor" && type.FullName == "WindowStateHelper")
            method.Body.MaxStackSize = 3;
        else if (method.Name == ".ctor" && type.FullName == "BadgeManager")
            method.Body.MaxStackSize = 4;
        else if (method.Name == ".ctor" && type.FullName == "BuildingPortrait")
            method.Body.MaxStackSize = 2;
        else if (method.Name == ".ctor" && (type.FullName == "AllianceStatsPopup" ||
            type.FullName == "GachaRevealPresentation"))
            method.Body.MaxStackSize = 2;
        else if (method.Name == ".ctor" &&
            type.FullName == "Facebook.Unity.Settings.FacebookSettings/UrlSchemes")
            method.Body.MaxStackSize = 2;
        else if (method.Name == "add_OnLocalizationChanged" && type.FullName == "EB.Localizer")
            method.Body.MaxStackSize = 2;
        else
            method.Body.MaxStackSize = method.Name == ".ctor" && type.BaseType != null &&
                type.BaseType.FullName != "System.Object" ? 1 : 0;
        return true;
    }

    static int RepairEditorFieldAccess(TypeDefinition type, string assemblyName,
        string targetStringLengthField, IDictionary<string, string> targetFieldAliases) {
        int changes = 0;
        if (targetStringLengthField != "m_stringLength" || targetFieldAliases.Count > 0) {
            foreach (MethodDefinition method in type.Methods) {
                if (!method.HasBody) continue;
                foreach (Instruction instruction in method.Body.Instructions) {
                    FieldReference field = instruction.Operand as FieldReference;
                    if (field == null) continue;
                    if (field.Name == "m_stringLength" &&
                        field.DeclaringType.FullName == "System.String" &&
                        field.FieldType.MetadataType == MetadataType.Int32 &&
                        targetStringLengthField != "m_stringLength") {
                        // Unity 6 renamed Mono's private String length field.
                        // Keep each original load/store opcode and retarget its
                        // reference to the corresponding editor-corelib field.
                        instruction.Operand = new FieldReference(targetStringLengthField,
                            method.Module.TypeSystem.Int32,
                            method.Module.ImportReference(field.DeclaringType));
                        changes++;
                        continue;
                    }
                    string alias;
                    string declaringName = field.DeclaringType.FullName;
                    string aliasKey = null;
                    if (declaringName.StartsWith("System.Collections.Generic.Dictionary`2<",
                        StringComparison.Ordinal))
                        aliasKey = "System.Collections.Generic.Dictionary`2|" + field.Name;
                    else if (declaringName.StartsWith("System.Collections.Generic.List`1<",
                        StringComparison.Ordinal) && field.Name == "_emptyArray")
                        aliasKey = "System.Collections.Generic.List`1|_emptyArray";
                    else if (declaringName == "System.Collections.Hashtable")
                        aliasKey = "System.Collections.Hashtable|" + field.Name;
                    else if (declaringName == "System.UnhandledExceptionEventArgs" &&
                        field.Name == "_Exception")
                        aliasKey = "System.UnhandledExceptionEventArgs|_Exception";
                    else if (declaringName == "System.Text.RegularExpressions.Capture")
                        aliasKey = "System.Text.RegularExpressions.Capture|" + field.Name;
                    else if (declaringName == "System.Net.IPEndPoint")
                        aliasKey = "System.Net.IPEndPoint|" + field.Name;
                    if (aliasKey == null || !targetFieldAliases.TryGetValue(aliasKey, out alias))
                        continue;
                    // These Unity 2020 to Unity 6 corelib private-field renames
                    // preserve the original instruction and its field type.
                    instruction.Operand = new FieldReference(alias,
                        method.Module.ImportReference(field.FieldType),
                        method.Module.ImportReference(field.DeclaringType));
                    changes++;
                }
            }
        }
        if (assemblyName == "Assembly-CSharp.dll" && type.FullName == "TutorialWidget") {
            FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == "_offset");
            if (field != null && field.IsPrivate) {
                // Recovered derived constructors write this field; the original
                // source access therefore requires protected visibility.
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Family;
                changes++;
            }
        }
        if (assemblyName == "Assembly-CSharp-firstpass.dll" && type.FullName == "UISlider") {
            FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == "rawValue");
            if (field != null && field.IsPrivate) {
                // UIScrollBar's recovered constructor writes the inherited value.
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Family;
                changes++;
            }
        }
        if (assemblyName == "Assembly-CSharp-firstpass.dll" && type.FullName == "EB.SafeValue") {
            MethodDefinition initializer = type.Methods.SingleOrDefault(m => m.Name == "Init" &&
                m.Parameters.Count == 1 && m.Parameters[0].ParameterType is ArrayType array &&
                array.ElementType.MetadataType == MetadataType.Byte);
            if (initializer != null && initializer.IsPrivate) {
                // SafeFloat and SafeValue are in the same recovered assembly;
                // the source helper must be assembly-visible to its sibling.
                initializer.Attributes = (initializer.Attributes & ~MethodAttributes.MemberAccessMask) |
                    MethodAttributes.Assembly;
                changes++;
            }
        }
        if (assemblyName == "Assembly-CSharp.dll" && type.FullName == "TFormMatineeStage") {
            FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == "_autoCorrectActorMirroring");
            if (field != null && field.IsPrivate) {
                // TFormGachaMatineeStage accesses the same inherited setting.
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Family;
                changes++;
            }
        }
        if (type.FullName == "Fabric.SerializableDictionary`2") {
            foreach (string fieldName in new[] { "keys", "values" }) {
                FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
                if (field != null && field.IsPrivate) {
                    // Mono's recovered generic specialization rejects the
                    // original private FieldDef references inside this
                    // callback. Assembly visibility preserves serialization
                    // while allowing its reconstructed implementation to run.
                    field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) |
                        FieldAttributes.Assembly;
                    changes++;
                }
            }
        }

        if (assemblyName != "Assembly-CSharp.dll" && assemblyName != "Assembly-CSharp-firstpass.dll")
            return changes;
        foreach (MethodDefinition method in type.Methods) {
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions) {
                FieldReference field = instruction.Operand as FieldReference;
                if (field == null || field.Name != "_size" ||
                    !field.DeclaringType.FullName.StartsWith(
                        "System.Collections.Generic.List`1<", StringComparison.Ordinal))
                    continue;
                if (instruction.OpCode != OpCodes.Ldfld || field.FieldType.MetadataType != MetadataType.Int32) {
                    Console.WriteLine("retained List<T>._size write for follow-up: " + method.FullName +
                        " (" + instruction.OpCode + ")");
                    continue;
                }
                // Cpp2IL emits direct reads of List<T>._size. IL2CPP tolerates
                // this, but Mono enforces the framework field's private access
                // during Unity import. Count returns the same value and works
                // under both Editor Mono and the Android IL2CPP build.
                var getter = new MethodReference("get_Count", method.Module.TypeSystem.Int32,
                    field.DeclaringType) { HasThis = true };
                instruction.OpCode = OpCodes.Callvirt;
                instruction.Operand = getter;
                changes++;
            }
        }
        return changes;
    }

    static TypeReference moduleVoid(FieldDefinition field) {
        return field.Module.TypeSystem.Void;
    }

    static void ReplaceMatchingMonoSecurity(string pluginDirectory, string frameworkAssemblyPath) {
        string recoveredPath = Path.Combine(pluginDirectory, "Mono.Security.dll");
        if (!File.Exists(recoveredPath) || !File.Exists(frameworkAssemblyPath)) return;

        using (AssemblyDefinition recovered = AssemblyDefinition.ReadAssembly(recoveredPath))
        using (AssemblyDefinition framework = AssemblyDefinition.ReadAssembly(frameworkAssemblyPath)) {
            if (!String.Equals(recovered.Name.FullName, framework.Name.FullName, StringComparison.Ordinal))
                throw new InvalidDataException("Unity Mono.Security identity does not match recovered dependency");
            TypeDefinition pkcs12 = framework.MainModule.GetType("Mono.Security.X509.PKCS12");
            bool hasExistingParameters = pkcs12 != null && pkcs12.Methods.Any(method =>
                method.Name == "GetExistingParameters" && method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType is ByReferenceType byReference &&
                byReference.ElementType.FullName == "System.Boolean");
            if (!hasExistingParameters)
                throw new InvalidDataException("Unity Mono.Security is missing PKCS12.GetExistingParameters(Boolean&)");
        }
        File.Copy(frameworkAssemblyPath, recoveredPath, true);
        Console.WriteLine("replaced Mono.Security with the identity-matched Unity 4.5 profile assembly");
    }

    static int RepairMalformedZipAesInitializer(string assemblyName, TypeDefinition type) {
        if (assemblyName != "ICSharpCode.SharpZipLib.dll" ||
            type.FullName != "ICSharpCode.SharpZipLib.Zip.Compression.Streams.DeflaterOutputStream")
            return 0;

        MethodDefinition method = type.Methods.SingleOrDefault(candidate =>
            candidate.Name == "InitializeAESPassword" && candidate.Parameters.Count == 4);
        if (method == null)
            throw new InvalidDataException("missing expected SharpZipLib AES initializer");
        if (method.ReturnType.MetadataType != MetadataType.Void ||
            method.Parameters[0].ParameterType.FullName != "ICSharpCode.SharpZipLib.Zip.ZipEntry" ||
            method.Parameters[1].ParameterType.MetadataType != MetadataType.String)
            throw new InvalidDataException("unexpected SharpZipLib AES initializer signature");

        ParameterDefinition[] outputBuffers = method.Parameters.Where(parameter => {
            ByReferenceType byReference = parameter.ParameterType as ByReferenceType;
            ArrayType array = byReference == null ? null : byReference.ElementType as ArrayType;
            return array != null && array.ElementType.MetadataType == MetadataType.Byte;
        }).ToArray();
        if (outputBuffers.Length != 2 || !method.HasBody)
            throw new InvalidDataException("unexpected SharpZipLib AES output parameters or body");

        bool hasRecoveredDebugArtifacts = method.Body.Instructions.Any(instruction =>
            instruction.Operand is string text &&
            (text.Contains("Unmanaged memory load:") || text.Contains("Method not found @")));
        if (!hasRecoveredDebugArtifacts) return 0;

        // Cpp2IL emitted an invalid IL2CPP-to-IL body here (including pseudo-IL
        // debug strings and malformed by-reference stack state). AES ZIP
        // initialization is not reconstructed; keep the API callable by clearing
        // its two output buffers, matching the local import stub's safe fallback.
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 1;
        ILProcessor il = method.Body.GetILProcessor();
        foreach (ParameterDefinition output in outputBuffers) {
            il.Append(Instruction.Create(OpCodes.Ldarg, output));
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Stind_Ref));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairIl2CppRejectedMethods(AssemblyDefinition assembly, string assemblyName,
        IEnumerable<string[]> unsupportedMethods) {
        int repaired = 0;
        foreach (string[] plan in unsupportedMethods) {
            if (plan[0] != assemblyName) continue;
            string typeName = plan[1];
            string methodName = plan[2];
            int parameterCount = Int32.Parse(plan[3]);
            string diagnosticSignature = plan[4];
            MethodDefinition[] candidates = AllTypes(assembly.MainModule.Types)
                .Where(type => type.FullName == typeName)
                .SelectMany(type => type.Methods)
                .Where(method => method.Name == methodName && method.Parameters.Count == parameterCount)
                .ToArray();
            MethodDefinition[] exact = candidates.Where(method => method.FullName == diagnosticSignature).ToArray();
            if (exact.Length == 1) candidates = exact;
            if (candidates.Length != 1)
                throw new InvalidDataException("diagnosed IL2CPP method did not resolve uniquely: " + diagnosticSignature);

            MethodDefinition target = candidates[0];
            if (target.IsConstructor || !target.HasBody || target.IsAbstract || target.IsPInvokeImpl ||
                (target.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                throw new InvalidDataException("refusing to stub a constructor or bodyless method: " + target.FullName);

            // Unity IL2CPP explicitly rejected this recovered Cpp2IL body. Keep
            // the API signature and fail clearly if runtime code invokes it.
            target.Body.ExceptionHandlers.Clear();
            target.Body.Variables.Clear();
            target.Body.Instructions.Clear();
            target.Body.InitLocals = false;
            target.Body.MaxStackSize = 1;
            ILProcessor il = target.Body.GetILProcessor();
            MethodReference ctor = assembly.MainModule.ImportReference(
                typeof(NotSupportedException).GetConstructor(Type.EmptyTypes));
            il.Append(Instruction.Create(OpCodes.Newobj, ctor));
            il.Append(Instruction.Create(OpCodes.Throw));
            Console.WriteLine("stubbed IL2CPP-rejected recovered method: " + target.FullName);
            repaired++;
        }
        return repaired;
    }

    static FieldDefinition RequireStoryPanelField(TypeDefinition type, string name,
        MetadataType expectedType) {
        FieldDefinition[] fields = type.Fields.Where(field => field.Name == name).ToArray();
        bool typeMatches = fields.Length == 1 &&
            (fields[0].FieldType.MetadataType == expectedType ||
             (expectedType == MetadataType.Class && fields[0].FieldType is GenericInstanceType));
        if (!typeMatches)
            throw new InvalidDataException("unexpected story panel field: " + type.FullName + "::" + name);
        return fields[0];
    }

    static void EmitStoryPanelList(ILProcessor il, MethodDefinition method, FieldDefinition field) {
        TypeReference listType = field.FieldType;
        GenericInstanceType genericList = listType as GenericInstanceType;
        if (genericList == null || genericList.ElementType.FullName != "System.Collections.Generic.List`1")
            throw new InvalidDataException("story panel collection is not List<T>: " + field.FullName);
        var constructor = new MethodReference(".ctor", method.Module.TypeSystem.Void, listType) {
            HasThis = true,
            CallingConvention = MethodCallingConvention.Default
        };
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(constructor)));
        il.Append(Instruction.Create(OpCodes.Stfld, field));
    }

    static void EmitStoryPanelFloat(ILProcessor il, MethodDefinition method, FieldDefinition field,
        float value) {
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, value));
        il.Append(Instruction.Create(OpCodes.Stfld, field));
    }

    static void EmitStoryPanelInt(ILProcessor il, MethodDefinition method, FieldDefinition field,
        int value) {
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, value));
        il.Append(Instruction.Create(OpCodes.Stfld, field));
    }

    static void EmitStoryPanelBaseCall(ILProcessor il, MethodDefinition method) {
        TypeDefinition baseType = method.DeclaringType.BaseType.Resolve();
        MethodDefinition constructor = baseType.Methods.SingleOrDefault(candidate =>
            candidate.IsConstructor && !candidate.IsStatic && candidate.Parameters.Count == 0);
        if (constructor == null)
            throw new InvalidDataException("missing parameterless base constructor for " + method.DeclaringType.FullName);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, method.Module.ImportReference(constructor)));
        il.Append(Instruction.Create(OpCodes.Ret));
    }

    static void EmitChapterPanelColor(ILProcessor il, MethodDefinition method,
        FieldDefinition field, byte red, byte green, byte blue, byte alpha) {
        if (field.FieldType.MetadataType != MetadataType.ValueType || field.FieldType.FullName != "UnityEngine.Color")
            throw new InvalidDataException("unexpected ChapterPanel color field: " + field.FullName);
        TypeReference color32 = method.Module.GetTypeReferences().SingleOrDefault(reference =>
            reference.FullName == "UnityEngine.Color32");
        if (color32 == null)
            throw new InvalidDataException("missing UnityEngine.Color32 reference for " + field.FullName);

        var constructor = new MethodReference(".ctor", method.Module.TypeSystem.Void, color32) {
            HasThis = true,
            CallingConvention = MethodCallingConvention.Default
        };
        foreach (int ignored in new[] { 0, 1, 2, 3 })
            constructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Byte));
        var conversion = new MethodReference("op_Implicit", field.FieldType, color32) {
            HasThis = false,
            CallingConvention = MethodCallingConvention.Default
        };
        conversion.Parameters.Add(new ParameterDefinition(color32));

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)red));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)green));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)blue));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)alpha));
        il.Append(Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(constructor)));
        il.Append(Instruction.Create(OpCodes.Call, method.Module.ImportReference(conversion)));
        il.Append(Instruction.Create(OpCodes.Stfld, field));
    }

    static int RepairChapterPanelConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "ChapterPanel") return 0;
        MethodDefinition[] constructors = type.Methods.Where(candidate => candidate.IsConstructor &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray();
        if (constructors.Length != 1 || !constructors[0].HasBody)
            throw new InvalidDataException("expected one parameterless ChapterPanel constructor");
        MethodDefinition method = constructors[0];
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 6;
        ILProcessor il = method.Body.GetILProcessor();
        EmitChapterPanelColor(il, method, RequireStoryPanelField(type, "CurrentColor", MetadataType.ValueType), 91, 165, 223, 255);
        EmitChapterPanelColor(il, method, RequireStoryPanelField(type, "CompleteColor", MetadataType.ValueType), 28, 112, 70, 255);
        EmitChapterPanelColor(il, method, RequireStoryPanelField(type, "MasteryColor", MetadataType.ValueType), 0, 186, 17, 255);
        EmitChapterPanelColor(il, method, RequireStoryPanelField(type, "LockedColor", MetadataType.ValueType), 97, 108, 118, 255);
        EmitStoryPanelBaseCall(il, method);
        return 1;
    }

    static void EmitQuestResultsColor(ILProcessor il, MethodDefinition method,
        FieldDefinition field, string getterName) {
        if (field.FieldType.MetadataType != MetadataType.ValueType || field.FieldType.FullName != "UnityEngine.Color")
            throw new InvalidDataException("unexpected QuestResultsScreenPresentation color field: " + field.FullName);
        var getter = new MethodReference(getterName, field.FieldType, field.FieldType) {
            HasThis = false,
            CallingConvention = MethodCallingConvention.Default
        };
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, method.Module.ImportReference(getter)));
        il.Append(Instruction.Create(OpCodes.Stfld, field));
    }

    static int RepairQuestResultsScreenConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "QuestResultsScreenPresentation") return 0;
        MethodDefinition[] constructors = type.Methods.Where(candidate => candidate.IsConstructor &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray();
        if (constructors.Length != 1 || !constructors[0].HasBody)
            throw new InvalidDataException("expected one parameterless QuestResultsScreenPresentation constructor");
        MethodDefinition method = constructors[0];
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 3;
        ILProcessor il = method.Body.GetILProcessor();
        EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_chestPreFXDelay", MetadataType.Single), 2.3f);
        EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_chestOpenDelay", MetadataType.Single), 2.3f);
        EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_ctaLabelDisabledAlpha", MetadataType.Single), 0.3f);
        FieldDefinition showBase = RequireStoryPanelField(type, "_showBaseAfterTutorial", MetadataType.String);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "RaidsTutorial"));
        il.Append(Instruction.Create(OpCodes.Stfld, showBase));
        EmitQuestResultsColor(il, method,
            RequireStoryPanelField(type, "_baseCTABackgroundColorNormal", MetadataType.ValueType), "get_black");
        EmitQuestResultsColor(il, method,
            RequireStoryPanelField(type, "_baseCTABackgroundColorWarning", MetadataType.ValueType), "get_red");
        FieldDefinition dailyTag = RequireStoryPanelField(type, "_dailyCategoryTag", MetadataType.String);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "Daily"));
        il.Append(Instruction.Create(OpCodes.Stfld, dailyTag));
        EmitStoryPanelList(il, method, RequireStoryPanelField(type, "_fxCollection", MetadataType.Class));
        EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_defaultCTALabelAlpha", MetadataType.Single), 1f);
        FieldDefinition baseButtonType = RequireStoryPanelField(type, "_baseButtonType", MetadataType.String);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, String.Empty));
        il.Append(Instruction.Create(OpCodes.Stfld, baseButtonType));
        EmitStoryPanelBaseCall(il, method);
        return 1;
    }

    static int RepairFilterToggleConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "FilterToggle") return 0;
        MethodDefinition[] constructors = type.Methods.Where(candidate => candidate.IsConstructor &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray();
        if (constructors.Length != 1 || !constructors[0].HasBody)
            throw new InvalidDataException("expected one parameterless FilterToggle constructor");
        MethodDefinition method = constructors[0];
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 4;
        ILProcessor il = method.Body.GetILProcessor();

        FieldDefinition inactive = RequireStoryPanelField(type, "inactiveSpriteName", MetadataType.String);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "CommonSquare"));
        il.Append(Instruction.Create(OpCodes.Stfld, inactive));
        FieldDefinition active = RequireStoryPanelField(type, "activeSpriteName", MetadataType.String);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "btn2nd"));
        il.Append(Instruction.Create(OpCodes.Stfld, active));
        EmitStoryPanelList(il, method, RequireStoryPanelField(type, "onChange", MetadataType.Class));

        FieldDefinition pressedOffset = RequireStoryPanelField(type, "pressedOffset", MetadataType.ValueType);
        if (pressedOffset.FieldType.FullName != "UnityEngine.Vector3")
            throw new InvalidDataException("unexpected FilterToggle pressedOffset type");
        var vectorConstructor = new MethodReference(".ctor", method.Module.TypeSystem.Void,
            pressedOffset.FieldType) { HasThis = true, CallingConvention = MethodCallingConvention.Default };
        vectorConstructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Single));
        vectorConstructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Single));
        vectorConstructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Single));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 2f));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, -2f));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 0f));
        il.Append(Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(vectorConstructor)));
        il.Append(Instruction.Create(OpCodes.Stfld, pressedOffset));

        FieldDefinition isActive = RequireStoryPanelField(type, "mIsActive", MetadataType.Boolean);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, isActive));
        EmitStoryPanelBaseCall(il, method);
        return 1;
    }

    static int RepairRedeemersDisplayPanelConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "RedeemersDisplayPanel") return 0;
        MethodDefinition[] constructors = type.Methods.Where(candidate => candidate.IsConstructor &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray();
        if (constructors.Length != 1 || !constructors[0].HasBody)
            throw new InvalidDataException("expected one parameterless RedeemersDisplayPanel constructor");
        MethodDefinition method = constructors[0];
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 2;
        ILProcessor il = method.Body.GetILProcessor();

        FieldDefinition useScrollView = RequireStoryPanelField(type, "useScrollView", MetadataType.Boolean);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, useScrollView));
        FieldDefinition showToolTips = RequireStoryPanelField(type, "ShowToolTips", MetadataType.Boolean);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, showToolTips));
        EmitStoryPanelFloat(il, method,
            RequireStoryPanelField(type, "_animIntroStartWaitTime", MetadataType.Single), 0.2f);
        EmitStoryPanelFloat(il, method,
            RequireStoryPanelField(type, "_addItemSpringValue", MetadataType.Single), 8f);
        EmitStoryPanelInt(il, method,
            RequireStoryPanelField(type, "_overrideBitFlag", MetadataType.Int32), -1);
        foreach (string fieldName in new[] { "_transitionInName", "_transitionOutName" }) {
            FieldDefinition field = RequireStoryPanelField(type, fieldName, MetadataType.String);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldstr, String.Empty));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        EmitStoryPanelBaseCall(il, method);
        return 1;
    }

    static int RepairSocialHubTrayButtonConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "SocialHubTrayButton") return 0;
        MethodDefinition method = RequireNGUIConstructor(type);
        ILProcessor il = method.Body.GetILProcessor();

        // These vectors are the exact ARM64 SIMD defaults at 0xDEF35C:
        // q0=(1,-1,1), q1=(-1.6,180,-45), and the RTL badge stores
        // (-1,1,1), (0,0,70), and (0,5,0) across the adjacent Vector3 fields.
        StoreNGUIVector(il, type, "iconRLScale", "UnityEngine.Vector3", 1f, -1f, 1f);
        StoreNGUIVector(il, type, "iconRLRotation", "UnityEngine.Vector3", 0f, 0f, 0f);
        StoreNGUIVector(il, type, "pivotRLScale", "UnityEngine.Vector3", 1f, -1f, 1f);
        StoreNGUIVector(il, type, "pivotRLRotation", "UnityEngine.Vector3", -1.6f, 180f, -45f);
        StoreNGUIVector(il, type, "pivotRLOffset", "UnityEngine.Vector3", 0f, 0f, 0f);
        StoreNGUIVector(il, type, "_badgePivotRtLScale", "UnityEngine.Vector3", -1f, 1f, 1f);
        StoreNGUIVector(il, type, "_badgePivotRtLRotation", "UnityEngine.Vector3", 0f, 0f, 70f);
        StoreNGUIVector(il, type, "_badgePivotRtLOffset", "UnityEngine.Vector3", 0f, 5f, 0f);
        StoreNGUIVector(il, type, "BGRLRotation", "UnityEngine.Vector3", 0f, 0f, -45f);
        AppendNGUIBaseCall(il, type);
        return 1;
    }

    static int RepairSpecialAttackIconConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll" || type.FullName != "SpecialAttackIcon") return 0;
        MethodDefinition method = RequireNGUIConstructor(type);
        FieldDefinition fadeTime = type.Fields.SingleOrDefault(field => field.Name == "FadeTime" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single);
        if (fadeTime == null || type.BaseType.FullName != "UnityEngine.MonoBehaviour")
            throw new InvalidDataException("unexpected SpecialAttackIcon constructor metadata");
        ILProcessor il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, 0.2f));
        il.Append(il.Create(OpCodes.Stfld, fadeTime));
        AppendNGUIBaseCall(il, type);
        return 1;
    }

    static int RepairMatineeStageConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Animation.Matinee.MatineeStage") return 0;
        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == ".ctor" &&
            !candidate.IsStatic && candidate.Parameters.Count == 0 && candidate.HasBody);
        if (method == null) throw new InvalidDataException("missing parameterless MatineeStage constructor");
        if (type.BaseType.FullName != "UnityEngine.MonoBehaviour")
            throw new InvalidDataException("unexpected MatineeStage base type");

        FieldDefinition position = RequireNGUIField(type, "position", "UnityEngine.Vector3");
        FieldDefinition rotation = RequireNGUIField(type, "rotation", "UnityEngine.Quaternion");
        FieldDefinition originalPosition = RequireNGUIField(type, "originalPosition", "UnityEngine.Vector3");
        FieldDefinition originalRotation = RequireNGUIField(type, "originalRotation", "UnityEngine.Quaternion");
        FieldDefinition playbackFilter = RequireNGUIField(type, "playbackFilter", "System.String");
        FieldDefinition actorInfoList = RequireNGUIField(type, "actorInfoList",
            "System.Collections.Generic.List`1<EB.Animation.Matinee.MatineeStage/ActorInfo>");
        FieldDefinition clipList = RequireNGUIField(type, "_matineeAnimationClipList",
            "System.Collections.Generic.List`1<EB.Animation.Matinee.MatineeStage/MatineeAnimationClip>");
        FieldDefinition trackEntityList = RequireNGUIField(type, "_trackEntityLibrary",
            "System.Collections.Generic.List`1<UnityEngine.GameObject>");
        FieldDefinition destroyOnComplete = RequireNGUIField(type, "_destroyStageOnCompletion", "System.Boolean");

        MethodReference vector3Zero = FindCall(method, "get_zero", "UnityEngine.Vector3");
        MethodReference quaternionIdentity = FindCall(method, "get_identity", "UnityEngine.Quaternion");
        MethodReference actorListConstructor = FindConstructor(method, actorInfoList.FieldType.FullName);
        MethodReference clipListConstructor = FindConstructor(method, clipList.FieldType.FullName);
        MethodReference trackEntityListConstructor = FindConstructor(method, trackEntityList.FieldType.FullName);
        MethodReference baseConstructor = FindCall(method, ".ctor", "UnityEngine.MonoBehaviour");
        if (vector3Zero == null || quaternionIdentity == null || actorListConstructor == null ||
            clipListConstructor == null || trackEntityListConstructor == null || baseConstructor == null)
            throw new InvalidDataException("missing MatineeStage constructor call metadata");

        ResetMethodBody(method);
        method.Body.MaxStackSize = 2;
        ILProcessor il = method.Body.GetILProcessor();
        foreach (FieldDefinition field in new[] { position, originalPosition }) {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, moduleImport(type, vector3Zero)));
            il.Append(il.Create(OpCodes.Stfld, field));
        }
        foreach (FieldDefinition field in new[] { rotation, originalRotation }) {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, moduleImport(type, quaternionIdentity)));
            il.Append(il.Create(OpCodes.Stfld, field));
        }
        StoreStringField(il, type, "playbackFilter", String.Empty);
        foreach (var item in new[] {
            Tuple.Create(actorInfoList, actorListConstructor),
            Tuple.Create(clipList, clipListConstructor),
            Tuple.Create(trackEntityList, trackEntityListConstructor)
        }) {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, moduleImport(type, item.Item2)));
            il.Append(il.Create(OpCodes.Stfld, item.Item1));
        }
        StoreBoolField(il, type, "_destroyStageOnCompletion", true);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, baseConstructor)));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static int RepairEBLightShadowConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp-firstpass.dll" ||
            type.FullName != "EB.Rendering.EBLightShadow") return 0;

        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == ".ctor" &&
            !candidate.IsStatic && candidate.Parameters.Count == 0 && candidate.HasBody);
        if (method == null || type.BaseType.FullName != "UnityEngine.MonoBehaviour")
            throw new InvalidDataException("unexpected EBLightShadow constructor metadata");

        FieldDefinition lightRotation = RequireNGUIField(type, "_LightUpToShadowCamera", "UnityEngine.Quaternion");
        FieldDefinition isOrthographic = RequireNGUIField(type, "IsOrthoGraphic", "System.Boolean");
        FieldDefinition exponentialValue = RequireNGUIField(type, "ExponentialValue", "System.Single");
        FieldDefinition priorityCascade = type.Fields.SingleOrDefault(field => field.Name == "PriorityCascade" &&
            !field.IsStatic && field.FieldType.Name == "eNUM_CASCADES");
        FieldDefinition shadowIsStatic = RequireNGUIField(type, "_ShadowIsStatic", "System.Boolean[]");
        FieldDefinition textureName = RequireNGUIField(type, "_TextureName", "System.String");
        FieldDefinition matrixName = RequireNGUIField(type, "_VPMatrixName", "System.String");
        FieldDefinition nearFarName = RequireNGUIField(type, "_ShadowZBufferParam", "System.String");
        FieldDefinition zBiasName = RequireNGUIField(type, "_ZBiasName", "System.String");
        FieldDefinition exponentialName = RequireNGUIField(type, "_ExponentialFactorName", "System.String");
        FieldDefinition textureProperty = RequireNGUIField(type, "_TextureProperty", "System.Int32");
        FieldDefinition matrixProperties = RequireNGUIField(type, "_VPMatrixProperty", "EB.Rendering.EBShaderGlobalProperty[]");
        FieldDefinition nearFarProperties = RequireNGUIField(type, "_ShadowZBufferProperty", "EB.Rendering.EBShaderGlobalProperty[]");
        FieldDefinition zBiasProperties = RequireNGUIField(type, "_ZBiasProperty", "EB.Rendering.EBShaderGlobalProperty[]");
        FieldDefinition exponentialProperty = RequireNGUIField(type, "_ExponentialFactorProperty", "System.Int32");
        FieldDefinition shadowSettings = RequireNGUIField(type, "ShadowSettings", "EB.Rendering.EBLightShadow/EBShadowSetting[]");
        FieldDefinition textureSizes = RequireNGUIField(type, "_ShadowTextureSizes", "System.Int32[][]");
        FieldDefinition numCascades = RequireNGUIField(type, "_NumCascades", "System.Int32");
        if (priorityCascade == null || priorityCascade.FieldType.MetadataType != MetadataType.ValueType)
            throw new InvalidDataException("unexpected EBLightShadow.PriorityCascade field metadata");

        ArrayType boolArray = shadowIsStatic.FieldType as ArrayType;
        ArrayType propertyArray = matrixProperties.FieldType as ArrayType;
        ArrayType settingsArray = shadowSettings.FieldType as ArrayType;
        ArrayType jaggedSizes = textureSizes.FieldType as ArrayType;
        ArrayType sizeRow = jaggedSizes?.ElementType as ArrayType;
        if (boolArray == null || propertyArray == null || settingsArray == null || sizeRow == null ||
            nearFarProperties.FieldType.FullName != matrixProperties.FieldType.FullName ||
            zBiasProperties.FieldType.FullName != matrixProperties.FieldType.FullName ||
            boolArray.ElementType.MetadataType != MetadataType.Boolean ||
            propertyArray.ElementType.FullName != "EB.Rendering.EBShaderGlobalProperty" ||
            settingsArray.ElementType.FullName != "EB.Rendering.EBLightShadow/EBShadowSetting" ||
            sizeRow.ElementType.MetadataType != MetadataType.Int32)
            throw new InvalidDataException("unexpected EBLightShadow array metadata");

        MethodReference euler = FindCall(method, "Euler", "UnityEngine.Quaternion");
        TypeDefinition settingType = type.NestedTypes.SingleOrDefault(nested => nested.Name == "EBShadowSetting");
        MethodDefinition[] settingConstructors = settingType?.Methods.Where(candidate => candidate.Name == ".ctor" &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray() ?? new MethodDefinition[0];
        if (euler == null || euler.HasThis || euler.ReturnType.FullName != "UnityEngine.Quaternion" ||
            euler.Parameters.Count != 3 || euler.Parameters.Any(parameter => parameter.ParameterType.MetadataType != MetadataType.Single) ||
            settingConstructors.Length != 1 || shadowSettings.FieldType.FullName != "EB.Rendering.EBLightShadow/EBShadowSetting[]")
            throw new InvalidDataException("missing EBLightShadow constructor call metadata");

        // Authored from the retained ARM64 trace at 0x1D3D0DC-0x1D3D39C.
        // No original managed method body or asset data is copied here.
        ResetMethodBody(method);
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 6;
        ILProcessor il = method.Body.GetILProcessor();

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_R4, 90f));
        il.Append(il.Create(OpCodes.Ldc_R4, 0f));
        il.Append(il.Create(OpCodes.Ldc_R4, 0f));
        il.Append(il.Create(OpCodes.Call, moduleImport(type, euler)));
        il.Append(il.Create(OpCodes.Stfld, lightRotation));
        StoreBoolField(il, type, "IsOrthoGraphic", true);
        StoreFloatField(il, type, "ExponentialValue", 250f);
        StoreIntField(il, type, "PriorityCascade", 2);

        foreach (FieldDefinition arrayField in new[] { shadowIsStatic, matrixProperties, nearFarProperties, zBiasProperties }) {
            ArrayType array = (ArrayType)arrayField.FieldType;
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_2));
            il.Append(il.Create(OpCodes.Newarr, array.ElementType));
            il.Append(il.Create(OpCodes.Stfld, arrayField));
        }
        StoreStringField(il, type, "_TextureName", "_ShadowMap");
        StoreStringField(il, type, "_VPMatrixName", "_ShadowMapVP");
        StoreStringField(il, type, "_ShadowZBufferParam", "_ShadowNearFar");
        StoreStringField(il, type, "_ZBiasName", "_ZBias");
        StoreStringField(il, type, "_ExponentialFactorName", "_ExponentialFactor");
        StoreIntField(il, type, "_TextureProperty", -1);
        StoreIntField(il, type, "_ExponentialFactorProperty", -1);

        MethodReference settingConstructor = moduleImport(type, settingConstructors[0]);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Newarr, settingsArray.ElementType));
        for (int index = 0; index < 2; index++) {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            il.Append(il.Create(OpCodes.Newobj, settingConstructor));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        il.Append(il.Create(OpCodes.Stfld, shadowSettings));

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Newarr, jaggedSizes.ElementType));
        int[] resolutions = { 256, 512, 1024 };
        for (int index = 0; index < resolutions.Length; index++) {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            il.Append(il.Create(OpCodes.Ldc_I4_2));
            il.Append(il.Create(OpCodes.Newarr, sizeRow.ElementType));
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldc_I4, resolutions[index]));
            il.Append(il.Create(OpCodes.Stelem_I4));
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ldc_I4, resolutions[index]));
            il.Append(il.Create(OpCodes.Stelem_I4));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        il.Append(il.Create(OpCodes.Stfld, textureSizes));
        StoreIntField(il, type, "_NumCascades", -1);
        AppendNGUIBaseCall(il, type);
        return 1;
    }

    static int RepairStoryPanelConstructor(TypeDefinition type, string assemblyName) {
        if (assemblyName != "Assembly-CSharp.dll") return 0;
        if (type.FullName != "QuestSelectPanelBase" && type.FullName != "ActPanel" &&
            type.FullName != "StoryEventPanel") return 0;

        MethodDefinition[] constructors = type.Methods.Where(candidate => candidate.IsConstructor &&
            !candidate.IsStatic && candidate.Parameters.Count == 0).ToArray();
        if (constructors.Length != 1 || !constructors[0].HasBody)
            throw new InvalidDataException("expected one parameterless story panel constructor: " + type.FullName);
        MethodDefinition method = constructors[0];
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = false;
        method.Body.MaxStackSize = 3;
        ILProcessor il = method.Body.GetILProcessor();

        if (type.FullName == "QuestSelectPanelBase") {
            EmitStoryPanelList(il, method, RequireStoryPanelField(type, "_chapterPanels", MetadataType.Class));
            EmitStoryPanelList(il, method, RequireStoryPanelField(type, "_questTiles", MetadataType.Class));
            EmitStoryPanelList(il, method, RequireStoryPanelField(type, "_emptyTiles", MetadataType.Class));
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_panelGrowthDuration", MetadataType.Single), 0.125f);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_chapterTransitionDuration", MetadataType.Single), 0.125f);
            EmitStoryPanelInt(il, method, RequireStoryPanelField(type, "_panelGutter", MetadataType.Int32), 20);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_textureFadeDuration", MetadataType.Single), 0.125f);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "_transitionCompletionDelay", MetadataType.Single), 0.25f);

            FieldDefinition offset = RequireStoryPanelField(type, "_lockAlertOffset", MetadataType.ValueType);
            MethodReference vectorConstructor = new MethodReference(".ctor", method.Module.TypeSystem.Void,
                offset.FieldType) { HasThis = true, CallingConvention = MethodCallingConvention.Default };
            vectorConstructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Single));
            vectorConstructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Single));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldc_R4, -146f));
            il.Append(Instruction.Create(OpCodes.Ldc_R4, 120f));
            il.Append(Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(vectorConstructor)));
            il.Append(Instruction.Create(OpCodes.Stfld, offset));
        } else if (type.FullName == "ActPanel") {
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "DurationPanelGrowth", MetadataType.Single), 0.125f);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "DurationChapterTransition", MetadataType.Single), 0.125f);
            EmitStoryPanelInt(il, method, RequireStoryPanelField(type, "PanelGutter", MetadataType.Int32), 20);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "ActTextureFadeTime", MetadataType.Single), 0.125f);
            EmitStoryPanelList(il, method, RequireStoryPanelField(type, "_listeners", MetadataType.Class));
        } else {
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "DurationPanelGrowth", MetadataType.Single), 0.125f);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "DurationChapterTransition", MetadataType.Single), 0.125f);
            EmitStoryPanelInt(il, method, RequireStoryPanelField(type, "PanelGutter", MetadataType.Int32), 20);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "TextureFadeDuration", MetadataType.Single), 0.125f);
            EmitStoryPanelFloat(il, method, RequireStoryPanelField(type, "TransitionCompletionDelay", MetadataType.Single), 0.5f);
        }

        EmitStoryPanelBaseCall(il, method);
        return 1;
    }

    static int ReplaceMethodBodyFromAuthoredSource(ModuleDefinition targetModule,
        MethodDefinition target, MethodDefinition source) {
        if (!source.IsStatic || target.IsStatic || source.Parameters.Count != target.Parameters.Count + 1 ||
            source.Parameters[0].ParameterType.FullName != target.DeclaringType.FullName)
            throw new InvalidDataException("authored method body signature does not match destination: " + target.FullName);
        for (int i = 0; i < target.Parameters.Count; i++) {
            if (source.Parameters[i + 1].ParameterType.FullName != target.Parameters[i].ParameterType.FullName)
                throw new InvalidDataException("authored method body parameter mismatch: " + target.FullName);
        }
        if (!source.HasBody || source.Body.ExceptionHandlers.Count != 0)
            throw new InvalidDataException("authored method body must have a body and no exception handlers: " + source.FullName);

        MethodBody sourceBody = source.Body;
        MethodBody targetBody = target.Body;
        targetBody.Instructions.Clear();
        targetBody.Variables.Clear();
        targetBody.ExceptionHandlers.Clear();
        targetBody.InitLocals = sourceBody.InitLocals;
        targetBody.MaxStackSize = sourceBody.MaxStackSize;

        var variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (VariableDefinition variable in sourceBody.Variables) {
            var imported = new VariableDefinition(targetModule.ImportReference(variable.VariableType));
            targetBody.Variables.Add(imported);
            variables.Add(variable, imported);
        }

        var instructions = new Dictionary<Instruction, Instruction>();
        foreach (Instruction instruction in sourceBody.Instructions) {
            Instruction copy = Instruction.Create(OpCodes.Nop);
            copy.OpCode = instruction.OpCode;
            instructions.Add(instruction, copy);
            targetBody.Instructions.Add(copy);
        }

        foreach (Instruction instruction in sourceBody.Instructions) {
            Instruction copy = instructions[instruction];
            object operand = instruction.Operand;
            if (operand == null) continue;
            if (operand is Instruction targetInstruction) {
                copy.Operand = instructions[targetInstruction];
            } else if (operand is Instruction[] targets) {
                copy.Operand = targets.Select(branchTarget => instructions[branchTarget]).ToArray();
            } else if (operand is VariableDefinition variable) {
                copy.Operand = variables[variable];
            } else if (operand is ParameterDefinition parameter) {
                if (parameter.Index == 0 && (instruction.OpCode.Code == Code.Ldarg ||
                    instruction.OpCode.Code == Code.Ldarg_S)) {
                    copy.OpCode = OpCodes.Ldarg_0;
                } else if (parameter.Index == 0) {
                    throw new InvalidDataException("authored body takes the implicit this address: " + source.FullName);
                } else {
                    copy.Operand = target.Parameters[parameter.Index - 1];
                }
            } else if (operand is MethodReference method) {
                if (method.DeclaringType.FullName == source.DeclaringType.FullName)
                    throw new InvalidDataException("authored body calls its helper type: " + method.FullName);
                copy.Operand = targetModule.ImportReference(method);
            } else if (operand is FieldReference field) {
                copy.Operand = targetModule.ImportReference(field);
            } else if (operand is TypeReference type) {
                copy.Operand = targetModule.ImportReference(type);
            } else if (operand is CallSite) {
                throw new InvalidDataException("authored body contains an unsupported call-site operand: " + source.FullName);
            } else {
                copy.Operand = operand;
            }
        }
        return 1;
    }

    static int RepairAuthoredMethodBodies(ModuleDefinition targetModule,
        ModuleDefinition sourceModule, string assemblyName) {
        if (assemblyName == "Assembly-CSharp.dll")
            return RepairBadgeStateConstructor(targetModule);
        if (assemblyName != "Assembly-CSharp-firstpass.dll") return 0;
        int repairs = RepairRecoveredPool(targetModule);
        repairs += RepairMatrixAdd(targetModule);
        repairs += RepairMatrixMultiply(targetModule);
        repairs += RepairMatrixSubtract(targetModule);
        repairs += RepairMatrixDivide(targetModule);
        repairs += RepairMatrixLerp(targetModule);
        repairs += RepairPointEquals(targetModule);
        repairs += RepairPointOperatorEquality(targetModule);
        repairs += RepairPointOperatorInequality(targetModule);
        repairs += RepairStringIDValueOperations(targetModule);
        repairs += RepairVector4Getters(targetModule);
        repairs += RepairVectorAdd(targetModule, "EB.Math.Vector2", new[] { "_x", "_y" });
        repairs += RepairVectorAdd(targetModule, "EB.Math.Vector4", new[] { "x", "y", "z", "w" });
        repairs += RepairPlaneDot(targetModule);
        repairs += RepairPlaneDotCoordinate(targetModule);
        repairs += RepairPlaneDotNormal(targetModule);
        repairs += RepairRectangleContainsPoint(targetModule);
        repairs += RepairRectangleContainsPointOut(targetModule);
        repairs += RepairRectangleContainsRectangle(targetModule);
        repairs += RepairQuaternionAdd(targetModule);
        repairs += RepairQuaternionSubtract(targetModule);
        repairs += RepairSafeNumberSetter(targetModule, "EB.SafeFloat", typeof(float));
        repairs += RepairSafeNumberSetter(targetModule, "EB.SafeInt", typeof(int));
        repairs += RepairStringUtilInitializer(targetModule);
        repairs += RepairStringUtilSafeKey(targetModule);
        repairs += RepairDotString(targetModule);
        repairs += RepairInventoryManagerOnUpdate(targetModule);
        repairs += RepairRequestSigning(targetModule);
        repairs += RepairManagedHmac(targetModule);
        repairs += RepairEncodingInitializer(targetModule);
        repairs += RepairHttpEndPointInitializer(targetModule);
        repairs += RepairUriInitializer(targetModule);
        repairs += RepairUriComponentLookup(targetModule);
        repairs += RepairUriParser(targetModule);
        repairs += RepairMapTileBuffMethods(targetModule, sourceModule);
        repairs += RepairMapGetTile(targetModule, sourceModule);
        repairs += RepairMapSetupBuffs(targetModule, sourceModule);
        repairs += RepairDynamicScrollViewPositions(targetModule);
        repairs += RepairWorldPainterLinesCross(targetModule);
        TypeDefinition sourceType = sourceModule.GetType("RecoverySources.AlignUIElementsGetObjectBounds");
        MethodDefinition source = sourceType == null ? null : sourceType.Methods.SingleOrDefault(method => method.Name == "Replace");
        TypeDefinition targetType = targetModule.GetType("AlignUIElements");
        MethodDefinition target = targetType == null ? null : targetType.Methods.SingleOrDefault(method =>
            method.Name == "GetObjectBounds" && !method.IsStatic && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == "AlignUIElements/AlignedObject" &&
            method.Parameters[1].ParameterType.FullName == "UnityEngine.Vector3&" &&
            method.Parameters[2].ParameterType.FullName == "UnityEngine.Vector3&");
        if (source == null || target == null)
            throw new InvalidDataException("missing authored GetObjectBounds source or 9.2 destination metadata");
        repairs += ReplaceMethodBodyFromAuthoredSource(targetModule, target, source);
        return repairs;
    }

    static int RepairDynamicScrollViewPositions(ModuleDefinition module) {
        TypeDefinition type = module.GetType("DynamicScrollView");
        if (type == null) throw new InvalidDataException("missing DynamicScrollView metadata");
        FieldDefinition cached = type.Fields.SingleOrDefault(field => field.Name == "_cachedItemsDict" &&
            !field.IsStatic && field.FieldType is GenericInstanceType);
        FieldDefinition data = type.Fields.SingleOrDefault(field => field.Name == "itemData" &&
            !field.IsStatic && field.FieldType.FullName == "System.Collections.IList");
        MethodDefinition target = type.Methods.SingleOrDefault(method => method.Name == "UpdatePositions" &&
            !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Void);
        MethodDefinition getPosition = type.Methods.SingleOrDefault(method => method.Name == "GetPositionForIndex" &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            method.ReturnType.FullName == "UnityEngine.Vector3");
        if (cached == null || data == null || target == null || getPosition == null)
            throw new InvalidDataException("unexpected DynamicScrollView.UpdatePositions metadata");

        GenericInstanceType dictionary = (GenericInstanceType)cached.FieldType;
        if (dictionary.GenericArguments.Count != 2 ||
            dictionary.GenericArguments[0].MetadataType != MetadataType.Int32 ||
            dictionary.ElementType.FullName != "System.Collections.Generic.Dictionary`2")
            throw new InvalidDataException("unexpected DynamicScrollView item-cache dictionary type");
        TypeReference itemType = dictionary.GenericArguments[1];
        TypeDefinition item = itemType.Resolve();
        FieldDefinition gameObject = item == null ? null : item.Fields.SingleOrDefault(field =>
            field.Name == "gameObject" && !field.IsStatic && field.FieldType.FullName == "UnityEngine.GameObject");
        if (itemType.FullName != "GameObjectItemPool/Item" || gameObject == null)
            throw new InvalidDataException("unexpected DynamicScrollView cached item metadata");

        MethodReference lookup = new MethodReference("TryGetValue", module.TypeSystem.Boolean, dictionary) {
            HasThis = true
        };
        lookup.Parameters.Add(new ParameterDefinition(dictionary.GenericArguments[0]));
        lookup.Parameters.Add(new ParameterDefinition(new ByReferenceType(dictionary.GenericArguments[1])));
        lookup = module.ImportReference(lookup);

        TypeReference collection = module.ImportReference(typeof(System.Collections.ICollection));
        MethodReference count = module.ImportReference(new MethodReference("get_Count",
            module.TypeSystem.Int32, collection) { HasThis = true });
        TypeReference unityObject = UnityEngineType(module, "Object");
        MethodReference isAlive = UnityStaticMethod(module, "Object", "op_Implicit",
            module.TypeSystem.Boolean, unityObject);
        MethodReference getTransform = UnityInstanceGetter(module, "GameObject", "get_transform",
            UnityEngineType(module, "Transform"));
        TypeReference transform = UnityEngineType(module, "Transform");
        MethodReference setLocalPosition = module.ImportReference(new MethodReference("set_localPosition",
            module.TypeSystem.Void, transform) { HasThis = true });
        setLocalPosition.Parameters.Add(new ParameterDefinition(UnityEngineType(module, "Vector3")));

        MethodBody body = ResetBody(target, out ILProcessor il);
        body.InitLocals = true;
        body.MaxStackSize = 4;
        var lastIndex = new VariableDefinition(module.TypeSystem.Int32);
        var index = new VariableDefinition(module.TypeSystem.Int32);
        var cachedItem = new VariableDefinition(itemType);
        var cachedObject = new VariableDefinition(UnityEngineType(module, "GameObject"));
        var cachedTransform = new VariableDefinition(transform);
        body.Variables.Add(lastIndex);
        body.Variables.Add(index);
        body.Variables.Add(cachedItem);
        body.Variables.Add(cachedObject);
        body.Variables.Add(cachedTransform);

        Instruction done = il.Create(OpCodes.Ret);
        Instruction loop = il.Create(OpCodes.Ldloc, index);
        Instruction next = il.Create(OpCodes.Ldloc, index);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, data));
        il.Append(il.Create(OpCodes.Brfalse, done));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, data));
        il.Append(il.Create(OpCodes.Callvirt, count));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Stloc, lastIndex));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, index));
        il.Append(loop);
        il.Append(il.Create(OpCodes.Ldloc, lastIndex));
        il.Append(il.Create(OpCodes.Bgt, done));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, cached));
        il.Append(il.Create(OpCodes.Ldloc, index));
        il.Append(il.Create(OpCodes.Ldloca, cachedItem));
        il.Append(il.Create(OpCodes.Callvirt, lookup));
        il.Append(il.Create(OpCodes.Brfalse, next));
        il.Append(il.Create(OpCodes.Ldloc, cachedItem));
        il.Append(il.Create(OpCodes.Brfalse, next));
        il.Append(il.Create(OpCodes.Ldloc, cachedItem));
        il.Append(il.Create(OpCodes.Ldfld, gameObject));
        il.Append(il.Create(OpCodes.Stloc, cachedObject));
        il.Append(il.Create(OpCodes.Ldloc, cachedObject));
        il.Append(il.Create(OpCodes.Call, isAlive));
        il.Append(il.Create(OpCodes.Brfalse, next));
        il.Append(il.Create(OpCodes.Ldloc, cachedObject));
        il.Append(il.Create(OpCodes.Callvirt, getTransform));
        il.Append(il.Create(OpCodes.Stloc, cachedTransform));
        il.Append(il.Create(OpCodes.Ldloc, cachedTransform));
        il.Append(il.Create(OpCodes.Call, isAlive));
        il.Append(il.Create(OpCodes.Brfalse, next));
        il.Append(il.Create(OpCodes.Ldloc, cachedTransform));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, index));
        il.Append(il.Create(OpCodes.Callvirt, getPosition));
        il.Append(il.Create(OpCodes.Callvirt, setLocalPosition));
        il.Append(next);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, index));
        il.Append(il.Create(OpCodes.Br, loop));
        il.Append(done);
        Console.WriteLine("reconstructed DynamicScrollView.UpdatePositions from the 9.2 cached-item and transform trace");
        return 1;
    }

    static int RepairMapTileBuffMethods(ModuleDefinition targetModule, ModuleDefinition sourceModule) {
        TypeDefinition tile = targetModule.GetType("EB.Missions.MapTile");
        TypeDefinition sourceType = sourceModule.GetType("RecoverySources.MapRecovery");
        if (tile == null || sourceType == null)
            throw new InvalidDataException("missing MapTile recovery source or destination metadata");
        int repairs = RepairMapTilePositionGetter(tile);
        repairs += ReplaceMethodBodyFromAuthoredSource(targetModule,
            FindInstanceMethod(tile, "AddBuffsFromTile", "EB.Missions.MapTile"),
            FindStaticMethod(sourceType, "AddBuffsFromTile", "EB.Missions.MapTile", "EB.Missions.MapTile"));
        repairs += ReplaceMethodBodyFromAuthoredSource(targetModule,
            FindInstanceMethod(tile, "AddBuffsFromSummary", "EB.Missions.Summary"),
            FindStaticMethod(sourceType, "AddBuffsFromSummary", "EB.Missions.MapTile", "EB.Missions.Summary"));
        repairs += ReplaceMethodBodyReturningList(targetModule,
            FindInstanceMethod(tile, "AddAttackerBuff", "EB.Missions.Buff"),
            FindStaticMethod(sourceType, "AddAttackerBuff", "EB.Missions.MapTile", "EB.Missions.Buff"),
            FindListSetter(tile, "set_attackerBuffs"));
        repairs += ReplaceMethodBodyReturningList(targetModule,
            FindInstanceMethod(tile, "AddDefenderBuff", "EB.Missions.Buff"),
            FindStaticMethod(sourceType, "AddDefenderBuff", "EB.Missions.MapTile", "EB.Missions.Buff"),
            FindListSetter(tile, "set_defenderBuffs"));
        Console.WriteLine("reconstructed MapTile attacker/defender buff copy and append methods from 9.2 ARM64 traces");
        return repairs;
    }

    static int RepairMapTilePositionGetter(TypeDefinition tile) {
        FieldDefinition position = tile.Fields.SingleOrDefault(field => field.Name == "<position>k__BackingField" &&
            !field.IsStatic && field.FieldType.FullName == "UnityEngine.Vector2");
        MethodDefinition getter = tile.Methods.SingleOrDefault(method => method.Name == "get_position" &&
            !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.FullName == "UnityEngine.Vector2");
        if (position == null || getter == null)
            throw new InvalidDataException("missing MapTile.position backing field or getter");
        ILProcessor il;
        MethodBody body = ResetBody(getter, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, position));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        int repairs = 1;

        TypeReference vector2 = UnityEngineType(tile.Module, "Vector2");
        foreach (string coordinate in new[] { "x", "y" }) {
            MethodDefinition coordinateGetter = tile.Methods.SingleOrDefault(method =>
                method.Name == "get_" + coordinate && !method.IsStatic && method.Parameters.Count == 0 &&
                method.ReturnType.MetadataType == MetadataType.Int32);
            if (coordinateGetter == null)
                throw new InvalidDataException("missing MapTile coordinate getter: " + coordinate);
            FieldReference component = tile.Module.ImportReference(new FieldReference(coordinate,
                tile.Module.TypeSystem.Single, vector2));
            body = ResetBody(coordinateGetter, out il);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, position));
            il.Append(Instruction.Create(OpCodes.Ldfld, component));
            il.Append(Instruction.Create(OpCodes.Conv_I4));
            il.Append(Instruction.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            repairs++;
        }
        Console.WriteLine("reconstructed MapTile x/y from the traced Vector2 position components");
        return repairs;
    }

    static int RepairMapGetTile(ModuleDefinition targetModule, ModuleDefinition sourceModule) {
        TypeDefinition map = targetModule.GetType("EB.Missions.Map");
        TypeDefinition sourceType = sourceModule.GetType("RecoverySources.MapRecovery");
        if (map == null || sourceType == null)
            throw new InvalidDataException("missing Map.GetTile recovery source or destination metadata");
        MethodDefinition target = FindInstanceMethod(map, "GetTile", "System.Int32", "System.Int32");
        MethodDefinition source = FindStaticMethod(sourceType, "GetTile", "EB.Missions.Map",
            "System.Int32", "System.Int32");
        int repairs = ReplaceMethodBodyFromAuthoredSource(targetModule, target, source);
        Console.WriteLine("reconstructed EB.Missions.Map.GetTile(int,int) bounds and grid lookup from 9.2 trace");
        return repairs;
    }

    static MethodDefinition FindInstanceMethod(TypeDefinition type, string name, params string[] parameters) {
        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == name &&
            !candidate.IsStatic && candidate.Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(parameters));
        if (method == null) throw new InvalidDataException("missing method " + type.FullName + "::" + name);
        return method;
    }

    static MethodDefinition FindStaticMethod(TypeDefinition type, string name, params string[] parameters) {
        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == name &&
            candidate.IsStatic && candidate.Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(parameters));
        if (method == null) throw new InvalidDataException("missing authored source " + type.FullName + "::" + name);
        return method;
    }

    static MethodDefinition FindListSetter(TypeDefinition type, string name) {
        MethodDefinition method = type.Methods.SingleOrDefault(candidate => candidate.Name == name &&
            !candidate.IsStatic && candidate.ReturnType.MetadataType == MetadataType.Void &&
            candidate.Parameters.Count == 1 && candidate.Parameters[0].ParameterType.FullName ==
                "System.Collections.Generic.List`1<EB.Missions.Buff>");
        if (method == null) throw new InvalidDataException("missing list setter " + type.FullName + "::" + name);
        return method;
    }

    static int ReplaceMethodBodyReturningList(ModuleDefinition targetModule, MethodDefinition target,
        MethodDefinition source, MethodDefinition setter) {
        if (source.ReturnType.FullName != "System.Collections.Generic.List`1<EB.Missions.Buff>")
            throw new InvalidDataException("authored list-return method has unexpected return type: " + source.FullName);
        ReplaceMethodBodyFromAuthoredSource(targetModule, target, source);
        MethodBody body = target.Body;
        var list = new VariableDefinition(targetModule.ImportReference(source.ReturnType));
        body.Variables.Add(list);
        body.InitLocals = true;
        Instruction[] returns = body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Ret).ToArray();
        if (returns.Length == 0) throw new InvalidDataException("authored list method has no return: " + source.FullName);
        foreach (Instruction ret in returns) {
            ret.OpCode = OpCodes.Stloc;
            ret.Operand = list;
            Instruction cursor = ret;
            Instruction[] writes = {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldloc, list),
                Instruction.Create(OpCodes.Call, targetModule.ImportReference(setter)),
                Instruction.Create(OpCodes.Ret)
            };
            foreach (Instruction write in writes) {
                body.GetILProcessor().InsertAfter(cursor, write);
                cursor = write;
            }
        }
        body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
        return 1;
    }

    static int RepairMapSetupBuffs(ModuleDefinition targetModule, ModuleDefinition sourceModule) {
        TypeDefinition map = targetModule.GetType("EB.Missions.Map");
        TypeDefinition sourceType = sourceModule.GetType("RecoverySources.MapSetupBuffs");
        MethodDefinition source = sourceType == null ? null : sourceType.Methods.SingleOrDefault(method =>
            method.Name == "Replace" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Int32);
        MethodDefinition target = map == null ? null : map.Methods.SingleOrDefault(method =>
            method.Name == "SetupBuffs" && !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.Missions.Summary" &&
            method.ReturnType.MetadataType == MetadataType.Void);
        MethodDefinition setHasBuffs = map == null ? null : map.Methods.SingleOrDefault(method =>
            method.Name == "set_hasBuffs" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        MethodDefinition setHasLinkBuffs = map == null ? null : map.Methods.SingleOrDefault(method =>
            method.Name == "set_hasLinkBuffs" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        if (map == null || source == null || target == null || setHasBuffs == null || setHasLinkBuffs == null)
            throw new InvalidDataException("missing authored Map.SetupBuffs source or 9.2 destination metadata");

        ReplaceMethodBodyFromAuthoredSource(targetModule, target, source);
        MethodBody body = target.Body;
        var flags = new VariableDefinition(targetModule.TypeSystem.Int32);
        body.Variables.Add(flags);
        body.InitLocals = true;
        Instruction[] returns = body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Ret).ToArray();
        if (returns.Length == 0) throw new InvalidDataException("authored Map.SetupBuffs body has no return");
        foreach (Instruction ret in returns) {
            // Keep the original instruction object so existing branches still land on the flag store.
            ret.OpCode = OpCodes.Stloc;
            ret.Operand = flags;
            Instruction cursor = ret;
            Instruction[] writes = {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldloc, flags),
                Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.And),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Cgt_Un),
                Instruction.Create(OpCodes.Call, targetModule.ImportReference(setHasBuffs)),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldloc, flags),
                Instruction.Create(OpCodes.Ldc_I4_2),
                Instruction.Create(OpCodes.And),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Cgt_Un),
                Instruction.Create(OpCodes.Call, targetModule.ImportReference(setHasLinkBuffs)),
                Instruction.Create(OpCodes.Ret)
            };
            foreach (Instruction write in writes) {
                body.GetILProcessor().InsertAfter(cursor, write);
                cursor = write;
            }
        }
        body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
        Console.WriteLine("reconstructed EB.Missions.Map.SetupBuffs from traced global, linked, and summary buff paths");
        return 1;
    }

    static int RepairMatrixAdd(ModuleDefinition module) {
        TypeDefinition matrix = module.GetType("EB.Math.Matrix");
        if (matrix == null) throw new InvalidDataException("missing EB.Math.Matrix metadata");
        string matrixRef = "EB.Math.Matrix&";
        MethodDefinition add = matrix.Methods.SingleOrDefault(method => method.Name == "Add" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 3 && method.Parameters.All(parameter =>
                parameter.ParameterType.FullName == matrixRef));
        if (add == null) throw new InvalidDataException("missing EB.Math.Matrix.Add(ref,ref,ref)");

        string[] fieldNames = {
            "m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24",
            "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"
        };
        FieldDefinition[] fields = fieldNames.Select(name => matrix.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (fields.Any(field => field == null) || fields.Distinct().Count() != fieldNames.Length)
            throw new InvalidDataException("unexpected EB.Math.Matrix scalar field layout");

        ILProcessor il;
        MethodBody body = ResetBody(add, out il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed EB.Math.Matrix.Add(ref,ref,ref) across 16 traced float fields");
        return 1;
    }

    static int RepairMatrixMultiply(ModuleDefinition module) {
        TypeDefinition matrix = module.GetType("EB.Math.Matrix");
        if (matrix == null) throw new InvalidDataException("missing EB.Math.Matrix metadata");
        string matrixRef = "EB.Math.Matrix&";
        MethodDefinition multiply = matrix.Methods.SingleOrDefault(method => method.Name == "Multiply" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 3 && method.Parameters.All(parameter =>
                parameter.ParameterType.FullName == matrixRef));
        if (multiply == null) throw new InvalidDataException("missing EB.Math.Matrix.Multiply(ref,ref,ref)");

        string[] fieldNames = {
            "m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24",
            "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"
        };
        FieldDefinition[] fields = fieldNames.Select(name => matrix.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (fields.Any(field => field == null) || fields.Distinct().Count() != fieldNames.Length)
            throw new InvalidDataException("unexpected EB.Math.Matrix scalar field layout");

        MethodBody body = ResetBody(multiply, out ILProcessor il);
        body.InitLocals = true;
        body.MaxStackSize = 5;
        var left = new VariableDefinition[16];
        var right = new VariableDefinition[16];
        for (int i = 0; i < 16; i++) {
            left[i] = new VariableDefinition(module.TypeSystem.Single);
            right[i] = new VariableDefinition(module.TypeSystem.Single);
            body.Variables.Add(left[i]);
            body.Variables.Add(right[i]);
        }

        // Snapshot both operands before writing the output so result may alias either input.
        for (int i = 0; i < 16; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, fields[i]));
            il.Append(Instruction.Create(OpCodes.Stloc, left[i]));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, fields[i]));
            il.Append(Instruction.Create(OpCodes.Stloc, right[i]));
        }

        for (int row = 0; row < 4; row++) {
            for (int column = 0; column < 4; column++) {
                il.Append(Instruction.Create(OpCodes.Ldarg_2));
                for (int inner = 0; inner < 4; inner++) {
                    il.Append(Instruction.Create(OpCodes.Ldloc, left[row * 4 + inner]));
                    il.Append(Instruction.Create(OpCodes.Ldloc, right[inner * 4 + column]));
                    il.Append(Instruction.Create(OpCodes.Mul));
                    if (inner > 0) il.Append(Instruction.Create(OpCodes.Add));
                }
                il.Append(Instruction.Create(OpCodes.Stfld, fields[row * 4 + column]));
            }
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        Console.WriteLine("reconstructed EB.Math.Matrix.Multiply(ref,ref,ref) from its traced 4x4 scalar product");
        return 1;
    }

    static int RepairSafeNumberSetter(ModuleDefinition module, string typeName, Type valueType) {
        TypeDefinition safeFloat = module.GetType(typeName);
        TypeDefinition safeValue = module.GetType("EB.SafeValue");
        if (safeFloat == null || safeValue == null)
            throw new InvalidDataException("missing " + typeName + "/SafeValue metadata");

        FieldDefinition value = safeFloat.Fields.SingleOrDefault(field => field.Name == "_v");
        Type valueTypeDefinition = valueType;
        MethodDefinition setter = safeFloat.Methods.SingleOrDefault(method => method.Name == "set_Value" &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == module.ImportReference(valueTypeDefinition).FullName &&
            method.ReturnType.MetadataType == MetadataType.Void);
        MethodDefinition constructor = safeValue.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 0);
        MethodDefinition init = safeValue.Methods.SingleOrDefault(method => method.Name == "Init" &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "System.Byte[]");
        MethodReference getBytes = module.ImportReference(typeof(BitConverter).GetMethod("GetBytes",
            new[] { valueType }));
        if (value == null || value.IsStatic || value.FieldType.FullName != "EB.SafeValue" ||
            setter == null || constructor == null || init == null || getBytes == null)
            throw new InvalidDataException("unexpected " + typeName + ".Value setter metadata");

        if ((constructor.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private) {
            // The recovered setter is a sibling caller of SafeValue's
            // parameterless constructor, so preserve that in-assembly call.
            constructor.Attributes = (constructor.Attributes & ~MethodAttributes.MemberAccessMask) |
                MethodAttributes.Assembly;
        }

        ILProcessor il;
        MethodBody body = ResetBody(setter, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor)));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, getBytes));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(init)));
        il.Append(Instruction.Create(OpCodes.Stfld, value));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed " + typeName + ".Value setter from traced SafeValue storage path");
        return 1;
    }

    static int RepairMatrixSubtract(ModuleDefinition module) {
        TypeDefinition matrix = module.GetType("EB.Math.Matrix");
        if (matrix == null) throw new InvalidDataException("missing EB.Math.Matrix metadata");
        string matrixRef = "EB.Math.Matrix&";
        MethodDefinition subtract = matrix.Methods.SingleOrDefault(method => method.Name == "Subtract" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 3 && method.Parameters.All(parameter =>
                parameter.ParameterType.FullName == matrixRef));
        if (subtract == null) throw new InvalidDataException("missing EB.Math.Matrix.Subtract(ref,ref,ref)");

        string[] fieldNames = {
            "m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24",
            "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"
        };
        FieldDefinition[] fields = fieldNames.Select(name => matrix.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (fields.Any(field => field == null) || fields.Distinct().Count() != fieldNames.Length)
            throw new InvalidDataException("unexpected EB.Math.Matrix scalar field layout");

        MethodBody body = ResetBody(subtract, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Sub));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed EB.Math.Matrix.Subtract(ref,ref,ref) across 16 scalar fields");
        return 1;
    }

    static int RepairMatrixDivide(ModuleDefinition module) {
        TypeDefinition matrix = module.GetType("EB.Math.Matrix");
        if (matrix == null) throw new InvalidDataException("missing EB.Math.Matrix metadata");
        const string matrixRef = "EB.Math.Matrix&";
        MethodDefinition divide = matrix.Methods.SingleOrDefault(method => method.Name == "Divide" &&
            method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            method.Parameters.All(parameter => parameter.ParameterType.FullName == matrixRef));
        string[] names = {
            "m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24",
            "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"
        };
        FieldDefinition[] fields = names.Select(name => matrix.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (divide == null || fields.Any(field => field == null) || fields.Distinct().Count() != names.Length)
            throw new InvalidDataException("unexpected EB.Math.Matrix.Divide(ref,ref,ref) metadata");

        MethodBody body = ResetBody(divide, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Div));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed EB.Math.Matrix.Divide across its 16 scalar fields");
        return 1;
    }

    static int RepairMatrixLerp(ModuleDefinition module) {
        TypeDefinition matrix = module.GetType("EB.Math.Matrix");
        if (matrix == null) throw new InvalidDataException("missing EB.Math.Matrix metadata");
        MethodDefinition lerp = matrix.Methods.SingleOrDefault(method => method.Name == "Lerp" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 4 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Matrix&" &&
            method.Parameters[1].ParameterType.FullName == "EB.Math.Matrix&" &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Single &&
            method.Parameters[3].ParameterType.FullName == "EB.Math.Matrix&");
        string[] names = {
            "m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24",
            "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"
        };
        FieldDefinition[] fields = names.Select(name => matrix.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (lerp == null || fields.Any(field => field == null) || fields.Distinct().Count() != names.Length)
            throw new InvalidDataException("unexpected EB.Math.Matrix.Lerp metadata");

        MethodBody body = ResetBody(lerp, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_3));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Sub));
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Mul));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 4;
        Console.WriteLine("reconstructed EB.Math.Matrix.Lerp over its 16 scalar fields");
        return 1;
    }

    static int RepairPointEquals(ModuleDefinition module) {
        TypeDefinition point = module.GetType("EB.Math.Point");
        FieldDefinition x = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "X" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition y = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "Y" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        MethodDefinition equals = point == null ? null : point.Methods.SingleOrDefault(method => method.Name == "Equals" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Point");
        if (x == null || y == null || equals == null)
            throw new InvalidDataException("unexpected EB.Math.Point.Equals metadata");

        MethodBody body = ResetBody(equals, out ILProcessor il);
        Instruction notEqual = il.Create(OpCodes.Ldc_I4_0);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarga, equals.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Bne_Un, notEqual));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarga, equals.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Bne_Un, notEqual));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(notEqual);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Point.Equals(Point) from its integer coordinate fields");
        return 1;
    }

    static int RepairPointOperatorEquality(ModuleDefinition module) {
        TypeDefinition point = module.GetType("EB.Math.Point");
        MethodDefinition equality = point == null ? null : point.Methods.SingleOrDefault(method =>
            method.Name == "op_Equality" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.Math.Point"));
        MethodDefinition equals = point == null ? null : point.Methods.SingleOrDefault(method => method.Name == "Equals" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Point");
        if (equality == null || equals == null)
            throw new InvalidDataException("unexpected EB.Math.Point.op_Equality metadata");

        MethodBody body = ResetBody(equality, out ILProcessor il);
        il.Append(Instruction.Create(OpCodes.Ldarga, equality.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(equals)));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Point.op_Equality through the point value equality method");
        return 1;
    }

    static int RepairPointOperatorInequality(ModuleDefinition module) {
        TypeDefinition point = module.GetType("EB.Math.Point");
        MethodDefinition inequality = point == null ? null : point.Methods.SingleOrDefault(method =>
            method.Name == "op_Inequality" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.Math.Point"));
        MethodDefinition equality = point == null ? null : point.Methods.SingleOrDefault(method =>
            method.Name == "op_Equality" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.Math.Point"));
        if (inequality == null || equality == null)
            throw new InvalidDataException("unexpected EB.Math.Point.op_Inequality metadata");

        MethodBody body = ResetBody(inequality, out ILProcessor il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(equality)));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Point.op_Inequality as the complement of value equality");
        return 1;
    }

    static int RepairStringIDValueOperations(ModuleDefinition module) {
        TypeDefinition stringId = module.GetType("EB.StringID");
        FieldDefinition id = stringId == null ? null : stringId.Fields.SingleOrDefault(field => field.Name == "_id" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        MethodDefinition equals = stringId == null ? null : stringId.Methods.SingleOrDefault(method => method.Name == "Equals" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.StringID");
        MethodDefinition equalOperator = stringId == null ? null : stringId.Methods.SingleOrDefault(method =>
            method.Name == "op_Equality" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.StringID"));
        MethodDefinition unequalOperator = stringId == null ? null : stringId.Methods.SingleOrDefault(method =>
            method.Name == "op_Inequality" && method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.StringID"));
        if (id == null || equals == null || equalOperator == null || unequalOperator == null)
            throw new InvalidDataException("unexpected EB.StringID equality metadata");

        MethodBody body = ResetBody(equals, out ILProcessor il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, id));
        il.Append(Instruction.Create(OpCodes.Ldarga, equals.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, id));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;

        body = ResetBody(equalOperator, out il);
        il.Append(Instruction.Create(OpCodes.Ldarga, equalOperator.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(equals)));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;

        body = ResetBody(unequalOperator, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(equalOperator)));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ceq));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.StringID value equality and operators from its integer identifier");
        return 3;
    }

    static int RepairVectorAdd(ModuleDefinition module, string typeName, string[] componentNames) {
        TypeDefinition vector = module.GetType(typeName);
        if (vector == null) throw new InvalidDataException("missing " + typeName + " metadata");
        string referenceType = typeName + "&";
        MethodDefinition add = vector.Methods.SingleOrDefault(method => method.Name == "Add" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            method.Parameters.All(parameter => parameter.ParameterType.FullName == referenceType));
        FieldDefinition[] fields = componentNames.Select(name => vector.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (add == null || fields.Any(field => field == null) || fields.Distinct().Count() != componentNames.Length)
            throw new InvalidDataException("unexpected " + typeName + ".Add metadata");

        MethodBody body = ResetBody(add, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed " + typeName + ".Add across " + fields.Length + " scalar components");
        return 1;
    }

    static int RepairVector4Getters(ModuleDefinition module) {
        TypeDefinition vector = module.GetType("EB.Math.Vector4");
        if (vector == null) throw new InvalidDataException("missing EB.Math.Vector4 metadata");
        string[] components = { "X", "Y", "Z", "W" };
        string[] fields = { "x", "y", "z", "w" };
        int repairs = 0;
        for (int i = 0; i < components.Length; i++) {
            MethodDefinition getter = vector.Methods.SingleOrDefault(method => method.Name == "get_" + components[i] &&
                !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Single);
            FieldDefinition field = vector.Fields.SingleOrDefault(candidate => candidate.Name == fields[i] &&
                !candidate.IsStatic && candidate.FieldType.MetadataType == MetadataType.Single);
            if (getter == null || field == null)
                throw new InvalidDataException("unexpected EB.Math.Vector4." + components[i] + " metadata");
            MethodBody body = ResetBody(getter, out ILProcessor il);
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            repairs++;
        }
        Console.WriteLine("reconstructed EB.Math.Vector4 X/Y/Z/W getters from their scalar fields");
        return repairs;
    }

    static int RepairPlaneDot(ModuleDefinition module) {
        TypeDefinition plane = module.GetType("EB.Math.Plane");
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        TypeDefinition vector4 = module.GetType("EB.Math.Vector4");
        FieldDefinition normal = plane == null ? null : plane.Fields.SingleOrDefault(field => field.Name == "Normal" &&
            !field.IsStatic && field.FieldType.FullName == "EB.Math.Vector3");
        FieldDefinition d = plane == null ? null : plane.Fields.SingleOrDefault(field => field.Name == "D" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single);
        FieldDefinition[] coordinates = vector3 == null ? new FieldDefinition[0] : new[] { "x", "y", "z" }
            .Select(name => vector3.Fields.SingleOrDefault(field => field.Name == name && !field.IsStatic &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        MethodDefinition dot = plane == null ? null : plane.Methods.SingleOrDefault(method => method.Name == "Dot" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Vector4&" &&
            method.Parameters[1].ParameterType.FullName == "System.Single&");
        MethodDefinition[] getters = vector4 == null ? new MethodDefinition[0] : new[] { "X", "Y", "Z", "W" }
            .Select(name => vector4.Methods.SingleOrDefault(method => method.Name == "get_" + name &&
                !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Single))
            .ToArray();
        if (normal == null || d == null || coordinates.Length != 3 || coordinates.Any(field => field == null) ||
            dot == null || getters.Length != 4 || getters.Any(getter => getter == null))
            throw new InvalidDataException("unexpected EB.Math.Plane.Dot(ref Vector4,out float) metadata");

        MethodBody body = ResetBody(dot, out ILProcessor il);
        for (int i = 0; i < 3; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, normal));
            il.Append(Instruction.Create(OpCodes.Ldfld, coordinates[i]));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(getters[i])));
            il.Append(Instruction.Create(OpCodes.Mul));
            if (i > 0) il.Append(Instruction.Create(OpCodes.Add));
        }
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, d));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(getters[3])));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Stind_R4));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Plane.Dot from normal, distance, and Vector4 components");
        return 1;
    }

    static int RepairPlaneDotCoordinate(ModuleDefinition module) {
        TypeDefinition plane = module.GetType("EB.Math.Plane");
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        FieldDefinition normal = plane == null ? null : plane.Fields.SingleOrDefault(field => field.Name == "Normal" &&
            !field.IsStatic && field.FieldType.FullName == "EB.Math.Vector3");
        FieldDefinition distance = plane == null ? null : plane.Fields.SingleOrDefault(field => field.Name == "D" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single);
        FieldDefinition[] components = vector3 == null ? new FieldDefinition[0] : new[] { "x", "y", "z" }
            .Select(name => vector3.Fields.SingleOrDefault(field => field.Name == name && !field.IsStatic &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        MethodDefinition method = plane == null ? null : plane.Methods.SingleOrDefault(candidate =>
            candidate.Name == "DotCoordinate" && !candidate.IsStatic && candidate.ReturnType.MetadataType == MetadataType.Void &&
            candidate.Parameters.Count == 2 && candidate.Parameters[0].ParameterType.FullName == "EB.Math.Vector3&" &&
            candidate.Parameters[1].ParameterType.FullName == "System.Single&");
        if (normal == null || distance == null || components.Length != 3 || components.Any(field => field == null) || method == null)
            throw new InvalidDataException("unexpected EB.Math.Plane.DotCoordinate metadata");

        MethodBody body = ResetBody(method, out ILProcessor il);
        for (int i = 0; i < components.Length; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, normal));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Mul));
            if (i != 0) il.Append(Instruction.Create(OpCodes.Add));
        }
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, distance));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Stind_R4));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Plane.DotCoordinate from normal, distance, and Vector3 components");
        return 1;
    }

    static int RepairPlaneDotNormal(ModuleDefinition module) {
        TypeDefinition plane = module.GetType("EB.Math.Plane");
        TypeDefinition vector3 = module.GetType("EB.Math.Vector3");
        FieldDefinition normal = plane == null ? null : plane.Fields.SingleOrDefault(field => field.Name == "Normal" &&
            !field.IsStatic && field.FieldType.FullName == "EB.Math.Vector3");
        FieldDefinition[] components = vector3 == null ? new FieldDefinition[0] : new[] { "x", "y", "z" }
            .Select(name => vector3.Fields.SingleOrDefault(field => field.Name == name && !field.IsStatic &&
                field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        MethodDefinition method = plane == null ? null : plane.Methods.SingleOrDefault(candidate =>
            candidate.Name == "DotNormal" && !candidate.IsStatic && candidate.ReturnType.MetadataType == MetadataType.Void &&
            candidate.Parameters.Count == 2 && candidate.Parameters[0].ParameterType.FullName == "EB.Math.Vector3&" &&
            candidate.Parameters[1].ParameterType.FullName == "System.Single&");
        if (normal == null || components.Length != 3 || components.Any(field => field == null) || method == null)
            throw new InvalidDataException("unexpected EB.Math.Plane.DotNormal metadata");

        MethodBody body = ResetBody(method, out ILProcessor il);
        for (int i = 0; i < components.Length; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, normal));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, components[i]));
            il.Append(Instruction.Create(OpCodes.Mul));
            if (i != 0) il.Append(Instruction.Create(OpCodes.Add));
        }
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Stind_R4));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Plane.DotNormal from normal and Vector3 components");
        return 1;
    }

    static int RepairRectangleContainsPoint(ModuleDefinition module) {
        TypeDefinition rectangle = module.GetType("EB.Math.Rectangle");
        TypeDefinition point = module.GetType("EB.Math.Point");
        FieldDefinition x = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "X" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition y = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "Y" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        MethodDefinition containsPoint = rectangle == null ? null : rectangle.Methods.SingleOrDefault(method =>
            method.Name == "Contains" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "EB.Math.Point");
        MethodDefinition containsCoordinates = rectangle == null ? null : rectangle.Methods.SingleOrDefault(method =>
            method.Name == "Contains" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.Parameters.Count == 2 && method.Parameters.All(parameter => parameter.ParameterType.MetadataType == MetadataType.Int32));
        FieldDefinition rectX = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "X" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition rectY = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Y" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition width = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Width" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition height = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Height" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        if (x == null || y == null || containsPoint == null || containsCoordinates == null ||
            rectX == null || rectY == null || width == null || height == null)
            throw new InvalidDataException("unexpected EB.Math.Rectangle.Contains(Point) metadata");

        // Use the usual half-open rectangle bounds. The recovered integer overload
        // is also malformed, so rebuild it directly rather than delegating to it.
        MethodBody coordinateBody = ResetBody(containsCoordinates, out ILProcessor coordinateIl);
        Instruction outside = Instruction.Create(OpCodes.Ldc_I4_0);
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, rectX));
        coordinateIl.Append(Instruction.Create(OpCodes.Blt, outside));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, rectX));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, width));
        coordinateIl.Append(Instruction.Create(OpCodes.Add));
        coordinateIl.Append(Instruction.Create(OpCodes.Bge, outside));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_2));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, rectY));
        coordinateIl.Append(Instruction.Create(OpCodes.Blt, outside));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_2));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, rectY));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldfld, height));
        coordinateIl.Append(Instruction.Create(OpCodes.Add));
        coordinateIl.Append(Instruction.Create(OpCodes.Bge, outside));
        coordinateIl.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        coordinateIl.Append(Instruction.Create(OpCodes.Ret));
        coordinateIl.Append(outside);
        coordinateIl.Append(Instruction.Create(OpCodes.Ret));
        coordinateBody.MaxStackSize = 2;

        MethodBody body = ResetBody(containsPoint, out ILProcessor il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarga, containsPoint.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarga, containsPoint.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(containsCoordinates)));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed Rectangle.Contains overloads with half-open bounds and point-coordinate forwarding");
        return 2;
    }

    static int RepairRectangleContainsPointOut(ModuleDefinition module) {
        TypeDefinition rectangle = module.GetType("EB.Math.Rectangle");
        TypeDefinition point = module.GetType("EB.Math.Point");
        FieldDefinition x = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "X" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition y = point == null ? null : point.Fields.SingleOrDefault(field => field.Name == "Y" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        MethodDefinition containsOut = rectangle == null ? null : rectangle.Methods.SingleOrDefault(method =>
            method.Name == "Contains" && !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Void &&
            method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == "EB.Math.Point&" &&
            method.Parameters[1].ParameterType.FullName == "System.Boolean&");
        FieldDefinition rectX = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "X" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition rectY = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Y" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition width = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Width" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        FieldDefinition height = rectangle == null ? null : rectangle.Fields.SingleOrDefault(field => field.Name == "Height" &&
            !field.IsStatic && field.FieldType.MetadataType == MetadataType.Int32);
        if (point == null || x == null || y == null || rectX == null || rectY == null || width == null || height == null || containsOut == null)
            throw new InvalidDataException("unexpected EB.Math.Rectangle.Contains(Point&,out bool) metadata");

        MethodBody body = ResetBody(containsOut, out ILProcessor il);
        Instruction outside = Instruction.Create(OpCodes.Ldarg_2);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        if (!point.IsValueType) il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, rectX));
        il.Append(Instruction.Create(OpCodes.Blt, outside));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        if (!point.IsValueType) il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, rectX));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, width));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Bge, outside));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        if (!point.IsValueType) il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, rectY));
        il.Append(Instruction.Create(OpCodes.Blt, outside));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        if (!point.IsValueType) il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, rectY));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, height));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Bge, outside));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stind_I1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(outside);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Stind_I1));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed Rectangle.Contains(Point&,out bool) from the half-open coordinate bounds");
        return 1;
    }

    static int RepairRectangleContainsRectangle(ModuleDefinition module) {
        TypeDefinition rectangle = module.GetType("EB.Math.Rectangle");
        if (rectangle == null) throw new InvalidDataException("missing EB.Math.Rectangle metadata");
        FieldDefinition[] fields = new[] { "X", "Y", "Width", "Height" }.Select(name =>
            rectangle.Fields.SingleOrDefault(field => field.Name == name && !field.IsStatic &&
                field.FieldType.MetadataType == MetadataType.Int32)).ToArray();
        MethodDefinition contains = rectangle.Methods.SingleOrDefault(method => method.Name == "Contains" &&
            !method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "EB.Math.Rectangle");
        if (fields.Any(field => field == null) || contains == null)
            throw new InvalidDataException("unexpected EB.Math.Rectangle.Contains(Rectangle) metadata");

        FieldDefinition x = fields[0], y = fields[1], width = fields[2], height = fields[3];
        MethodBody body = ResetBody(contains, out ILProcessor il);
        Instruction outside = Instruction.Create(OpCodes.Ldc_I4_0);
        // The other rectangle must start within this rectangle and its far edges
        // must not exceed this rectangle's far edges.
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Blt, outside));
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Blt, outside));
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, width));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, x));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, width));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Bgt, outside));
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarga, contains.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldfld, height));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, y));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, height));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Bgt, outside));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(outside);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.WriteLine("reconstructed EB.Math.Rectangle.Contains(Rectangle) from half-open extents");
        return 1;
    }

    static int RepairQuaternionAdd(ModuleDefinition module) {
        TypeDefinition quaternion = module.GetType("EB.Math.Quaternion");
        if (quaternion == null) throw new InvalidDataException("missing EB.Math.Quaternion metadata");
        MethodDefinition add = quaternion.Methods.SingleOrDefault(method => method.Name == "Add" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.Math.Quaternion&"));
        string[] names = { "x", "y", "z", "w" };
        FieldDefinition[] fields = names.Select(name => quaternion.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (add == null || fields.Any(field => field == null) || fields.Distinct().Count() != names.Length)
            throw new InvalidDataException("unexpected EB.Math.Quaternion.Add metadata");

        MethodBody body = ResetBody(add, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed EB.Math.Quaternion.Add across its four scalar fields");
        return 1;
    }

    static int RepairQuaternionSubtract(ModuleDefinition module) {
        TypeDefinition quaternion = module.GetType("EB.Math.Quaternion");
        if (quaternion == null) throw new InvalidDataException("missing EB.Math.Quaternion metadata");
        MethodDefinition subtract = quaternion.Methods.SingleOrDefault(method => method.Name == "Subtract" && method.IsStatic &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 3 &&
            method.Parameters.All(parameter => parameter.ParameterType.FullName == "EB.Math.Quaternion&"));
        string[] names = { "x", "y", "z", "w" };
        FieldDefinition[] fields = names.Select(name => quaternion.Fields.SingleOrDefault(field =>
            field.Name == name && !field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)).ToArray();
        if (subtract == null || fields.Any(field => field == null) || fields.Distinct().Count() != names.Length)
            throw new InvalidDataException("unexpected EB.Math.Quaternion.Subtract metadata");

        MethodBody body = ResetBody(subtract, out ILProcessor il);
        foreach (FieldDefinition field in fields) {
            il.Append(Instruction.Create(OpCodes.Ldarg_2));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Ldfld, field));
            il.Append(Instruction.Create(OpCodes.Sub));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed EB.Math.Quaternion.Subtract across its four scalar fields");
        return 1;
    }

    static int RepairWorldPainterLinesCross(ModuleDefinition module) {
        TypeDefinition owner = module.GetType("EBWorldPainterData");
        TypeDefinition point = module.GetType("EBWorldPainterData/Point");
        MethodDefinition method = owner == null ? null : owner.Methods.SingleOrDefault(candidate =>
            candidate.Name == "LinesCross" && candidate.IsStatic && candidate.ReturnType.MetadataType == MetadataType.Boolean &&
            candidate.Parameters.Count == 4 && candidate.Parameters.All(parameter =>
                parameter.ParameterType.FullName == "EBWorldPainterData/Point"));
        FieldDefinition location = point == null ? null : point.Fields.SingleOrDefault(field =>
            field.Name == "location" && !field.IsStatic && field.FieldType.FullName == "UnityEngine.Vector2");
        if (owner == null || point == null || method == null || location == null)
            throw new InvalidDataException("missing EBWorldPainterData.LinesCross or Point.location metadata");

        TypeReference vector2 = UnityEngineType(module, "Vector2");
        FieldReference x = module.ImportReference(new FieldReference("x", module.TypeSystem.Single, vector2));
        FieldReference y = module.ImportReference(new FieldReference("y", module.TypeSystem.Single, vector2));
        MethodBody body = ResetBody(method, out ILProcessor il);
        body.InitLocals = true;
        body.MaxStackSize = 4;
        VariableDefinition[] values = new VariableDefinition[8];
        for (int i = 0; i < values.Length; i++) {
            values[i] = new VariableDefinition(module.TypeSystem.Single);
            body.Variables.Add(values[i]);
        }

        Instruction noCross = il.Create(OpCodes.Ldc_I4_0);
        Instruction done = il.Create(OpCodes.Ret);
        for (int i = 0; i < 4; i++) {
            il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[i]));
            il.Append(Instruction.Create(OpCodes.Brfalse, noCross));
        }

        // r = p2-p1; s = p4-p3; denominator = cross(r,s).
        EmitWorldPointCoordinate(il, method, location, x, 1);
        EmitWorldPointCoordinate(il, method, location, x, 0);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[0]));
        EmitWorldPointCoordinate(il, method, location, y, 1);
        EmitWorldPointCoordinate(il, method, location, y, 0);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[1]));
        EmitWorldPointCoordinate(il, method, location, x, 3);
        EmitWorldPointCoordinate(il, method, location, x, 2);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[2]));
        EmitWorldPointCoordinate(il, method, location, y, 3);
        EmitWorldPointCoordinate(il, method, location, y, 2);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[3]));

        il.Append(Instruction.Create(OpCodes.Ldloc, values[0]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[3]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[1]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[2]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[4]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[4]));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 0f));
        il.Append(Instruction.Create(OpCodes.Beq, noCross));

        // q = p3-p1; t = cross(q,s)/denominator; u = cross(q,r)/denominator.
        EmitWorldPointCoordinate(il, method, location, x, 2);
        EmitWorldPointCoordinate(il, method, location, x, 0);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[5]));
        EmitWorldPointCoordinate(il, method, location, y, 2);
        EmitWorldPointCoordinate(il, method, location, y, 0);
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Stloc, values[6]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[5]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[3]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[6]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[2]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[4]));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Stloc, values[7]));

        // Segment parameters must be inside both closed intervals [0,1].
        il.Append(Instruction.Create(OpCodes.Ldloc, values[7]));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 0f));
        il.Append(Instruction.Create(OpCodes.Blt_Un, noCross));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[7]));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 1f));
        il.Append(Instruction.Create(OpCodes.Bgt_Un, noCross));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[5]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[1]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[6]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[0]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[4]));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 0f));
        il.Append(Instruction.Create(OpCodes.Blt_Un, noCross));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[5]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[1]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[6]));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[0]));
        il.Append(Instruction.Create(OpCodes.Mul));
        il.Append(Instruction.Create(OpCodes.Sub));
        il.Append(Instruction.Create(OpCodes.Ldloc, values[4]));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Ldc_R4, 1f));
        il.Append(Instruction.Create(OpCodes.Bgt_Un, noCross));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Br, done));
        il.Append(noCross);
        il.Append(done);
        Console.WriteLine("reconstructed EBWorldPainterData.LinesCross from the 9.2 segment intersection trace");
        return 1;
    }

    static void EmitWorldPointCoordinate(ILProcessor il, MethodDefinition method,
        FieldDefinition location, FieldReference coordinate, int parameterIndex) {
        il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[parameterIndex]));
        il.Append(Instruction.Create(OpCodes.Ldflda, location));
        il.Append(Instruction.Create(OpCodes.Ldfld, coordinate));
    }

    static int RepairBadgeStateConstructor(ModuleDefinition module) {
        TypeDefinition badge = module.GetType("EB.UI.Social.SocialStateModelBase/BadgeState");
        if (badge == null) throw new InvalidDataException("missing SocialStateModelBase.BadgeState metadata");
        FieldDefinition count = badge.Fields.SingleOrDefault(field => field.Name == "Count");
        FieldDefinition isNew = badge.Fields.SingleOrDefault(field => field.Name == "IsNew");
        MethodDefinition constructor = badge.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean);
        if (count == null || count.IsStatic || count.FieldType.MetadataType != MetadataType.Int32 ||
            isNew == null || isNew.IsStatic || isNew.FieldType.MetadataType != MetadataType.Boolean ||
            constructor == null)
            throw new InvalidDataException("unexpected BadgeState constructor metadata");

        ILProcessor il;
        MethodBody body = ResetBody(constructor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stfld, count));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.And));
        il.Append(Instruction.Create(OpCodes.Stfld, isNew));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        Console.WriteLine("reconstructed BadgeState constructor from the 9.2 ARM64 field-write trace");
        return 1;
    }

    static int RepairRequestSigning(ModuleDefinition module) {
        TypeDefinition digest = module.GetType("EB.Digest");
        TypeDefinition hmac = module.GetType("EB.Hmac");
        TypeDefinition encoding = module.GetType("EB.Encoding");
        TypeDefinition endpoint = module.GetType("EB.Sparx.HttpEndPoint");
        TypeDefinition request = module.GetType("EB.Sparx.Request");
        TypeDefinition uri = module.GetType("EB.Uri");
        if (digest == null || hmac == null || encoding == null || endpoint == null || request == null || uri == null)
            throw new InvalidDataException("missing 9.2 request-signing types");

        FieldDefinition digestValue = digest.Fields.SingleOrDefault(field => field.Name == "_digest");
        FieldDefinition hmacDigest = hmac.Fields.SingleOrDefault(field => field.Name == "_digest");
        FieldDefinition hmacKey = hmac.Fields.SingleOrDefault(field => field.Name == "_key");
        FieldDefinition hmacMac = hmac.Fields.SingleOrDefault(field => field.Name == "_mac");
        FieldDefinition endpointHmac = endpoint.Fields.SingleOrDefault(field => field.Name == "_hmac");
        FieldDefinition endpointMutex = endpoint.Fields.SingleOrDefault(field => field.Name == "_hmacMutex");
        FieldDefinition endpointSeparator = endpoint.Fields.SingleOrDefault(field => field.Name == "sEndl" && field.IsStatic);
        MethodDefinition requestUri = request.Properties.SingleOrDefault(property => property.Name == "uri")?.GetMethod;
        if (digestValue == null || hmacDigest == null || hmacKey == null || hmacMac == null ||
            endpointHmac == null || endpointMutex == null || endpointSeparator == null || requestUri == null)
            throw new InvalidDataException("missing 9.2 request-signing fields");

        MethodDefinition digestCtor = digest.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == digestValue.FieldType.FullName);
        MethodDefinition digestSize = digest.Methods.SingleOrDefault(method => method.Name == "get_DigestSize" &&
            method.ReturnType.MetadataType == MetadataType.Int32 && method.Parameters.Count == 0);
        MethodDefinition digestImplementation = digest.Methods.SingleOrDefault(method => method.Name == "get_Implementation" &&
            method.Parameters.Count == 0 && method.ReturnType.FullName == digestValue.FieldType.FullName);
        MethodDefinition digestSha1 = digest.Methods.SingleOrDefault(method => method.Name == "Sha1" &&
            method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.FullName == digest.FullName);
        MethodDefinition hmacCtor = hmac.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == digest.FullName &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Array);
        MethodDefinition hmacSize = hmac.Methods.SingleOrDefault(method => method.Name == "get_DigestSize" &&
            method.ReturnType.MetadataType == MetadataType.Int32 && method.Parameters.Count == 0);
        MethodDefinition hmacKeyGetter = hmac.Methods.SingleOrDefault(method => method.Name == "get_Key" &&
            method.ReturnType.MetadataType == MetadataType.Array && method.Parameters.Count == 0);
        MethodDefinition hmacSha1 = hmac.Methods.SingleOrDefault(method => method.Name == "Sha1" &&
            method.IsStatic && method.Parameters.Count == 1 && method.ReturnType.FullName == hmac.FullName);
        MethodDefinition hmacReset = hmac.Methods.SingleOrDefault(method => method.Name == "Reset" &&
            !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.FullName == hmac.FullName);
        MethodDefinition hmacUpdateText = hmac.Methods.SingleOrDefault(method => method.Name == "Update" &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String && method.ReturnType.FullName == hmac.FullName);
        MethodDefinition hmacUpdateBytes = hmac.Methods.SingleOrDefault(method => method.Name == "Update" &&
            !method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Array && method.ReturnType.FullName == hmac.FullName);
        MethodDefinition hmacUpdateRange = hmac.Methods.SingleOrDefault(method => method.Name == "Update" &&
            !method.IsStatic && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Array && method.ReturnType.FullName == hmac.FullName);
        MethodDefinition hmacFinal = hmac.Methods.SingleOrDefault(method => method.Name == "Final" &&
            !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Array);
        MethodDefinition getBytes = encoding.Methods.SingleOrDefault(method => method.Name == "GetBytes" &&
            method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.MetadataType == MetadataType.Array);
        MethodDefinition sign = endpoint.Methods.SingleOrDefault(method => method.Name == "Sign" &&
            !method.IsStatic && method.Parameters.Count == 4 && method.ReturnType.MetadataType == MetadataType.String);
        MethodDefinition host = uri.Properties.SingleOrDefault(property => property.Name == "Host")?.GetMethod;
        MethodDefinition path = uri.Properties.SingleOrDefault(property => property.Name == "Path")?.GetMethod;
        if (digestCtor == null || digestSize == null || digestImplementation == null || digestSha1 == null ||
            hmacCtor == null || hmacSize == null || hmacKeyGetter == null || hmacSha1 == null || hmacReset == null ||
            hmacUpdateText == null || hmacUpdateBytes == null || hmacUpdateRange == null || hmacFinal == null ||
            getBytes == null || sign == null || host == null || path == null)
            throw new InvalidDataException("missing 9.2 request-signing methods");

        TypeDefinition objectType = module.TypeSystem.Object.Resolve();
        MethodReference objectCtor = module.ImportReference(objectType.Methods.Single(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 0));
        TypeDefinition digestImplType = digestValue.FieldType.Resolve();
        TypeDefinition macType = hmacMac.FieldType.Resolve();
        TypeDefinition keyType = hmacKey.FieldType.Resolve();
        MethodReference digestSizeCall = module.ImportReference(digestImplType.Methods.Single(method =>
            method.Name == "GetDigestSize" && method.Parameters.Count == 0));
        MethodReference macResetCall = module.ImportReference(macType.Methods.Single(method =>
            method.Name == "Reset" && method.Parameters.Count == 0));
        MethodReference macBlockUpdateCall = module.ImportReference(macType.Methods.Single(method =>
            method.Name == "BlockUpdate" && method.Parameters.Count == 3));
        MethodReference macSizeCall = module.ImportReference(macType.Methods.Single(method =>
            method.Name == "GetMacSize" && method.Parameters.Count == 0));
        MethodReference macFinalCall = module.ImportReference(macType.Methods.Single(method =>
            method.Name == "DoFinal" && method.Parameters.Count == 2));
        MethodReference macInitCall = module.ImportReference(macType.Methods.Single(method =>
            method.Name == "Init" && method.Parameters.Count == 1));
        MethodReference keyGetter = module.ImportReference(keyType.Methods.Single(method =>
            method.Name == "GetKey" && method.Parameters.Count == 0));
        MethodReference utf8Getter = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
        MethodReference utf8GetBytes = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetBytes", new[] { typeof(string) }));
        MethodReference waitOne = module.ImportReference(typeof(System.Threading.Mutex).GetMethod("WaitOne", Type.EmptyTypes));
        MethodReference releaseMutex = module.ImportReference(typeof(System.Threading.Mutex).GetMethod("ReleaseMutex", Type.EmptyTypes));
        MethodReference base64 = encoding.Methods.SingleOrDefault(method => method.Name == "ToBase64String" &&
            method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Array);
        if (base64 == null) throw new InvalidDataException("missing 9.2 EB.Encoding.ToBase64String(byte[])");

        MethodReference sha1Ctor = FindMethodReference(module, "Org.BouncyCastle.Crypto.Digests.Sha1Digest", ".ctor", 0);
        MethodReference macCtor = FindMethodReference(module, "Org.BouncyCastle.Crypto.Macs.HMac", ".ctor", 1);
        MethodReference keyCtor = FindMethodReference(module, "Org.BouncyCastle.Crypto.Parameters.KeyParameter", ".ctor", 1);
        if (sha1Ctor == null || macCtor == null || keyCtor == null)
            return RepairHttpEndPointSign(module);

        int repaired = 0;
        ILProcessor il;
        MethodBody body;
        body = ResetBody(digestCtor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stfld, digestValue));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        repaired++;

        body = ResetBody(digestImplementation, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, digestValue));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        repaired++;

        body = ResetBody(digestSize, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, digestValue));
        il.Append(Instruction.Create(OpCodes.Callvirt, digestSizeCall));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        repaired++;

        body = ResetBody(digestSha1, out il);
        il.Append(Instruction.Create(OpCodes.Newobj, sha1Ctor));
        il.Append(Instruction.Create(OpCodes.Newobj, digestCtor));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        repaired++;

        body = ResetBody(getBytes, out il);
        il.Append(Instruction.Create(OpCodes.Call, utf8Getter));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, utf8GetBytes));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        repaired++;

        body = ResetBody(hmacCtor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stfld, hmacDigest));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Newobj, keyCtor));
        il.Append(Instruction.Create(OpCodes.Stfld, hmacKey));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, digestImplementation));
        il.Append(Instruction.Create(OpCodes.Newobj, macCtor));
        il.Append(Instruction.Create(OpCodes.Stfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacKey));
        il.Append(Instruction.Create(OpCodes.Callvirt, macInitCall));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        repaired++;

        body = ResetBody(hmacSize, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Callvirt, macSizeCall));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        repaired++;

        body = ResetBody(hmacKeyGetter, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacKey));
        il.Append(Instruction.Create(OpCodes.Callvirt, keyGetter));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        repaired++;

        body = ResetBody(hmacReset, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Callvirt, macResetCall));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        repaired++;

        body = ResetBody(hmacUpdateText, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, getBytes));
        il.Append(Instruction.Create(OpCodes.Call, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        repaired++;

        body = ResetBody(hmacUpdateBytes, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldlen));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Call, hmacUpdateRange));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 4;
        repaired++;

        body = ResetBody(hmacUpdateRange, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Callvirt, macBlockUpdateCall));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 4;
        repaired++;

        VariableDefinition digestBuffer = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body = ResetBody(hmacFinal, out il);
        body.InitLocals = true;
        body.Variables.Add(digestBuffer);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Callvirt, macSizeCall));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Byte));
        il.Append(Instruction.Create(OpCodes.Stloc, digestBuffer));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacMac));
        il.Append(Instruction.Create(OpCodes.Ldloc, digestBuffer));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, macFinalCall));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldloc, digestBuffer));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        repaired++;

        body = ResetBody(hmacSha1, out il);
        il.Append(Instruction.Create(OpCodes.Call, digestSha1));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, hmacCtor));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        repaired++;

        FieldDefinition endpointHmacRef = module.ImportReference(endpointHmac).Resolve();
        FieldDefinition endpointMutexRef = module.ImportReference(endpointMutex).Resolve();
        FieldDefinition endpointSeparatorRef = module.ImportReference(endpointSeparator).Resolve();
        MethodReference requestUriRef = module.ImportReference(requestUri);
        VariableDefinition signatureBytes = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body = ResetBody(sign, out il);
        body.InitLocals = true;
        body.Variables.Add(signatureBytes);
        Instruction returnNull = Instruction.Create(OpCodes.Ldnull);
        Instruction hasPostData = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointMutexRef));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUriRef));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointMutexRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, waitOne));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacReset));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldsfld, endpointSeparatorRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUriRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, host));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldsfld, endpointSeparatorRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUriRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, path));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldsfld, endpointSeparatorRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, sign.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Brfalse, hasPostData));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldsfld, endpointSeparatorRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, sign.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacUpdateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(hasPostData);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointHmacRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, hmacFinal));
        il.Append(Instruction.Create(OpCodes.Stloc, signatureBytes));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, endpointMutexRef));
        il.Append(Instruction.Create(OpCodes.Callvirt, releaseMutex));
        il.Append(Instruction.Create(OpCodes.Ldloc, signatureBytes));
        il.Append(Instruction.Create(OpCodes.Call, base64));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(returnNull);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        repaired++;

        return repaired;
    }

    static int RepairHttpEndPointSign(ModuleDefinition module) {
        TypeDefinition endpoint = module.GetType("EB.Sparx.HttpEndPoint");
        TypeDefinition request = module.GetType("EB.Sparx.Request");
        TypeDefinition uri = module.GetType("EB.Uri");
        TypeDefinition encoding = module.GetType("EB.Encoding");
        if (endpoint == null || request == null || uri == null || encoding == null)
            throw new InvalidDataException("missing 9.2 HttpEndPoint.Sign metadata");
        FieldDefinition hmacField = endpoint.Fields.Single(field => field.Name == "_hmac");
        FieldDefinition mutexField = endpoint.Fields.Single(field => field.Name == "_hmacMutex");
        FieldDefinition separatorField = endpoint.Fields.Single(field => field.Name == "sEndl" && field.IsStatic);
        MethodDefinition requestUri = request.Properties.Single(property => property.Name == "uri").GetMethod;
        MethodDefinition host = uri.Properties.Single(property => property.Name == "Host").GetMethod;
        MethodDefinition path = uri.Properties.Single(property => property.Name == "Path").GetMethod;
        MethodDefinition reset = endpoint.Module.GetType("EB.Hmac").Methods.Single(method => method.Name == "Reset" && method.Parameters.Count == 0);
        MethodDefinition updateText = endpoint.Module.GetType("EB.Hmac").Methods.Single(method => method.Name == "Update" &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        MethodDefinition updateBytes = endpoint.Module.GetType("EB.Hmac").Methods.Single(method => method.Name == "Update" &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Array);
        MethodDefinition final = endpoint.Module.GetType("EB.Hmac").Methods.Single(method => method.Name == "Final" && method.Parameters.Count == 0);
        MethodDefinition getBytes = encoding.Methods.Single(method => method.Name == "GetBytes" && method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        MethodDefinition base64 = encoding.Methods.Single(method => method.Name == "ToBase64String" && method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Array);
        MethodReference utf8Getter = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
        MethodReference utf8GetBytes = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetBytes", new[] { typeof(string) }));
        MethodDefinition sign = endpoint.Methods.Single(method => method.Name == "Sign" && !method.IsStatic &&
            method.Parameters.Count == 4 && method.ReturnType.MetadataType == MetadataType.String);
        MethodReference waitOne = module.ImportReference(typeof(System.Threading.Mutex).GetMethod("WaitOne", Type.EmptyTypes));
        MethodReference releaseMutex = module.ImportReference(typeof(System.Threading.Mutex).GetMethod("ReleaseMutex", Type.EmptyTypes));

        ILProcessor il;
        MethodBody body = ResetBody(sign, out il);
        VariableDefinition signatureBytes = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body.InitLocals = true;
        body.Variables.Add(signatureBytes);
        Instruction returnNull = Instruction.Create(OpCodes.Ldnull);
        Instruction noPostData = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, mutexField));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUri));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, mutexField));
        il.Append(Instruction.Create(OpCodes.Callvirt, waitOne));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Callvirt, reset));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldsfld, separatorField));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUri));
        il.Append(Instruction.Create(OpCodes.Callvirt, host));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldsfld, separatorField));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, requestUri));
        il.Append(Instruction.Create(OpCodes.Callvirt, path));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldsfld, separatorField));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateText));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, sign.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Brfalse, noPostData));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldsfld, separatorField));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Ldarg_S, sign.Parameters[3]));
        il.Append(Instruction.Create(OpCodes.Callvirt, updateBytes));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(noPostData);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, hmacField));
        il.Append(Instruction.Create(OpCodes.Callvirt, final));
        il.Append(Instruction.Create(OpCodes.Stloc, signatureBytes));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, mutexField));
        il.Append(Instruction.Create(OpCodes.Callvirt, releaseMutex));
        il.Append(Instruction.Create(OpCodes.Ldloc, signatureBytes));
        il.Append(Instruction.Create(OpCodes.Call, base64));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(returnNull);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        body = ResetBody(getBytes, out il);
        il.Append(Instruction.Create(OpCodes.Call, utf8Getter));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, utf8GetBytes));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        return 2;
    }

    static MethodBody ResetBody(MethodDefinition method, out ILProcessor il) {
        if (method == null) throw new InvalidDataException("cannot rebuild a missing method body");
        MethodBody body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        il = body.GetILProcessor();
        return body;
    }

    static MethodReference FindMethodReference(ModuleDefinition module, string typeName, string methodName, int parameterCount) {
        foreach (TypeDefinition type in AllTypes(module.Types)) {
            foreach (MethodDefinition method in type.Methods) {
                if (!method.HasBody) continue;
                foreach (Instruction instruction in method.Body.Instructions) {
                    if (instruction.Operand is MethodReference reference &&
                        reference.DeclaringType.FullName == typeName && reference.Name == methodName &&
                        reference.Parameters.Count == parameterCount)
                        return module.ImportReference(reference);
                }
            }
        }
        return null;
    }

    static int RepairManagedHmac(ModuleDefinition module) {
        TypeDefinition hmac = module.GetType("EB.Hmac");
        if (hmac == null) throw new InvalidDataException("missing 9.2 EB.Hmac");
        TypeReference hashType = module.ImportReference(typeof(System.Security.Cryptography.HMAC));
        FieldDefinition algorithm = hmac.Fields.SingleOrDefault(field => field.Name == "_recoveredManagedHmac");
        if (algorithm == null) {
            algorithm = new FieldDefinition("_recoveredManagedHmac", FieldAttributes.Private, hashType);
            hmac.Fields.Add(algorithm);
        }
        MethodDefinition emptyConstructor = hmac.Methods.SingleOrDefault(method => method.IsConstructor &&
            !method.IsStatic && method.Parameters.Count == 0);
        if (emptyConstructor == null) {
            emptyConstructor = new MethodDefinition(".ctor", MethodAttributes.Private | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            hmac.Methods.Add(emptyConstructor);
        }
        MethodReference objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
        MethodReference hmacSha1Ctor = module.ImportReference(typeof(System.Security.Cryptography.HMACSHA1)
            .GetConstructor(new[] { typeof(byte[]) }));
        MethodReference hmacMd5Ctor = module.ImportReference(typeof(System.Security.Cryptography.HMACMD5)
            .GetConstructor(new[] { typeof(byte[]) }));
        MethodReference initialize = module.ImportReference(typeof(System.Security.Cryptography.HashAlgorithm)
            .GetMethod("Initialize", Type.EmptyTypes));
        MethodReference transformBlock = module.ImportReference(typeof(System.Security.Cryptography.HashAlgorithm)
            .GetMethod("TransformBlock", new[] { typeof(byte[]), typeof(int), typeof(int), typeof(byte[]), typeof(int) }));
        MethodReference transformFinal = module.ImportReference(typeof(System.Security.Cryptography.HashAlgorithm)
            .GetMethod("TransformFinalBlock", new[] { typeof(byte[]), typeof(int), typeof(int) }));
        MethodReference getHash = module.ImportReference(typeof(System.Security.Cryptography.HashAlgorithm)
            .GetProperty("Hash").GetGetMethod());
        MethodReference getHashSize = module.ImportReference(typeof(System.Security.Cryptography.HashAlgorithm)
            .GetProperty("HashSize").GetGetMethod());
        MethodReference getKey = module.ImportReference(typeof(System.Security.Cryptography.HMAC)
            .GetProperty("Key").GetGetMethod());
        MethodReference utf8 = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
        MethodReference utf8Bytes = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetBytes", new[] { typeof(string) }));
        MethodDefinition ctor = hmac.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 2);
        MethodDefinition size = hmac.Methods.Single(method => method.Name == "get_DigestSize" && method.Parameters.Count == 0);
        MethodDefinition key = hmac.Methods.Single(method => method.Name == "get_Key" && method.Parameters.Count == 0);
        MethodDefinition sha1 = hmac.Methods.Single(method => method.Name == "Sha1" && method.IsStatic && method.Parameters.Count == 1);
        MethodDefinition md5 = hmac.Methods.Single(method => method.Name == "MD5" && method.IsStatic && method.Parameters.Count == 1);
        MethodDefinition reset = hmac.Methods.Single(method => method.Name == "Reset" && !method.IsStatic && method.Parameters.Count == 0);
        MethodDefinition updateString = hmac.Methods.Single(method => method.Name == "Update" && !method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        MethodDefinition updateBytes = hmac.Methods.Single(method => method.Name == "Update" && !method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Array);
        MethodDefinition updateRange = hmac.Methods.Single(method => method.Name == "Update" && !method.IsStatic && method.Parameters.Count == 3);
        MethodDefinition final = hmac.Methods.Single(method => method.Name == "Final" && !method.IsStatic && method.Parameters.Count == 0);
        MethodReference updateArray = module.ImportReference(updateBytes);
        int count = 0;
        ILProcessor il;
        MethodBody body = ResetBody(emptyConstructor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        count++;

        body = ResetBody(ctor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Newobj, hmacSha1Ctor));
        il.Append(Instruction.Create(OpCodes.Stfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        count++;

        body = ResetBody(sha1, out il);
        il.Append(Instruction.Create(OpCodes.Newobj, emptyConstructor));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, hmacSha1Ctor));
        il.Append(Instruction.Create(OpCodes.Stfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        count++;

        body = ResetBody(md5, out il);
        il.Append(Instruction.Create(OpCodes.Newobj, emptyConstructor));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, hmacMd5Ctor));
        il.Append(Instruction.Create(OpCodes.Stfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        count++;

        body = ResetBody(size, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Callvirt, getHashSize));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_8));
        il.Append(Instruction.Create(OpCodes.Div));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        count++;

        body = ResetBody(key, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Callvirt, getKey));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        count++;

        body = ResetBody(reset, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Callvirt, initialize));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        count++;

        body = ResetBody(updateString, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, utf8));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, utf8Bytes));
        il.Append(Instruction.Create(OpCodes.Call, updateArray));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        count++;

        body = ResetBody(updateBytes, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldlen));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Call, updateRange));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 4;
        count++;

        body = ResetBody(updateRange, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Callvirt, transformBlock));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 6;
        count++;

        VariableDefinition empty = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body = ResetBody(final, out il);
        body.InitLocals = true;
        body.Variables.Add(empty);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Byte));
        il.Append(Instruction.Create(OpCodes.Stloc, empty));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Ldloc, empty));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, transformFinal));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, algorithm));
        il.Append(Instruction.Create(OpCodes.Callvirt, getHash));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 4;
        count++;

        return count;
    }

    static int RepairEncodingInitializer(ModuleDefinition module) {
        TypeDefinition encoding = module.GetType("EB.Encoding");
        if (encoding == null) throw new InvalidDataException("missing EB.Encoding");
        FieldDefinition encodingField = encoding.Fields.Single(field => field.Name == "_encoding" && field.IsStatic);
        FieldDefinition hexTable = encoding.Fields.Single(field => field.Name == "_HexStringTable" && field.IsStatic);
        MethodDefinition initializer = encoding.Methods.Single(method => method.IsConstructor && method.IsStatic);
        MethodReference utf8 = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
        MethodReference format = module.ImportReference(typeof(string).GetMethod("Format", new[] { typeof(string), typeof(object) }));
        TypeReference stringType = module.TypeSystem.String;
        VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
        ILProcessor il;
        MethodBody body = ResetBody(initializer, out il);
        body.InitLocals = true;
        body.Variables.Add(index);
        Instruction loop = Instruction.Create(OpCodes.Ldloc, index);
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Call, utf8));
        il.Append(Instruction.Create(OpCodes.Stsfld, encodingField));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 256));
        il.Append(Instruction.Create(OpCodes.Newarr, stringType));
        il.Append(Instruction.Create(OpCodes.Stsfld, hexTable));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(loop);
        il.Append(Instruction.Create(OpCodes.Ldc_I4, 256));
        il.Append(Instruction.Create(OpCodes.Bge, done));
        il.Append(Instruction.Create(OpCodes.Ldsfld, hexTable));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Ldstr, "{0:x2}"));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(Instruction.Create(OpCodes.Call, format));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(Instruction.Create(OpCodes.Br, loop));
        il.Append(done);
        body.MaxStackSize = 4;
        return 1;
    }

    static int RepairHttpEndPointInitializer(ModuleDefinition module) {
        TypeDefinition endpoint = module.GetType("EB.Sparx.HttpEndPoint");
        TypeDefinition encoding = module.GetType("EB.Encoding");
        if (endpoint == null || encoding == null) throw new InvalidDataException("missing HttpEndPoint initializer metadata");
        FieldDefinition lastMemoryCheck = endpoint.Fields.Single(field => field.Name == "_lastMemoryCheck" && field.IsStatic);
        FieldDefinition separator = endpoint.Fields.Single(field => field.Name == "sEndl" && field.IsStatic);
        MethodDefinition initializer = endpoint.Methods.Single(method => method.IsConstructor && method.IsStatic);
        MethodDefinition getBytes = encoding.Methods.Single(method => method.Name == "GetBytes" && method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        MethodReference getUtcNow = module.ImportReference(typeof(DateTime).GetProperty("UtcNow").GetGetMethod());
        ILProcessor il;
        MethodBody body = ResetBody(initializer, out il);
        il.Append(Instruction.Create(OpCodes.Call, getUtcNow));
        il.Append(Instruction.Create(OpCodes.Stsfld, lastMemoryCheck));
        il.Append(Instruction.Create(OpCodes.Ldstr, "\n"));
        il.Append(Instruction.Create(OpCodes.Call, getBytes));
        il.Append(Instruction.Create(OpCodes.Stsfld, separator));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        return 1;
    }

    static int RepairUriInitializer(ModuleDefinition module) {
        TypeDefinition uri = module.GetType("EB.Uri");
        if (uri == null) throw new InvalidDataException("missing EB.Uri");
        FieldDefinition trimChars = uri.Fields.Single(field => field.Name == "kUriCombineTrimChars" && field.IsStatic);
        MethodDefinition initializer = uri.Methods.Single(method => method.IsConstructor && method.IsStatic);
        ILProcessor il;
        MethodBody body = ResetBody(initializer, out il);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Char));
        il.Append(Instruction.Create(OpCodes.Stsfld, trimChars));
        il.Append(Instruction.Create(OpCodes.Ldsfld, trimChars));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(Instruction.Create(OpCodes.Stelem_I2));
        il.Append(Instruction.Create(OpCodes.Ldsfld, trimChars));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)'/'));
        il.Append(Instruction.Create(OpCodes.Stelem_I2));
        il.Append(Instruction.Create(OpCodes.Ldsfld, trimChars));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)':'));
        il.Append(Instruction.Create(OpCodes.Stelem_I2));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        return 1;
    }

    static int RepairUriComponentLookup(ModuleDefinition module) {
        TypeDefinition uri = module.GetType("EB.Uri");
        if (uri == null) throw new InvalidDataException("missing EB.Uri");
        FieldDefinition components = uri.Fields.Single(field => field.Name == "_components" && !field.IsStatic);
        MethodDefinition getComponent = uri.Methods.Single(method => method.Name == "GetComponent" &&
            !method.IsStatic && method.Parameters.Count == 2 && method.ReturnType.MetadataType == MetadataType.String);
        MethodReference isNullOrEmpty = module.ImportReference(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }));
        VariableDefinition value = new VariableDefinition(module.TypeSystem.String);
        ILProcessor il;
        MethodBody body = ResetBody(getComponent, out il);
        body.InitLocals = true;
        body.Variables.Add(value);
        Instruction useDefault = Instruction.Create(OpCodes.Ldarg_2);
        Instruction found = Instruction.Create(OpCodes.Ldloc, value);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, components));
        il.Append(Instruction.Create(OpCodes.Brfalse, useDefault));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Blt, useDefault));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, components));
        il.Append(Instruction.Create(OpCodes.Ldlen));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Bge, useDefault));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, components));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldelem_Ref));
        il.Append(Instruction.Create(OpCodes.Stloc, value));
        il.Append(Instruction.Create(OpCodes.Ldloc, value));
        il.Append(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        il.Append(Instruction.Create(OpCodes.Brfalse, found));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(found);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(useDefault);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;
        return 1;
    }

    static int RepairUriParser(ModuleDefinition module) {
        TypeDefinition uri = module.GetType("EB.Uri");
        if (uri == null) throw new InvalidDataException("missing EB.Uri");
        FieldDefinition componentsField = uri.Fields.Single(field => field.Name == "_components" && !field.IsStatic);
        MethodDefinition parse = uri.Methods.Single(method => method.Name == "Parse" && !method.IsStatic &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.MetadataType == MetadataType.Boolean);
        MethodDefinition stringCtor = uri.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 1);
        MethodDefinition emptyCtor = uri.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        MethodReference objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
        MethodReference tryCreate = module.ImportReference(typeof(global::System.Uri).GetMethod("TryCreate",
            new[] { typeof(string), typeof(UriKind), typeof(global::System.Uri).MakeByRefType() }));
        MethodReference getScheme = module.ImportReference(typeof(global::System.Uri).GetProperty("Scheme").GetGetMethod());
        MethodReference getUserInfo = module.ImportReference(typeof(global::System.Uri).GetProperty("UserInfo").GetGetMethod());
        MethodReference getHost = module.ImportReference(typeof(global::System.Uri).GetProperty("Host").GetGetMethod());
        MethodReference getPort = module.ImportReference(typeof(global::System.Uri).GetProperty("Port").GetGetMethod());
        MethodReference intToString = module.ImportReference(typeof(Convert).GetMethod("ToString", new[] { typeof(int) }));
        MethodReference getAbsolutePath = module.ImportReference(typeof(global::System.Uri).GetProperty("AbsolutePath").GetGetMethod());
        MethodReference getQuery = module.ImportReference(typeof(global::System.Uri).GetProperty("Query").GetGetMethod());
        MethodReference getStringLength = module.ImportReference(typeof(string).GetProperty("Length").GetGetMethod());
        MethodReference getStringChar = module.ImportReference(typeof(string).GetProperty("Chars").GetGetMethod());
        MethodReference substring = module.ImportReference(typeof(string).GetMethod("Substring", new[] { typeof(int) }));
        if (tryCreate == null || getScheme == null || getUserInfo == null || getHost == null || getPort == null ||
            intToString == null || getAbsolutePath == null || getQuery == null || getStringLength == null ||
            getStringChar == null || substring == null)
            throw new InvalidDataException("missing Unity 2020 System.Uri parser metadata");

        VariableDefinition parsed = new VariableDefinition(module.ImportReference(typeof(global::System.Uri)));
        VariableDefinition values = new VariableDefinition(new ArrayType(module.TypeSystem.String));
        VariableDefinition query = new VariableDefinition(module.TypeSystem.String);
        ILProcessor il;
        MethodBody body = ResetBody(parse, out il);
        body.InitLocals = true;
        body.Variables.Add(parsed);
        body.Variables.Add(values);
        body.Variables.Add(query);
        Instruction failed = Instruction.Create(OpCodes.Ldc_I4_0);
        Instruction storeQuery = Instruction.Create(OpCodes.Ldloc, values);
        Instruction commitAndReturn = Instruction.Create(OpCodes.Ldarg_0);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_8));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.String));
        il.Append(Instruction.Create(OpCodes.Stloc, values));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Brfalse, failed));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldloca, parsed));
        il.Append(Instruction.Create(OpCodes.Call, tryCreate));
        il.Append(Instruction.Create(OpCodes.Brfalse, failed));

        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getScheme));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getUserInfo));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_3));
        il.Append(Instruction.Create(OpCodes.Ldstr, ""));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_4));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getHost));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_5));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getPort));
        il.Append(Instruction.Create(OpCodes.Call, intToString));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_6));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getAbsolutePath));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, parsed));
        il.Append(Instruction.Create(OpCodes.Callvirt, getQuery));
        il.Append(Instruction.Create(OpCodes.Stloc, query));
        il.Append(Instruction.Create(OpCodes.Ldloc, query));
        il.Append(Instruction.Create(OpCodes.Callvirt, getStringLength));
        il.Append(Instruction.Create(OpCodes.Brfalse, storeQuery));
        il.Append(Instruction.Create(OpCodes.Ldloc, query));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, getStringChar));
        il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)'?'));
        il.Append(Instruction.Create(OpCodes.Bne_Un, storeQuery));
        il.Append(Instruction.Create(OpCodes.Ldloc, query));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Callvirt, substring));
        il.Append(Instruction.Create(OpCodes.Stloc, query));
        il.Append(storeQuery);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_7));
        il.Append(Instruction.Create(OpCodes.Ldloc, query));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(commitAndReturn);
        il.Append(Instruction.Create(OpCodes.Ldloc, values));
        il.Append(Instruction.Create(OpCodes.Stfld, componentsField));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(failed);
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 3;

        body = ResetBody(stringCtor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, parse));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;

        body = ResetBody(emptyCtor, out il);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, objectCtor));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_8));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.String));
        il.Append(Instruction.Create(OpCodes.Stfld, componentsField));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        return 3;
    }

    // Rebuild EB.StringUtil's static initialization from the exact clean 9.2
    // ARM64 trace at 0x122C2A0. It creates the allowed-character table and
    // enables the legacy warning; the per-call buffer remains null until used.
    static int RepairStringUtilInitializer(ModuleDefinition module) {
        TypeDefinition type = module.GetType("EB.StringUtil");
        FieldDefinition valid = type == null ? null : type.Fields.SingleOrDefault(field =>
            field.Name == "valid" && field.IsStatic && field.FieldType is ArrayType array &&
            array.ElementType.MetadataType == MetadataType.Char);
        FieldDefinition legacyWarning = type == null ? null : type.Fields.SingleOrDefault(field =>
            field.Name == "giveLegacyWarning" && field.IsStatic &&
            field.FieldType.MetadataType == MetadataType.Boolean);
        MethodDefinition initializer = type == null ? null : type.Methods.SingleOrDefault(method =>
            method.Name == ".cctor" && method.IsStatic && method.Parameters.Count == 0 &&
            method.ReturnType.MetadataType == MetadataType.Void);
        System.Reflection.MethodInfo toCharArrayMethod = typeof(string).GetMethod("ToCharArray", Type.EmptyTypes);
        if (valid == null || legacyWarning == null || initializer == null || toCharArrayMethod == null)
            throw new InvalidDataException("missing clean 9.2 EB.StringUtil initializer metadata");

        initializer.Body.ExceptionHandlers.Clear();
        initializer.Body.Variables.Clear();
        initializer.Body.Instructions.Clear();
        initializer.Body.InitLocals = true;
        ILProcessor il = initializer.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldstr, "abcdefghijklmnopqrstuvwxyz0123456789/_"));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(toCharArrayMethod)));
        il.Append(Instruction.Create(OpCodes.Stsfld, valid));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stsfld, legacyWarning));
        il.Append(Instruction.Create(OpCodes.Ret));
        initializer.Body.MaxStackSize = 1;
        return 1;
    }

    // Rebuild the two-argument SafeKey from the 9.2 native trace at
    // 0x122BE78: lowercase when a character set is supplied, remove characters
    // outside that set, and leave null inputs unchanged. The one-time legacy
    // diagnostic is intentionally omitted; the warning flag is still cleared.
    static int RepairStringUtilSafeKey(ModuleDefinition module) {
        TypeDefinition type = module.GetType("EB.StringUtil");
        FieldDefinition valid = type == null ? null : type.Fields.SingleOrDefault(field =>
            field.Name == "valid" && field.IsStatic && field.FieldType is ArrayType validArray &&
            validArray.ElementType.MetadataType == MetadataType.Char);
        FieldDefinition warning = type == null ? null : type.Fields.SingleOrDefault(field =>
            field.Name == "giveLegacyWarning" && field.IsStatic &&
            field.FieldType.MetadataType == MetadataType.Boolean);
        MethodDefinition method = type == null ? null : type.Methods.SingleOrDefault(candidate =>
            candidate.Name == "SafeKey" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.String && candidate.Parameters.Count == 2 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            candidate.Parameters[1].ParameterType is ArrayType array &&
            array.ElementType.MetadataType == MetadataType.Char);
        MethodDefinition wrapper = type == null ? null : type.Methods.SingleOrDefault(candidate =>
            candidate.Name == "SafeKey" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.String && candidate.Parameters.Count == 1 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        System.Reflection.MethodInfo lower = typeof(string).GetMethod("ToLowerInvariant", Type.EmptyTypes);
        System.Reflection.MethodInfo length = typeof(string).GetProperty("Length").GetGetMethod();
        System.Reflection.MethodInfo getChar = typeof(string).GetProperty("Chars").GetGetMethod();
        System.Reflection.MethodInfo indexOf = typeof(string).GetMethod("IndexOf", new[] { typeof(char) });
        System.Reflection.ConstructorInfo stringFromChars = typeof(string).GetConstructor(new[] { typeof(char[]) });
        Type builderType = typeof(System.Text.StringBuilder);
        System.Reflection.ConstructorInfo builderConstructor = builderType.GetConstructor(Type.EmptyTypes);
        System.Reflection.MethodInfo appendChar = builderType.GetMethod("Append", new[] { typeof(char) });
        System.Reflection.MethodInfo builderToString = builderType.GetMethod("ToString", Type.EmptyTypes);
        if (valid == null || warning == null || method == null || wrapper == null || lower == null || length == null || getChar == null ||
            indexOf == null || stringFromChars == null || builderConstructor == null ||
            appendChar == null || builderToString == null)
            throw new InvalidDataException("missing clean 9.2 EB.StringUtil.SafeKey metadata");

        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        method.Body.InitLocals = true;
        VariableDefinition text = new VariableDefinition(module.TypeSystem.String);
        VariableDefinition validText = new VariableDefinition(module.TypeSystem.String);
        VariableDefinition builder = new VariableDefinition(module.ImportReference(builderType));
        VariableDefinition index = new VariableDefinition(module.TypeSystem.Int32);
        method.Body.Variables.Add(text);
        method.Body.Variables.Add(validText);
        method.Body.Variables.Add(builder);
        method.Body.Variables.Add(index);
        ILProcessor il = method.Body.GetILProcessor();
        Instruction returnNull = Instruction.Create(OpCodes.Ldnull);
        Instruction original = Instruction.Create(OpCodes.Ldarg_0);
        Instruction loop = Instruction.Create(OpCodes.Ldloc, index);
        Instruction skipAppend = Instruction.Create(OpCodes.Ldloc, index);
        Instruction done = Instruction.Create(OpCodes.Ldloc, builder);
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Brfalse, original));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Stsfld, warning));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Brfalse, returnNull));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(lower)));
        il.Append(Instruction.Create(OpCodes.Stloc, text));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(stringFromChars)));
        il.Append(Instruction.Create(OpCodes.Stloc, validText));
        il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(builderConstructor)));
        il.Append(Instruction.Create(OpCodes.Stloc, builder));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(loop);
        il.Append(Instruction.Create(OpCodes.Ldloc, text));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(length)));
        il.Append(Instruction.Create(OpCodes.Bge, done));
        il.Append(Instruction.Create(OpCodes.Ldloc, validText));
        il.Append(Instruction.Create(OpCodes.Ldloc, text));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(getChar)));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(indexOf)));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Blt, skipAppend));
        il.Append(Instruction.Create(OpCodes.Ldloc, builder));
        il.Append(Instruction.Create(OpCodes.Ldloc, text));
        il.Append(Instruction.Create(OpCodes.Ldloc, index));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(getChar)));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(appendChar)));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(skipAppend);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Add));
        il.Append(Instruction.Create(OpCodes.Stloc, index));
        il.Append(Instruction.Create(OpCodes.Br, loop));
        il.Append(done);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(builderToString)));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(original);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(returnNull);
        il.Append(Instruction.Create(OpCodes.Ret));
        method.Body.MaxStackSize = 3;

        wrapper.Body.ExceptionHandlers.Clear();
        wrapper.Body.Variables.Clear();
        wrapper.Body.Instructions.Clear();
        wrapper.Body.InitLocals = true;
        ILProcessor wrapperIl = wrapper.Body.GetILProcessor();
        wrapperIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        wrapperIl.Append(Instruction.Create(OpCodes.Ldsfld, valid));
        wrapperIl.Append(Instruction.Create(OpCodes.Call, method));
        wrapperIl.Append(Instruction.Create(OpCodes.Ret));
        wrapper.Body.MaxStackSize = 2;
        return 2;
    }

    // Rebuild the private EB.Dot.String lookup primitive from its 9.2 ARM64
    // trace. It clears the out value, finds the named object, converts it using
    // NumberUtils.InvariantSafeToString, then applies the optional lookup table.
    static int RepairDotString(ModuleDefinition module) {
        TypeDefinition dot = module.GetType("EB.Dot");
        TypeDefinition numbers = module.GetType("EB.NumberUtils");
        MethodDefinition method = dot == null ? null : dot.Methods.SingleOrDefault(candidate =>
            candidate.Name == "String" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.Boolean && candidate.Parameters.Count == 4 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            candidate.Parameters[1].ParameterType.MetadataType == MetadataType.Object &&
            candidate.Parameters[2].ParameterType.FullName == "System.String&" &&
            candidate.Parameters[3].ParameterType.FullName == "System.Collections.IDictionary");
        MethodDefinition find = dot == null ? null : dot.Methods.SingleOrDefault(candidate =>
            candidate.Name == "Find" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.Object && candidate.Parameters.Count == 2 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            candidate.Parameters[1].ParameterType.MetadataType == MetadataType.Object);
        MethodDefinition invariantToString = numbers == null ? null : numbers.Methods.SingleOrDefault(candidate =>
            candidate.Name == "InvariantSafeToString" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.String && candidate.Parameters.Count == 1 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.Object);
        MethodDefinition lookup = dot == null ? null : dot.Methods.SingleOrDefault(candidate =>
            candidate.Name == "GetStringLookupValue" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.String && candidate.Parameters.Count == 2 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            candidate.Parameters[1].ParameterType.FullName == "System.Collections.IDictionary");
        if (method == null || find == null || invariantToString == null || lookup == null)
            throw new InvalidDataException("missing 9.2 EB.Dot.String trace dependencies");

        ModuleDefinition target = method.Module;
        FieldReference stringEmpty = target.ImportReference(typeof(string).GetField("Empty"));
        VariableDefinition value = new VariableDefinition(target.TypeSystem.Object);
        VariableDefinition text = new VariableDefinition(target.TypeSystem.String);
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = true;
        method.Body.Variables.Add(value);
        method.Body.Variables.Add(text);
        ILProcessor il = method.Body.GetILProcessor();
        Instruction missing = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldsfld, stringEmpty));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, find));
        il.Append(Instruction.Create(OpCodes.Stloc, value));
        il.Append(Instruction.Create(OpCodes.Ldloc, value));
        il.Append(Instruction.Create(OpCodes.Brfalse, missing));
        il.Append(Instruction.Create(OpCodes.Ldloc, value));
        il.Append(Instruction.Create(OpCodes.Call, invariantToString));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Call, lookup));
        il.Append(Instruction.Create(OpCodes.Stloc, text));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldloc, text));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(missing);
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ret));
        method.Body.MaxStackSize = 2;
        return 1;
    }

    // Rebuild the 9.2 inventory update handler from its ARM64 trace. The server
    // wire contract supplies { item, quantity }; the client validates both,
    // normalizes the item name, and writes the absolute quantity to _data and
    // the caller's change dictionary. This is authored control flow, not copied
    // recovered method text.
    static int RepairInventoryManagerOnUpdate(ModuleDefinition module) {
        TypeDefinition type = module.GetType("EB.Sparx.InventoryManager");
        if (type == null) throw new InvalidDataException("missing EB.Sparx.InventoryManager");

        MethodDefinition method = type.Methods.SingleOrDefault(candidate =>
            candidate.Name == "OnUpdate" && !candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.Void && candidate.Parameters.Count == 2 &&
            candidate.Parameters[0].ParameterType.FullName == "System.Object" &&
            candidate.Parameters[1].ParameterType.FullName == "System.Collections.IDictionary&");
        FieldDefinition data = type.Fields.SingleOrDefault(field => field.Name == "_data" &&
            !field.IsStatic && field.FieldType.FullName == "System.Collections.IDictionary");
        MethodDefinition safeName = type.Methods.SingleOrDefault(candidate =>
            candidate.Name == "SafeName" && !candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.String && candidate.Parameters.Count == 1 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        TypeDefinition debug = module.GetType("EB.Debug");
        MethodDefinition logError = debug == null ? null : debug.Methods.SingleOrDefault(candidate =>
            candidate.Name == "LogError" && candidate.IsStatic &&
            candidate.ReturnType.MetadataType == MetadataType.Void && candidate.Parameters.Count == 2 &&
            candidate.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
            candidate.Parameters[1].ParameterType.FullName == "System.Object[]");
        if (method == null || data == null || safeName == null || logError == null)
            throw new InvalidDataException("missing 9.2 InventoryManager.OnUpdate metadata");

        TypeReference dictionaryType = module.ImportReference(typeof(System.Collections.IDictionary));
        MethodReference getItem = module.ImportReference(typeof(System.Collections.IDictionary)
            .GetProperty("Item").GetGetMethod());
        MethodReference setItem = module.ImportReference(typeof(System.Collections.IDictionary)
            .GetProperty("Item").GetSetMethod());
        MethodReference hashtableConstructor = module.ImportReference(typeof(System.Collections.Hashtable)
            .GetConstructor(Type.EmptyTypes));
        MethodReference isNullOrEmpty = module.ImportReference(typeof(string).GetMethod(
            "IsNullOrEmpty", new[] { typeof(string) }));
        TypeReference intType = module.TypeSystem.Int32;
        TypeReference longType = module.ImportReference(typeof(long));
        TypeReference doubleType = module.ImportReference(typeof(double));
        TypeReference floatType = module.ImportReference(typeof(float));
        VariableDefinition updateData = new VariableDefinition(dictionaryType);
        VariableDefinition rawItem = new VariableDefinition(module.TypeSystem.Object);
        VariableDefinition item = new VariableDefinition(module.TypeSystem.String);
        VariableDefinition rawQuantity = new VariableDefinition(module.TypeSystem.Object);
        VariableDefinition quantity = new VariableDefinition(intType);
        VariableDefinition key = new VariableDefinition(module.TypeSystem.String);
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = true;
        method.Body.Variables.Add(updateData);
        method.Body.Variables.Add(rawItem);
        method.Body.Variables.Add(item);
        method.Body.Variables.Add(rawQuantity);
        method.Body.Variables.Add(quantity);
        method.Body.Variables.Add(key);
        ILProcessor il = method.Body.GetILProcessor();
        Instruction hasChanges = Instruction.Create(OpCodes.Nop);
        Instruction itemMissing = Instruction.Create(OpCodes.Nop);
        Instruction itemRead = Instruction.Create(OpCodes.Nop);
        Instruction validItem = Instruction.Create(OpCodes.Nop);
        Instruction quantityMissing = Instruction.Create(OpCodes.Nop);
        Instruction quantityRead = Instruction.Create(OpCodes.Nop);
        Instruction quantityIsLong = Instruction.Create(OpCodes.Nop);
        Instruction quantityIsDouble = Instruction.Create(OpCodes.Nop);
        Instruction quantityIsFloat = Instruction.Create(OpCodes.Nop);
        Instruction validQuantity = Instruction.Create(OpCodes.Nop);

        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Brtrue, hasChanges));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Newobj, hashtableConstructor));
        il.Append(Instruction.Create(OpCodes.Stind_Ref));
        il.Append(hasChanges);

        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Isinst, dictionaryType));
        il.Append(Instruction.Create(OpCodes.Stloc, updateData));
        il.Append(Instruction.Create(OpCodes.Ldloc, updateData));
        il.Append(Instruction.Create(OpCodes.Brfalse, itemMissing));
        il.Append(Instruction.Create(OpCodes.Ldloc, updateData));
        il.Append(Instruction.Create(OpCodes.Ldstr, "item"));
        il.Append(Instruction.Create(OpCodes.Callvirt, getItem));
        il.Append(Instruction.Create(OpCodes.Stloc, rawItem));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawItem));
        il.Append(Instruction.Create(OpCodes.Isinst, module.TypeSystem.String));
        il.Append(Instruction.Create(OpCodes.Stloc, item));
        il.Append(Instruction.Create(OpCodes.Br, itemRead));
        il.Append(itemMissing);
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Stloc, item));
        il.Append(itemRead);

        il.Append(Instruction.Create(OpCodes.Ldc_I4_M1));
        il.Append(Instruction.Create(OpCodes.Stloc, quantity));
        il.Append(Instruction.Create(OpCodes.Ldloc, updateData));
        il.Append(Instruction.Create(OpCodes.Brfalse, quantityMissing));
        il.Append(Instruction.Create(OpCodes.Ldloc, updateData));
        il.Append(Instruction.Create(OpCodes.Ldstr, "quantity"));
        il.Append(Instruction.Create(OpCodes.Callvirt, getItem));
        il.Append(Instruction.Create(OpCodes.Stloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Isinst, intType));
        il.Append(Instruction.Create(OpCodes.Brfalse, quantityIsLong));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Unbox_Any, intType));
        il.Append(Instruction.Create(OpCodes.Stloc, quantity));
        il.Append(Instruction.Create(OpCodes.Br, quantityRead));
        il.Append(quantityIsLong);
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Isinst, longType));
        il.Append(Instruction.Create(OpCodes.Brfalse, quantityIsDouble));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Unbox_Any, longType));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Stloc, quantity));
        il.Append(Instruction.Create(OpCodes.Br, quantityRead));
        il.Append(quantityIsDouble);
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Isinst, doubleType));
        il.Append(Instruction.Create(OpCodes.Brfalse, quantityIsFloat));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Unbox_Any, doubleType));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Stloc, quantity));
        il.Append(Instruction.Create(OpCodes.Br, quantityRead));
        il.Append(quantityIsFloat);
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Isinst, floatType));
        il.Append(Instruction.Create(OpCodes.Brfalse, quantityMissing));
        il.Append(Instruction.Create(OpCodes.Ldloc, rawQuantity));
        il.Append(Instruction.Create(OpCodes.Unbox_Any, floatType));
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Stloc, quantity));
        il.Append(Instruction.Create(OpCodes.Br, quantityRead));
        il.Append(quantityMissing);
        il.Append(quantityRead);

        il.Append(Instruction.Create(OpCodes.Ldloc, item));
        il.Append(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        il.Append(Instruction.Create(OpCodes.Brfalse, validItem));
        il.Append(Instruction.Create(OpCodes.Ldstr, "Invalid item name ({0})"));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, item));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Call, logError));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(validItem);

        il.Append(Instruction.Create(OpCodes.Ldloc, quantity));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Bge, validQuantity));
        il.Append(Instruction.Create(OpCodes.Ldstr, "Invalid quantity ({0}) for item {1}"));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldloc, quantity));
        il.Append(Instruction.Create(OpCodes.Box, intType));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Dup));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldloc, item));
        il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        il.Append(Instruction.Create(OpCodes.Call, logError));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(validQuantity);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, data));
        il.Append(Instruction.Create(OpCodes.Ldloc, item));
        il.Append(Instruction.Create(OpCodes.Callvirt, safeName));
        il.Append(Instruction.Create(OpCodes.Stloc, key));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, data));
        il.Append(Instruction.Create(OpCodes.Ldloc, key));
        il.Append(Instruction.Create(OpCodes.Ldloc, quantity));
        il.Append(Instruction.Create(OpCodes.Box, intType));
        il.Append(Instruction.Create(OpCodes.Callvirt, setItem));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref));
        il.Append(Instruction.Create(OpCodes.Ldloc, key));
        il.Append(Instruction.Create(OpCodes.Ldloc, quantity));
        il.Append(Instruction.Create(OpCodes.Box, intType));
        il.Append(Instruction.Create(OpCodes.Callvirt, setItem));
        il.Append(Instruction.Create(OpCodes.Ret));
        method.Body.MaxStackSize = 4;
        return 1;
    }

    // Cpp2IL emitted placeholder Console.WriteLine stubs for this generic
    // collection. Reconstruct its conventional stack-backed pool behavior so
    // Unity's importer and runtime do not execute those placeholders.
    static int RepairRecoveredPool(ModuleDefinition module) {
        TypeDefinition type = module.GetType("EB.Collections.Pool`1");
        if (type == null) throw new InvalidDataException("missing recovered EB.Collections.Pool<T>");
        if (type.GenericParameters.Count != 1)
            throw new InvalidDataException("unexpected EB.Collections.Pool<T> arity");
        GenericParameter item = type.GenericParameters[0];
        FieldDefinition items = type.Fields.SingleOrDefault(field => field.Name == "_items");
        FieldDefinition constructor = type.Fields.SingleOrDefault(field => field.Name == "_constructor");
        FieldDefinition onRecycle = type.Fields.SingleOrDefault(field => field.Name == "_onRecycle");
        FieldDefinition initialSize = type.Fields.SingleOrDefault(field => field.Name == "<InitialSize>k__BackingField");
        MethodDefinition ctor = type.Methods.SingleOrDefault(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 3);
        MethodDefinition use = type.Methods.SingleOrDefault(method => method.Name == "Use" && method.Parameters.Count == 0);
        MethodDefinition recycle = type.Methods.SingleOrDefault(method => method.Name == "Recycle" && method.Parameters.Count == 1);
        MethodDefinition clear = type.Methods.SingleOrDefault(method => method.Name == "Clear" && method.Parameters.Count == 0);
        MethodDefinition count = type.Methods.SingleOrDefault(method => method.Name == "get_NumAvailable" && method.Parameters.Count == 0);
        if (items == null || constructor == null || onRecycle == null || initialSize == null || ctor == null || use == null ||
            recycle == null || clear == null || count == null)
            throw new InvalidDataException("unexpected recovered EB.Collections.Pool<T> members");

        GenericInstanceType stack = new GenericInstanceType(module.ImportReference(typeof(Stack<>)));
        stack.GenericArguments.Add(item);
        GenericInstanceType pool = new GenericInstanceType(type);
        pool.GenericArguments.Add(item);
        FieldReference itemsRef = module.ImportReference(new FieldReference(items.Name, items.FieldType, pool));
        FieldReference constructorRef = module.ImportReference(new FieldReference(constructor.Name, constructor.FieldType, pool));
        FieldReference onRecycleRef = module.ImportReference(new FieldReference(onRecycle.Name, onRecycle.FieldType, pool));
        FieldReference initialSizeRef = module.ImportReference(new FieldReference(initialSize.Name, initialSize.FieldType, pool));
        MethodReference objectCtor = new MethodReference(".ctor", module.TypeSystem.Void,
            module.TypeSystem.Object) { HasThis = true };
        MethodReference stackCtor = new MethodReference(".ctor", module.TypeSystem.Void, stack) { HasThis = true };
        MethodReference push = new MethodReference("Push", module.TypeSystem.Void, stack) { HasThis = true };
        push.Parameters.Add(new ParameterDefinition(item));
        MethodReference pop = new MethodReference("Pop", item, stack) { HasThis = true };
        MethodReference getCount = new MethodReference("get_Count", module.TypeSystem.Int32, stack) { HasThis = true };
        var function = (GenericInstanceType)constructor.FieldType;
        MethodReference create = new MethodReference("Invoke", item, function) { HasThis = true };
        var action = (GenericInstanceType)onRecycle.FieldType;
        MethodReference callback = new MethodReference("Invoke", module.TypeSystem.Void, action) { HasThis = true };
        callback.Parameters.Add(new ParameterDefinition(item));

        // Pool(int size, Function<T> constructor, Action<T> onRecycle)
        ctor.Body.ExceptionHandlers.Clear(); ctor.Body.Variables.Clear(); ctor.Body.Instructions.Clear();
        ctor.Body.InitLocals = true; ctor.Body.MaxStackSize = 3;
        var ctorIndex = new VariableDefinition(module.TypeSystem.Int32); ctor.Body.Variables.Add(ctorIndex);
        ILProcessor il = ctor.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, objectCtor));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Newobj, stackCtor)); il.Append(il.Create(OpCodes.Stfld, itemsRef));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldarg_2)); il.Append(il.Create(OpCodes.Stfld, constructorRef));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldarg_3)); il.Append(il.Create(OpCodes.Stfld, onRecycleRef));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldarg_1)); il.Append(il.Create(OpCodes.Stfld, initialSizeRef));
        il.Append(il.Create(OpCodes.Ldc_I4_0)); il.Append(il.Create(OpCodes.Stloc, ctorIndex));
        Instruction ctorLoop = il.Create(OpCodes.Ldloc, ctorIndex);
        Instruction ctorDone = il.Create(OpCodes.Ret);
        il.Append(ctorLoop); il.Append(il.Create(OpCodes.Ldarg_1)); il.Append(il.Create(OpCodes.Bge, ctorDone));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, itemsRef));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, constructorRef));
        var ctorValue = new VariableDefinition(item); ctor.Body.Variables.Add(ctorValue);
        Instruction defaultItem = il.Create(OpCodes.Pop);
        Instruction pushItem = il.Create(OpCodes.Ldloc, ctorValue);
        il.Append(il.Create(OpCodes.Dup)); il.Append(il.Create(OpCodes.Brfalse, defaultItem));
        il.Append(il.Create(OpCodes.Callvirt, create)); il.Append(il.Create(OpCodes.Stloc, ctorValue));
        il.Append(il.Create(OpCodes.Br, pushItem));
        il.Append(defaultItem); il.Append(il.Create(OpCodes.Ldloca, ctorValue));
        il.Append(il.Create(OpCodes.Initobj, item));
        il.Append(pushItem); il.Append(il.Create(OpCodes.Callvirt, push));
        il.Append(il.Create(OpCodes.Ldloc, ctorIndex)); il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add)); il.Append(il.Create(OpCodes.Stloc, ctorIndex)); il.Append(il.Create(OpCodes.Br, ctorLoop));
        il.Append(ctorDone);

        // Use(): pop an available item, otherwise call the factory or return default(T).
        use.Body.ExceptionHandlers.Clear(); use.Body.Variables.Clear(); use.Body.Instructions.Clear();
        use.Body.InitLocals = true; use.Body.MaxStackSize = 2;
        var useValue = new VariableDefinition(item); use.Body.Variables.Add(useValue);
        il = use.Body.GetILProcessor();
        Instruction useFactory = il.Create(OpCodes.Ldarg_0);
        Instruction invokeFactory = il.Create(OpCodes.Callvirt, create);
        Instruction useDefault = il.Create(OpCodes.Pop);
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, itemsRef)); il.Append(il.Create(OpCodes.Callvirt, getCount));
        il.Append(il.Create(OpCodes.Brfalse, useFactory));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, itemsRef)); il.Append(il.Create(OpCodes.Callvirt, pop)); il.Append(il.Create(OpCodes.Ret));
        il.Append(useFactory); il.Append(il.Create(OpCodes.Ldfld, constructorRef)); il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brtrue, invokeFactory)); il.Append(useDefault);
        il.Append(il.Create(OpCodes.Ldloca, useValue)); il.Append(il.Create(OpCodes.Initobj, item));
        il.Append(il.Create(OpCodes.Ldloc, useValue)); il.Append(il.Create(OpCodes.Ret));
        il.Append(invokeFactory); il.Append(il.Create(OpCodes.Ret));

        // Recycle() invokes the optional callback, then returns non-null items to the stack.
        recycle.Body.ExceptionHandlers.Clear(); recycle.Body.Variables.Clear(); recycle.Body.Instructions.Clear();
        recycle.Body.InitLocals = true; recycle.Body.MaxStackSize = 3;
        var recycler = new VariableDefinition(onRecycle.FieldType); recycle.Body.Variables.Add(recycler);
        il = recycle.Body.GetILProcessor();
        Instruction recycleDone = il.Create(OpCodes.Ret);
        Instruction skipCallback = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Ldarg_1)); il.Append(il.Create(OpCodes.Box, item)); il.Append(il.Create(OpCodes.Brfalse, recycleDone));
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, onRecycleRef)); il.Append(il.Create(OpCodes.Stloc, recycler));
        il.Append(il.Create(OpCodes.Ldloc, recycler)); il.Append(il.Create(OpCodes.Brfalse, skipCallback));
        il.Append(il.Create(OpCodes.Ldloc, recycler)); il.Append(il.Create(OpCodes.Ldarg_1)); il.Append(il.Create(OpCodes.Callvirt, callback));
        il.Append(skipCallback); il.Append(il.Create(OpCodes.Ldfld, itemsRef)); il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Callvirt, push)); il.Append(recycleDone);

        // Recycle() has already run the callback when items entered the pool; Clear() drops cached items.
        clear.Body.ExceptionHandlers.Clear(); clear.Body.Variables.Clear(); clear.Body.Instructions.Clear();
        clear.Body.InitLocals = false; clear.Body.MaxStackSize = 1;
        il = clear.Body.GetILProcessor();
        MethodReference clearStack = new MethodReference("Clear", module.TypeSystem.Void, stack) { HasThis = true };
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, itemsRef));
        il.Append(il.Create(OpCodes.Callvirt, clearStack)); il.Append(il.Create(OpCodes.Ret));

        // NumAvailable is the count in the initialized stack.
        count.Body.ExceptionHandlers.Clear(); count.Body.Variables.Clear(); count.Body.Instructions.Clear();
        count.Body.InitLocals = false; count.Body.MaxStackSize = 1;
        il = count.Body.GetILProcessor(); il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldfld, itemsRef));
        il.Append(il.Create(OpCodes.Callvirt, getCount)); il.Append(il.Create(OpCodes.Ret));
        Console.WriteLine("reconstructed EB.Collections.Pool<T> with a conventional Stack<T> approximation; original method bodies were unavailable");
        return 1;
    }

    static int Main(string[] args) {
        if (args.Length != 8) throw new ArgumentException("plugin directory, constructor plan, editor assemblies directory, core library, System assembly, Mono.Security assembly, IL2CPP failure plan, and authored method-body assembly required");
        string pluginDirectory = Path.GetFullPath(args[0]);
        string planPath = Path.GetFullPath(args[1]);
        string engineDirectory = Path.GetFullPath(args[2]);
        string authoredBodiesPath = Path.GetFullPath(args[7]);
        string targetStringLengthField;
        var targetFieldAliases = new Dictionary<string, string>();
        using (AssemblyDefinition coreLibrary = AssemblyDefinition.ReadAssembly(Path.GetFullPath(args[3]))) {
            TypeDefinition stringType = coreLibrary.MainModule.GetType("System.String");
            if (stringType == null) throw new InvalidDataException("target core library has no System.String type");
            if (stringType.Fields.Any(field => field.Name == "m_stringLength" &&
                field.FieldType.MetadataType == MetadataType.Int32)) targetStringLengthField = "m_stringLength";
            else if (stringType.Fields.Any(field => field.Name == "_stringLength" &&
                field.FieldType.MetadataType == MetadataType.Int32)) targetStringLengthField = "_stringLength";
            else throw new InvalidDataException("target core library has no known System.String length field");
            TypeDefinition dictionaryType = coreLibrary.MainModule.GetType(
                "System.Collections.Generic.Dictionary`2");
            if (dictionaryType != null) {
                foreach (string[] names in new[] {
                    new[] { "buckets", "_buckets" }, new[] { "entries", "_entries" },
                    new[] { "count", "_count" }, new[] { "version", "_version" },
                    new[] { "freeList", "_freeList" }, new[] { "freeCount", "_freeCount" },
                    new[] { "comparer", "_comparer" }, new[] { "keys", "_keys" },
                    new[] { "values", "_values" }, new[] { "syncRoot", "_syncRoot" }
                }) {
                    FieldDefinition replacement = dictionaryType.Fields.SingleOrDefault(field => field.Name == names[1]);
                    if (replacement != null && !dictionaryType.Fields.Any(field => field.Name == names[0]))
                        targetFieldAliases.Add("System.Collections.Generic.Dictionary`2|" + names[0], names[1]);
                }
            }
            TypeDefinition listType = coreLibrary.MainModule.GetType("System.Collections.Generic.List`1");
            if (listType != null && listType.Fields.Any(field => field.Name == "s_emptyArray" &&
                field.FieldType is ArrayType array && array.ElementType is GenericParameter) &&
                !listType.Fields.Any(field => field.Name == "_emptyArray"))
                targetFieldAliases.Add("System.Collections.Generic.List`1|_emptyArray", "s_emptyArray");
            TypeDefinition hashtableType = coreLibrary.MainModule.GetType("System.Collections.Hashtable");
            if (hashtableType != null) {
                foreach (string[] names in new[] {
                    new[] { "buckets", "_buckets" }, new[] { "count", "_count" },
                    new[] { "occupancy", "_occupancy" }, new[] { "loadsize", "_loadsize" },
                    new[] { "loadFactor", "_loadFactor" }, new[] { "version", "_version" },
                    new[] { "isWriterInProgress", "_isWriterInProgress" },
                    new[] { "keys", "_keys" }, new[] { "values", "_values" }
                }) {
                    FieldDefinition replacement = hashtableType.Fields.SingleOrDefault(field => field.Name == names[1]);
                    if (replacement != null &&
                        !hashtableType.Fields.Any(field => field.Name == names[0]))
                        targetFieldAliases.Add("System.Collections.Hashtable|" + names[0], names[1]);
                }
            }
            TypeDefinition exceptionArgsType = coreLibrary.MainModule.GetType(
                "System.UnhandledExceptionEventArgs");
            if (exceptionArgsType != null && exceptionArgsType.Fields.Any(field => field.Name == "_exception" &&
                field.FieldType.FullName == "System.Object") &&
                !exceptionArgsType.Fields.Any(field => field.Name == "_Exception"))
                targetFieldAliases.Add("System.UnhandledExceptionEventArgs|_Exception", "_exception");
        }
        using (AssemblyDefinition systemLibrary = AssemblyDefinition.ReadAssembly(Path.GetFullPath(args[4]))) {
            TypeDefinition captureType = systemLibrary.MainModule.GetType("System.Text.RegularExpressions.Capture");
            if (captureType != null) {
                foreach (string[] names in new[] {
                    new[] { "_text", "<Text>k__BackingField" },
                    new[] { "_index", "<Index>k__BackingField" },
                    new[] { "_length", "<Length>k__BackingField" }
                }) {
                    FieldDefinition replacement = captureType.Fields.SingleOrDefault(field => field.Name == names[1]);
                    if (replacement != null && !captureType.Fields.Any(field => field.Name == names[0]))
                        targetFieldAliases.Add("System.Text.RegularExpressions.Capture|" + names[0], names[1]);
                }
            }
            TypeDefinition ipEndPointType = systemLibrary.MainModule.GetType("System.Net.IPEndPoint");
            if (ipEndPointType != null) {
                foreach (string[] names in new[] {
                    new[] { "m_Address", "_address" }, new[] { "m_Port", "_port" }
                }) {
                    FieldDefinition replacement = ipEndPointType.Fields.SingleOrDefault(field => field.Name == names[1]);
                    if (replacement != null && !ipEndPointType.Fields.Any(field => field.Name == names[0]))
                        targetFieldAliases.Add("System.Net.IPEndPoint|" + names[0], names[1]);
                }
            }
        }
        Console.WriteLine("target System.String length field: " + targetStringLengthField);
        foreach (var alias in targetFieldAliases)
            Console.WriteLine("target corelib field alias: " + alias.Key + " -> " + alias.Value);
        var targets = File.ReadAllLines(planPath).Where(line => !String.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t')).ToDictionary(row => row[0] + "\t" + row[1], row => row);
        string[][] unsupportedMethods = File.ReadAllLines(Path.GetFullPath(args[6]))
            .Where(line => !String.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t')).ToArray();
        var authoredResolver = new DefaultAssemblyResolver();
        authoredResolver.AddSearchDirectory(pluginDirectory);
        authoredResolver.AddSearchDirectory(engineDirectory);
        using (AssemblyDefinition authoredBodies = AssemblyDefinition.ReadAssembly(authoredBodiesPath,
            new ReaderParameters { AssemblyResolver = authoredResolver })) {
        var found = new Dictionary<string, int>();
        foreach (string path in Directory.GetFiles(pluginDirectory, "*.dll")) {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(pluginDirectory);
            resolver.AddSearchDirectory(engineDirectory);
            bool changed = false;
            string temporary = path + ".repaired-tmp";
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = resolver })) {
                int valueTypeFlagRepairs = RepairAssemblyValueTypeFlags(assembly);
                if (valueTypeFlagRepairs > 0) {
                    changed = true;
                    Console.WriteLine("restored " + valueTypeFlagRepairs +
                        " value-type flags using the matching Unity 2020.3 managed assemblies in " + Path.GetFileName(path));
                }
                if (Path.GetFileName(path) == "Assembly-CSharp-firstpass.dll") {
                    TypeDefinition color = assembly.MainModule.GetType("EB.Math.Color");
                    if (color != null) {
                        int colorConstructorRepairs = RepairPackedColorConstructor(color,
                            Path.GetFileName(path));
                        if (colorConstructorRepairs > 0) {
                            changed = true;
                            Console.WriteLine("repaired " + colorConstructorRepairs +
                                " malformed EB.Math.Color vector constructor(s)");
                        }
                        int colorEqualityRepairs = RepairPackedColorEquality(color,
                            Path.GetFileName(path));
                        if (colorEqualityRepairs > 0) {
                            changed = true;
                            Console.WriteLine("repaired EB.Math.Color packed-value Equals, ==, and != operations");
                        }
                        int colorLerpRepairs = RepairPackedColorLerp(color,
                            Path.GetFileName(path));
                        if (colorLerpRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Math.Color.Lerp channel-wise from the recovered packed-field layout");
                        }
                        int colorConversionRepairs = RepairPackedColorConversions(color,
                            Path.GetFileName(path));
                        if (colorConversionRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Math.Color premultiplication, scalar multiplication, and vector conversions from 9.2 traces");
                        }
                    }
                    TypeDefinition boundingBox = assembly.MainModule.GetType("EB.Math.BoundingBox");
                    if (boundingBox != null) {
                        int containsRepairs = RepairBoundingBoxContains(boundingBox,
                            Path.GetFileName(path));
                        if (containsRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Math.BoundingBox point/box/sphere containment overloads from the 9.2 ARM64 traces");
                        }
                    }
                    TypeDefinition boundingSphere = assembly.MainModule.GetType("EB.Math.BoundingSphere");
                    if (boundingSphere != null) {
                        int sphereRepairs = RepairBoundingSphereGeometry(boundingSphere,
                            Path.GetFileName(path));
                        if (sphereRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Math.BoundingSphere point/sphere containment and sphere/plane intersection from 9.2 traces");
                        }
                    }
                    TypeDefinition cameraData = assembly.MainModule.GetType("EB.Director.CameraData");
                    if (cameraData != null) {
                        int cameraLerpRepairs = RepairCameraDataLerp(cameraData,
                            Path.GetFileName(path));
                        if (cameraLerpRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Director.CameraData.Lerp from the 9.2 ARM64 field layout and helper calls");
                        }
                    }
                    TypeDefinition cacheType = assembly.MainModule.GetType("EB.Cache");
                    if (cacheType != null) {
                        int cachePurgeRepairs = RepairCachePurge(cacheType, Path.GetFileName(path));
                        if (cachePurgeRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Cache.PurgeCache(TimeSpan) from its native 9.2 file-age sweep and failure handler");
                        }
                    }
                    TypeDefinition bitStream = assembly.MainModule.GetType("EB.BitStream");
                    if (bitStream != null) {
                        int byteArrayRepairs = RepairBitStreamByteArray(bitStream, Path.GetFileName(path));
                        if (byteArrayRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.BitStream.Serialize(ref byte[]) from native 9.2 length-prefix and ArraySegment traces");
                        }
                        int bufferRepairs = RepairBitStreamBuffer(bitStream, Path.GetFileName(path));
                        if (bufferRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.BitStream.Serialize(ref EB.Buffer) from native 9.2 length-prefix and ArraySegment traces");
                        }
                        int stringRepairs = RepairBitStreamString(bitStream, Path.GetFileName(path));
                        if (stringRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.BitStream.Serialize(ref string) from native 9.2 EB.Buffer string traces");
                        }
                    }
                    TypeDefinition beamRenderer = assembly.MainModule.GetType("EB.Rendering.BeamRenderer");
                    if (beamRenderer != null) {
                        int beamEvaluationRepairs = RepairBeamFloatEvaluation(beamRenderer, Path.GetFileName(path));
                        if (beamEvaluationRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Rendering.BeamRenderer.FloatEvlautation from its 9.2 ARM64 curve/list trace");
                        }
                        int beamUpdateRepairs = RepairBeamRendererUpdate(beamRenderer, Path.GetFileName(path));
                        if (beamUpdateRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Rendering.BeamRenderer.Update from its native 9.2 transform, startup-lerp, and lifetime trace");
                        }
                    }
                    TypeDefinition crash = assembly.MainModule.GetType("Crash");
                    if (crash != null) {
                        int crashAnimationRepairs = RepairCrashDoAnim(crash, Path.GetFileName(path));
                        if (crashAnimationRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed Crash.DoAnim from its native 9.2 transform and random-range trace");
                        }
                    }
                    TypeDefinition baseApi = assembly.MainModule.GetType("EB.Base.BaseAPI");
                    if (baseApi != null) {
                        int placeEntityRepairs = RepairBaseApiPlaceEntity(baseApi, Path.GetFileName(path));
                        if (placeEntityRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed EB.Base.BaseAPI.PlaceEntity route and seven arguments from its native 9.2 trace");
                        }
                    }
                    TypeDefinition copyMemberBinding = assembly.MainModule.GetType(
                        "EB.UI.DataBinding.CopyMemberBinding");
                    if (copyMemberBinding != null) {
                        int bindingTypeRepairs = RepairCopyMemberBindingEnsureTypeMatches(copyMemberBinding,
                            Path.GetFileName(path));
                        if (bindingTypeRepairs > 0) {
                            changed = true;
                            Console.WriteLine("reconstructed CopyMemberBinding.EnsureTypeMatches from native 9.2 assignability and default-value branches");
                        }
                    }
                    TypeDefinition label = assembly.MainModule.GetType("UILabel");
                    if (label != null && RepairUILabelConstructor(label,
                        Path.GetFileName(path)) > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed malformed UILabel constructor from verified 9.2 fields and 2.0.2 defaults");
                    }
                }
                foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types)) {
                    string assemblyName = Path.GetFileName(path);
                    int storyPanelConstructorRepairs = RepairStoryPanelConstructor(type, assemblyName);
                    if (storyPanelConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed " + storyPanelConstructorRepairs +
                            " story panel constructor(s) from retained 9.2 ARM64 field-write traces");
                    }
                    int chapterPanelConstructorRepairs = RepairChapterPanelConstructor(type, assemblyName);
                    if (chapterPanelConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed ChapterPanel color defaults from retained 9.2 ARM64 Color32 trace");
                    }
                    int questResultsConstructorRepairs = RepairQuestResultsScreenConstructor(type, assemblyName);
                    if (questResultsConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed QuestResultsScreenPresentation defaults from retained 9.2 ARM64 field writes");
                    }
                    int filterToggleConstructorRepairs = RepairFilterToggleConstructor(type, assemblyName);
                    if (filterToggleConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed FilterToggle defaults from retained 9.2 ARM64 field writes");
                    }
                    int redeemersDisplayConstructorRepairs = RepairRedeemersDisplayPanelConstructor(type, assemblyName);
                    if (redeemersDisplayConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed RedeemersDisplayPanel defaults from retained 9.2 ARM64 field writes");
                    }
                    int socialHubTrayButtonConstructorRepairs = RepairSocialHubTrayButtonConstructor(type, assemblyName);
                    if (socialHubTrayButtonConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed SocialHubTrayButton defaults from retained 9.2 ARM64 field writes");
                    }
                    int specialAttackIconConstructorRepairs = RepairSpecialAttackIconConstructor(type, assemblyName);
                    if (specialAttackIconConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed SpecialAttackIcon fade default from retained 9.2 ARM64 field write");
                    }
                    int matineeStageConstructorRepairs = RepairMatineeStageConstructor(type, assemblyName);
                    if (matineeStageConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed MatineeStage defaults and lists from retained 9.2 ARM64 field writes");
                    }
                    int lightShadowConstructorRepairs = RepairEBLightShadowConstructor(type, assemblyName);
                    if (lightShadowConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed EBLightShadow constructor from retained 9.2 ARM64 field and array traces");
                    }
                    int redeemerDisplayRepairs = RepairDefaultRedeemerDisplayConstructor(type, assemblyName);
                    if (redeemerDisplayRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed DefaultRedeemerDisplay constructor from native 9.2 field writes and overlay table");
                    }
                    int aveHistoryItemRepairs = RepairAveHistoryItemConstructor(type, assemblyName);
                    if (aveHistoryItemRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed AveHistoryItem constructor from retained 9.2 Color32 constants and field use");
                    }
                    int heroPortraitRepairs = RepairHeroPortraitConstructor(type, assemblyName);
                    if (heroPortraitRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed HeroPortrait constructor from native 9.2 field writes and defaults");
                    }
                    int dynamicScrollViewRepairs = RepairDynamicScrollViewConstructor(type, assemblyName);
                    if (dynamicScrollViewRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed DynamicScrollView constructor from native 9.2 field offsets and defaults");
                    }
                    int moveSequencerRepairs = RepairMoveSequencerInitializer(type, assemblyName);
                    if (moveSequencerRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed MoveSequencer static defaults from native 9.2 trace and preserved RVA tables");
                    }
                    int trailConfigRepairs = RepairTrailConfigConstructor(type, assemblyName);
                    if (trailConfigRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed TrailConfig constructor from native 9.2 field writes and constants");
                    }
                    int simulationChunkRepairs = RepairEBRBSimulationChunkConstructor(type, assemblyName);
                    if (simulationChunkRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed EBRBSimulationChunk parameterless defaults from recovered field types and constructor values");
                    }
                    int particleSimulationAllocationRepairs = RepairEBRBParticleSimulationReferenceAllocations(type,
                        assemblyName);
                    if (particleSimulationAllocationRepairs > 0) {
                        changed = true;
                        Console.WriteLine("restored typed Properties/RenderProperties allocations in EBRBParticleSimulation.Simulation constructors");
                    }
                    int vectorAnimationLoopRepairs = RepairVectorAnimationLoopReset(type, assemblyName);
                    if (vectorAnimationLoopRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed vector animation LoopReset from native 9.2 ARM64 field and branch traces: " + type.FullName);
                    }
                    int nguiConstructorRepairs = RepairNGUIConstructors(type, assemblyName);
                    if (nguiConstructorRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed NGUI constructor defaults from retained 9.2 fields and verified 2.0.2 behavior: " + type.FullName);
                    }
                    int nguiInitializerRepairs = RepairNGUIToolsInitializer(type, assemblyName);
                    if (nguiInitializerRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed NGUITools static defaults and key-code table from verified behavior");
                    }
                    int nguiValidationRepairs = RepairNGUIValidation(type, assemblyName);
                    if (nguiValidationRepairs > 0) {
                        changed = true;
                        Console.WriteLine("reconstructed NGUI editor validation callback from verified behavior: " + type.FullName);
                    }
                    int zipAesRepairs = RepairMalformedZipAesInitializer(assemblyName, type);
                    if (zipAesRepairs > 0) {
                        changed = true;
                        Console.WriteLine("replaced malformed recovered SharpZipLib AES initializer body with an unsupported-feature fallback");
                    }
                    if (assemblyName == "Assembly-CSharp.dll") {
                        int ownerlessRepairs = RepairKnownOwnerless9_2References(type);
                        if (ownerlessRepairs > 0) {
                            changed = true;
                            Console.WriteLine("repaired " + ownerlessRepairs +
                                " evidence-backed ownerless IL type reference(s) in " + type.FullName);
                        }
                    }
                    int malformedGenericRepairs = 0;
                    foreach (MethodDefinition method in type.Methods)
                        malformedGenericRepairs += RepairOwnerlessILTypeReferences(method);
                    if (malformedGenericRepairs > 0) {
                        changed = true;
                        Console.WriteLine("rebound " + malformedGenericRepairs +
                            " ownerless IL generic type token(s) in " + type.FullName);
                    }
                    int finalizerRepairs = RepairKnownEmpty9_2Finalizer(assemblyName, type);
                    if (finalizerRepairs > 0) {
                        changed = true;
                        Console.WriteLine("restored empty recovered 9.2 finalizer in " + type.FullName);
                    }
                    if (type.FullName == "Cpp2ILInjected.Cpp2ILHelpers" &&
                        !type.IsInterface && type.BaseType == null) {
                        // Cpp2IL's injected helper is a class, but its emitted
                        // TypeDefinition has no base reference. Unity 2020
                        // IL2CPP's WarmNamingComponent passes that null into
                        // TypeReferenceEqualityComparer and aborts the build.
                        type.BaseType = assembly.MainModule.TypeSystem.Object;
                        changed = true;
                        Console.WriteLine("restored System.Object base type for " + type.FullName);
                    }
                    int pInvokeChanges = RepairMissingPInvokeMetadata(assembly, type,
                        Path.GetFileName(path));
                    if (pInvokeChanges > 0) {
                        changed = true;
                        Console.WriteLine("restored " + pInvokeChanges +
                            " P/Invoke mapping(s) for " + Path.GetFileName(path) + ":" + type.FullName);
                    }
                    int compatibilityChanges = RepairEditorFieldAccess(type, Path.GetFileName(path),
                        targetStringLengthField, targetFieldAliases);
                    if (compatibilityChanges > 0) {
                        changed = true;
                        Console.WriteLine("repaired " + compatibilityChanges +
                            " Mono field access(es) in " + type.FullName);
                    }
                    foreach (var entry in targets) {
                        string[] row = entry.Value;
                        if (type.FullName != row[0]) continue;
                        MethodDefinition[] methods = type.Methods.Where(m => m.Name == row[1] &&
                            (row[1] == ".cctor" ? m.IsStatic :
                             row[1] == "GetInterpolator" ? m.IsStatic && m.Parameters.Count == 1 &&
                                m.Parameters[0].ParameterType.FullName == "EZAnimation/EASING_TYPE" :
                             row[0] == "Facebook.Unity.Settings.FacebookSettings/UrlSchemes" && row[1] == ".ctor" ?
                                !m.IsStatic && m.Parameters.Count == 1 &&
                                m.Parameters[0].ParameterType.FullName == "System.Collections.Generic.List`1<System.String>" :
                             row[1] == "add_OnLocalizationChanged" ? m.IsStatic && m.Parameters.Count == 1 :
                             row[1] == "SetupTuneables_Internal" ? !m.IsStatic && m.Parameters.Count == 3 :
                             row[0] == "EB.Hash" && row[1] == "FNV64" ? m.IsStatic && m.Parameters.Count == 2 &&
                                m.Parameters[0].ParameterType.FullName == "System.Byte[]" &&
                                m.Parameters[1].ParameterType.MetadataType == MetadataType.Int64 :
                             row[0] == "EB.Core.ThreadSafeRandom" && row[1] == "get_Randomizer" ? m.IsStatic && m.Parameters.Count == 0 :
                             row[1] == "Init" && row[0] == "AIRageSettings" ? !m.IsStatic && m.Parameters.Count == 1 :
                             row[1] == "Init" && row[0] == "EB.SafeValue" ? !m.IsStatic && m.Parameters.Count == 1 &&
                                m.Parameters[0].ParameterType.FullName == "System.Byte[]" :
                             !m.IsStatic && m.Parameters.Count == 0)).ToArray();
                        if (methods.Length != 1) throw new InvalidDataException("expected one target method: " + type.FullName + row[1]);
                        int expectedFields = Int32.Parse(row[2]);
                        if (expectedFields >= 0 && type.Fields.Count != expectedFields)
                            throw new InvalidDataException("unexpected field count in " + type.FullName);
                        Repair(methods[0], type);
                        found[entry.Key] = found.ContainsKey(entry.Key) ? found[entry.Key] + 1 : 1;
                        changed = true;
                    }
                }
                // Apply these last: the explicit Unity IL2CPP diagnostics are
                // authoritative, so generic compatibility repairs above must
                // not overwrite their narrowly scoped unsupported fallbacks.
                int rejectedIl2CppRepairs = RepairIl2CppRejectedMethods(assembly,
                    Path.GetFileName(path), unsupportedMethods);
                if (rejectedIl2CppRepairs > 0) changed = true;
                int authoredBodyRepairs = RepairAuthoredMethodBodies(assembly.MainModule,
                    authoredBodies.MainModule, Path.GetFileName(path));
                if (authoredBodyRepairs > 0) {
                    changed = true;
                    Console.WriteLine("transplanted authored 9.2 ARM64 recovery source: AlignUIElements.GetObjectBounds");
                }
                if (changed) assembly.Write(temporary);
            }
            if (changed) {
                string backup = path + ".repair-backup";
                if (File.Exists(backup)) {
                    if (!File.Exists(path)) File.Move(backup, path);
                    else File.Delete(backup);
                }
                File.Replace(temporary, path, backup);
                File.Delete(backup);
            }
        }
        foreach (var entry in targets) {
            if (!found.ContainsKey(entry.Key) || found[entry.Key] != 1)
                throw new InvalidDataException("target type not found exactly once: " + entry.Key);
            Console.WriteLine("sanitized " + entry.Key.Replace('\t', ' '));
        }
        ReplaceMatchingMonoSecurity(pluginDirectory, Path.GetFullPath(args[5]));
        }
        return 0;
    }
}
