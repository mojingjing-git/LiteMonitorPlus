using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-065 / R01 · 主窗体构造期空引用（启动阻断）**
/// <para>复核者自写用例。判据与实现者写的 <c>MainFormStartupOrderTests</c> **不同**：
/// 实现者只查"第一个 stfld 早于第一个 newobj"，本文件查的是</para>
/// <list type="bullet">
/// <item><b>每一条</b> <c>newobj UIController</c> 都要有**先于它**的 <c>stfld _winHelper</c>（不止第一条）；</item>
/// <item>字段是 <c>readonly</c> ⇒ 构造顺序就是唯一顺序；</item>
/// <item>构造函数里调用的**任何**"会读 _winHelper 的 MainForm 方法"都必须在赋值之后；</item>
/// <item>危险链本身仍然存在（UIController 构造链确实会调到 MainForm.IsWindowLayered）——
///       否则"顺序修复"就是空转；</item>
/// <item>窗口助手的构造函数不依赖 UIController（说明把赋值前移是安全的）。</item>
/// </list>
/// </summary>
public sealed class VerifierLm065Tests
{
    private static FieldInfo WinHelperField =>
        typeof(MainForm).GetField("_winHelper", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainForm._winHelper 字段不存在 —— 用例前提被破坏。");

    private static ConstructorInfo[] MainFormCtors =>
        typeof(MainForm).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static bool IsSameMethod(MethodBase? a, MethodBase? b) =>
        a != null && b != null &&
        a.DeclaringType == b.DeclaringType &&
        a.MetadataToken == b.MetadataToken;

    private static bool IsWinHelperWrite(VerifierIlInstruction i) =>
        i.IsFieldWrite &&
        i.Field != null &&
        i.Field.DeclaringType == typeof(MainForm) &&
        i.Field.Name == "_winHelper";

    private static bool IsWinHelperRead(VerifierIlInstruction i) =>
        i.IsFieldRead &&
        i.Field != null &&
        i.Field.DeclaringType == typeof(MainForm) &&
        i.Field.Name == "_winHelper";

    // ---------------------------------------------------------------- V1

    /// <summary>
    /// V1（**载荷用例**）：主窗体每一个构造函数里，**每一条** <c>new UIController</c>
    /// 都必须有一条**先于它**的 <c>stfld _winHelper</c>。
    /// </summary>
    [Fact]
    public void V1_每条UIController构造之前都必须已赋值窗口助手()
    {
        ConstructorInfo[] ctors = MainFormCtors;
        Assert.NotEmpty(ctors);

        int totalNewObj = 0;
        int totalWrites = 0;

        foreach (ConstructorInfo ctor in ctors)
        {
            List<VerifierIlInstruction> il = VerifierIl.Decode(VerifierIl.EffectiveBody(ctor));

            int[] writes = il.Where(IsWinHelperWrite).Select(i => i.Offset).ToArray();
            int[] news = il.Where(i => i.IsNewObj && i.Constructor?.DeclaringType == typeof(UIController))
                           .Select(i => i.Offset).ToArray();

            totalNewObj += news.Length;
            totalWrites += writes.Length;

            foreach (int newOffset in news)
            {
                Assert.True(
                    writes.Any(w => w < newOffset),
                    "初始化顺序错误：MainForm." + ctor.Name + "() 在 IL 偏移 " + newOffset +
                    " 构造 UIController，但 `_winHelper` 的赋值（偏移 " +
                    string.Join(",", writes.Select(w => w.ToString()).ToArray()) +
                    "）没有全部发生在它之前 ⇒ UIController 构造期读 _winHelper 就是**启动即崩**的空引用（R01）。");
            }
        }

        // 非空断言：确保本用例真的在检查东西，而不是"没有 newobj 所以什么也没查"
        Assert.True(totalNewObj > 0, "主窗体构造函数里一条 `new UIController` 都没有 —— 用例前提被破坏。");
        Assert.True(totalWrites > 0, "主窗体构造函数里一条 `stfld _winHelper` 都没有 —— 用例前提被破坏。");
    }

    // ---------------------------------------------------------------- V2

    /// <summary>
    /// V2：<c>_winHelper</c> 必须是 <c>readonly</c> 字段 ——
    /// 那样"构造顺序"就是唯一的顺序，不存在"别处偷偷补赋值"的可能。
    /// </summary>
    [Fact]
    public void V2_窗口助手字段是只读的_所以构造顺序是唯一顺序()
    {
        Assert.True(
            WinHelperField.IsInitOnly,
            "MainForm._winHelper 必须是 readonly —— 否则赋值顺序之外的路径也能改变它，本用例的结论不成立。");
    }

    // ---------------------------------------------------------------- V3

    /// <summary>
    /// V3：主窗体构造函数里，调用**任何**"会读 _winHelper 的 MainForm 方法"时，
    /// 赋值必须已经发生。
    /// </summary>
    [Fact]
    public void V3_构造函数调用读窗口助手的方法时赋值必须已完成()
    {
        var readers = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodInfo m in typeof(MainForm).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.IsAbstract) continue;
            if (VerifierIl.ReadsField(m, WinHelperField)) readers.Add(m.Name);
        }

