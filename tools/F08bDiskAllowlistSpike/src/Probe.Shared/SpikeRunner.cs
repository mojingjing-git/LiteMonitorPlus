using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace F08b.Spike
{
    /// <summary>
    /// P-F08b Spike —— 判定 LHM / DiskInfoToolkit 是否提供"**在构造设备对象与打开句柄之前**过滤磁盘"的入口。
    ///
    /// ★ 本文件被 Probe.Baseline（LHM 0.9.6 / DIT 1.1.2）与 Probe.Latest（LHM 0.9.7-pre771 / DIT 2.2.2）
    ///   两个变体工程共用（<c>Link</c> 方式引入），逻辑完全一致 ⇒ 差异只来自依赖版本。
    ///
    /// ★ 判定不是"看版本号"，而是三件可核验的事：
    ///   1. LHM 存储组的构造序列里，枚举入口（拿到磁盘清单的那一步）**有没有可注入的过滤参数/回调**；
    ///   2. 设备对象（StorageDevice / DiskInfoToolkit 的 device）是不是**枚举时即被逐个构造**（无一丝 DOE 机会）；
    ///   3. 句柄打开（CreateFile 之类的原生入口）到底发生在**构造期**还是**首次取值期**。
    /// </summary>
    internal static class SpikeRunner
    {
        internal static int Run(string scenario, string[] args)
        {
            switch (scenario)
            {
                case "facts":
                    DumpFacts();
                    return 0;
                case "il":
                    DumpIl(args.Length > 1 ? args[1] : null);
                    return 0;
                case "handles":
                    DumpHandleSites();
                    return 0;
                case "type":
                    DumpType(args.Length > 1 ? args[1] : null);
                    return 0;
                case "searchtype":
                    SearchType(args.Length > 1 ? args[1] : null);
                    return 0;
                case "gate":
                    DumpGroupGates();
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown scenario: {scenario}");
                    return 2;
            }
        }

        // =====================================================================
        // 事实堆：一眼看清"能不能在构造前过滤"
        // =====================================================================
        private static void DumpFacts()
        {
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Console.WriteLine("== 程序集 ==");
            Console.WriteLine($"LHM file          = {lhm.Location}");
            Console.WriteLine($"LHM informational = {lhm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");

            Assembly dit = FindDiskInfoToolkit(lhm);
            Console.WriteLine($"DIT assembly      = {dit?.FullName ?? "(not loaded)"}");
            Console.WriteLine($"DIT informational = {dit?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            Console.WriteLine();

            ReportLibrarySurface(lhm);
            Console.WriteLine();
            ReportDiskToolkitSurface(dit);
        }

        private static Assembly FindDiskInfoToolkit(Assembly lhm)
        {
            // DiskInfoToolkit 是被引用而非已加载 ⇒ 先在已加载集合里找，找不到就按需加载。
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "DiskInfoToolkit");
            if (loaded != null) return loaded;

            try
            {
                foreach (AssemblyName reference in lhm.GetReferencedAssemblies())
                {
                    if (reference.Name == "DiskInfoToolkit")
                        return Assembly.Load(reference);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(load DiskInfoToolkit failed: {ex.Message})");
            }
            return null;
        }

        /// <summary>LHM 侧：存储组的构造序列与可用接缝。</summary>
        private static void ReportLibrarySurface(Assembly lhm)
        {
            Console.WriteLine("== LHM 存储设备组：构造序列与可用接缝 ==");

            Type storageGroup = lhm.GetType("LibreHardwareMonitor.Hardware.Storage.StorageGroup");
            if (storageGroup == null) { Console.WriteLine("!! StorageGroup 不存在"); return; }

            foreach (ConstructorInfo ctor in storageGroup.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Console.WriteLine($"ctor StorageGroup({string.Join(", ", ctor.GetParameters().Select(DescribeParameter))})");
                Console.WriteLine("  调用序列：");
                foreach (MethodBase call in CallSequence(ctor).Take(40))
                {
                    Console.WriteLine($"    {call.DeclaringType?.Name}.{call.Name}");
                }
            }

            Type storageDevice = lhm.GetType("LibreHardwareMonitor.Hardware.Storage.StorageDevice");
            Console.WriteLine();
            Console.WriteLine($"StorageDevice ctor:");
            if (storageDevice != null)
            {
                foreach (ConstructorInfo ctor in storageDevice.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Console.WriteLine($"  ctor({string.Join(", ", ctor.GetParameters().Select(DescribeParameter))})");
                    Console.WriteLine($"    调用序列：{SequenceOf(CallSequence(ctor).Take(24))}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("== LHM Computer：是否存在'构造前'钩子（可据此在打开前排除设备）==");
            Type computer = typeof(LibreHardwareMonitor.Hardware.Computer);
            foreach (EventInfo evt in computer.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Console.WriteLine($"  event {evt.Name} : {evt.EventHandlerType?.Name}");
            foreach (MethodInfo m in computer.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         .Where(m => m.IsVirtual && !m.IsFinal && m.Name != "ToString"))
                Console.WriteLine($"  virtual {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
            Console.WriteLine("  (以上为空 ⇒ 没有任何'构造前回调/可覆写过滤点')");

            Console.WriteLine();
            Console.WriteLine("== IGroup：组抽象是否允许外部注入设备清单 ==");
            Type iGroup = lhm.GetType("LibreHardwareMonitor.Hardware.IGroup");
            if (iGroup != null)
            {
                foreach (MemberInfo member in iGroup.GetMembers())
                    Console.WriteLine($"  {member.MemberType} {member.Name}");
            }
        }

        /// <summary>
        /// 各硬件组的"开关闸门"取证：确认每个组的构造是否都被对应的 <c>Is*Enabled</c> 开关**前置**守护。
        /// 与磁盘同理 —— 只有"开关 false ⇒ 根本不 new 组"才算**构造前隔离**。
        /// </summary>
        private static void DumpGroupGates()
        {
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Type computer = typeof(LibreHardwareMonitor.Hardware.Computer);
            MethodInfo addGroups = computer.GetMethod("AddGroups", BindingFlags.NonPublic | BindingFlags.Instance);

            Console.WriteLine("== Computer 的 Is*Enabled 开关 → 组构造 的对应关系 ==");
            Console.WriteLine("   （判定：读取开关字段 → 条件分支 → 组 newobj；三者顺序正确才算构造前隔离）");
            Console.WriteLine();

            List<IlEntry> il = Read(addGroups);
            FieldInfo pendingField = null;
            int guardIndex = -1;

            for (int i = 0; i < il.Count; i++)
            {
                IlEntry ins = il[i];

                // 记录最近一次读取的开关字段
                if (ins.Field != null && ins.Field.DeclaringType == computer && ins.Field.Name.EndsWith("Enabled"))
                {
                    pendingField = ins.Field;
                    guardIndex = i;
                    Console.WriteLine($"  [{i,4}] ldfld {ins.Field.Name}");
                    continue;
                }

                if (pendingField != null && (ins.Op == OpCodes.Brfalse || ins.Op == OpCodes.Brfalse_S))
                {
                    Console.WriteLine($"  [{i,4}]   brfalse（开关为 false 时跳过下面的组构造）");
                    continue;
                }

                if (ins.Op == OpCodes.Newobj && ins.Target?.DeclaringType != null && guardIndex >= 0)
                {
                    string group = ins.Target.DeclaringType.Name;
                    Console.WriteLine($"  [{i,4}]   newobj {group}   ← 受 [{guardIndex}] {pendingField.Name} 守护");
                }
            }
        }

        private struct IlEntry
        {
            public OpCode Op;
            public FieldInfo Field;
            public MethodBase Target;
        }

        private static List<IlEntry> Read(MethodBase method)
        {
            List<IlEntry> result = new List<IlEntry>();
            MethodBody body = method.GetMethodBody();
            if (body == null) return result;
            byte[] il = body.GetILAsByteArray();
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int i = 0;
            while (i < il.Length)
            {
                OpCode op;
                if (il[i] == 0xFE) { if (i + 1 >= il.Length) break; op = TwoByte[il[i + 1]]; i += 2; }
                else { op = OneByte[il[i]]; i += 1; }

                FieldInfo field = null;
                MethodBase target = null;

                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(il, i); i += 4;
                            try { target = method.Module.ResolveMethod(token, typeArgs, methodArgs); } catch { }
                            break;
                        }
                    case OperandType.InlineField:
                        {
                            int token = BitConverter.ToInt32(il, i); i += 4;
                            try { field = method.Module.ResolveField(token, typeArgs, methodArgs); } catch { }
                            break;
                        }
                    case OperandType.InlineString:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineSig:
                    case OperandType.InlineI:
                    case OperandType.InlineBrTarget:
                        i += 4; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8; break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineVar:
                        i += 1; break;
                    case OperandType.InlineVar:
                        i += 2; break;
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(il, i);
                            i += 4 + 4 * count;
                            break;
                        }
                }
                result.Add(new IlEntry { Op = op, Field = field, Target = target });
            }
            return result;
        }

        private static void SearchType(string fragment)
        {
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Assembly dit = FindDiskInfoToolkit(lhm);
            if (dit == null) { Console.Error.WriteLine("DiskInfoToolkit not loaded"); return; }

            foreach (Assembly asm in new[] { lhm, dit })
            {
                if (asm == null) continue;
                Console.WriteLine($"-- 在 {asm.GetName().Name} 中查找名称含 \"{fragment}\" 的类型");
                foreach (Type type in asm.GetTypes().Where(t => t.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"   {(type.IsPublic ? "public   " : "internal ")} {type.FullName}");
                    if (type.Name.Contains("Detection") || type.Name.Contains("Group"))
                    {
                        foreach (MethodBase mb in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                                  BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                                      .OfType<MethodBase>())
                        {
                            string access = mb.IsPublic ? "public" : mb.IsFamily ? "protected" : mb.IsAssembly ? "internal" : "private";
                            Console.WriteLine($"        {access} {mb.Name}({string.Join(", ", mb.GetParameters().Select(DescribeParameter))})");
                        }
                    }
                }
            }
        }

        private static void DumpType(string typeName)
        {
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Type type = lhm.GetType(typeName) ?? FindDiskInfoToolkit(lhm)?.GetType(typeName) ?? Type.GetType(typeName);
            if (type == null) { Console.Error.WriteLine($"type not found: {typeName}"); return; }

            Console.WriteLine($"-- {type.FullName} ({type.Assembly.GetName().Name})");
            Console.WriteLine($"   visibility: {(type.IsPublic ? "public" : "internal/not-public")}");
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodBase mb)
                {
                    string access = mb.IsPublic ? "public" : mb.IsFamily ? "protected" : mb.IsAssembly ? "internal" : "private";
                    string generics = mb is MethodInfo mi2 && mi2.IsGenericMethod
                        ? "<" + string.Join(",", mi2.GetGenericArguments().Select(g => g.Name)) + ">" : string.Empty;
                    Console.WriteLine($"   {access} {mb.Name}{generics}({string.Join(", ", mb.GetParameters().Select(DescribeParameter))})");
                }
            }
        }

        /// <summary>
        /// 句柄打开点取证：枚举两个程序集里声明的原生句柄入口（CreateFile / DeviceIoControl 等），
        /// 并报告它们各自被哪个类型的哪个方法引用 ⇒ 判定"打开句柄"发生在构造期还是取值期。
        /// </summary>
        private static void DumpHandleSites()
        {
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Assembly dit = FindDiskInfoToolkit(lhm);

            foreach (Assembly asm in new[] { lhm, dit })
            {
                if (asm == null) continue;
                Console.WriteLine($"== {asm.GetName().Name} {asm.GetName().Version} ：原生入口声明 ==");
                foreach (Type type in asm.GetTypes())
                {
                    foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                             BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        DllImportAttribute dll = m.GetCustomAttribute<DllImportAttribute>();
                        if (dll == null) continue;
                        if (!dll.EntryPoint.Contains("CreateFile") &&
                            !dll.EntryPoint.Contains("DeviceIoControl") &&
                            !dll.EntryPoint.Contains("CreateFileW"))
                            continue;
                        Console.WriteLine($"  [{dll.Value}!{dll.EntryPoint}] {type.FullName}.{m.Name}");
                    }
                }
                Console.WriteLine();
            }

            Console.WriteLine("== 反查：谁在调用这些原生入口（限定 Storage / Disk 相关类型） ==");
            foreach (Assembly asm in new[] { lhm, dit })
            {
                if (asm == null) continue;
                foreach (Type type in asm.GetTypes())
                {
                    if (!type.FullName.Contains("Storage") && !type.FullName.Contains("Disk") &&
                        !type.FullName.Contains("Smart") && !type.FullName.Contains("Device")) continue;

                    foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                                  BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (!(member is MethodBase mb)) continue;
                        List<MethodBase> calls = CallSequence(mb);
                        List<string> hits = calls
                            .Where(c => c.Name.Contains("CreateFile") || c.Name.Contains("DeviceIoControl"))
                            .Select(c => c.Name)
                            .Distinct()
                            .ToList();
                        if (hits.Count == 0) continue;
                        Console.WriteLine($"  {type.Name}.{mb.Name} -> {string.Join(",", hits)}");
                    }
                }
            }
        }

        /// <summary>DiskInfoToolkit 侧：枚举入口有没有"只要这几块盘"这类参数。</summary>
        private static void ReportDiskToolkitSurface(Assembly dit)
        {
            Console.WriteLine("== DiskInfoToolkit：磁盘枚举入口 ==");
            if (dit == null) { Console.WriteLine("!! 未找到 DiskInfoToolkit"); return; }

            string[] candidateNames = { "DiskInfoToolkit.StorageManager", "DiskInfoToolkit.Storage" };
            foreach (string typeName in candidateNames)
            {
                Type type = dit.GetType(typeName);
                if (type == null) { Console.WriteLine($"-- {typeName}: 不存在"); continue; }

                Console.WriteLine($"-- {typeName}");
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                             .Where(m => IsEnumerationLike(m)))
                {
                    Console.WriteLine($"     static? {m.IsStatic} {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                }
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Console.WriteLine($"     prop {p.PropertyType.Name} {p.Name} {{{(p.CanRead ? " get;" : "")}{(p.CanWrite ? " set;" : "")} }}");
                }
                foreach (EventInfo e in type.GetEvents(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Console.WriteLine($"     event {e.Name} : {e.EventHandlerType?.Name}");
            }

            Console.WriteLine();
            Console.WriteLine("== 是否存在'允许列表/过滤委托'形参（这是本次 spike 的判定核心） ==");
            List<string> filterish = new List<string>();
            foreach (Type type in dit.GetTypes())
            {
                foreach (MethodBase mb in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                              .Cast<MethodBase>()
                                              .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)))
                {
                    foreach (ParameterInfo p in mb.GetParameters())
                    {
                        Type pt = p.ParameterType;
                        bool delegateLike = typeof(Delegate).IsAssignableFrom(pt) ||
                                            (pt.IsGenericType && pt.GetGenericArguments().Any(g => typeof(Delegate).IsAssignableFrom(g)));
                        if (!delegateLike) continue;
                        if (!pt.Name.Contains("Func") && !pt.Name.Contains("Predicate") && !pt.Name.Contains("Filter")) continue;
                        filterish.Add($"{type.Name}.{mb.Name}({p.ParameterType.Name} {p.Name})");
                    }
                }
            }
            Console.WriteLine(filterish.Count == 0
                ? "  结论：DiskInfoToolkit **没有任何** Predicate/Func<T,bool> 形式的过滤形参 ⇒ 无法在枚举前按允许列表裁剪。"
                : "  发现候选：" + string.Join(" | ", filterish.Take(40)));
        }

        private static bool IsEnumerationLike(MethodInfo m)
        {
            string n = m.Name;
            return n.Contains("Get") || n.Contains("Reload") || n.Contains("Enumerate") || n.Contains("Detect") ||
                   n.Contains("Disk") || n.Contains("Storage");
        }

        // =====================================================================
        // IL：按场景 dump 指定方法的调用序列
        // =====================================================================
        private static void DumpIl(string typeAndMethod)
        {
            if (string.IsNullOrEmpty(typeAndMethod))
            {
                Console.Error.WriteLine("usage: il <AssemblyQualifiedType>!<MethodName>");
                return;
            }

            string[] parts = typeAndMethod.Split('!');
            Assembly lhm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            Type type = lhm.GetType(parts[0]) ?? FindDiskInfoToolkit(lhm)?.GetType(parts[0]) ?? Type.GetType(parts[0]);
            if (type == null) { Console.Error.WriteLine($"type not found: {parts[0]}"); return; }

            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (parts.Length > 1 && !string.Equals(member.Name, parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                if (!(member is MethodBase mb)) continue;
                Console.WriteLine($"-- {type.Name}.{mb.Name}");
                Console.WriteLine($"   {SequenceOf(CallSequence(mb).Take(60))}");
            }
        }

        private static string SequenceOf(IEnumerable<MethodBase> calls) =>
            string.Join(" -> ", calls.Select(c => $"{c.DeclaringType?.Name}.{c.Name}"));

        private static string DescribeParameter(ParameterInfo p) => $"{p.ParameterType.Name} {p.Name}";

        // ---------------------------------------------------------------------
        // 最小 IL 读取器（与仓库 tests/LiteMonitor.Tests/IlInspector.cs 同一思路，
        // 此处只取"调用目标"，够判定构造顺序即可）。
        // ---------------------------------------------------------------------
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        static SpikeRunner()
        {
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                OpCode op = (OpCode)field.GetValue(null);
                ushort value = unchecked((ushort)op.Value);
                if (value < 0x100) OneByte[value] = op;
                else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
            }
        }

        private static List<MethodBase> CallSequence(MethodBase method)
        {
            List<MethodBase> calls = new List<MethodBase>();
            MethodBody body = method.GetMethodBody();
            if (body == null) return calls;

            byte[] il = body.GetILAsByteArray();
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int i = 0;
            while (i < il.Length)
            {
                OpCode op;
                if (il[i] == 0xFE)
                {
                    if (i + 1 >= il.Length) break;
                    op = TwoByte[il[i + 1]];
                    i += 2;
                }
                else
                {
                    op = OneByte[il[i]];
                    i += 1;
                }

                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            try
                            {
                                MethodBase target = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                                if (target != null) calls.Add(target);
                            }
                            catch { /* 解析不了就跳过 */ }
                            break;
                        }
                    case OperandType.InlineField:
                    case OperandType.InlineString:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineSig:
                    case OperandType.InlineI:
                    case OperandType.InlineBrTarget:
                        i += 4; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8; break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineVar:
                        i += 1; break;
                    case OperandType.InlineVar:
                        i += 2; break;
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(il, i);
                            i += 4 + 4 * count;
                            break;
                        }
                }
            }
            return calls;
        }
    }
}
