using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// IL 结构检查器（**共享**测试辅助类）。
    ///
    /// 两个用途：
    ///  1. A2b 护栏（<c>HardwareWiringTests</c>）：扫描方法体里出现的调用 / 字符串 /
    ///     异常构造（<c>throw;</c> / <c>throw new ...</c>），用于断言"绝不重抛"这类结构性质；
    ///  2. Task 5 护栏（<c>FpsStateTests</c>）：断言**两条 IL 指令的先后顺序**
    ///     （<c>Interlocked.Exchange(ref _serviceStarted, 1)</c> 必须早于 <c>_isRunning = true</c>）。
    ///
    /// ★ 提为共享类的原因：IL 解析（尤其"两字节 opcode <c>rethrow</c>"、以及
    ///   <c>InlineMethod</c> / <c>InlineField</c> / <c>InlineString</c> 操作数解析）只有**一份实现**，
    ///   避免两处各写一套、日后各自演化导致护栏悄悄退化。
    ///
    /// ★ 可见性：<c>internal</c> —— 只在测试工程内部可见，**不是**产品公开 API。
    /// </summary>
    internal static class IlInspector
    {
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        static IlInspector()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)field.GetValue(null);
                ushort value = unchecked((ushort)op.Value);
                if (value < 0x100) OneByte[value] = op;
                else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
            }
        }

        /// <summary>
        /// 一条 IL 指令（带字节偏移）。
        /// </summary>
        internal sealed class IlInstruction
        {
            /// <summary>该指令在方法体 IL 字节流中的起始偏移（可用于比较先后顺序）。</summary>
            public int Offset { get; }

            /// <summary>助记符。</summary>
            public OpCode OpCode { get; }

            /// <summary>
            /// 方法类操作数解析出的目标（<c>call</c> / <c>callvirt</c> / <c>newobj</c> / <c>ldftn</c> …）；
            /// 解析失败时为 <c>null</c>。
            /// </summary>
            public MethodBase Target { get; }

            /// <summary>
            /// 字段类操作数解析出的字段（<c>ldfld</c> / <c>ldflda</c> / <c>stfld</c> …）；
            /// 解析失败时为 <c>null</c>。
            /// </summary>
            public FieldInfo Field { get; }

            /// <summary>字符串字面量（<c>ldstr</c>）；非字符串指令为 <c>null</c>。</summary>
            public string StringOperand { get; }

            public IlInstruction(int offset, OpCode opCode, MethodBase target, FieldInfo field, string stringOperand)
            {
                Offset = offset;
                OpCode = opCode;
                Target = target;
                Field = field;
                StringOperand = stringOperand;
            }
        }

        /// <summary>某个方法体的反汇编结果。</summary>
        internal sealed class Disassembly
        {
            /// <summary>按出现顺序排列的指令。</summary>
            public List<IlInstruction> Instructions { get; } = new List<IlInstruction>();

            /// <summary>解析失败的方法类操作数数量（正常应为 0；不为 0 说明扫描器对该模块不可用）。</summary>
            public int UnresolvedCalls { get; set; }

            /// <summary>解析失败的字段类操作数数量（正常应为 0）。</summary>
            public int UnresolvedFields { get; set; }
        }

        /// <summary>某个方法体的扫描结果。</summary>
        internal sealed class ScanResult
        {
            /// <summary>方法体里被调用的方法（按出现顺序）。</summary>
            public List<MethodBase> Calls { get; } = new List<MethodBase>();

            /// <summary>方法体里用到的字符串字面量（ldstr）。</summary>
            public List<string> Strings { get; } = new List<string>();

            /// <summary>解析失败的调用指令数量（正常应为 0；不为 0 说明扫描器对该模块不可用）。</summary>
            public int UnresolvedCalls { get; set; }

            /// <summary>
            /// 方法体里 <c>rethrow</c> 指令的数量（CIL 两字节 opcode <c>0xFE 0x1A</c>）。
            /// C# 的 <c>throw;</c> 编译成它 —— 即"把刚捕获的异常原样再抛出去"。
            /// </summary>
            public int RethrowCount { get; set; }

            /// <summary>
            /// 方法体里 <c>throw</c> 指令的数量（CIL 单字节 opcode <c>0x26</c>）。
            /// C# 的 <c>throw &lt;表达式&gt;;</c> 编译成它。
            /// </summary>
            public int ThrowCount { get; set; }

            /// <summary>
            /// 方法体里"造一个异常出来"的调用（<c>newobj</c> 到 <see cref="Exception"/> 的派生类型）。
            /// C# 的 <c>throw new InvalidOperationException("x", ex);</c> 编译成
            /// <c>newobj</c> + <c>throw</c> 两步，这里把 <c>newobj</c> 这一半单独记下来，
            /// 这样即便异常对象是被间接抛出的，护栏也能变红。
            /// </summary>
            public List<MethodBase> ExceptionConstructions { get; } = new List<MethodBase>();
        }

        /// <summary>
        /// 反汇编一个方法体，返回按出现顺序排列的指令列表（含每条指令的字节偏移）。
        ///
        /// ★ 与 <see cref="Scan"/> 共用同一套操作数解析逻辑 —— 避免两处实现漂移。
        /// </summary>
        internal static Disassembly Disassemble(MethodBase method)
        {
            var result = new Disassembly();
            if (method == null) return result;

            var body = method.GetMethodBody();
            if (body == null) return result;
            byte[] il = body.GetILAsByteArray();
            if (il == null) return result;

            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments()
                : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int i = 0;
            while (i < il.Length)
            {
                int offset = i;
                OpCode op;
                byte first = il[i];
                if (first == 0xFE)
                {
                    if (i + 1 >= il.Length) break;
                    op = TwoByte[il[i + 1]];
                    i += 2;
                }
                else
                {
                    op = OneByte[first];
                    i += 1;
                }

                MethodBase target = null;
                FieldInfo field = null;
                string text = null;

                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            try { target = module.ResolveMethod(token, typeArgs, methodArgs); }
                            catch (Exception) { /* 解析不了就记为未解析（下面的断言会暴露它） */ }
                            if (target == null) result.UnresolvedCalls++;
                            break;
                        }
                    case OperandType.InlineField:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            try { field = module.ResolveField(token, typeArgs, methodArgs); }
                            catch (Exception) { /* 同上：解析不了就留 null */ }
                            if (field == null) result.UnresolvedFields++;
                            break;
                        }
                    case OperandType.InlineString:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            try { text = module.ResolveString(token); }
                            catch (Exception) { }
                            break;
                        }
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR:
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineSig:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        i += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8;
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineVar:
                        i += 1;
                        break;
                    case OperandType.InlineVar:
                        i += 2;
                        break;
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(il, i);
                            i += 4 + 4 * count;
                            break;
                        }
                    default:
                        break;   // InlineNone / InlinePhi（已废弃）
                }

                result.Instructions.Add(new IlInstruction(offset, op, target, field, text));
            }

            return result;
        }

        /// <summary>扫描一个方法体。</summary>
        internal static ScanResult Scan(MethodBase method)
        {
            var result = new ScanResult();
            var disassembly = Disassemble(method);
            result.UnresolvedCalls = disassembly.UnresolvedCalls;

            foreach (var instruction in disassembly.Instructions)
            {
                // ★ N1：识别"把异常抛出去"的指令形态。
                //   rethrow = C# `throw;`（两字节 opcode 0xFE 0x1A）
                //   throw   = C# `throw <表达式>;`（单字节 opcode 0x26）
                //   两者都是 InlineNone（无操作数），这里单独计数。
                if (instruction.OpCode == OpCodes.Rethrow) { result.RethrowCount++; continue; }
                if (instruction.OpCode == OpCodes.Throw) { result.ThrowCount++; continue; }

                if (instruction.Target != null)
                {
                    result.Calls.Add(instruction.Target);
                    // ★ N1：`newobj` 到 Exception 派生类型的构造函数 = "造一个异常出来"。
                    //   注意 newobj 的操作数也是 InlineMethod，所以**必须**靠 opcode 区分，
                    //   光看操作数类型会把普通的构造函数调用也当成异常构造。
                    if (instruction.OpCode == OpCodes.Newobj &&
                        instruction.Target.DeclaringType != null &&
                        typeof(Exception).IsAssignableFrom(instruction.Target.DeclaringType) &&
                        (instruction.Target is ConstructorInfo || instruction.Target.Name == ".ctor"))
                    {
                        result.ExceptionConstructions.Add(instruction.Target);
                    }
                }

                if (instruction.StringOperand != null) result.Strings.Add(instruction.StringOperand);
            }

            return result;
        }
    }
}