        Assert.NotEmpty(readers);

        foreach (ConstructorInfo ctor in MainFormCtors)
        {
            List<VerifierIlInstruction> il = VerifierIl.Decode(VerifierIl.EffectiveBody(ctor));

            int firstWrite = il.Where(IsWinHelperWrite)
                               .Select(i => i.Offset)
                               .DefaultIfEmpty(int.MaxValue)
                               .Min();

            foreach (VerifierIlInstruction call in il.Where(i =>
                         i.IsCallOrCallVirt &&
                         i.Method?.DeclaringType == typeof(MainForm) &&
                         readers.Contains(i.Method.Name)))
            {
                Assert.True(
                    call.Offset > firstWrite,
                    "MainForm." + ctor.Name + "() 在 IL 偏移 " + call.Offset +
                    " 调用了会读 `_winHelper` 的 " + call.Method!.Name +
                    "()，但赋值发生在偏移 " +
                    (firstWrite == int.MaxValue ? "(没有)" : firstWrite.ToString()) + " ⇒ 空引用（R01）。");
            }
        }
    }

    // ---------------------------------------------------------------- V4

    /// <summary>
    /// V4：构造函数里**直接**读 _winHelper 的地方，必须都在赋值之后。
    /// </summary>
    [Fact]
    public void V4_构造函数直接读窗口助手时必须已赋值()
    {
        foreach (ConstructorInfo ctor in MainFormCtors)
        {
            List<VerifierIlInstruction> il = VerifierIl.Decode(VerifierIl.EffectiveBody(ctor));

            int firstWrite = il.Where(IsWinHelperWrite)
                               .Select(i => i.Offset)
                               .DefaultIfEmpty(int.MaxValue)
                               .Min();

            foreach (VerifierIlInstruction read in il.Where(IsWinHelperRead))
            {
                Assert.True(
                    firstWrite != int.MaxValue && read.Offset > firstWrite,
                    "MainForm." + ctor.Name + "() 在 IL 偏移 " + read.Offset +
                    " 直接读 `_winHelper`，但此前没有赋值（R01）。");
            }
        }
    }

    // ---------------------------------------------------------------- V5

    /// <summary>
    /// V5：危险链**仍然存在** —— UIController 的构造函数沿调用链确实会调到
    /// <c>MainForm.IsWindowLayered()</c>，而后者确实解引用 <c>_winHelper</c>。
    /// <para>这条保证"顺序修复"不是空转：如果哪天有人把这条链改掉了，
    /// 本用例会红，提醒复核者这条断言的前提需要重写。</para>
    /// </summary>
    [Fact]
    public void V5_UIController构造链确实会读窗口助手_顺序修复不是空转()
    {
        MethodInfo isLayered = typeof(MainForm).GetMethod(
            "IsWindowLayered",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        Assert.True(isLayered != null, "MainForm.IsWindowLayered() 不存在 —— 用例前提被破坏。");
        Assert.True(
            VerifierIl.ReadsField(isLayered!, WinHelperField),
            "MainForm.IsWindowLayered() 必须真的解引用 `_winHelper`，否则危险链不存在。");

        ConstructorInfo[] uiCtors = typeof(UIController).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(uiCtors);

        bool reached = false;
        foreach (ConstructorInfo ctor in uiCtors)
        {
            HashSet<MethodBase> reachable = VerifierIl.Reachable(ctor, 8);
            if (reachable.Any(m => IsSameMethod(m, isLayered)))
            {
                reached = true;
                break;
            }
        }

        Assert.True(
            reached,
            "UIController 的构造函数沿调用链**没有**到达 MainForm.IsWindowLayered() —— " +
            "要么危险链不存在，要么已经改道；R01 的「顺序」约束需要重新评估。");
    }

    // ---------------------------------------------------------------- V6

    /// <summary>
    /// V6：窗口助手的构造函数**不依赖 UIController** ⇒ 把赋值前移是安全的
    /// （不会引入新的构造期依赖环）。
    /// </summary>
    [Fact]
    public void V6_窗口助手构造函数不依赖UIController_所以前移赋值是安全的()
    {
        ConstructorInfo[] ctors = typeof(MainFormWinHelper).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(ctors);

        foreach (ConstructorInfo ctor in ctors)
        {
            List<VerifierIlInstruction> il = VerifierIl.Decode(VerifierIl.EffectiveBody(ctor));

            bool touchesUi = il.Any(i =>
                i.IsFieldRead &&
                i.Field != null &&
                i.Field.DeclaringType == typeof(MainForm) &&
                i.Field.Name == "_ui");

            Assert.False(
                touchesUi,
                "MainFormWinHelper 的构造函数读取了 MainForm._ui ⇒ 把 `_winHelper` 赋值前移到 " +
                "`new UIController(...)` 之前会引入新的构造期依赖问题。");
        }
    }
}