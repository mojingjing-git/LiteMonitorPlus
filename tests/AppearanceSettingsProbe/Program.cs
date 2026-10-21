using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.AppearanceSettingsProbe
{
    /// <summary>
    /// W3「设置页意外回写」缺陷包的防回退探针入口。
    ///
    /// 覆盖两组门槛：
    ///   · 运行时 —— 回写抑制作用域的语义；以及设置页的立即绑定在抑制作用域内
    ///     确实**不**回写、在作用域外确实回写（证明回写通道是通的，而不是根本没挂上）。
    ///   · 源码级 —— 8 个 Add* 的抑制守卫、3 个刷新体的作用域包裹、MonitorPage 的
    ///     _linkHorizontalEdited 三元、SystemHardwarPage 的 6 个 AddComboPair、
    ///     LiteThresholdRow 的守卫 —— 防止将来有人改坏时无声退化。
    ///
    /// 用法：
    ///   AppearanceSettingsProbe [--repo-root &lt;仓库根&gt;] [--out &lt;证据目录&gt;]
    ///
    /// 退出码：0 = 全部用例通过；1 = 有用例失败；2 = 探针自身异常。
    /// </summary>
    internal static class Program
    {
        private static readonly List<(string Name, bool Pass, string Detail)> Results = new();

        /// <summary>探针用的设置页：只需要基类的抑制作用域与刷新机制。</summary>
        private sealed class ProbePage : SettingsPageBase
        {
            public void ApplyContext(Settings cfg) => SetContext(cfg, null!, null!);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // ★ 控制台编码只是"看起来好看"：stdout 被重定向（没有控制台）时，
            //   设置 Console.OutputEncoding 会直接抛异常并让整个探针崩掉 —— 所以这里必须吞掉。
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 无控制台（输出被重定向）时忽略
            }

            string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");
            string? repoRootOverride = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--repo-root")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("参数 --repo-root 缺少路径。");
                        PrintUsage();
                        return 2;
                    }
                    repoRootOverride = args[i + 1];
                    i++;
                }
                else if (args[i] == "--out")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("参数 --out 缺少路径。");
                        PrintUsage();
                        return 2;
                    }
                    outDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    PrintUsage();
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine("未知参数：" + args[i]);
                    PrintUsage();
                    return 2;
                }
            }

            try
            {
                RunRuntimeChecks();
                RunSourceLevelChecks(repoRootOverride);
            }
            catch (Exception ex)
            {
                Add("探针自身异常", false, ex.ToString());
            }

            try
            {
                WriteEvidence(outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("写证据失败：" + ex.Message);
            }

            int failed = Results.Count(r => !r.Pass);
            Console.WriteLine();
            foreach (var (name, pass, detail) in Results)
            {
                Console.WriteLine((pass ? "  [PASS] " : "  [FAIL] ") + name + (detail.Length > 0 ? " — " + detail : ""));
            }
            Console.WriteLine();
            Console.WriteLine("合计：" + Results.Count + " 个用例，通过 " + (Results.Count - failed) + " 个，失败 " + failed + " 个。");
            Console.WriteLine("证据：" + Path.Combine(outDir, "appearance-settings-probe.json"));
            return failed == 0 ? 0 : 1;
        }

        // ==================================================================
        //  运行时断言
        // ==================================================================

        private static void RunRuntimeChecks()
        {
            // ---- 抑制作用域的语义 ----
            var page = new ProbePage();
            Add("作用域外未被抑制", !page.IsWriteBackSuppressed, "IsWriteBackSuppressed 应为 false");

            IDisposable? outer = page.BeginSuppressWriteBack();
            Add("作用域内已抑制", page.IsWriteBackSuppressed, "进入作用域后应为 true");

            IDisposable? inner = page.BeginSuppressWriteBack();
            Add("嵌套作用域内已抑制", page.IsWriteBackSuppressed, "嵌套后应为 true");
            inner?.Dispose();
            Add("内层释放后仍在抑制", page.IsWriteBackSuppressed, "外层还在，应保持 true");
            outer?.Dispose();
            Add("最外层释放后恢复", !page.IsWriteBackSuppressed, "全部释放后应回到 false");

            // 重复释放不应把深度带成负数
            var scope = page.BeginSuppressWriteBack();
            scope.Dispose();
            scope.Dispose();
            Add("作用域重复释放安全", !page.IsWriteBackSuppressed, "重复 Dispose 后应仍为 false");

            // ---- AddCombo：抑制内程序化改 ⇒ 不回写 ----
            string draftA = "keep";
            var group = new LiteSettingsGroup("probe");
            var cmbA = group.AddCombo(page, "Menu.Probe", new[] { "A", "B" }, () => draftA, v => draftA = v);
            Add("AddCombo 构造本身不回写", draftA == "keep", "构造期的选中项赋值早于绑定，不应回写；实际=" + draftA);

            using (page.BeginSuppressWriteBack())
            {
                cmbA.SelectedIndex = 1;
            }
            Add("AddCombo 抑制内改选中项不回写", draftA == "keep", "程序化改动不得写回；实际=" + draftA);

            // ---- AddCombo：作用域外用户改 ⇒ 确实回写（证明通道是通的）----
            string draftB = "keep";
            var cmbB = group.AddCombo(page, "Menu.Probe", new[] { "A", "B" }, () => draftB, v => draftB = v);
            cmbB.SelectedIndex = 1;
            Add("AddCombo 作用域外改选中项会回写", draftB == "B", "回写通道必须仍然可用；实际=" + draftB);

            // ---- AddCombo：选中项为 -1 时不写空串 ----
            string draftC = "keep";
            var cmbC = group.AddCombo(page, "Menu.Probe", new[] { "A", "B" }, () => draftC, v => draftC = v);
            cmbC.SelectedIndex = -1;
            Add("AddCombo 选中项为空时不写空串", draftC == "keep", "清空/重填期间不得把空串写进配置；实际=" + draftC);

            // ---- AddToggle ----
            bool boolD = false;
            var chkD = group.AddToggle(page, "Menu.Probe", () => boolD, v => boolD = v);
            using (page.BeginSuppressWriteBack())
            {
                chkD.Checked = true;
            }
            Add("AddToggle 抑制内改不回写", !boolD, "程序化改动不得写回；实际=" + boolD);

            bool boolE = false;
            var chkE = group.AddToggle(page, "Menu.Probe", () => boolE, v => boolE = v);
            chkE.Checked = true;
            Add("AddToggle 作用域外改会回写", boolE, "回写通道必须仍然可用；实际=" + boolE);

            // ---- AddComboPair ----
            string pairF = "orig";
            var cmbF = group.AddComboPair(page, "Probe",
                new List<dynamic> { new { Label = "自动", Value = "" }, new { Label = "设备A", Value = "devA" } },
                () => pairF, v => pairF = v);
            using (page.BeginSuppressWriteBack())
            {
                cmbF.SelectValue("devA");
            }
            Add("AddComboPair 抑制内改不回写", pairF == "orig", "程序化改动不得写回；实际=" + pairF);

            string pairG = "orig";
            var cmbG = group.AddComboPair(page, "Probe",
                new List<dynamic> { new { Label = "自动", Value = "" }, new { Label = "设备A", Value = "devA" } },
                () => pairG, v => pairG = v);
            cmbG.SelectValue("devA");
            Add("AddComboPair 作用域外改会回写", pairG == "devA", "回写通道必须仍然可用；实际=" + pairG);

            // ---- AddInt / AddInput ----
            int intH = 5;
            var intInput = group.AddInt(page, "Menu.Probe", "MB", () => intH, v => intH = v);
            using (page.BeginSuppressWriteBack())
            {
                intInput.Inner.Text = "999";
            }
            Add("AddInt 抑制内改文本不回写", intH == 5, "程序化改动不得写回；实际=" + intH);

            string textI = "orig";
            var textInput = group.AddInput(page, "Menu.Probe", () => textI, v => textI = v);
            using (page.BeginSuppressWriteBack())
            {
                textInput.Inner.Text = "changed";
            }
            Add("AddInput 抑制内改文本不回写", textI == "orig", "程序化改动不得写回；实际=" + textI);

            // ---- 刷新体：原值不在候选里时不得被冲掉（W3-2 的核心场景）----
            var page2 = new ProbePage();
            var group2 = new LiteSettingsGroup("probe");
            string draftJ = "Z";   // 不在候选 ["A","B"] 里
            group2.AddCombo(page2, "Menu.Probe", new[] { "A", "B" }, () => draftJ, v => draftJ = v);
            page2.ApplyContext(new Settings());
            Add("SetContext 触发刷新体不改原值", draftJ == "Z", "刷新时选中项落到第一项是程序化改动；实际=" + draftJ);

            var page3 = new ProbePage();
            var group3 = new LiteSettingsGroup("probe");
            string draftK = "Z";
            group3.AddCombo(page3, "Menu.Probe", new[] { "A", "B" }, () => draftK, v => draftK = v);
            page3.ApplyContext(new Settings());
            page3.OnShow();
            Add("OnShow 刷新不改原值", draftK == "Z", "刷新时选中项落到第一项是程序化改动；实际=" + draftK);

            // ---- 负向对照：证明上面那些断言真的有鉴别力 ----
            //   若「没有抑制守卫」的绑定在抑制作用域内也会回写，
            //   那么「抑制内不回写」这条断言才不是恒真的摆设。
            var negCmb = new LiteComboBox();
            negCmb.Items.Add("A");
            negCmb.Items.Add("B");
            negCmb.SelectedIndex = 0;
            string negDraft = "keep";
            negCmb.Inner.SelectedIndexChanged += (s, e) => negDraft = negCmb.Text;   // ← 故意不加守卫
            using (page.BeginSuppressWriteBack())
            {
                negCmb.SelectedIndex = 1;
            }
            Add("负向对照：无守卫的绑定在抑制作用域内确实会回写", negDraft == "B",
                negDraft == "B"
                    ? "鉴别力成立：去掉守卫就会回写，上述断言能抓住回退"
                    : "鉴别力不成立：即使没有守卫也不回写，上面的断言没有意义；实际=" + negDraft);

            // 负向对照：非法输入经 ValueDouble 会变成 0 —— 这正是阈值被静默改成 0 的路径。
            var negInput = new LiteNumberInput("75", "MB", "probe", 140, System.Drawing.Color.Black);
            negInput.Inner.Text = "abc";   // 模拟粘贴（KeyPress 拦不住粘贴）
            Add("负向对照：非法输入经 ValueDouble 会变成 0", negInput.ValueDouble == 0.0,
                "这说明阈值行不能直接用 ValueDouble 回写，必须自己 TryParse 且失败不写；实际=" + negInput.ValueDouble);

            // ==============================================================
            //  严格解析：只接受「界面上手打得出来」的形式
            // ==============================================================
            string[] mustReject = { "+5", "1e5", "1,000", " 8 ", "NaN", "Infinity", "-", ".", "-.", "1.2.3", "85-", "" };
            var rejected = mustReject.Where(t => LiteNumberInput.TryParseStrict(t, out _)).ToList();
            Add("严格解析拒绝界面打不出来的形式", rejected.Count == 0,
                rejected.Count == 0
                    ? "含 \"+5\" / \"1e5\" / \"1,000\" / \" 8 \" / NaN / Infinity / \"-\" / \".\""
                    : "竟然接受了：" + string.Join(", ", rejected.Select(t => "\"" + t + "\"")));

            bool acceptsPlain = LiteNumberInput.TryParseStrict("85", out double v85) && v85 == 85.0
                && LiteNumberInput.TryParseStrict("-12.5", out double vNeg) && vNeg == -12.5
                && LiteNumberInput.TryParseStrict("0", out double vZero) && vZero == 0.0;
            Add("严格解析接受正常数字", acceptsPlain,
                "85 / -12.5 / 0 都必须能被解析；实际=" + acceptsPlain);

            // ==============================================================
            //  ★ 端到端复现本轮修掉的缺陷：
            //    往阈值框粘进 "." 再移开焦点 ⇒ 阈值**不能**被静默改成 0。
            //    旧实现的链条：Leave 把文本硬改成 "0" ⇒ 同步触发 TextChanged
            //                   ⇒ 立即绑定的回写分支看到 "0"（可解析）⇒ 把 0 写进配置。
            // ==============================================================
            var rowPage = new ProbePage();
            var rowRange = new ValueRange { Warn = 75, Crit = 90 };
            var row = new LiteThresholdRow(rowPage, "probe", "MB", rowRange);
            row.WarnInput.Inner.Text = ".";        // 模拟粘贴（KeyPress 拦不住粘贴）
            RaiseLeave(row.WarnInput.Inner);       // 移开焦点
            Add("阈值行遇非法粘贴离场后配置不变", rowRange.Warn == 75,
                "旧实现会把它改成 0（用户只是粘了个 \".\"）；实际 Warn=" + rowRange.Warn);
            Add("阈值行离场后显示回到旧值", row.WarnInput.Inner.Text == "75",
                "显示与配置必须一致；实际=\"" + row.WarnInput.Inner.Text + "\"");

            // ==============================================================
            //  ★ 不变量 Crit >= Warn（结构性）：一旦 Crit < Warn，warn 档不可达。
            // ==============================================================
            var invRange = new ValueRange { Warn = 60, Crit = 85 };
            var invRow = new LiteThresholdRow(new ProbePage(), "probe", "%", invRange);
            invRow.CritInput.Inner.Text = "40";     // 用户把严重值调到低于告警值
            Add("阈值行强制 Crit >= Warn", invRange.Crit >= invRange.Warn,
                "把严重值调到低于告警值时，告警档必须仍然可达；实际 Crit=" + invRange.Crit + " Warn=" + invRange.Warn);
            Add("阈值不变量调整有可见行内提示", !string.IsNullOrEmpty(invRow.HintText),
                "钳制/不变量调整不能静默改值，必须让用户看见；提示=\"" + (invRow.HintText ?? "(无)") + "\"");

            // ==============================================================
            //  ★ 钳制：Load（%）0~100；越界时拉回范围并给出提示
            // ==============================================================
            var clampRange = new ValueRange { Warn = 60, Crit = 85 };
            var clampRow = new LiteThresholdRow(new ProbePage(), "probe", "%", clampRange,
                clamp: raw => (Math.Min(100.0, Math.Max(0.0, raw)), (string?)null));
            clampRow.WarnInput.Inner.Text = "150";
            Add("阈值钳制把越界值拉回范围", clampRange.Warn == 100.0,
                "150 应被钳到 100；实际 Warn=" + clampRange.Warn);
            Add("阈值钳制后显示与配置一致", clampRow.WarnInput.Inner.Text == "100",
                "钳制后控件文本必须等于配置值；实际=\"" + clampRow.WarnInput.Inner.Text + "\"");
            Add("阈值钳制有可见行内提示", !string.IsNullOrEmpty(clampRow.HintText),
                "钳制必须让用户看见；提示=\"" + (clampRow.HintText ?? "(无)") + "\"");
        }

        // ==================================================================
        //  源码级门槛
        // ==================================================================

        private static void RunSourceLevelChecks(string? repoRootOverride)
        {
            string root = repoRootOverride ?? FindRepoRoot();
            if (root.Length == 0)
            {
                Add("定位仓库根", false, "未找到 LiteMonitor.csproj");
                return;
            }
            Add("定位仓库根", true, root);

            string builderPath = Path.Combine(root, "src", "UI", "Settings", "SettingsUIBuilder.cs");
            string monitorPath = Path.Combine(root, "src", "UI", "Settings", "MonitorPage.cs");
            string hardwarePath = Path.Combine(root, "src", "UI", "Settings", "SystemHardwarPage.cs");
            string liteUiPath = Path.Combine(root, "src", "UI", "Controls", "LiteUI.cs");
            string slnPath = Path.Combine(root, "LiteMonitor.sln");

            if (!File.Exists(builderPath) || !File.Exists(monitorPath) || !File.Exists(hardwarePath) || !File.Exists(liteUiPath))
            {
                Add("源码文件齐备", false, "缺少被扫描的源码文件");
                return;
            }

            string builder = File.ReadAllText(builderPath);
            string monitor = File.ReadAllText(monitorPath);
            string hardware = File.ReadAllText(hardwarePath);
            string liteUi = File.ReadAllText(liteUiPath);

            // ---- S1：8 个有绑定的 Add* 都必须有抑制守卫 ----
            string[] boundAddMethods =
            {
                "AddToggle", "AddInput", "AddInt", "AddDouble", "AddColor",
                "AddCombo", "AddComboIndex", "AddComboPair",
            };
            var missingGuard = new List<string>();
            foreach (string name in boundAddMethods)
            {
                string body = ExtractBlock(builder, name);
                if (body.Length == 0) { missingGuard.Add(name + "(未找到方法体)"); continue; }
                if (!body.Contains("IsWriteBackSuppressed")) missingGuard.Add(name);
            }
            Add("SettingsUIBuilder 八个 Add* 都有抑制守卫", missingGuard.Count == 0,
                missingGuard.Count == 0 ? "8/8" : "缺守卫：" + string.Join(",", missingGuard));

            // ---- S2：三种 ComboBox 的刷新体都包进作用域 ----
            var missingScope = new List<string>();
            foreach (string name in new[] { "AddCombo", "AddComboIndex", "AddComboPair" })
            {
                string body = ExtractBlock(builder, name);
                if (body.Length == 0) { missingScope.Add(name + "(未找到方法体)"); continue; }
                if (!body.Contains("BeginSuppressWriteBack")) missingScope.Add(name);
            }
            Add("三种 ComboBox 的刷新体都包进作用域", missingScope.Count == 0,
                missingScope.Count == 0 ? "3/3" : "缺作用域：" + string.Join(",", missingScope));

            // ---- S3：MonitorPage 的三处要点 ----
            string chkBlock = ExtractBlock(monitor, "_chkLinkHorizontal.CheckedChanged +=");
            Add("MonitorPage 复选框有抑制守卫", chkBlock.Contains("IsWriteBackSuppressed"),
                chkBlock.Contains("IsWriteBackSuppressed") ? "CheckedChanged 已守门" : "CheckedChanged 缺守卫");

            string saveBlock = ExtractBlock(monitor, "public override void Save()");
            Add("MonitorPage.Save 用 _linkHorizontalEdited 决定提交值",
                saveBlock.Contains("_linkHorizontalEdited"),
                saveBlock.Contains("_linkHorizontalEdited") ? "已按『用户是否真改过』提交" : "Save 未使用 _linkHorizontalEdited");

            string syncBlock = ExtractBlock(monitor, "private void SyncLinkHorizontalFromConfig()");
            Add("MonitorPage 同步复选框走作用域", syncBlock.Contains("BeginSuppressWriteBack"),
                syncBlock.Contains("BeginSuppressWriteBack") ? "同步已包进作用域" : "同步未包进作用域");

            // ---- S4：SystemHardwarPage 的两处 Fill* 与 6 个设备偏好下拉 ----
            string fillSync = ExtractBlock(hardware, "void FillSync(LiteComboBox combo");
            string fillGpu = ExtractBlock(hardware, "void FillGpuSync(LiteComboBox combo");
            Add("FillSync 重填走作用域", fillSync.Contains("BeginSuppressWriteBack"),
                fillSync.Contains("BeginSuppressWriteBack") ? "已包进作用域" : "未包进作用域");
            Add("FillGpuSync 重填走作用域", fillGpu.Contains("BeginSuppressWriteBack"),
                fillGpu.Contains("BeginSuppressWriteBack") ? "已包进作用域" : "未包进作用域");

            string[] deviceDropdowns = { "_cbDisk", "_cbNet", "_cbMobo", "_cbFanCpu", "_cbFanPump", "_cbFanCase" };
            var wrongBinding = new List<string>();
            foreach (string field in deviceDropdowns)
            {
                string? line = hardware.Split('\n').FirstOrDefault(l => l.Contains(field + " ="));
                if (line == null) { wrongBinding.Add(field + "(未找到)"); continue; }
                if (!line.Contains("AddComboPair")) wrongBinding.Add(field);
            }
            Add("六个设备偏好下拉用 AddComboPair", wrongBinding.Count == 0,
                wrongBinding.Count == 0 ? "6/6" : "未用成对下拉：" + string.Join(",", wrongBinding));

            // ---- S5：LiteThresholdRow 的守卫（阈值行也是立即绑定）----
            string threshold = ExtractBlock(liteUi, "public LiteThresholdRow(SettingsPageBase page");
            Add("LiteThresholdRow 立即绑定有抑制守卫", threshold.Contains("IsWriteBackSuppressed"),
                threshold.Contains("IsWriteBackSuppressed") ? "已守门" : "阈值行缺守卫");
            Add("LiteThresholdRow 刷新体走作用域", threshold.Contains("BeginSuppressWriteBack"),
                threshold.Contains("BeginSuppressWriteBack") ? "已包进作用域" : "阈值行刷新未包进作用域");
            Add("LiteThresholdRow 解析失败时不写回",
                threshold.Contains("TryParseStrict") && !threshold.Contains("= inputWarn.ValueDouble"),
                "非法输入不得把阈值静默改成 0");

            // ★ 离场回填已**下沉**到 LiteNumberInput（根因修复）。
            //   行里不再自带 Leave 处理器 —— 两套机制并存正是上一轮把回填打穿的原因：
            //   行的 Leave 先跑（或后跑）改写文本，同步触发 TextChanged，
            //   于是「非法文本」被改写成「合法的 0」，立即绑定就把 0 写进了配置。
            Add("阈值行不再自带离场回填处理器", !threshold.Contains("Inner.Leave"),
                "离场回填由 LiteNumberInput 统一负责，避免两套机制互相打穿");
            string numberInput = ExtractBlock(liteUi, "public class LiteNumberInput");
            Add("数字输入记录最近一次合法值", numberInput.Contains("_lastValid"),
                "离场时回填「最近一次合法值」，而不是硬编码 \"0\"");
            Add("数字输入离场不再硬编码 0", !numberInput.Contains("Inner.Text = \"0\""),
                "硬编码 \"0\" 会同步触发 TextChanged ⇒ 把 0 真的写进配置（用户只是粘了个 \".\"）");
            Add("数字输入离场回填最近合法值",
                numberInput.Contains("Inner.Leave") && numberInput.Contains("_lastValid"),
                "离场时把文本回填成最后一个合法值，显示与配置保持一致");

            // 阈值解析必须**严格**：只接受 KeyPress 放行的形式（数字/负号/小数点）。
            // 粘贴能绕过 KeyPress，用默认 NumberStyles 的话 "1e5" / "1,000" / " 8 " 都会被写进配置。
            // ★ 现在统一在 LiteNumberInput.TryParseStrict（行与控件共用同一套判定）。
            string thresholdParse = ExtractBlock(liteUi, "public static bool TryParseStrict(");
            bool strictStyles = thresholdParse.Contains("AllowLeadingSign")
                && thresholdParse.Contains("AllowDecimalPoint")
                && !thresholdParse.Contains("NumberStyles.Float")
                && !thresholdParse.Contains("AllowThousands")
                && !thresholdParse.Contains("AllowExponent")
                && !thresholdParse.Contains("AllowLeadingWhite")
                && !thresholdParse.Contains("AllowTrailingWhite");
            Add("阈值解析用严格 NumberStyles", strictStyles,
                strictStyles
                    ? "只接受数字/负号/小数点，\"1e5\" / \"1,000\" / \" 8 \" 均被拒"
                    : "解析样式过宽：粘贴可写入界面手打不出来的形式");
            Add("阈值解析拒绝 NaN / Infinity", thresholdParse.Contains("IsFinite"),
                thresholdParse.Contains("IsFinite") ? "已拒非有限值" : "未拒非有限值");
            // ★ 字符集校验才是拒 '+' 的真正机制 —— NumberStyles.AllowLeadingSign **本身会**放行 '+'。
            //   运行时门槛已证明 "+5" 被拒；这条源码门槛守住「实现里确实有字符集校验」，
            //   避免将来有人只留 NumberStyles，把 '+' 放回来。
            bool charsetGuard = thresholdParse.Contains("hasDigit") && thresholdParse.Contains("hasDot");
            Add("阈值解析有字符集校验（拒前导加号）", charsetGuard,
                charsetGuard
                    ? "字符集与 KeyPress 一致 ⇒ \"+5\" 这类界面打不出来的形式被拒"
                    : "缺少字符集校验：AllowLeadingSign 本身会放行 '+'");

            // ★ 结构性不变量：Crit >= Warn —— 状态判定是 value >= crit → crit，
            //   否则 value >= warn → warn；一旦 Crit < Warn，warn 档不可达。
            Add("阈值行强制 Crit >= Warn 不变量",
                threshold.Contains("range.Crit < value") && threshold.Contains("range.Warn > value"),
                "改 warn 时抬 crit / 改 crit 时压 warn —— 刚被用户改的那个值保持不变");
            Add("阈值钳制有可见行内提示",
                threshold.Contains("SetHint") && threshold.Contains("Menu.Threshold"),
                "钳制与不变量调整必须让用户看见，不能静默改值");

            // ---- S6：字体必须是进程内复用的，不能每次 new ----
            // Control.Dispose **不**释放借用的 Font ⇒ 每次 new Font 都是一个
            // 只靠终结器回收的 GDI 资源，反复开设置页就持续累积。
            string uiFonts = ExtractBlock(liteUi, "public static class UIFonts");
            Add("UIFonts 是进程内缓存", uiFonts.Contains("ConcurrentDictionary"),
                uiFonts.Contains("ConcurrentDictionary") ? "按字号/样式复用同一实例" : "UIFonts 又变回每次 new");
            int newFontCount = Regex.Matches(liteUi, "new Font\\(").Count;
            Add("LiteUI.cs 内不再有裸 new Font", newFontCount <= 1,
                "剩余 " + newFontCount + " 处（应只剩 UIFonts 缓存内部那 1 处）");
            Add("LiteNavBtn 重绘不再新建字体", !liteUi.Contains("new Font(Font,"),
                "原写法每次 OnPaint 都 new 一个 Font，鼠标划过就漏一个 GDI 对象");
            Add("UIFonts 提供 CachedCount 自检口", uiFonts.Contains("CachedCount"),
                "A17 的 S1 断言要用「反复开关设置页前后 CachedCount 不变」—— 断言不变量，而不是某个具体个数（字体键数会随新字号增长，写死数字必然误报）");

            // ---- 语言资源：阈值提示文案必须 9 种语言全覆盖 ----
            //   只加中文是最常见的漏 —— 缺键时 LanguageManager 会回退成键名后缀，
            //   界面上就会显示 "ThresholdCritRaised" 这种天书。
            string[] langFiles = { "zh", "zh-tw", "en", "ja", "ko", "de", "fr", "es", "ru" };
            string[] newLangKeys = { "ThresholdClamped", "ThresholdCritRaised", "ThresholdWarnLowered" };
            var missingLang = new List<string>();
            foreach (string lg in langFiles)
            {
                string lp = Path.Combine(root, "resources", "lang", lg + ".json");
                if (!File.Exists(lp)) { missingLang.Add(lg + "(缺文件)"); continue; }
                string lt = File.ReadAllText(lp);
                foreach (string k in newLangKeys)
                {
                    if (!lt.Contains("\"" + k + "\"")) missingLang.Add(lg + "." + k);
                }
            }
            Add("阈值提示文案 9 种语言全覆盖", missingLang.Count == 0,
                missingLang.Count == 0
                    ? "zh / zh-tw / en / ja / ko / de / fr / es / ru 均含 3 个新键"
                    : "缺失：" + string.Join(", ", missingLang));

            // ---- S7：探针工程不进 sln ----
            bool inSln = false;
            if (File.Exists(slnPath))
            {
                string sln = File.ReadAllText(slnPath);
                inSln = sln.Contains("AppearanceSettingsProbe");
            }
            Add("探针工程不进 sln", !inSln, inSln ? "不应出现在 sln 里" : "未出现在 sln 里");
        }

        // ==================================================================
        //  工具
        // ==================================================================

        private static void Add(string name, bool pass, string detail)
        {
            Results.Add((name, pass, detail));
        }

        /// <summary>
        /// 从 <paramref name="marker"/> 处取出其后第一个花括号块（跳过字符串与注释）。
        /// </summary>
        private static string ExtractBlock(string text, string marker)
        {
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return "";
            int open = text.IndexOf('{', idx);
            if (open < 0) return "";

            int depth = 0;
            bool inString = false, inChar = false, inLineComment = false, inBlockComment = false, inVerbatim = false;

            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                char n = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
                if (inBlockComment) { if (c == '*' && n == '/') { inBlockComment = false; i++; } continue; }
                if (inString)
                {
                    if (inVerbatim)
                    {
                        if (c == '"' && n == '"') { i++; continue; }   // "" 是转义的一个引号
                        if (c == '"') { inString = false; inVerbatim = false; }
                        continue;
                    }
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }

                if (c == '/' && n == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
                if (c == '@' && n == '"') { inString = true; inVerbatim = true; i++; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return "";
        }

        /// <summary>
        /// 触发控件的 Leave 事件（等价于「移开焦点」）。
        /// <para>★ 走真实的 <c>Control.OnLeave</c> ⇒ 注册在 Leave 上的处理器按**注册顺序**执行，
        ///   与实机的事件顺序一致 —— 这正是本轮缺陷的复现关键：
        ///   两套离场处理机制并存时，谁先跑决定了配置会不会被改坏。</para>
        /// </summary>
        private static void RaiseLeave(Control c)
        {
            var m = typeof(Control).GetMethod("OnLeave", BindingFlags.Instance | BindingFlags.NonPublic);
            m?.Invoke(c, new object[] { EventArgs.Empty });
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return "";
        }

        private static void WriteEvidence(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var root = new JsonObject
            {
                ["probe"] = "AppearanceSettingsProbe",
                ["purpose"] = "W3 设置页「意外回写」缺陷包的防回退门槛",
                ["total"] = Results.Count,
                ["passed"] = Results.Count(r => r.Pass),
                ["failed"] = Results.Count(r => !r.Pass),
            };

            var cases = new JsonArray();
            foreach (var (name, pass, detail) in Results)
            {
                cases.Add(new JsonObject
                {
                    ["name"] = name,
                    ["result"] = pass ? "PASS" : "FAIL",
                    ["detail"] = detail,
                });
            }
            root["cases"] = cases;

            string path = Path.Combine(outDir, "appearance-settings-probe.json");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：AppearanceSettingsProbe [--repo-root <仓库根>] [--out <证据目录>]");
            Console.WriteLine("  --repo-root  仓库根目录（默认从可执行文件位置向上查找 LiteMonitor.csproj）");
            Console.WriteLine("  --out        证据 JSON 输出目录（默认 <可执行文件目录>\\evidence）");
        }
    }
}
