#!/usr/bin/env python3
"""Repair diagnosed recovered IL and import metadata for Unity Android builds."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path


REPAIR_SOURCE = r"""
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

    static int Main(string[] args) {
        if (args.Length != 6) throw new ArgumentException("plugin directory, plan, editor assemblies directory, core library, System assembly, and Mono.Security assembly required");
        string pluginDirectory = Path.GetFullPath(args[0]);
        string planPath = Path.GetFullPath(args[1]);
        string engineDirectory = Path.GetFullPath(args[2]);
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
        var found = new Dictionary<string, int>();
        foreach (string path in Directory.GetFiles(pluginDirectory, "*.dll")) {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(pluginDirectory);
            resolver.AddSearchDirectory(engineDirectory);
            bool changed = false;
            string temporary = path + ".repaired-tmp";
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = resolver })) {
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
                    }
                }
                foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types)) {
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
        return 0;
    }
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="staged 9.2 Unity project")
    parser.add_argument("--unity", type=Path, required=True, help="Unity Editor executable")
    parser.add_argument("--diagnostics", type=Path, help="Unity log whose malformed constructor errors should be repaired")
    args = parser.parse_args()

    project = args.project.expanduser().resolve()
    unity = args.unity.expanduser().resolve()
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        parser.error(f"not a Unity project: {project}")
    plugin_dir = project / "Assets/Plugins"
    if not (plugin_dir / "Assembly-CSharp.dll").is_file():
        parser.error(f"recovered game assembly is missing: {plugin_dir / 'Assembly-CSharp.dll'}")

    editor_root = unity.parent
    mono_root = editor_root / "Data/MonoBleedingEdge"
    mcs = mono_root / "bin/mcs"
    mono = mono_root / "bin/mono"
    core_library = mono_root / "lib/mono/4.5/mscorlib.dll"
    system_library = mono_root / "lib/mono/4.5/System.dll"
    mono_security = mono_root / "lib/mono/4.5/Mono.Security.dll"
    cecil_gac = mono_root / "lib/mono/gac/Mono.Cecil"
    cecil_versions = list(cecil_gac.glob("*/Mono.Cecil.dll"))
    def version_key(path: Path) -> tuple[int, ...]:
        match = re.match(r"([0-9]+(?:\.[0-9]+)+)_", path.parent.name)
        return tuple(int(part) for part in match.group(1).split(".")) if match else ()
    cecil_versions.sort(key=version_key, reverse=True)
    cecil = cecil_versions[0] if cecil_versions else cecil_gac / "0.10.0.0__0738eb9f132ed756/Mono.Cecil.dll"
    if not all(path.is_file() for path in (mcs, mono, cecil, core_library, system_library, mono_security)):
        parser.error(f"Unity's bundled Mono compiler or Mono.Cecil is missing under {mono_root}")

    targets: dict[tuple[str, str], int] = {
        ("Quests.Presentation.QuestNodeTuning", ".ctor"): 43,
        ("Quests.Presentation.GameboardBuilder", ".ctor"): -1,
        ("Quests.Presentation.GameboardBuilder", ".cctor"): -1,
        ("AVEBattlegroupSelectionPanel", ".ctor"): -1,
        ("EB.Rendering.EBParticlePal", ".ctor"): -1,
        ("EB.Rendering.EBParticlePal", ".cctor"): -1,
        ("EB.Rendering.EBParticlePal/Condition", ".ctor"): -1,
        ("UILabel", ".cctor"): -1,
        ("EB.UI.PrefabDiff.PrefabDiffTracker", ".cctor"): -1,
        ("EB.Localizer", ".cctor"): -1,
        ("EB.Localizer", "add_OnLocalizationChanged"): -1,
        ("EB.UI.Social.FuseSocialHub", ".cctor"): -1,
        ("EB.UI.SystemMessage.FuseSystemMessageOverlay", ".cctor"): -1,
        ("EB.UI.Social.FuseSocialHub/FuseSocialHubPresentationConfig", ".ctor"): -1,
        ("EB.UI.SystemMessage.FuseSystemMessageOverlay/SystemMessagePresentationConfig", ".ctor"): -1,
        ("EBWorldPainterData", ".cctor"): -1,
        ("AudioPal", ".cctor"): -1,
        ("BuffsController", ".cctor"): -1,
        ("EB.MoveEditor.PrefabLib", ".cctor"): -1,
        ("CriticalError", ".cctor"): -1,
        ("CriticalError/ConfigData", ".ctor"): -1,
        ("CriticalError/ConfigData/<>c", ".cctor"): -1,
        ("EZAnimation", "GetInterpolator"): -1,
        ("EZAnimation", ".cctor"): -1,
        ("AITuneables", "OnAfterDeserialize"): -1,
        ("AITuneablesSet", "SetupTuneables_Internal"): -1,
        ("AIRageSettings", "Init"): -1,
        ("Fabric.SerializableDictionary`2", "OnBeforeSerialize"): -1,
        ("WindowStateHelper", ".ctor"): -1,
        ("BadgeManager", ".ctor"): -1,
        ("BuildingPortrait", ".ctor"): -1,
        ("AllianceStatsPopup", ".ctor"): -1,
        ("GachaRevealPresentation", ".ctor"): -1,
        ("Facebook.Unity.Settings.FacebookSettings/UrlSchemes", ".ctor"): -1,
        ("EB.Hash", ".cctor"): -1,
        ("EB.Hash", "FNV64"): -1,
        ("EB.SafeValue", "Init"): -1,
        ("EB.Core.ThreadSafeRandom", "get_Randomizer"): -1,
    }
    if args.diagnostics:
        log = args.diagnostics.expanduser().resolve().read_text(errors="replace")
        for type_name, method, parameters in re.findall(
            r"InvalidProgramException: Invalid IL code in (.+?):([A-Za-z0-9_.$/+`<>]+)\s*\(([^)]*)\):", log
        ):
            if method == "OnValidate" and not parameters.strip():
                # Unity invokes OnValidate during editor import. Replacing only
                # diagnosed malformed validation callbacks keeps gameplay
                # methods intact while allowing serialized assets to load.
                targets.setdefault((type_name, method), -1)
            elif method in (".ctor", ".cctor") and not parameters.strip():
                # Preserve parameterized decoding constructors and avoid
                # replacing type initializers that merely threw at runtime.
                targets.setdefault((type_name, method), -1)

    with tempfile.TemporaryDirectory(prefix="rea-9.2-constructor-repair-") as temporary:
        temporary_path = Path(temporary)
        source = temporary_path / "RepairRecoveredConstructor.cs"
        executable = temporary_path / "RepairRecoveredConstructor.exe"
        plan = temporary_path / "repair-plan.tsv"
        source.write_text(REPAIR_SOURCE)
        plan.write_text("".join(f"{type_name}\t{method}\t{fields}\n" for (type_name, method), fields in sorted(targets.items())))
        subprocess.run(
            [str(mcs), f"-r:{cecil}", f"-out:{executable}", str(source)],
            check=True,
        )
        env = os.environ.copy()
        env["MONO_PATH"] = str(cecil.parent)
        subprocess.run(
            [str(mono), str(executable), str(plugin_dir), str(plan),
             str(editor_root / "Data/Managed/UnityEngine"), str(core_library), str(system_library),
             str(mono_security)],
            check=True,
            env=env,
        )

    report = {
        "repairs": [f"{type_name}::{method}()" for (type_name, method) in sorted(targets)],
        "structural_repairs": [
            "Set Cpp2ILInjected.Cpp2ILHelpers.BaseType to System.Object in every recovered plugin where the helper class has no base reference",
            "Replace Mono.Security.dll with Unity's 4.5 profile assembly only when its full strong-name identity matches and it contains PKCS12.GetExistingParameters(Boolean&)",
            "Rebuild EB.Math.Color vector constructors from native 9.2 channel clamp, byte conversion, and packing behavior"
        ],
        "reason": "Cpp2IL emitted malformed IL. Recovered import repairs rebuild GameboardBuilder's collection fields and three scalar constants, restore BattlegroupColours, reconstruct EBParticlePal's enum counts and observed instance defaults, initialize each Condition tuning entry, initialize UILabel's shared collections and localization subscription, initialize PrefabDiffTracker's modifier map and timeslice default, initialize Localizer's maps, format provider, flags, and a harmless placeholder regex until its lost literal is recovered, preserve QuestNodeTuning's serialized fields, correct a derived-field visibility mismatch, reconstruct EB.Hash static constants and FNV64, EB.SafeValue.Init, EB.Math.Color vector constructors, and ThreadSafeRandom.get_Randomizer from native 9.2 behavior, and clear only diagnosed parameterless constructors or editor OnValidate callbacks. PrefabDiffTracker's two custom modifier delegates and RAID_START_POS remain unrecovered.",
        "notice": "Local generated build input only; untouched Cpp2IL output remains under cpp2il/.",
    }
    (project / "recovered-import-repairs.json").write_text(json.dumps(report, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
