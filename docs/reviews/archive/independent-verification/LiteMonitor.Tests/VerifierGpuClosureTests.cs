using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【独立 verifier 专用】GPU 切换闭环的独立复核用例（第二轮，基线 cf1c8ee/7283b91）。
/// <para>本文件由独立复核者编写，只落在复核者自己的隔离 worktree 内，
/// 不修改任何既有生产代码 / 测试代码 / 文档。</para>
/// <para>覆盖：合同 §6.2（清理责任转交 INV-1~INV-6）、§6.4（重试预算与退避）、
/// §7.2 / §7.4（句柄代次唯一真相源与三重丢弃判据）、§8（HandleCreated 判别位）、
/// §5.3（诊断字段互不覆盖）、§9（fallback 样式 + 主审裁决 D-H）、§10 GPU-005（pending 语义）、
/// 以及插件沙箱契约常量与公开面。</para>
/// </summary>
public sealed class VerifierGpuClosureTests
{
    // ====================================================================
    // 共享 IL 扫描缓存
    // ====================================================================

    private static readonly Lazy<List<(Type Type, MethodBase Method, List<IlInspector.IlInstruction> Instructions)>>
        ProductionIl = new(BuildProductionIl);

    private static List<(Type Type, MethodBase Method, List<IlInspector.IlInstruction> Instructions)> BuildProductionIl()
    {
        List<(Type, MethodBase, List<IlInspector.IlInstruction>)> result = [];
        Assembly assembly = typeof(UIController).Assembly;

        foreach (Type type in EnumerateTypes(assembly))
        {
            foreach (MethodBase method in EnumerateMethods(type))
            {
                try
                {
                    result.Add((type, method, IlInspector.Disassemble(method).Instructions));
                }
                catch (Exception)
                {
                    // 读不动的成员跳过（不影响"是否存在调用点"的判定）。
                }
            }
        }

        return result;
    }

    /// <summary>找出生产代码里调用了 <paramref name="target"/> 的全部位置。</summary>
    private static List<string> FindCallSites(MethodBase? target)
    {
        List<string> sites = [];
        if (target is null) return sites;

        foreach ((Type type, MethodBase method, List<IlInspector.IlInstruction> instructions) in ProductionIl.Value)
        {
            foreach (IlInspector.IlInstruction instruction in instructions)
            {
                if (instruction.Target is null) continue;
                if (!SameMethod(instruction.Target, target)) continue;
                sites.Add($"{type.FullName}.{method.Name}");
            }
        }

        return sites;
    }

    private static bool SameMethod(MethodBase left, MethodBase right) =>
        left.Module == right.Module && left.MetadataToken == right.MetadataToken;

