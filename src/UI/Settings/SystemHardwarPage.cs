using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class SystemHardwarPage : SettingsPageBase
    {
        private Panel _container;
        
        // ★★★ 修复：类型更正为 LiteComboBox ★★★
        private LiteComboBox _cbDisk, _cbNet, _cbGpu, _cbMobo;
        private LiteComboBox _cbFanCpu, _cbFanPump, _cbFanCase;

        // ★ [#166] CPU 电压来源下拉（枚举**已发布**的候选，保存稳定传感器标识）
        private LiteComboBox _cbCpuVoltage;

        // ★ 实际检测到的显卡数量, 决定「监控显卡数量」下拉的可选上限 ★
        private int _detectedGpuCount = 0;

        // ★「监控显卡数量」下拉的句柄: 构造时拿不到真实卡数, 拿到之后再收窄选项 ★
        //   (CreateSourceCard() 在构造函数里就会被调用, 编译器看不到这条赋值链, 故显式抑制)
        private LiteComboBox _cbGpuCount = null!;

        // ★ 程序化改动下拉选项时抑制回调 ★
        //   真正会触发 SelectedIndexChanged 的是后面的 `SelectedIndex = cur - 1` 赋值;
        //   实测 `Items.Clear()` 本身**不**触发该事件, 也不会把 cmb.Text 清空。
        //   抑制之后, 夹紧后的值由 RefreshGpuCountOptions 显式回写, 不走事件。
        private bool _suppressGpuCountEvent = false;

        public SystemHardwarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);

            InitializeUI();
        }

        private void InitializeUI()
        {
            CreateSourceCard();
            CreateIpExclusionCard();
            CreateCalibrationCard();
            CreateSystemCard();
        }

        // ===========================================================
        // ★ [#610] 按指定 IP 排除实时网速
        // ===========================================================

        private TextBox? _ipListBox;
        private Label? _ipValidationLabel;
        private Label? _ipStatusLabel;

        /// <summary>
        /// IP 排除设置卡片：开关 + 多行 IP 列表 + 逐条校验结果 + 当前后端状态 + 权限提示。
        /// <para>★ 本页**不触发**任何硬件采集、不重开硬件树、不启用额外控制器。</para>
        /// </summary>
        private void CreateIpExclusionCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.IpTrafficExclusion"));

            var toggle = group.AddToggle(this, "Menu.IpTrafficExclusionEnabled",
                () => Config?.IpTrafficExclusionEnabled ?? false,
                v => { if (Config != null) Config.IpTrafficExclusionEnabled = v; });
            toggle.CheckedChanged += (s, e) => RefreshIpExclusionStatus();

            // ---- 多行 IP 列表 ----
            var ipPanel = new Panel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.White };
            var ipCaption = new Label
            {
                Text = LanguageManager.T("Menu.ExcludedTrafficIps"),
                AutoSize = true,
                Dock = DockStyle.Top,
                Font = UIFonts.Regular(9F),
                ForeColor = UIColors.TextMain,
            };
            _ipListBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Height = UIUtils.S(86),
                Font = UIFonts.Regular(9F),
                BackColor = Color.White,
                ForeColor = UIColors.TextSub,
            };
            _ipListBox.TextChanged += (s, e) =>
            {
                if (IsWriteBackSuppressed || Config == null) { return; }
                Config.ExcludedTrafficIps = SplitIpLines(_ipListBox.Text);
                RefreshIpExclusionValidation();
            };
            ipPanel.Controls.Add(_ipListBox);
            ipPanel.Controls.Add(ipCaption);
            group.AddFullItem(ipPanel);

            // ---- 校验结果 ----
            _ipValidationLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Font = UIFonts.Regular(8F),
                ForeColor = UIColors.TextSub,
                Padding = new Padding(0, 4, 0, 4),
            };
            group.AddFullItem(_ipValidationLabel);

            // ---- 当前状态 ----
            _ipStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Font = UIFonts.Regular(8F),
                ForeColor = UIColors.TextSub,
                Padding = new Padding(0, 4, 0, 4),
            };
            group.AddFullItem(_ipStatusLabel);

            // ---- 提示：口径 + 权限 ----
            group.AddHint(LanguageManager.T("Menu.IpExclusionCaliberHint"));
            group.AddHint(LanguageManager.T("Menu.IpExclusionAdminHint"));

            RegisterRefresh(() =>
            {
                if (_ipListBox == null) { return; }
                using (BeginSuppressWriteBack())
                {
                    _ipListBox.Text = string.Join("\r\n", Config?.ExcludedTrafficIps ?? new List<string>());
                }
                RefreshIpExclusionValidation();
                RefreshIpExclusionStatus();
            });

            AddGroupToPage(group);
        }

        /// <summary>把多行文本拆成规则行（去空行、去首尾空白）。</summary>
        internal static List<string> SplitIpLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) { return result; }
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) { result.Add(trimmed); }
            }
            return result;
        }

        /// <summary>逐条标出被拒绝的规则行（不静默忽略）。</summary>
        private void RefreshIpExclusionValidation()
        {
            if (_ipValidationLabel == null) { return; }

            var lines = Config?.ExcludedTrafficIps ?? new List<string>();
            IpRuleCompilationResult result = IpExclusionRules.CompileWithResult(lines);

            if (result.Errors.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine(LanguageManager.T("Menu.IpExclusionInvalid"));
                foreach (IpRuleError err in result.Errors.Take(8))
                {
                    sb.AppendLine("#" + (err.LineNumber + 1) + " " + err.Text + " — " + err.Reason);
                }
                _ipValidationLabel.Text = sb.ToString().TrimEnd();
                _ipValidationLabel.ForeColor = UIColors.TextWarn;
                return;
            }

            if (result.Rules.IsEmpty)
            {
                _ipValidationLabel.Text = LanguageManager.T("Menu.IpExclusionNoRule");
                _ipValidationLabel.ForeColor = UIColors.TextWarn;
                return;
            }

            _ipValidationLabel.Text = string.Format(LanguageManager.T("Menu.IpExclusionValid"), result.Rules.RuleCount);
            _ipValidationLabel.ForeColor = UIColors.TextSub;
        }

        /// <summary>显示后端当前状态（准备中/运行/降级/失败 + 权限提示）。</summary>
        private void RefreshIpExclusionStatus()
        {
            if (_ipStatusLabel == null) { return; }

            NetworkTrafficExclusionStatus? status = HardwareMonitor.Instance?.GetTrafficExclusionStatus();
            if (status == null)
            {
                _ipStatusLabel.Text = LanguageManager.T("Menu.IpExclusionStatus") + ": " +
                                      LanguageManager.T("Menu.IpExclusionStateDisabled");
                _ipStatusLabel.ForeColor = UIColors.TextSub;
                return;
            }

            string stateKey = status.State switch
            {
                NetworkTrafficExclusionState.Disabled => "Menu.IpExclusionStateDisabled",
                NetworkTrafficExclusionState.Idle => "Menu.IpExclusionStateIdle",
                NetworkTrafficExclusionState.Preparing => "Menu.IpExclusionStatePreparing",
                NetworkTrafficExclusionState.Running => "Menu.IpExclusionStateRunning",
                NetworkTrafficExclusionState.Degraded => "Menu.IpExclusionStateDegraded",
                _ => "Menu.IpExclusionStateFailed",
            };

            string text = LanguageManager.T("Menu.IpExclusionStatus") + ": " + LanguageManager.T(stateKey);
            if (!string.IsNullOrEmpty(status.Detail)) { text += " (" + status.Detail + ")"; }
            _ipStatusLabel.Text = text;
            _ipStatusLabel.ForeColor = status.Effective ? UIColors.TextSub : UIColors.TextWarn;
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;
            PopulateAsyncData();
        }

        // 将 PopulateAsyncData 改为“批量处理”模式
        private async void PopulateAsyncData()
        {
            try 
            {
                string strAuto = LanguageManager.T("Menu.Auto");

                // 1. 并行等待所有数据返回 (使用 HardwareScanner)
                var taskDisks = Task.Run(() => HardwareScanner.ListAllDisks(HardwareMonitor.Instance.ComputerInstance));
                var taskNets  = Task.Run(() => HardwareScanner.ListAllNetworks(HardwareMonitor.Instance.ComputerInstance));
                var taskGpus  = Task.Run(() => HardwareMonitor.ListAllGpuOptions());
                var taskFans  = Task.Run(() => HardwareScanner.ListAllFans(HardwareMonitor.Instance.ComputerInstance, HardwareMonitor.Instance.SyncLock));
                var taskMobo  = Task.Run(() => HardwareScanner.ListAllMoboTemps(HardwareMonitor.Instance.ComputerInstance, HardwareMonitor.Instance.SyncLock));
                // ★ [#166] CPU 电压候选：只读**已发布**的快照（不扫硬件树、不触发采集）
                var taskCpuVoltage = Task.Run(() => HardwareMonitor.ListCpuVoltageCandidates());

                await Task.WhenAll(taskDisks, taskNets, taskGpus, taskFans, taskMobo, taskCpuVoltage);

                // ★ 页面可能在 await 期间被释放（设置窗体关闭 ⇒ DisposeCachedPages 释放全部页面）。
                //   此时继续操作控件没有意义；而且 async void 的未捕获异常会经
                //   SynchronizationContext 重新抛出 ⇒ 直接崩进程。
                if (IsDisposed) return;

                // ★ 实际检测到的显卡数量 (用于「监控显卡数量」下拉的上限) ★
                //   用局部变量承接: 直接写 `taskGpus.Result?.Count` 会把 Result 的可空状态
                //   污染到下面的 FillGpuSync(_cbGpu, taskGpus.Result, ...) 调用上 (CS8604)。
                var detectedGpus = taskGpus.Result;
                _detectedGpuCount = detectedGpus?.Count ?? 0;

                // ★ 真实卡数拿到了, 收窄「监控显卡数量」下拉的选项 ★
                // (构造时只能给满 1..8, 因为此刻硬件还没枚举完)
                RefreshGpuCountOptions();

                // 2. ★★★ 锁定全局布局 (防止每填一个框就重绘一次) ★★★
                this.SuspendLayout();
                
            
                // 定义一个同步填充的 Action，避免重复代码
                void FillSync(LiteComboBox combo, List<string> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    var fullList = new List<string>(data);

                    // ★ W3-3：重填期间必须**抑制选中事件**。
                    //   原值不在候选里（U 盘没插、网卡改名等）时，旧代码会落到"自动"，
                    //   事件随即把空串写回配置 ⇒ 用户的设备偏好就此丢失。
                    using (BeginSuppressWriteBack())
                    {
                        combo.Inner.BeginUpdate(); // 锁定 ComboBox 自身
                        combo.Inner.Items.Clear();
                        combo.AddItem(strAuto, "");
                        foreach (var item in fullList) combo.AddItem(item, item);

                        // 原值不可用时**保留原值**，并加一条只读提示项：
                        // 提示项的取值仍然是原值 ⇒ 即便用户又点回它，写回的也是原值。
                        if (!string.IsNullOrEmpty(currentVal) && !fullList.Contains(currentVal))
                            combo.AddItem(string.Format(LanguageManager.T("Menu.PreferenceUnavailable"), currentVal), currentVal);

                        combo.Inner.EndUpdate(); // 解锁 ComboBox

                        if (!string.IsNullOrEmpty(currentVal)) combo.SelectValue(currentVal);
                        else combo.SelectedIndex = 0;
                    }
                }

                void FillGpuSync(LiteComboBox combo, List<HardwareScanner.GpuOption> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    HardwareScanner.GpuOption? ResolveCurrent()
                    {
                        if (string.IsNullOrWhiteSpace(currentVal)) return null;

                        var byValue = data.FirstOrDefault(x =>
                            string.Equals(x.Value, currentVal, StringComparison.OrdinalIgnoreCase));
                        if (byValue != null) return byValue;

                        var byName = data.Where(x =>
                            string.Equals(x.Name, currentVal, StringComparison.OrdinalIgnoreCase)).ToList();
                        return byName.Count == 1 ? byName[0] : null;
                    }

                    // ★ W3-3：重填期间抑制选中事件（否则"落到自动"会写空串）。
                    using (BeginSuppressWriteBack())
                    {
                        combo.Inner.BeginUpdate();
                        combo.Inner.Items.Clear();
                        combo.AddItem(strAuto, "");
                        foreach (var item in data) combo.AddItem(item.Label, item.Value);

                        var selected = ResolveCurrent();

                        // 显卡原值不可用时同样保留原值 + 只读提示项，
                        // 绝不落到"自动"把配置写成空串。
                        if (selected == null && !string.IsNullOrWhiteSpace(currentVal))
                            combo.AddItem(string.Format(LanguageManager.T("Menu.PreferenceUnavailable"), currentVal), currentVal);

                        combo.Inner.EndUpdate();
                        combo.SelectValue(selected?.Value ?? currentVal ?? "");
                    }
                }

                // ★ [#166] CPU 电压来源下拉
                //   ★ 只列**已发布**的候选；明确不是核心电压的轨（SoC / GT / DIMM / 输入供电…）不列。
                //   ★ 手动指定后来源消失（换机器 / 热重载后标识变了）→ 保留原值 + 只读提示项，
                //     绝不悄悄落到"自动"（那等于换了另一种含义的电压）。
                void FillCpuVoltageSync(LiteComboBox combo, List<CpuVoltageCandidate> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    using (BeginSuppressWriteBack())
                    {
                        combo.Inner.BeginUpdate();
                        combo.Inner.Items.Clear();
                        combo.AddItem(strAuto, "");

                        foreach (var c in data)
                        {
                            if (c == null) continue;
                            CpuVoltageSourceKind kind = CpuVoltageResolver.Classify(c.HardwareLevel, c.SensorName, out _);
                            if (kind == CpuVoltageSourceKind.Excluded) continue;

                            string label = string.Format("{0} [{1}] - {2}",
                                c.SensorName, c.HardwareName, MetricUtils.GetCpuVoltageSourceKindLabel(kind));
                            combo.AddItem(label, c.SensorIdentifier);
                        }

                        bool currentExists = data.Any(x => x != null &&
                            string.Equals(x.SensorIdentifier, currentVal, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(currentVal) && !currentExists)
                        {
                            combo.AddItem(string.Format(LanguageManager.T("Menu.PreferenceUnavailable"), currentVal), currentVal);
                        }

                        combo.Inner.EndUpdate();

                        if (!string.IsNullOrEmpty(currentVal)) combo.SelectValue(currentVal);
                        else combo.SelectedIndex = 0;
                    }
                }

                // 3. 瞬间填入所有数据 (因为布局被挂起，用户看不见中间过程)
                FillSync(_cbDisk, taskDisks.Result, Config.PreferredDisk);
                FillSync(_cbNet, taskNets.Result, Config.PreferredNetwork);
                FillGpuSync(_cbGpu, taskGpus.Result, Config.PreferredGpu);
                FillSync(_cbMobo, taskMobo.Result, Config.PreferredMoboTemp);
                
                // Fan 的数据是复用的
                FillSync(_cbFanCpu, taskFans.Result, Config.PreferredCpuFan);
                FillSync(_cbFanPump, taskFans.Result, Config.PreferredCpuPump);
                FillSync(_cbFanCase, taskFans.Result, Config.PreferredCaseFan);

                // ★ [#166] CPU 电压来源
                FillCpuVoltageSync(_cbCpuVoltage, taskCpuVoltage.Result, Config.PreferredCpuVoltageSensor);
            }
            catch (Exception ex)
            {
                // 记录日志，或者只是简单地忽略（硬件读取失败不应该影响用户进入设置）
                Console.WriteLine("硬件列表加载失败: " + ex.Message);
            }
            finally
            {
                // 4. 恢复布局 (此时所有控件已就绪，一次性渲染)
                // ★ 页面可能已释放 —— 此时不要碰控件。
                //   （上面 await 之后已提前 return，SuspendLayout 也没执行过，
                //     所以这里跳过 ResumeLayout 才是对称的。）
                if (!IsDisposed) this.ResumeLayout(true);
            }
        }

        /// <summary>
        /// 按实际检测到的显卡数收窄「监控显卡数量」下拉的选项。
        /// CreateSourceCard() 是在构造函数里跑的 (InitializeUI), 那一刻硬件还没枚举完,
        /// _detectedGpuCount 恒为 0 —— 所以必须等 PopulateAsyncData() 之后再调一次。
        /// </summary>
        private void RefreshGpuCountOptions()
        {
            if (_cbGpuCount == null) return;

            // ★ 还没探测到显卡 (或探测失败) 时不要收窄选项, 更不要动用户的配置 ★
            if (_detectedGpuCount <= 0) return;

            int maxSelectable = Math.Max(1, Math.Min(GpuKey.MaxGpuCount, _detectedGpuCount));
            int cur = Math.Max(1, Math.Min(maxSelectable, Config?.MonitoredGpuCount ?? 1));

            try
            {
                // ★ 抑制事件回调: 下面改 **SelectedIndex** 会触发 SelectedIndexChanged,
                //   把中间态写进 draft。夹紧后的值由下面**显式**回写, 不走事件。 ★
                //   (实测: Items.Clear() 本身不触发该事件, 真正会触发的是 SelectedIndex 赋值)
                _suppressGpuCountEvent = true;

                _cbGpuCount.Items.Clear();
                for (int i = 1; i <= maxSelectable; i++) _cbGpuCount.Items.Add(i.ToString());
                _cbGpuCount.SelectedIndex = cur - 1;
            }
            finally
            {
                _suppressGpuCountEvent = false;
            }

            // ★★★ [关键] 把夹紧后的值显式写回 draft ★★★
            //   不写回会有个隐蔽的坑: 设置页**再次打开**时, SettingsPageBase.OnShow()
            //   会重跑 AddCombo 注册的刷新回调, 而它的逻辑是
            //     "当前值不在 Items 里 -> SelectedIndex = 0" ——
            //   于是超出实际卡数的值 (比如 3) 会被**静默改成 1**, 而不是我们期望的 2。
            //   先写回夹紧值, 下次打开时当前值就在 Items 里, 不会被改成 1。
            if (Config != null && Config.MonitoredGpuCount != cur)
            {
                Config.MonitoredGpuCount = cur;
            }
        }

        private void CreateSourceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.HardwareSettings"));
            string strAuto = LanguageManager.T("Menu.Auto");
            
            group.AddToggle(this, "Menu.UseWinPerCounters", () => Config?.UseWinPerCounters ?? false, v => { if(Config!=null) Config.UseWinPerCounters = v; });
            
            // ★★★ [新增] 忽略 SMB 流量开关 ★★★
            // 直接使用中文作为 Key，如果 LanguageManager 找不到 Key 会原样返回
            group.AddToggle(this, LanguageManager.T("Menu.IgnoreSMBTraffic"),
                () => Config?.IgnoreSmbTraffic ?? false, 
                v => { if(Config!=null) Config.IgnoreSmbTraffic = v; }
            );

            // 内存/显存显示模式 (从主界面设置移来)
            string[] memOptions = { LanguageManager.T("Menu.Percent"), LanguageManager.T("Menu.UsedSize") };
            group.AddComboIndex(this, "Menu.MemoryDisplayMode", memOptions,
                () => Config?.MemoryDisplayMode ?? 0,
                idx => { if (Config != null) Config.MemoryDisplayMode = idx; }
            );

            int[] rates = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };
            group.AddCombo(this, "Menu.Refresh", rates.Select(r => r + " ms"),
                () => (Config?.RefreshMs ?? 1000) + " ms",
                v => { if (Config != null) Config.RefreshMs = MetricUtils.ParseInt(v); }
            );
            // ★ 说明：刷新频率只决定界面重绘节奏，普通硬件数据的采集上限是每秒一次
            group.AddHint(LanguageManager.T("Menu.RefreshTip"));

            // ★★★ 修复：强制转换为 LiteComboBox ★★★
            // ★ W3-3：设备偏好改用「标签/取值」成对的下拉 ——
            //   原值不可用时可以加一条"提示项"（标签写明不可用，取值仍是原值），
            //   选中它写回的也是原值，不会把配置冲成空串。
            _cbDisk = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.DiskSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredDisk ?? "",
                v => { if (Config != null) Config.PreferredDisk = v ?? ""; });

            _cbNet = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.NetworkSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredNetwork ?? "",
                v => { if (Config != null) Config.PreferredNetwork = v ?? ""; });

            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });

            // ★★★ [多显卡] 监控显卡数量下拉 ★★★
            // 1 = 只监控 1 张 (沿用旧行为), 2 及以上 = 按优先级监控前 N 张
            // ★ 选项上限按**实际检测到的显卡数**决定, 不能给满 8 ——
            //   否则 2 卡机器上选 8 会凭空多出 6 组永远没有数据的空白监控项。 ★
            // ★ 但 CreateSourceCard() 是在构造函数里跑的 (InitializeUI), 那一刻硬件还没枚举完,
            //   _detectedGpuCount 恒为 0 —— 所以这里先给满 1..8,
            //   等 PopulateAsyncData() 拿到真实卡数后再调 RefreshGpuCountOptions() 收窄。 ★
            var gpuCountOptions = Enumerable.Range(1, GpuKey.MaxGpuCount).Select(c => c.ToString()).ToList();
            _cbGpuCount = (LiteComboBox)group.AddCombo(this, "Menu.GpuMonitorCount", gpuCountOptions,
                () => (Config?.MonitoredGpuCount ?? 1).ToString(),
                v => {
                    if (Config == null) return;
                    // ★ 程序化刷新选项 (RefreshGpuCountOptions) 时会触发本回调, 此时忽略:
                    //   夹紧后的值由 RefreshGpuCountOptions 显式回写, 不走事件。 ★
                    if (_suppressGpuCountEvent) return;
                    // ★ 只写数量, 不在这里改 MonitorItems ★
                    // 监控项由 Task 5.5 (保存设置时) 统一生成, 避免绕过 draft/live 机制。
                    // 上限不在这里夹紧 —— 下拉的选项本身已按实际卡数收窄, 用户选不超;
                    // 真正的夹紧由 Task 5.5 的 GpuItemSync.Ensure(cfg, availableGpuCount) 在保存时完成。
                    Config.MonitoredGpuCount = Math.Max(1, MetricUtils.ParseInt(v));
                }
            );
            group.AddHint(LanguageManager.T("Menu.GpuMonitorCountTip"));

            _cbMobo = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Items.MOBO.Temp"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredMoboTemp ?? "",
                v => { if (Config != null) Config.PreferredMoboTemp = v ?? ""; });

            _cbFanCpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Items.CPU.Fan"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredCpuFan ?? "",
                v => { if (Config != null) Config.PreferredCpuFan = v ?? ""; });
            
            _cbFanPump = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Items.CPU.Pump"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredCpuPump ?? "",
                v => { if (Config != null) Config.PreferredCpuPump = v ?? ""; });

            _cbFanCase = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Items.CASE.Fan"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredCaseFan ?? "",
                v => { if (Config != null) Config.PreferredCaseFan = v ?? ""; });

            // ★ [#166] CPU 电压来源 ★
            //   保存的是 LHM 的**稳定传感器标识**（形如 "/amdcpu/0/voltage/0"），
            //   空串 = 自动。选项**只来自已发布的候选**（见 PopulateAsyncData），
            //   这里不扫描硬件树、不调用 hw.Update()、也不启用任何额外控制器。
            _cbCpuVoltage = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.CpuVoltageSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredCpuVoltageSensor ?? "",
                v => { if (Config != null) Config.PreferredCpuVoltageSensor = v ?? ""; });
            group.AddHint(LanguageManager.T("Menu.CpuVoltageSourceTip"));

            AddGroupToPage(group);
        }

        private void CreateCalibrationCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Calibration"));
            string suffix = " (" + LanguageManager.T("Menu.MaxLimits") + ")";

            void AddCalib(string key, string unit, Func<float> get, Action<float> set)
            {
                var input = group.AddDouble(this, "RAW_TITLE_HACK", unit, 
                    () => (int)(get?.Invoke() ?? 0),        
                    v => set?.Invoke((float)(int)v)
                );
                if(input.Parent.Controls[0] is Label lbl) lbl.Text = LanguageManager.T(key) + suffix; 
            }
            
            group.AddHint(LanguageManager.T("Menu.CalibrationTip"));
            AddCalib("Items.CPU.Power", "W",   () => Config?.RecordedMaxCpuPower ?? 100, v => { if(Config!=null) Config.RecordedMaxCpuPower = v; });
            AddCalib("Items.CPU.Clock", "MHz", () => Config?.RecordedMaxCpuClock ?? 5000, v => { if(Config!=null) Config.RecordedMaxCpuClock = v; });
            AddCalib("Items.GPU.Power", "W",   () => Config?.RecordedMaxGpuPower ?? 300, v => { if(Config!=null) Config.RecordedMaxGpuPower = v; });
            AddCalib("Items.GPU.Clock", "MHz", () => Config?.RecordedMaxGpuClock ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuClock = v; });
            AddCalib("Items.CPU.Fan",   "RPM", () => Config?.RecordedMaxCpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuFan = v; });
            AddCalib("Items.CPU.Pump",  "RPM", () => Config?.RecordedMaxCpuPump ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuPump = v; });
            AddCalib("Items.GPU.Fan",   "RPM", () => Config?.RecordedMaxGpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuFan = v; });
            AddCalib("Items.CASE.Fan",  "RPM", () => Config?.RecordedMaxChassisFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxChassisFan = v; });

            AddGroupToPage(group);
        }

        private void CreateSystemCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.SystemSettings"));
            
            var langs = new List<string>();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir))
                langs.AddRange(Directory.EnumerateFiles(langDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f).ToUpper()));
            
            group.AddCombo(this, "Menu.Language", langs,
                () => string.IsNullOrEmpty(Config?.Language) ? LanguageManager.CurrentLang.ToUpper() : Config.Language.ToUpper(),
                v => { if(Config!=null) Config.Language = v.ToLower(); }
            );

            group.AddToggle(this, "Menu.AutoStart", () => Config?.AutoStart ?? false, v => { if(Config!=null) Config.AutoStart = v; });
            group.AddToggle(this, "Menu.AutoCheckUpdate", () => Config?.AutoCheckUpdate ?? true, v => { if(Config!=null) Config.AutoCheckUpdate = v; });

            var chkTray = group.AddToggle(this, "Menu.HideTrayIcon", 
                () => Config?.HideTrayIcon ?? false, 
                v => { if(Config!=null) Config.HideTrayIcon = v; });
            chkTray.CheckedChanged += (s, e) => { if(Config!=null) EnsureSafeVisibility(null, chkTray, null); };

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
