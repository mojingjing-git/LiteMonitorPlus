using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **复核者自写的 IL 解码器** —— 独立于实现者写的 <c>tests/LiteMonitor.Tests/IlInspector.cs</c>。
/// <para>用途：让复核用例能从**字节码**层面独立判定"赋值先于使用""同一条入口""共用同一个判据"
/// 这类结构性质，而不是依赖实现者自己挑的判据。</para>
/// </summary>
internal sealed class VerifierIlInstruction
{
    internal int Offset { get; init; }
    internal OpCode OpCode { get; init; }
    internal object? Operand { get; init; }

    internal bool IsCallOrCallVirt =>
        OpCode == OpCodes.Call || OpCode == OpCodes.Callvirt;

    internal bool IsNewObj => OpCode == OpCodes.Newobj;

    internal bool IsFieldRead =>
        OpCode == OpCodes.Ldfld || OpCode == OpCodes.Ldflda;

    internal bool IsFieldWrite => OpCode == OpCodes.Stfld;

    internal MethodBase? Method => Operand as MethodBase;
    internal FieldInfo? Field => Operand as FieldInfo;
    internal ConstructorInfo? Constructor => Operand as ConstructorInfo;
}

/// <summary>复核者自写的 IL 读取器（见 <see cref="VerifierIlInstruction"/>）。</summary>
internal static class VerifierIl
{
    private static readonly Dictionary<ushort, OpCode> OpcodeTable = BuildOpcodeTable();

    private static Dictionary<ushort, OpCode> BuildOpcodeTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode)) continue;
            var opCode = (OpCode)field.GetValue(null)!;
            table[(ushort)opCode.Value] = opCode;
        }

        return table;
    }

    /// <summary>解码一个方法的 IL。</summary>
    internal static List<VerifierIlInstruction> Decode(MethodBase method)
    {
        var result = new List<VerifierIlInstruction>();
        MethodBody? body = method.GetMethodBody();
        byte[] il = body?.GetILAsByteArray() ?? Array.Empty<byte>();
        if (il.Length == 0) return result;

        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } dt
            ? dt.GetGenericArguments()
            : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        Module module = method.Module;

        int pos = 0;
        while (pos < il.Length)
        {
            int offset = pos;
            ushort key = il[pos++];
            if (key == 0xFE)
            {
                if (pos >= il.Length) break;
                key = (ushort)(0xFE00 | il[pos++]);
            }

            if (!OpcodeTable.TryGetValue(key, out OpCode opCode)) break;

            object? operand = null;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    operand = (int)il[pos++];
                    break;
                case OperandType.InlineVar:
                    operand = (int)BitConverter.ToUInt16(il, pos);
                    pos += 2;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    {
                        int token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        operand = ResolveToken(module, token, opCode.OperandType, typeArgs, methodArgs);
                        break;
                    }
                case OperandType.InlineI8:
                    operand = BitConverter.ToInt64(il, pos);
                    pos += 8;
                    break;
                case OperandType.InlineR:
                    operand = BitConverter.ToDouble(il, pos);
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    {
                        int count = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        var targets = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            targets[i] = BitConverter.ToInt32(il, pos);
                            pos += 4;
                        }
                        operand = targets;
                        break;
                    }
                default:
                    break;
            }

            result.Add(new VerifierIlInstruction
            {
                Offset = offset,
                OpCode = opCode,
                Operand = operand,
            });
        }

        return result;
    }

    private static object? ResolveToken(
        Module module, int token, OperandType kind, Type[]? typeArgs, Type[]? methodArgs)
    {
        try
        {
            switch (kind)
            {
                case OperandType.InlineMethod:
                    try { return module.ResolveMethod(token, typeArgs, methodArgs); }
                    catch { return module.ResolveMember(token, typeArgs, methodArgs); }
                case OperandType.InlineField:
                    return module.ResolveField(token, typeArgs, methodArgs);
                case OperandType.InlineType:
                    return module.ResolveType(token, typeArgs, methodArgs);
                case OperandType.InlineString:
                    return module.ResolveString(token);
                case OperandType.InlineTok:
                    try { return module.ResolveMember(token, typeArgs, methodArgs); }
                    catch { return null; }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// async 方法的**真实方法体**在编译器生成的状态机里（<c>&lt;Name&gt;d__NN.MoveNext</c>）。
    /// <para>本方法把 async 方法映射到它的 <c>MoveNext</c>；否则对 async 方法做 IL 判定
    /// 只会看到一层空壳（复核者第一版就踩了这个坑）。</para>
    /// </summary>
    internal static MethodBase EffectiveBody(MethodBase method)
    {
        var attr = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        if (attr == null) return method;

        MethodBase? moveNext = attr.StateMachineType.GetMethod(
            "MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return moveNext ?? method;
    }

    /// <summary>
    /// 从一个方法出发，沿 <c>call</c> / <c>callvirt</c> / <c>newobj</c> 做广度优先遍历，
    /// 收集**与根方法同一个程序集内**可达的方法（不含 BCL）。
    /// </summary>
    internal static HashSet<MethodBase> Reachable(
        MethodBase root, int maxDepth, int maxMethods = 6000)
    {
        // ★ 作用域 = **根方法所在的程序集**（生产代码那个），不是本测试程序集。
        Assembly? scope = root.DeclaringType?.Assembly;

        var visited = new HashSet<MethodBase>();
        var queue = new Queue<(MethodBase Method, int Depth)>();
        MethodBase start = EffectiveBody(root);
        visited.Add(start);
        queue.Enqueue((start, 0));

        while (queue.Count > 0 && visited.Count < maxMethods)
        {
            (MethodBase current, int depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (VerifierIlInstruction instruction in Decode(current))
            {
                if (!instruction.IsCallOrCallVirt && !instruction.IsNewObj) continue;

                MethodBase? target = instruction.Method;
                if (target == null) continue;

                Type? declaring = target.DeclaringType;
                if (declaring == null) continue;

                Assembly? assembly = declaring.Assembly;
                if (assembly == null) continue;
                if (scope != null && assembly != scope) continue;

                MethodBase effective = EffectiveBody(target);
                if (!visited.Add(effective)) continue;
                queue.Enqueue((effective, depth + 1));
            }
        }

        return visited;
    }

    /// <summary>该方法体里是否读取了指定字段（<c>ldfld</c> / <c>ldflda</c>）。</summary>
    internal static bool ReadsField(MethodBase method, FieldInfo field)
    {
        foreach (VerifierIlInstruction instruction in Decode(EffectiveBody(method)))
        {
            if (!instruction.IsFieldRead) continue;
            FieldInfo? read = instruction.Field;
            if (read == null) continue;
            if (read == field || (read.Name == field.Name && read.DeclaringType == field.DeclaringType))
            {
                return true;
            }
        }

        return false;
    }
}