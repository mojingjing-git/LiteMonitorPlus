using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【R01 · 启动阻断护栏】
/// <para><b>缺陷：</b><c>MainForm</c> 构造函数里 <c>_winHelper</c>（窗口助手）的赋值**晚于**
/// <c>new UIController(...)</c>，而 <c>UIController</c> 构造函数会走
/// <c>ApplyTheme → ApplyGpuRenderingRoute → MainForm.IsWindowLayered()</c>，
/// 该转发方法直接解引用 <c>_winHelper</c> ⇒ <b>启动即抛 NullReferenceException</b>。</para>
/// <para><b>实测复现：</b>运行 <c>tools/GpuHostLifecycleProbe</c> 的 <c>mainform</c> 场景，
/// 产物 <c>mainform-construction-error.log</c> 记录
/// <c>System.NullReferenceException at LiteMonitor.MainForm.IsWindowLayered()</c>，
/// 堆栈为 <c>MainForm.IsWindowLayered → UIController.ApplyGpuRenderingRoute →
/// UIController.ApplyTheme → UIController..ctor → MainForm..ctor</c>。
/// 该异常与主题、GPU 开关、安全模式<strong>全部无关</strong>（该调用无条件求值，
/// 且发生在协调器自身的 catch 之前）。</para>
/// <para><b>本护栏守四件事：</b></para>
/// <list type="number">
/// <item>窗口助手的赋值必须**早于** UIController 的构造（顺序）；</item>
/// <item>构造函数里每一次读取 <c>_winHelper</c> 之前都必须已经赋值过；</item>
/// <item>分层窗口查询必须**转发**给窗口助手 —— 禁止"直接返回 false"式的假修
///       （那会静默破坏 GPU-003 的分层窗口判定）；</item>
/// <item>窗口助手的分层查询必须**实时**读句柄 —— 禁止把 null/句柄未就绪
///       当成"非 layered"永久缓存。</item>
/// </list>
/// </summary>
public sealed class MainFormStartupOrderTests
{
    // ---------------------------------------------------------------- 顺序

    [Fact]
    public void 护栏_窗口助手必须在UIController构造之前赋值()
    {
        IlInspector.Disassembly body = IlInspector.Disassemble(MainFormCtor);

        int assignOffset = FindStfldOffset(body, "_winHelper");
        int createOffset = FindNewobjOffset(body, typeof(UIController));

        Assert.True(
            assignOffset >= 0,
            "MainForm 构造函数里找不到 `_winHelper` 的赋值（stfld）—— 护栏前提被破坏，请同步更新本用例。");
        Assert.True(
            createOffset >= 0,
            "MainForm 构造函数里找不到 `new UIController(...)`（newobj）—— 护栏前提被破坏，请同步更新本用例。");

        Assert.True(
            assignOffset < createOffset,
            "初始化顺序错误：`_winHelper` 的赋值在 IL 偏移 " + assignOffset +
            "，而 `new UIController(...)` 在偏移 " + createOffset +
            "。UIController 构造函数会经 ApplyTheme → ApplyGpuRenderingRoute 读 `_winHelper`" +
            "（MainForm.IsWindowLayered），晚赋值就是**启动即崩**的空引用（R01）。");
    }

    [Fact]
    public void 护栏_构造函数读取窗口助手前必须已赋值()
    {
        IlInspector.Disassembly body = IlInspector.Disassemble(MainFormCtor);

        int assignedAt = -1;
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Stfld && IsWinHelperField(instruction.Field))
            {
                assignedAt = instruction.Offset;
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldfld && IsWinHelperField(instruction.Field))
            {
                Assert.True(
                    assignedAt >= 0 && assignedAt < instruction.Offset,
                    "MainForm 构造函数在 IL 偏移 " + instruction.Offset + " 读取 `_winHelper`，" +
                    "但此前没有对它的赋值（R01）。");
            }
        }
    }

    // ---------------------------------------------------------------- 转发

    [Fact]
    public void 护栏_分层窗口查询必须转发给窗口助手()
    {
        MethodInfo method = typeof(MainForm).GetMethod(
            "IsWindowLayered",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        Assert.True(method != null, "MainForm.IsWindowLayered() 不存在 —— 护栏前提被破坏。");

        IlInspector.Disassembly body = IlInspector.Disassemble(method);

        bool forwarded = body.Instructions.Any(instruction =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Target is MethodInfo called &&
            called.DeclaringType == typeof(MainFormWinHelper) &&
            called.Name == "IsWindowLayered");

        Assert.True(
            forwarded,
            "MainForm.IsWindowLayered() 必须转发给 MainFormWinHelper.IsWindowLayered()。" +
            "改成直接返回常量虽然能绕开空引用，却会静默让分层窗口判定恒为 false，" +
            "破坏 GPU-003（WDA 与 WS_EX_LAYERED 互斥）的真实读数。");
    }

    [Fact]
    public void 护栏_窗口助手的分层查询必须实时读句柄()
    {
        MethodInfo method = typeof(MainFormWinHelper).GetMethod(
            "IsWindowLayered",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        Assert.True(method != null, "MainFormWinHelper.IsWindowLayered() 不存在 —— 护栏前提被破坏。");

        IlInspector.Disassembly body = IlInspector.Disassemble(method);

        bool readsHandle = body.Instructions.Any(instruction =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Target is MethodInfo called &&
            called.Name == "get_IsHandleCreated");

        Assert.True(
            readsHandle,
            "MainFormWinHelper.IsWindowLayered() 必须实时检查 IsHandleCreated。" +
            "把'助手未就绪/句柄未创建'当成'非 layered'永久缓存，会让 GPU-003 读到过期的分层状态。");
    }

    // ---------------------------------------------------------------- 辅助

    private static ConstructorInfo MainFormCtor
    {
        get
        {
            ConstructorInfo? ctor = typeof(MainForm).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            return ctor ?? throw new InvalidOperationException("MainForm 无参构造函数不存在。");
        }
    }

    private static bool IsWinHelperField(FieldInfo field) =>
        field != null &&
        field.DeclaringType == typeof(MainForm) &&
        field.Name == "_winHelper";

    private static int FindStfldOffset(IlInspector.Disassembly body, string name)
    {
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Stfld &&
                instruction.Field != null &&
                instruction.Field.DeclaringType == typeof(MainForm) &&
                instruction.Field.Name == name)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }

    private static int FindNewobjOffset(IlInspector.Disassembly body, Type type)
    {
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Newobj &&
                instruction.Target is ConstructorInfo ctor &&
                ctor.DeclaringType == type)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }
}