    private static IEnumerable<Type> EnumerateTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MethodInfo method in type.GetMethods(flags))
        {
            yield return method;
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(flags))
        {
            yield return ctor;
        }

        ConstructorInfo? cctor = type.TypeInitializer;
        if (cctor is not null) yield return cctor;
    }

    /// <summary>
    /// 计算方法体里所有分支指令的**跳转目标偏移**（用于判定"某条赋值是否会被跳过"）。
    /// </summary>
    private static HashSet<int> ComputeBranchTargets(MethodBase method)
    {
        HashSet<int> targets = [];
        MethodBody? body = method.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is null) return targets;

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(method).Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            IlInspector.IlInstruction instruction = instructions[i];
            int nextOffset = i + 1 < instructions.Count ? instructions[i + 1].Offset : il.Length;
            int opcodeSize = il[instruction.Offset] == 0xFE ? 2 : 1;

            switch (instruction.OpCode.OperandType)
            {
                case OperandType.InlineBrTarget:
                    {
                        int delta = BitConverter.ToInt32(il, instruction.Offset + opcodeSize);
                        targets.Add(nextOffset + delta);
                        break;
                    }
                case OperandType.ShortInlineBrTarget:
                    {
                        sbyte delta = unchecked((sbyte)il[instruction.Offset + opcodeSize]);
                        targets.Add(nextOffset + delta);
                        break;
                    }
                case OperandType.InlineSwitch:
                    {
                        int count = BitConverter.ToInt32(il, instruction.Offset + opcodeSize);
                        int baseAddress = nextOffset + (4 * count);
                        for (int s = 0; s < count; s++)
                        {
                            int offsetInTable = instruction.Offset + opcodeSize + 4 + (4 * s);
                            if (offsetInTable + 4 > il.Length) break;
                            int delta = BitConverter.ToInt32(il, offsetInTable);
                            targets.Add(baseAddress + delta);
                        }

                        break;
                    }
            }
        }

        return targets;
    }

    // ====================================================================
    // A. GPU-001 闭环：清理责任转交（合同 §6.2）
    // ====================================================================

    /// <summary>
    /// A1：生产代码里**必须有人订阅**宿主的清理责任移交出口。
    /// <para>若无人订阅，宿主停止时的清理记录只会停留在 <c>LastCapturePolicyCleanup</c> 上，
    /// 而宿主随即被释放 ⇒ 清理责任**彻底丢失**（旧 HWND 永远保持 WDA 排除）。</para>
    /// </summary>
    [Fact]
    public void 护栏_生产代码必须订阅清理责任移交出口()
    {
        EventInfo? handoff = typeof(GpuPresentationHostController)
            .GetEvent("CapturePolicyCleanupRequired", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(handoff);

        List<string> sites = FindCallSites(handoff!.GetAddMethod());
        Assert.True(
            sites.Count > 0,
            "合同 §6.2：UI 生命周期所有者必须订阅 GpuPresentationHostController.CapturePolicyCleanupRequired " +
            "并把记录登记进 CapturePolicyCleanupLedger；生产代码里**找不到任何订阅点**（清理责任会被丢弃）。");
    }

    /// <summary>
    /// A2：生产代码里**必须有人调用** ledger 的 <c>Add</c>。
    /// <para>ledger 若永远收不到记录，§6.4 的退避重试、§6.3 的终结迁移全部是空转。</para>
    /// </summary>
    [Fact]
    public void 护栏_生产代码必须把移交的清理责任登记进ledger()
    {
        MethodInfo? add = typeof(CapturePolicyCleanupLedger).GetMethod(
            "Add",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(add);

        List<string> sites = FindCallSites(add);
        Assert.True(
            sites.Count > 0,
            "合同 §6.2：ledger 必须真的被喂入清理责任（Add）；生产代码里**找不到任何调用点**。");
    }

    /// <summary>
    /// A3：INV-6 —— 新代次取得 WDA 所有权前必须查询同代次是否仍有未完成清理。
    /// <para>宿主侧的探针属性必须由 ledger 持有者注入，否则该守卫恒不生效。</para>
    /// </summary>
    [Fact]
    public void 护栏_生产代码必须注入同代次清理挂起探针()
    {
        PropertyInfo? probe = typeof(GpuPresentationHostController).GetProperty(
            "CapturePolicyCleanupPendingProbe",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(probe);

        List<string> sites = FindCallSites(probe!.GetSetMethod());
        Assert.True(
            sites.Count > 0,
            "合同 §6.2 INV-6：必须把 ledger.HasPendingForGeneration 注入宿主的 CapturePolicyCleanupPendingProbe；" +
            "生产代码里**找不到任何注入点**（守卫恒不生效）。");
    }

    /// <summary>
    /// A4（INV-2）：<c>StopGpuPresentation()</c> 之后 <c>_gpuPresentation</c> 必须**无条件**为 null。
    /// <para>判据：给 <c>_gpuPresentation</c> 赋值的那条 <c>stfld</c> 落在 <c>finally</c> 子句里，
    /// 且**不是任何分支的跳转目标** ⇒ 正常路径与异常路径都会执行。</para>
    /// </summary>
    [Fact]
    public void 护栏_停止呈现之后宿主引用必须无条件为null()
    {
        MethodInfo stop = typeof(UIController).GetMethod(
            "StopGpuPresentation",
            BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(stop);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(stop).Instructions;
        FieldInfo field = typeof(UIController).GetField(
            "_gpuPresentation",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(field);

        List<int> stores = instructions
            .Where(i => i.OpCode == OpCodes.Stfld && i.Field is not null && i.Field.Name == field.Name)
            .Select(i => i.Offset)
            .ToList();

        Assert.True(stores.Count >= 1, "StopGpuPresentation 必须把 _gpuPresentation 置 null。");

        MethodBody body = stop.GetMethodBody()!;
        HashSet<int> branchTargets = ComputeBranchTargets(stop);

        foreach (int storeOffset in stores)
        {
            // 不得是任何分支的跳转目标 ⇒ 无条件执行。
            Assert.False(
                branchTargets.Contains(storeOffset),
                $"合同 §6.2 INV-2：_gpuPresentation = null（IL {storeOffset}）不得被条件分支跳过" +
                "（清理责任永不通过宿主引用表达）。");

            // 必须落在异常处理子句（finally）里 ⇒ 异常路径同样执行。
            bool insideHandler = body.ExceptionHandlingClauses.Any(c =>
                storeOffset >= c.HandlerOffset &&
                storeOffset < c.HandlerOffset + c.HandlerLength);
            Assert.True(
                insideHandler,
                $"合同 §6.2 INV-2：_gpuPresentation = null（IL {storeOffset}）必须在 finally 子句里，" +
                "否则宿主 Dispose 抛异常时会保留引用。");
        }
    }

    /// <summary>
    /// A9：移交出口**与** <c>LastCapturePolicyCleanup</c> 兜底**双路并存**，
    /// 且两条路都真的把责任交进 ledger（合同 §6.2 + 移交不能丢）。
    /// </summary>
    [Fact]
    public void 宿主_移交出口与兜底都必须把责任交进ledger()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory("verifier-a9");
            try
            {
                // ── 路线 1：事件移交 ──
                CapturePolicyCleanupLedger eventLedger = NewLedger();
                RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 2);
                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter);
                host.CapturePolicyCleanupRequired += cleanup => eventLedger.Add(cleanup);

                PresentationSession session = new(adapter);
                Assert.True(session.Start(
                    new IntPtr(42),
                    new RenderGenerations(1, 1, 1, 1, 1),
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out _,
                    out _));
                host.SessionOwner.Bind(session);
                host.Dispose();

                Assert.Single(eventLedger.Entries);
                Assert.Equal(2, eventLedger.Entries[0].Attempts);
                Assert.True(eventLedger.HasPendingForGeneration(eventLedger.Entries[0].Generation));

                // ── 路线 2：事件无人订阅（订阅者异常/未送达）⇒ 兜底仍能取到记录 ──
                CapturePolicyCleanupLedger fallbackLedger = NewLedger();
                RecordingWindowAdapter adapter2 = new(originalAffinity: 0, failRestoreWrites: 2);
                using Form form2 = new();
                using GpuPresentationHostController host2 = new(
                    form2,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\B"),
                    windowAdapter: adapter2);

                PresentationSession session2 = new(adapter2);
                Assert.True(session2.Start(
                    new IntPtr(43),
                    new RenderGenerations(1, 1, 1, 1, 1),
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out _,
                    out _));
                host2.SessionOwner.Bind(session2);
                host2.Dispose();

                Assert.NotNull(host2.LastCapturePolicyCleanup);
                fallbackLedger.Add(host2.LastCapturePolicyCleanup!);
                Assert.Single(fallbackLedger.Entries);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    /// <summary>
    /// A10：移交出口未送达（订阅者异常）时，必须有 <c>LastCapturePolicyCleanup</c> 兜底，
    /// 且兜底记录必须真的登记进 ledger（合同 §6.2"移交不能丢"）。
    /// </summary>
    [Fact]
    public void 护栏_停止呈现必须保留移交兜底路径()
    {
        MethodInfo stop = typeof(UIController).GetMethod(
            "StopGpuPresentation",
            BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(stop);

        bool readsFallback = false;
        bool callsAdd = false;
        foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(stop).Instructions)
        {
            MethodBase? target = instruction.Target;
            if (target is null) continue;
            if (target.DeclaringType == typeof(GpuPresentationHostController) &&
                target.Name == "get_LastCapturePolicyCleanup")
            {
                readsFallback = true;
            }

            if (target.DeclaringType == typeof(CapturePolicyCleanupLedger) && target.Name == "Add")
            {
                callsAdd = true;
            }
        }

        Assert.True(
            readsFallback,
            "合同 §6.2：事件移交未送达时必须用 LastCapturePolicyCleanup 兜底（移交不能丢）。");
        Assert.True(callsAdd, "兜底记录必须真的登记进 ledger。");
    }

    /// <summary>
    /// A11：<c>GpuPresentationHostController</c> 的**唯一**创建点必须是订阅了移交出口的工厂方法
    /// ⇒ 不存在"忘了订阅"的旁路（合同 §6.2）。
    /// </summary>
    [Fact]
    public void 护栏_所有宿主创建点都必须经过唯一工厂()
    {
        List<string> creators = [];
        foreach ((Type type, MethodBase method, List<IlInspector.IlInstruction> instructions) in ProductionIl.Value)
        {
            foreach (IlInspector.IlInstruction instruction in instructions)
            {
                if (instruction.OpCode != OpCodes.Newobj) continue;
                if (instruction.Target?.DeclaringType != typeof(GpuPresentationHostController)) continue;
                creators.Add($"{type.FullName}.{method.Name}");
            }
        }

        Assert.True(
            creators.Count == 1,
            "合同 §6.2：宿主创建点必须唯一（都经过订阅了移交出口的工厂）；实际有：" +
            string.Join(", ", creators));
        Assert.Contains("CreateGpuPresentationHost", creators[0]);
    }

    /// <summary>
    /// A12：移交回调 <c>OnGpuCapturePolicyCleanupRequired</c> 必须把记录**真的**登记进 ledger。
    /// <para>（若回调里只是"收到事件"却不登记，ledger 仍然收不到责任 ⇒ 移交形同虚设。）</para>
    /// </summary>
    [Fact]
    public void 护栏_移交回调必须把记录登记进ledger()
    {
        MethodInfo handler = typeof(UIController).GetMethod(
            "OnGpuCapturePolicyCleanupRequired",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(handler);

        bool callsAdd = false;
        foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(handler).Instructions)
        {
            MethodBase? target = instruction.Target;
            if (target?.DeclaringType != typeof(CapturePolicyCleanupLedger)) continue;
            if (target.Name != "Add") continue;
            callsAdd = true;
        }

        Assert.True(
            callsAdd,
            "合同 §6.2：移交回调必须调用 CapturePolicyCleanupLedger.Add 把记录登记进 ledger。");
    }

    /// <summary>A5（INV-4）：同一代次重复登记必须幂等 —— 至多一条 Pending。</summary>
    [Fact]
    public void ledger_同一代次重复登记必须幂等()
    {
        long generation = 7;
        List<WindowCapturePolicyCleanup> added = [];
        CapturePolicyCleanupLedger ledger = new(
            () => generation,
            _ => true,
            cleanup =>
            {
                added.Add(cleanup);
                return new CapturePolicyRestoreResult(false, "e", "m");
            });

        WindowCapturePolicyCleanup first = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = generation,
            OriginalAffinity = 0,
            AffinityChangedBySession = true,
        };
        WindowCapturePolicyCleanup second = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = generation,
            OriginalAffinity = 0,
            AffinityChangedBySession = true,
        };

        Assert.True(ledger.Add(first));
        Assert.False(ledger.Add(second));
        Assert.Single(ledger.Entries);
        Assert.Same(first, ledger.Entries[0]);
    }

    /// <summary>A6（INV-6）：同代次存在未完成清理时必须能查询出来（拒绝叠加新的 affinity 所有者）。</summary>
    [Fact]
    public void ledger_必须能查询同代次未完成清理()
    {
        long generation = 9;
        CapturePolicyCleanupLedger ledger = NewLedger();

        Assert.False(ledger.HasPendingForGeneration(generation));

        WindowCapturePolicyCleanup cleanup = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = generation,
            AffinityChangedBySession = true,
        };
        Assert.True(ledger.Add(cleanup));
        Assert.True(ledger.HasPendingForGeneration(generation));

        cleanup.TryMarkCompleted("restored");
        Assert.False(ledger.HasPendingForGeneration(generation));
    }

    /// <summary>
    /// A7 / D2（合同 §7.4）：代次判据**必须先于** IsWindow。
    /// <para>旧代次 ⇒ 禁止任何 Win32 调用（含 IsWindow）⇒ 直接终结为 superseded。</para>
    /// </summary>
    [Fact]
    public void ledger_旧代次不得触碰任何窗口调用()
    {
        int probes = 0;
        int restores = 0;
        long current = 10;
        CapturePolicyCleanupLedger ledger = new(
            () => current,
            _ =>
            {
                probes++;
                return true;
            },
            _ =>
            {
                restores++;
                return new CapturePolicyRestoreResult(false, "e", "m");
            });

        WindowCapturePolicyCleanup stale = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = 9,
            AffinityChangedBySession = true,
        };
        Assert.True(ledger.Add(stale));

        ledger.Pump(DateTimeOffset.UnixEpoch);

        Assert.Equal(0, probes);
        Assert.Equal(0, restores);
        Assert.Equal(CapturePolicyCleanupState.Completed, stale.State);
        Assert.Equal("superseded_by_new_generation", stale.TerminalReason);
        Assert.Equal("gpu_capture_policy_cleanup_superseded", ledger.CleanupWarningCode);
    }

    /// <summary>
    /// A8：移交出口必须真的把记录交给订阅者；**一个订阅者抛异常不得阻断其它订阅者**。
    /// </summary>
    [Fact]
    public void 宿主_移交出口必须把记录交给全部订阅者()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory("verifier-a8");
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 2);
                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter);

                List<WindowCapturePolicyCleanup> first = [];
                List<WindowCapturePolicyCleanup> second = [];
                host.CapturePolicyCleanupRequired += cleanup =>
                {
                    first.Add(cleanup);
                    throw new InvalidOperationException("订阅者异常不得影响其它订阅者");
                };
                host.CapturePolicyCleanupRequired += cleanup => second.Add(cleanup);

                PresentationSession session = new(adapter);
                Assert.True(session.Start(
                    new IntPtr(42),
                    new RenderGenerations(1, 1, 1, 1, 1),
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out _,
                    out _));
                host.SessionOwner.Bind(session);

                host.Dispose();

                Assert.Single(first);
                Assert.Single(second);
                Assert.Same(first[0], second[0]);
                Assert.Equal(CapturePolicyCleanupState.Pending, first[0].State);
                Assert.Equal(2, first[0].Attempts);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ====================================================================
    // B. 句柄代次唯一性（主审裁决 D-G）
    // ====================================================================

    /// <summary>B1：除类型自身的单例外，生产代码不得再 <c>new WindowHandleGeneration()</c>。</summary>
    [Fact]
    public void 护栏_除类型自身外生产代码不得新建句柄代次实例()
    {
        List<string> offenders = [];
        foreach ((Type type, MethodBase method, List<IlInspector.IlInstruction> instructions) in ProductionIl.Value)
        {
            foreach (IlInspector.IlInstruction instruction in instructions)
            {
                if (instruction.OpCode != OpCodes.Newobj) continue;
                if (instruction.Target is not ConstructorInfo ctor) continue;
                if (ctor.DeclaringType != typeof(WindowHandleGeneration)) continue;
                if (type == typeof(WindowHandleGeneration)) continue;
                offenders.Add($"{type.FullName}.{method.Name}");
            }
        }

        Assert.True(offenders.Count == 0, "主审裁决 D-G 违例：" + string.Join(", ", offenders));
    }

    /// <summary>
    /// B2（F7 闭环）：<c>LiveGlassController</c> 不得再持有第二个句柄代次计数器，
    /// 必须改为**只读消费** <c>WindowHandleGeneration.Instance.Current</c>。
    /// </summary>
    [Fact]
    public void 护栏_LiveGlassController不得再持有第二个句柄代次计数器()
    {
        Type liveGlass = typeof(UIController).Assembly.GetType("LiteMonitor.LiveGlassController")!;
        Assert.NotNull(liveGlass);

        List<string> counters = liveGlass
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(long) &&
                        f.Name.IndexOf("handleGeneration", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(f => f.Name)
            .ToList();

        Assert.True(
            counters.Count == 0,
            "合同 §13 自检项「是否新建了第二个句柄代次计数器」未闭环；LiveGlassController 仍持有：" +
            string.Join(", ", counters));

        MethodBase[] readers = liveGlass
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m =>
            {
                foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(m).Instructions)
                {
                    MethodBase? target = instruction.Target;
                    if (target?.DeclaringType != typeof(WindowHandleGeneration)) continue;
                    if (target.Name == "get_Instance" || target.Name == "get_Current") return true;
                }

                return false;
            })
            .ToArray();

        Assert.True(
            readers.Length > 0,
            "LiveGlassController 必须只读消费 WindowHandleGeneration.Instance.Current（不得自增代次）。");
    }

    /// <summary>B3：仓库里不得再出现 <c>SharedWindowHandleGeneration</c> 这个类型名。</summary>
    [Fact]
    public void 护栏_仓库不得再存在SharedWindowHandleGeneration类型名()
    {
        List<string> hits = EnumerateTypes(typeof(UIController).Assembly)
            .Where(t => string.Equals(t.Name, "SharedWindowHandleGeneration", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(hits.Count == 0, "主审裁决 D-G 违例：" + string.Join(", ", hits));
    }
    // ====================================================================
    // C. HandleCreated 判别位（合同 §8）
    // ====================================================================

    /// <summary>C1：构造期首次创建 / 非受控创建 **禁止** 拉起 GPU（不得进入 GPU 重建路径）。</summary>
    [Fact]
    public void 判别位_非受控创建不得进入GPU重建路径()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);

        coordinator.OnHandleCreated(controlled: false, newHandleGeneration: generations.Advance());

        Assert.NotEqual(RenderingTransitionPhase.RecreatingHandle, coordinator.Phase);
        Assert.NotEqual(RenderingTransitionPhase.Restoring, coordinator.Phase);
        Assert.False(coordinator.CanStartGpuPresentation);
    }

    /// <summary>C2：关闭优先 —— 关闭期间不得置位判别位、不得发起新的切换。</summary>
    [Fact]
    public void 判别位_关闭期间不得置位也不得开始切换()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);
        coordinator.NotifyFormClosing();

        Assert.Equal(0, coordinator.RequestControlledRecreate("closing"));
        Assert.Equal(0, coordinator.PendingControlledRecreateCount);
        Assert.False(coordinator.TryConsumeControlledRecreate(out _));
        Assert.False(coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _));
        Assert.False(coordinator.AllowsWindowOperations);
    }

    /// <summary>C3：超时兜底 —— 5 秒未被消费 ⇒ 归零 + <c>gpu_presentation_handle_recreate_not_observed</c>。</summary>
    [Fact]
    public void 判别位_超时兜底必须归零并记告警()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8)));
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations, clock);

        coordinator.RequestControlledRecreate("r");
        Assert.Equal(1, coordinator.PendingControlledRecreateCount);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(coordinator.EnforceControlledRecreateTimeout());
        Assert.Equal(1, coordinator.PendingControlledRecreateCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.EnforceControlledRecreateTimeout());
        Assert.Equal(0, coordinator.PendingControlledRecreateCount);
        Assert.Equal(
            "gpu_presentation_handle_recreate_not_observed",
            coordinator.State.CleanupWarningCode);
    }

    /// <summary>C4：陈旧残留兜底 —— 再次置位前发现残留 ⇒ 归零 + <c>gpu_presentation_handle_recreate_stale_flag</c>。</summary>
    [Fact]
    public void 判别位_陈旧残留必须归零并记告警()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);

        coordinator.RequestControlledRecreate("first");
        coordinator.RequestControlledRecreate("second");

        Assert.Equal(1, coordinator.PendingControlledRecreateCount);
        Assert.Equal(
            "gpu_presentation_handle_recreate_stale_flag",
            coordinator.State.CleanupWarningCode);
    }

    /// <summary>
    /// C5：<c>RecreateHandle()</c> 抛异常 ⇒ 立即取消并归零。
    /// <para>入口方法必须带 catch 子句且在 catch 里调用 <c>CancelControlledRecreate</c>。</para>
    /// </summary>
    [Fact]
    public void 护栏_受控重建抛异常必须立即取消并归零()
    {
        MethodInfo entry = typeof(MainForm).GetMethod(
            "TryRecreateHandleForTransition",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(entry);

        MethodBody? body = entry.GetMethodBody();
        Assert.NotNull(body);
        Assert.True(
            body!.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause),
            "受控重建入口必须有 catch 子句（合同 §8.1 规则 ②）。");

        MethodInfo cancel = typeof(UIController).GetMethod(
            "CancelControlledRecreate",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        List<string> sites = FindCallSites(cancel);
        Assert.True(
            sites.Count > 0,
            "合同 §8.1 规则 ②：RecreateHandle 抛异常时必须立即调用 CancelControlledRecreate 归零。");
    }

    /// <summary>C6：判别位必须在 <c>RecreateHandle()</c> **之前**置位。</summary>
    [Fact]
    public void 护栏_判别位必须在重建之前置位()
    {
        MethodInfo entry = typeof(MainForm).GetMethod(
            "TryRecreateHandleForTransition",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        int requestOffset = -1;
        int cancelOffset = -1;
        int controlRecreateOffset = -1;
        foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(entry).Instructions)
        {
            MethodBase? target = instruction.Target;
            if (target is null) continue;
            if (target.DeclaringType == typeof(UIController))
            {
                if (target.Name == "RequestControlledRecreate" && requestOffset < 0) requestOffset = instruction.Offset;
                if (target.Name == "CancelControlledRecreate" && cancelOffset < 0) cancelOffset = instruction.Offset;
            }

            if (target.DeclaringType == typeof(System.Windows.Forms.Control) &&
                target.Name == "RecreateHandle" &&
                controlRecreateOffset < 0)
            {
                controlRecreateOffset = instruction.Offset;
            }
        }

        Assert.True(requestOffset >= 0, "受控重建入口必须调用 RequestControlledRecreate 置位判别位。");
        Assert.True(controlRecreateOffset >= 0, "受控重建入口必须直接调用 Control.RecreateHandle。");
        Assert.True(
            requestOffset < controlRecreateOffset,
            $"判别位置位（IL {requestOffset}）必须早于 RecreateHandle（IL {controlRecreateOffset}）。");
        Assert.True(cancelOffset >= 0, "受控重建入口必须在异常路径调用 CancelControlledRecreate。");
    }

    // ====================================================================
    // D. 三重丢弃判据（合同 §7.4 / U05）
    // ====================================================================

    /// <summary>D1：<c>TransitionId</c> / <c>HandleGeneration</c> / <c>HostGeneration</c> 缺一不可。</summary>
    [Fact]
    public void 三重丢弃_三个判据缺一不可()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);

        coordinator.OnHandleCreated(controlled: true, newHandleGeneration: generations.Advance());
        long host = coordinator.NextHostGeneration();
        long transition = coordinator.TransitionId;
        long handle = coordinator.HandleGeneration;

        Assert.False(coordinator.ShouldDiscardCallback(transition, handle, host));

        // 旧切换请求
        Assert.True(coordinator.ShouldDiscardCallback(transition - 1, handle, host));
        // 旧 HWND 代次
        Assert.True(coordinator.ShouldDiscardCallback(transition, handle - 1, host));
        // 旧宿主会话
        Assert.True(coordinator.ShouldDiscardCallback(transition, handle, host - 1));
    }

    // ====================================================================
    // E. 诊断字段互不覆盖（合同 §5.3 规则 2）
    // ====================================================================

    /// <summary>E1：写清理诊断不得清空呈现根因 <c>FailureCode</c>。</summary>
    [Fact]
    public void 诊断_写清理诊断不得清空呈现根因()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);

        coordinator.FailHandleRecreate();
        Assert.Equal("gpu_presentation_handle_recreate_failed", coordinator.State.FailureCode);

        coordinator.RecordCleanupState(
            CapturePolicyCleanupState.Pending,
            "presentation_capture_policy_restore_failed");

        Assert.Equal("gpu_presentation_handle_recreate_failed", coordinator.State.FailureCode);
        Assert.Equal("presentation_capture_policy_restore_failed", coordinator.State.CleanupErrorCode);
    }

    /// <summary>E2：写呈现根因不得清空清理诊断。</summary>
    [Fact]
    public void 诊断_写呈现根因不得清空清理诊断()
    {
        WindowHandleGeneration generations = new();
        RenderingTransitionCoordinator coordinator = new(generations);

        coordinator.RecordCleanupState(
            CapturePolicyCleanupState.Pending,
            "presentation_capture_policy_restore_failed");
        Assert.Equal("presentation_capture_policy_restore_failed", coordinator.State.CleanupErrorCode);

        coordinator.FailHandleRecreate();

        Assert.Equal("gpu_presentation_handle_recreate_failed", coordinator.State.FailureCode);
        Assert.Equal("presentation_capture_policy_restore_failed", coordinator.State.CleanupErrorCode);
    }

    /// <summary>E3：非致命诊断 <c>gpu_fallback_style_invalid</c> **不得**进入主题校验错误列表。</summary>
    [Fact]
    public void 诊断_非致命样式诊断不得进主题校验错误列表()
    {
        ThemeRenderingConfig config = new()
        {
            FallbackStyle = new ThemeFallbackStyle { Background = "这不是一个颜色" },
        };

        IReadOnlyList<string> errors = config.Validate(out IReadOnlyList<string> diagnostics);

        Assert.True(
            errors.All(e => !e.Contains("gpu_fallback_style_invalid", StringComparison.Ordinal)),
            "合同 §5.3 规则 1：非致命诊断不得进入 Validate() 的错误列表。");
        Assert.True(
            diagnostics.Any(d => d.Contains("gpu_fallback_style_invalid", StringComparison.Ordinal)),
            "非法 fallback 样式必须产出 gpu_fallback_style_invalid 诊断。");
    }

    /// <summary>E4：样式非法时**主题仍可用**（错误列表必须为空）。</summary>
    [Fact]
    public void 诊断_样式非法时主题仍可用()
    {
        ThemeRenderingConfig config = new()
        {
            FallbackStyle = new ThemeFallbackStyle { Background = "这不是一个颜色" },
        };

        Assert.Empty(config.Validate());
    }

    // ====================================================================
    // F. 回退样式 + 主审裁决 D-H（合同 §9 / 计划 §4.5.11）
    // ====================================================================

    /// <summary>F1：派生样式是纯函数 —— 不得修改传入的主题对象，也不得碰 <c>ThemeManager.Current</c>。</summary>
    [Fact]
    public void 回退样式_解析不得修改主题对象()
    {
        ThemeRenderingConfig config = new()
        {
            FallbackStyle = new ThemeFallbackStyle
            {
                Background = "#101112",
                PanelBackground = "#202122",
                Border = "#303132",
                Text = "#404142",
                MutedText = "#505152",
                Accent = "#606162",
            },
        };

        object? currentBefore = ThemeManager.Current;

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);

        Assert.Equal("#101112", resolved.Background);
        Assert.Equal("#606162", resolved.Accent);
        Assert.Equal("#101112", config.FallbackStyle!.Background);
        Assert.Equal("#606162", config.FallbackStyle.Accent);
        Assert.True(ReferenceEquals(currentBefore, ThemeManager.Current));
    }

    /// <summary>F2：缺省 ⇒ 中性可读默认值（不报错）。</summary>
    [Fact]
    public void 回退样式_缺省必须使用中性默认值()
    {
        ThemeRenderingConfig config = new();

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);

        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultPanelBackground, resolved.PanelBackground);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultBorder, resolved.Border);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultText, resolved.Text);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultMutedText, resolved.MutedText);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultAccent, resolved.Accent);
    }

    /// <summary>F3：非法值 ⇒ 该字段退回默认 + 诊断 <c>gpu_fallback_style_invalid</c>，其余字段不受影响。</summary>
    [Fact]
    public void 回退样式_非法值必须退回默认并产生诊断()
    {
        ThemeRenderingConfig config = new()
        {
            FallbackStyle = new ThemeFallbackStyle
            {
                Background = "这不是一个颜色",
                Text = "#ABCDEF",
            },
        };

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);

        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal("#ABCDEF", resolved.Text);
        Assert.True(
            resolved.Diagnostics.Any(d => d.Contains("gpu_fallback_style_invalid", StringComparison.Ordinal)),
            "非法值必须产出 gpu_fallback_style_invalid 诊断。");
    }

    /// <summary>F4：三份内置 GPU 主题都声明了该块且可解析。</summary>
    [Theory]
    [InlineData("CyberPunk_GPU.json")]
    [InlineData("LiquidGlass_GPU.json")]
    [InlineData("LiquidGlass_Toolbar_GPU.json")]
    public void 回退样式_三份内置GPU主题都声明该块且可解析(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "resources", "themes", fileName);
        Assert.True(File.Exists(path), "找不到内置 GPU 主题：" + path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement rendering = document.RootElement.GetProperty("rendering");

        ThemeRenderingConfig config = JsonSerializer.Deserialize<ThemeRenderingConfig>(
            rendering,
            ThemeManager.CreateSerializerOptions())!;

        Assert.NotNull(config.FallbackStyle);
        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);
        Assert.Empty(resolved.Diagnostics);

        // 原 GPU 外观不受影响：主题仍然可用，且仍是 GPU 路线。
        Assert.Empty(config.Validate());
        Assert.Equal(ThemeRenderingConfig.GpuBackend, config.Backend);
    }

    /// <summary>F5：编辑器克隆往返不得丢 <c>rendering.fallbackStyle</c> 块。</summary>
    [Fact]
    public void 回退样式_编辑器克隆往返不得丢块()
    {
        ThemeRenderingConfig source = new()
        {
            FallbackStyle = new ThemeFallbackStyle
            {
                Background = "#101112",
                PanelBackground = "#202122",
                Border = "#303132",
                Text = "#404142",
                MutedText = "#505152",
                Accent = "#606162",
            },
        };

        ThemeRenderingConfig clone = LiteMonitor.ThemeEditor.AppearanceThemeConfig.CloneRendering(source);

        Assert.NotNull(clone.FallbackStyle);
        Assert.Equal("#101112", clone.FallbackStyle!.Background);
        Assert.Equal("#202122", clone.FallbackStyle.PanelBackground);
        Assert.Equal("#303132", clone.FallbackStyle.Border);
        Assert.Equal("#404142", clone.FallbackStyle.Text);
        Assert.Equal("#505152", clone.FallbackStyle.MutedText);
        Assert.Equal("#606162", clone.FallbackStyle.Accent);
    }

    /// <summary>
    /// F6：<c>LegacyFallbackStyleResolver.Resolve</c> 的输出必须有**生产消费点**。
    /// <para>若无人消费，<c>rendering.fallbackStyle</c> 整块就是"只写不读"的死数据，
    /// 计划 §4.6「为三份 GPU 主题提供背景 / 分组底色 / 边框与必要前景色回退」没有落地。</para>
    /// </summary>
    [Fact]
    public void 护栏_回退样式解析结果必须有生产消费点()
    {
        MethodInfo? resolve = typeof(LegacyFallbackStyleResolver).GetMethod(
            "Resolve",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(resolve);

        List<string> sites = FindCallSites(resolve);
        Assert.True(
            sites.Count > 0,
            "计划 §4.6 要求 legacy 回退真正使用这组样式；生产代码里**找不到 Resolve 的调用点**" +
            "（rendering.fallbackStyle 整块为死数据）。");
    }

    /// <summary>
    /// F7（主审裁决 D-H）：<c>Accent</c> 的落点必须是 <c>ColorConfig.TextTitle</c>；
    /// **不得**占用 <c>ValueSafe/ValueWarn/ValueCrit</c>（告警语义色）、
    /// <c>BarLow/BarMid/BarHigh</c>（进度条分段色），也**不得**占用 <c>TextGroup</c>。
    /// </summary>
    [Fact]
    public void 裁决DH_accent必须落到标题色且不得占用告警与进度色()
    {
        Theme source = new();
        source.Color.Background = "#ORIGIN_BG";
        source.Color.GroupBackground = "#ORIGIN_GBG";
        source.Color.Border = "#ORIGIN_BORDER";
        source.Color.TextPrimary = "#ORIGIN_PRIMARY";
        source.Color.TextSecondary = "#ORIGIN_SECONDARY";
        source.Color.TextTitle = "#ORIGIN_TITLE";
        source.Color.TextGroup = "#ORIGIN_GROUP";
        source.Color.ValueSafe = "#ORIGIN_SAFE";
        source.Color.ValueWarn = "#ORIGIN_WARN";
        source.Color.ValueCrit = "#ORIGIN_CRIT";
        source.Color.BarLow = "#ORIGIN_BARLOW";
        source.Color.BarMid = "#ORIGIN_BARMID";
        source.Color.BarHigh = "#ORIGIN_BARHIGH";

        LegacyFallbackStyle style = new(
            "#FBG",
            "#FPANEL",
            "#FBORDER",
            "#FTEXT",
            "#FMUTED",
            "#FACCENT",
            Array.Empty<string>());

        Theme derived = LegacyFallbackStyleBridge
            .BuildDerivedTheme(source, style);

        // ★ 裁决 D-H：accent → TextTitle。
        Assert.Equal("#FACCENT", derived.Color.TextTitle);

        // ★ 不得占用告警语义色。
        Assert.Equal("#ORIGIN_SAFE", derived.Color.ValueSafe);
        Assert.Equal("#ORIGIN_WARN", derived.Color.ValueWarn);
        Assert.Equal("#ORIGIN_CRIT", derived.Color.ValueCrit);

        // ★ 不得占用进度条分段色。
        Assert.Equal("#ORIGIN_BARLOW", derived.Color.BarLow);
        Assert.Equal("#ORIGIN_BARMID", derived.Color.BarMid);
        Assert.Equal("#ORIGIN_BARHIGH", derived.Color.BarHigh);

        // ★ 不得占用 TextGroup（与 MutedText 同源会塌陷层级）。
        Assert.Equal("#ORIGIN_GROUP", derived.Color.TextGroup);

        // 其余落点与合同 §4.6 一致。
        Assert.Equal("#FBG", derived.Color.Background);
        Assert.Equal("#FPANEL", derived.Color.GroupBackground);
        Assert.Equal("#FBORDER", derived.Color.Border);
        Assert.Equal("#FTEXT", derived.Color.TextPrimary);
        Assert.Equal("#FMUTED", derived.Color.TextSecondary);

        // ★ 源主题不被修改；派生副本是独立对象。
        Assert.Equal("#ORIGIN_TITLE", source.Color.TextTitle);
        Assert.Equal("#ORIGIN_BG", source.Color.Background);
        Assert.NotSame(source, derived);
        Assert.NotSame(source.Color, derived.Color);
    }

    /// <summary>F8：派生主题**不得**改 <c>ThemeManager.Current</c>，也不得写盘。</summary>
    [Fact]
    public void 回退样式_派生主题不得改全局主题也不得写盘()
    {
        Theme source = new();
        LegacyFallbackStyle style = new(
            "#FBG", "#FPANEL", "#FBORDER", "#FTEXT", "#FMUTED", "#FACCENT",
            Array.Empty<string>());

        object? currentBefore = ThemeManager.Current;
        string titleBefore = ThemeManager.Current.Color.TextTitle;

        Theme derived = LegacyFallbackStyleBridge
            .BuildDerivedTheme(source, style);

        Assert.True(ReferenceEquals(currentBefore, ThemeManager.Current));
        Assert.Equal(titleBefore, ThemeManager.Current.Color.TextTitle);
        Assert.NotSame(ThemeManager.Current, derived);

        // 不写盘：派生过程不得触碰任何主题文件。
        string themesDirectory = Path.Combine(AppContext.BaseDirectory, "resources", "themes");
        DateTime before = DateTime.UtcNow.AddMinutes(-1);
        foreach (string file in Directory.GetFiles(themesDirectory, "*.json"))
        {
            Assert.True(File.GetLastWriteTimeUtc(file) <= before,
                $"派生回退样式不得写盘，但 {file} 的修改时间发生了变化。");
        }
    }
    // ====================================================================
    // G. owner A 接线（合同 §10 GPU-005 / 裁决 D6）
    // ====================================================================

    /// <summary>
    /// G1：<c>AbandonPreservingPending()</c> 只注销尝试 —— 刻意**不删** pending 标记文件、
    /// **不复位** <c>_attemptWritten</c>。
    /// </summary>
    [Fact]
    public void pending_放弃尝试必须保留标记且不复位已写标志()
    {
        string directory = TemporaryDirectory("verifier-g1");
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));
            Assert.NotNull(attempt);

            string pendingPath = SinglePendingFile(directory);
            string before = File.ReadAllText(pendingPath);

            attempt!.AbandonPreservingPending();

            // 1. pending 标记文件**仍在**。
            Assert.True(File.Exists(pendingPath), "放弃尝试不得删除 pending 标记文件。");
            Assert.Equal(before, File.ReadAllText(pendingPath));

            // 2. 再次开尝试时**不得**重写标记（_attemptWritten 未被复位）。
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? second, out _, out _));
            Assert.NotNull(second);
            Assert.Equal(before, File.ReadAllText(pendingPath));

            // 3. 下次进程进入 conservative。
            GpuStartupGuard nextProcess = new(directory, @"C:\apps\A");
            Assert.True(nextProcess.ConservativeForThisProcess);
            Assert.Equal(
                "gpu_startup_guard_previous_attempt_unclean",
                nextProcess.StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>G2：放弃是幂等的；终结后不可再标记成功。</summary>
    [Fact]
    public void pending_放弃必须幂等且终结后不可再标记成功()
    {
        string directory = TemporaryDirectory("verifier-g2");
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));

            attempt!.AbandonPreservingPending();
            attempt.AbandonPreservingPending();   // 幂等：不得抛异常

            Assert.Throws<ObjectDisposedException>(() => attempt.MarkFirstSuccessfulFrame());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// G3：停止流程**不干净**时 pending 必须保留；**干净**时 pending 必须被清除。
    /// <para>直接驱动宿主私有的 <c>ReleaseStartupAttempt(bool stopClean)</c>，避免依赖真实 DXGI。</para>
    /// </summary>
    [Fact]
    public void pending_停止不干净时保留干净时清除()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory("verifier-g3");
            try
            {
                MethodInfo release = typeof(GpuPresentationHostController).GetMethod(
                    "ReleaseStartupAttempt",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.NotNull(release);
                FieldInfo attemptField = typeof(GpuPresentationHostController).GetField(
                    "_startupAttempt",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.NotNull(attemptField);

                // ── 不干净：pending 必须保留 ──
                GpuStartupGuard dirtyGuard = new(directory, @"C:\apps\Dirty");
                Assert.True(dirtyGuard.TryBeginAttempt(out GpuStartupAttempt? dirtyAttempt, out _, out _));
                string dirtyPending = SinglePendingFile(directory);

                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: dirtyGuard);
                attemptField.SetValue(host, dirtyAttempt);
                release.Invoke(host, [false]);

                Assert.True(File.Exists(dirtyPending), "停止流程不干净时 pending 必须保留。");
                Assert.True(new GpuStartupGuard(directory, @"C:\apps\Dirty").ConservativeForThisProcess);

                // ── 干净：pending 必须被清除 ──
                GpuStartupGuard cleanGuard = new(directory, @"C:\apps\Clean");
                Assert.True(cleanGuard.TryBeginAttempt(out GpuStartupAttempt? cleanAttempt, out _, out _));
                string cleanPending = SinglePendingFile(directory);

                using Form form2 = new();
                using GpuPresentationHostController host2 = new(
                    form2,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: cleanGuard);
                attemptField.SetValue(host2, cleanAttempt);
                release.Invoke(host2, [true]);

                Assert.False(File.Exists(cleanPending), "停止流程干净时 pending 必须被清除。");
                Assert.False(new GpuStartupGuard(directory, @"C:\apps\Clean").ConservativeForThisProcess);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ====================================================================
    // H. 公开面与既有契约常量
    // ====================================================================

    /// <summary>H2：插件沙箱的 6 个契约常量必须一个都没改。</summary>
    [Fact]
    public void 护栏_插件沙箱六个契约常量逐字未改()
    {
        Type limits = EnumerateTypes(typeof(UIController).Assembly)
            .First(t => string.Equals(t.Name, "PluginSandboxLimits", StringComparison.Ordinal));
        Assert.NotNull(limits);

        Assert.Equal(16, ReadConstant<int>(limits, "MaxStepsPerExecution"));
        Assert.Equal(64, ReadConstant<int>(limits, "MaxTargetsPerStep"));
        Assert.Equal(30000, ReadConstant<int>(limits, "MaxExecutionDurationMs"));
        Assert.Equal(8, ReadConstant<int>(limits, "MaxConcurrentRequests"));
        Assert.Equal(2 * 1024 * 1024, ReadConstant<int>(limits, "MaxResponseBytes"));
        Assert.Equal(500, ReadConstant<int>(limits, "MaxRegexMatchTimeoutMs"));
    }

    /// <summary>
    /// H3：合同 §6.4 的重试预算 6 次与退避表 250 / 500 / 1000 / 2000 ms 必须在代码里**逐字可查**。
    /// </summary>
    [Fact]
    public void 护栏_重试预算与退避表必须逐字可查()
    {
        Assert.Equal(6, CapturePolicyCleanupPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, CapturePolicyCleanupPolicy.BackoffAfterAttempt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), CapturePolicyCleanupPolicy.BackoffAfterAttempt(2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), CapturePolicyCleanupPolicy.BackoffAfterAttempt(3));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), CapturePolicyCleanupPolicy.BackoffAfterAttempt(4));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), CapturePolicyCleanupPolicy.BackoffAfterAttempt(5));

        Assert.Equal(6, CapturePolicyCleanupLedger.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), CapturePolicyCleanupLedger.BackoffAfterAttempt(2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), CapturePolicyCleanupLedger.BackoffAfterAttempt(3));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), CapturePolicyCleanupLedger.BackoffAfterAttempt(4));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), CapturePolicyCleanupLedger.BackoffAfterAttempt(5));

        // 逐字可查：两份实现里都必须出现 250 / 500 / 1000 / 2000 这四个数字字面量。
        string root = FindRepoRoot();
        foreach (string relative in new[]
        {
            "src/UI/CapturePolicyCleanupLedger.cs",
            "src/Rendering/Presentation/CapturePolicyCleanupPolicy.cs",
        })
        {
            string text = File.ReadAllText(Path.Combine(root, relative));
            foreach (string literal in new[] { "250", "500", "1000", "2000" })
            {
                Assert.True(
                    text.Contains(literal, StringComparison.Ordinal),
                    $"{relative} 里查不到退避字面量 {literal}。");
            }
        }
    }

    /// <summary>
    /// H4：合同 §13 自检 —— 期望路由仍由**唯一**纯函数 <c>RenderingRoutePolicy.Decide</c> 产出，
    /// 不得出现第二个决策函数或第二个句柄代次计数器。
    /// </summary>
    [Fact]
    public void 护栏_期望路由必须仍由唯一纯函数产出()
    {
        List<string> decideCallers = FindCallSites(
            typeof(RenderingRoutePolicy).GetMethod("Decide", BindingFlags.Public | BindingFlags.Static));
        Assert.True(decideCallers.Count > 0, "RenderingRoutePolicy.Decide 必须有生产调用点。");

        // 全仓不得出现第二个"决定期望路由"的函数（同名 Decide 只能有一个）。
        List<string> decisions = EnumerateTypes(typeof(UIController).Assembly)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == "Decide" && m.ReturnType == typeof(RenderingRouteDecision))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name)
            .ToList();
        Assert.True(decisions.Count == 1,
            "合同 §13：不得新建第二个决策函数；实际有：" + string.Join(", ", decisions));
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static CapturePolicyCleanupLedger NewLedger()
    {
        long generation = 5;
        return new CapturePolicyCleanupLedger(
            () => generation,
            _ => true,
            _ => new CapturePolicyRestoreResult(false, "e", "m"));
    }

    private static int ReadConstant<T>(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        object? value = field!.GetRawConstantValue();
        Assert.NotNull(value);
        return (int)Convert.ChangeType(value, typeof(int));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LiteMonitor.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（LiteMonitor.sln）。");
    }

    private static string TemporaryDirectory(string tag)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SinglePendingFile(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.pending");
        Assert.Single(files);
        return files[0];
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        internal FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta) => _now += delta;
    }

    private static RenderingRouteRequest CreateGpuRouteRequest() =>
        new(
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = ThemeRenderingConfig.LiquidGlassMaterial,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                },
            },
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false);

    /// <summary>可记录的窗口适配器（用于制造 affinity 恢复失败）。</summary>
    private sealed class RecordingWindowAdapter : IPresentationWindowAdapter
    {
        private readonly uint _originalAffinity;
        private int _failRestoreWrites;

        internal RecordingWindowAdapter(uint originalAffinity = 0, int failRestoreWrites = 0)
        {
            _originalAffinity = originalAffinity;
            _failRestoreWrites = failRestoreWrites;
        }

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            affinity = _originalAffinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }

        public bool TrySetDisplayAffinity(
            IntPtr windowHandle,
            uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            // 只让"恢复原始 affinity"的写入失败（与生产语义一致：策略施加本身必须成功）。
            if (affinity == _originalAffinity && _failRestoreWrites > 0)
            {
                _failRestoreWrites--;
                errorCode = "presentation_capture_policy_restore_failed";
                errorMessage = "simulated restore failure";
                return false;
            }

            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}