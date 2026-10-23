using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.WebServer;
using System.Diagnostics;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MainPanelPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public bool GpuStartupRetryRequested { get; private set; }

        public void AcknowledgeGpuStartupRetryRequest() =>
            GpuStartupRetryRequested = false;

        public MainPanelPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);

            CreateBehaviorCard();
            CreateAppearanceCard();
            CreateGpuMaterialCard();
            CreateHorizontalModeGroup();
            CreateWebCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateBehaviorCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.MainFormSettings"));

            // 1. Hide Main Form (with logic)
            var chkHide = group.AddToggle(this, "Menu.HideMainForm", 
                () => Config.HideMainForm, 
                v => Config.HideMainForm = v);
            
            chkHide.CheckedChanged += (s, e) => EnsureSafeVisibility(chkHide, null, null);

            // 2. Toggles
            group.AddToggle(this, "Menu.TopMost", () => Config.TopMost, v => Config.TopMost = v);
            group.AddToggle(this, "Menu.ClampToScreen", () => Config.ClampToScreen, v => Config.ClampToScreen = v);
            group.AddToggle(this, "Menu.AutoHide", () => Config.AutoHide, v => Config.AutoHide = v);
            group.AddToggle(this, "Menu.ClickThrough", () => Config.ClickThrough, v => Config.ClickThrough = v);

            // 3. Double Click Action
            string[] actions = { 
                LanguageManager.T("Menu.ActionSwitchLayout"),
                LanguageManager.T("Menu.ActionTaskMgr"),
                LanguageManager.T("Menu.ActionSettings"),
                LanguageManager.T("Menu.ActionTrafficHistory"),
                LanguageManager.T("Menu.CleanMemory"),
                LanguageManager.T("Menu.OpenWeb")
            };
            group.AddComboIndex(this, "Menu.DoubleClickAction", actions,
                () => Config.MainFormDoubleClickAction,
                idx => Config.MainFormDoubleClickAction = idx
            );

            AddGroupToPage(group);
        }

        private void CreateAppearanceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Appearance"));

            // 1. Theme
            group.AddCombo(this, "Menu.Theme", ThemeManager.GetAvailableThemes(), 
                () => Config.Skin, 
                v => Config.Skin = v);

            // 2. Orientation
            group.AddComboIndex(this, "Menu.DisplayMode", 
                new[] { LanguageManager.T("Menu.Vertical"), LanguageManager.T("Menu.Horizontal") },
                () => Config.HorizontalMode ? 1 : 0, 
                idx => Config.HorizontalMode = (idx == 1));

            // 3. Width
            int[] widths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            group.AddCombo(this, "Menu.Width", 
                widths.Select(w => w + " px"), 
                () => Config.PanelWidth + " px",
                s => Config.PanelWidth = MetricUtils.ParseInt(s));

            // 4. Opacity
            double[] opacities = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            group.AddCombo(this, "Menu.Opacity",
                opacities.Select(o => Math.Round(o * 100) + "%"),
                () => Math.Round(Config.Opacity * 100) + "%",
                s => Config.Opacity = MetricUtils.ParseDouble(s) / 100.0);

            // 5. Scale
            double[] scales = { 2.0, 1.75, 1.5, 1.25, 1.0, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5 };
            group.AddCombo(this, "Menu.Scale",
                scales.Select(s => (s * 100) + "%"),
                () => (Config.UIScale * 100) + "%",
                s => Config.UIScale = MetricUtils.ParseDouble(s) / 100.0);

            // 6. 实时玻璃（M2 / A16）
            //   ★ 默认关闭（M2 合同 §8.1）；开启是**显式**操作，且首次开启必须弹副作用提示。
            var liveGlassCheck = new LiteCheck(Config.LiveGlassEnabled, LanguageManager.T("Menu.Enable"));
            liveGlassCheck.CheckedChanged += (s, e) =>
            {
                bool want = liveGlassCheck.Checked;

                if (want && !Config.LiveGlassHintShown)
                {
                    // ★ 首次提示（M2 合同 §8.3）：必须如实表述副作用。
                    //   用户点"取消" ⇒ 不开启（把开关拨回去），不得"先开后问"。
                    if (!ShowLiveGlassHint())
                    {
                        liveGlassCheck.Checked = false;
                        return;
                    }
                    Config.LiveGlassHintShown = true;
                }

                Config.LiveGlassEnabled = want;
                UI?.SetLiveGlassEnabled(want);
                UpdateLiveGlassStatus();
            };
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.LiveGlass"), liveGlassCheck));

            // 实际状态行：显示**真实**的降级原因，而不是只回显配置值
            // （合同 §7「降级不静默」；A16「配置和实际降级状态一致」）。
            _liveGlassStatusLabel = new Label
            {
                AutoSize = false,
                Width = UIUtils.S(240),
                Height = UIUtils.S(22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(8F),
                Text = UI?.LiveGlassStatusText ?? "实时玻璃未启用。",
            };
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.LiveGlassStatus"), _liveGlassStatusLabel));

            AddGroupToPage(group);

        }

        /// <summary>实时玻璃的实际状态标签（设置页用；null = 页面尚未创建）。</summary>
        private Label? _liveGlassStatusLabel;

        private void CreateGpuMaterialCard()
        {
            string language = Config.Language ?? string.Empty;
            bool chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            string Text(string zh, string en) => chinese ? zh : en;

            var group = new LiteSettingsGroup(Text("GPU 材质渲染", "GPU material rendering"));
            group.AddToggle(
                this,
                Text("GPU 材质渲染", "GPU material rendering"),
                () => Config.GpuThemeEffectsEnabled,
                value =>
                {
                    Config.GpuThemeEffectsEnabled = value;
                    if (value)
                    {
                        GpuStartupRetryRequested = true;
                    }
                });
            group.AddHint(Text(
                "只对带有 rendering.backend=gpu 的新主题生效；旧主题继续使用原 GDI+ 路径。",
                "Only new themes with rendering.backend=gpu are affected; legacy themes keep the original GDI+ path."));

            var liveBackground = new LiteCheck(
                Config.GpuLiveBackgroundEnabled,
                Text("启用", "Enable"));
            liveBackground.CheckedChanged += (_, _) =>
            {
                if (IsWriteBackSuppressed)
                {
                    return;
                }

                bool want = liveBackground.Checked;
                if (want && !Config.GpuWdaCapturePolicyAccepted)
                {
                    DialogResult accepted = MessageBox.Show(
                        FindForm(),
                        Text(
                            "实时玻璃需要使用 Desktop Duplication，并通过 WDA_EXCLUDEFROMCAPTURE 排除整个顶层窗口。\n\n开启后，受支持的系统截图和录屏会看不到 LiteMonitor++。旧主题和固定输入模式不受影响。\n\n是否接受并开启实时背景？",
                            "Live glass uses Desktop Duplication and excludes the entire top-level window with WDA_EXCLUDEFROMCAPTURE.\n\nWhile active, supported screenshots and recordings will not show LiteMonitor++. Legacy themes and fixed-input mode are unaffected.\n\nAccept and enable live background?"),
                        Text("实时玻璃捕获说明", "Live glass capture policy"),
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning);
                    if (accepted != DialogResult.OK)
                    {
                        using (BeginSuppressWriteBack())
                        {
                            liveBackground.Checked = false;
                        }
                        return;
                    }

                    Config.GpuWdaCapturePolicyAccepted = true;
                }

                Config.GpuLiveBackgroundEnabled = want;
                if (want)
                {
                    GpuStartupRetryRequested = true;
                }
            };
            group.AddItem(new LiteSettingsItem(
                Text("实时桌面背景", "Live desktop background"),
                liveBackground));
            RegisterRefresh(() => liveBackground.Checked =
                Config.GpuLiveBackgroundEnabled);
            group.AddHint(Text(
                "实时背景与旧 LiveGlassEnabled 完全独立；赛博朋克主题始终为零抓屏。",
                "Live background is independent from legacy LiveGlassEnabled; cyberpunk always performs zero desktop capture."));

            Label status = new()
            {
                AutoSize = false,
                Width = UIUtils.S(320),
                Height = UIUtils.S(34),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(8F),
                Text = GpuStatusText(chinese)
            };
            group.AddItem(new LiteSettingsItem(
                Text("实际状态", "Actual state"),
                status));
            RegisterRefresh(() => status.Text = GpuStatusText(chinese));

            AddGroupToPage(group);
        }

        private string GpuStatusText(bool chinese)
        {
            if (UI?.IsGpuPresentationActive == true)
            {
                if (UI.IsGpuLiveBackgroundActive)
                    return chinese ? "实时玻璃运行中（系统截图/录屏会排除整个窗口）。" :
                        "Live glass is active; supported screenshots/recordings exclude the entire window.";
                if (UI.IsGpuLiveBackgroundRequested)
                    return chinese ? "正在显示固定背景，实时捕获暂不可用：" + UI.GpuRuntimeFailure :
                        "Fixed background; live capture is unavailable: " + UI.GpuRuntimeFailure;
                return chinese
                    ? "GPU 材质运行中，使用固定背景（未捕获桌面）。"
                    : "GPU material is active with a fixed background; desktop capture is off.";
            }

            string? fallback = UI?.GpuFallbackCode;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return chinese
                    ? "当前未运行 GPU 呈现：" + fallback
                    : "GPU presentation is inactive: " + fallback;
            }

            return chinese
                ? "当前主题未请求 GPU 呈现。"
                : "The current theme does not request GPU presentation.";
        }

        /// <summary>
        /// 首次开启实时玻璃时的副作用提示。
        /// <para>★ 文案由语言资源提供，必须如实表述：捕获瞬间面板可能从录屏/截图中短暂消失或闪动
        /// （M2 合同 §8.3），不得承诺"所有录屏软件都无感"（§8.4）。</para>
        /// </summary>
        private bool ShowLiveGlassHint()
        {
            string title = LanguageManager.T("Menu.LiveGlass");
            string text = LanguageManager.T("Menu.LiveGlassHint");

            IWin32Window? owner = FindForm();
            DialogResult result = owner is null
                ? MessageBox.Show(text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
                : MessageBox.Show(owner, text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

            return result == DialogResult.OK;
        }

        /// <summary>刷新实时玻璃的实际状态显示。</summary>
        private void UpdateLiveGlassStatus()
        {
            if (_liveGlassStatusLabel is null) return;
            _liveGlassStatusLabel.Text = UI?.LiveGlassStatusText ?? "实时玻璃未启用。";
        }

        private void CreateHorizontalModeGroup()
        {
            // --- Group: Horizontal Mode ---
            var groupHorz = new LiteSettingsGroup(LanguageManager.T("Menu.Horizontal"));

          

            // 1. Follow Taskbar
            groupHorz.AddToggle(this, "Menu.HorizontalFollowsTaskbar", 
                () => Config.HorizontalFollowsTaskbar, 
                v => Config.HorizontalFollowsTaskbar = v);
                
            // 2. Single Line
            groupHorz.AddToggle(this, "Menu.TaskbarSingleLine",
                () => Config.HorizontalSingleLine,
                v => Config.HorizontalSingleLine = v);

            // 3. Spacing
            groupHorz.AddInt(this, "Menu.TaskbarItemSpacing", "px",
                () => Config.HorizontalItemSpacing,
                v => Config.HorizontalItemSpacing = v);

            groupHorz.AddInt(this, "Menu.TaskbarInnerSpacing", "px",
                () => Config.HorizontalInnerSpacing,
                v => Config.HorizontalInnerSpacing = v);
            
            AddGroupToPage(groupHorz);
        }

        private void CreateWebCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.WebServer")); 

            // A. Header with Action Button
            group.AddButton(LanguageManager.T("Menu.WebServerTip"), LanguageManager.T("Menu.OpenWeb"), () => 
            {
                Core.Actions.WebActions.OpenWebMonitor(Config);
            });

            // B. Settings
            group.AddToggle(this, "Menu.WebServer", 
                () => Config.WebServerEnabled, 
                v => Config.WebServerEnabled = v
            );

            // ★ 网页版帧率开关 (默认关闭)
            //   帧率需要额外的外部采集服务，开启会带来额外的采集开销，
            //   所以默认不勾选，由用户显式决定是否让网页端显示 FPS。
            group.AddToggle(this, "Menu.WebServerFps",
                () => Config.WebFpsEnabled,
                v => Config.WebFpsEnabled = v
            );
            group.AddHint(LanguageManager.T("Menu.WebServerFpsTip"));

            group.AddInt(this, "Menu.WebServerPort", "", 
                () => Config.WebServerPort, 
                v => Config.WebServerPort = v,
                60
            );

            bool isChinese = !string.IsNullOrEmpty(Config.Language) && 
                                     Config.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            // ★ 监听范围（SEC-001 / 契约 W12）
            //   新安装默认「仅本机」；存量配置（WebServerEnabled=true 且旧版本没有该字段）
            //   会被保留为「局域网」，并在下面提示用户收敛。
            group.AddComboIndex(this,
                isChinese ? "监听范围" : "Bind scope",
                isChinese
                    ? new[] { "仅本机 (127.0.0.1)", "局域网（必须设置口令）" }
                    : new[] { "Loopback only (127.0.0.1)", "LAN (password required)" },
                () => Config.WebServerBindMode == WebServerBindMode.Lan ? 1 : 0,
                idx => Config.WebServerBindMode = idx == 1 ? WebServerBindMode.Lan : WebServerBindMode.Loopback);

            // ★ 局域网模式下额外允许的主机名 / IP（进入 Host / Origin 白名单）
            group.AddInput(this,
                isChinese ? "允许的局域网地址" : "Allowed LAN addresses",
                () => Config.WebServerLanAddresses,
                v => Config.WebServerLanAddresses = v,
                isChinese ? "例如 192.168.1.20；留空表示只允许本机访问" : "e.g. 192.168.1.20 (empty = loopback only)",
                160);

            // ★ 明文传输告警（契约 W11）：局域网模式下 HTTP / WebSocket 都是明文。
            group.AddHint(isChinese
                ? "⚠ 局域网模式下 HTTP 与 WebSocket 均为**明文传输**，局域网内可被嗅探：请自行部署反向代理提供 TLS，或只在可信网络中使用。"
                : "⚠ In LAN mode both HTTP and WebSocket are transported in **clear text** and can be sniffed on the LAN: deploy a TLS reverse proxy yourself, or use it on a trusted network only.");

            // ★ 实际监听地址与端口（契约 W11：设置界面必须显示实际监听地址）
            var listenInfo = new LiteLink("", () => { });
            group.AddAction(isChinese ? "实际监听地址" : "Listening address", listenInfo);
            listenInfo.Text = BuildListenAddressText(isChinese);
            RegisterRefresh(() => listenInfo.Text = BuildListenAddressText(isChinese));

            // Password
            group.AddInput(this, "Menu.WebServerPassword",
                () => Config.WebServerPassword,
                v => Config.WebServerPassword = v,
                LanguageManager.T("Menu.WebServerPasswordTip"),
                100, HorizontalAlignment.Center);

            group.AddHint(isChinese
                ? "口令只以 PBKDF2 哈希 + 盐保存（不再回显明文，也不会写进 settings.json）；留空表示不修改。局域网模式必须设置口令：没有口令时服务拒绝启动，运行中清除口令也会停止网页服务。"
                : "The password is stored as a PBKDF2 hash + salt (never in clear text, never in settings.json); leave it empty to keep it unchanged. LAN mode requires a password: the service refuses to start without one, and clearing the password while running stops the web service.");

            // ★ 清除口令：哈希与盐要一起清掉（只清明文字段是不够的）
            group.AddButton(
                isChinese ? "清除访问口令" : "Clear access password",
                isChinese ? "清除" : "Clear",
                () =>
                {
                    Config.WebServerPassword = "";
                    Config.WebServerPasswordHash = "";
                    Config.WebServerPasswordSalt = "";

                    // ★ 局域网模式必须设置口令（R03）：清除并保存后网页服务会被**停止**，
                    //   绝不能变成"局域网里无凭据可访问"。这里先把后果讲清楚。
                    if (Config.WebServerBindMode == WebServerBindMode.Lan)
                    {
                        MessageBox.Show(
                            isChinese
                                ? "已清除访问口令。\n\n当前监听范围是「局域网」：局域网模式必须设置访问口令。\n保存设置后网页服务会被停止（不会以无口令的方式继续对局域网开放）。\n\n如需继续在局域网访问，请重新填写口令。"
                                : "The access password has been cleared.\n\nThe bind scope is LAN, which requires an access password.\nSaving stops the web service (it will not stay open on the LAN without a password).\n\nEnter a password again if you still need LAN access.",
                            isChinese ? "清除访问口令" : "Clear access password",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

                    OnShow();   // 立即刷新界面（口令框回到空）
                });

            // ★ 存量配置提示：旧版本没有「监听范围」这一项，升级后仍保持旧的局域网行为，
            //   这里明确告诉用户可以收敛到「仅本机」。
            if (Config.WebServerBindModeNeedsConvergence)
            {
                group.AddHint(isChinese
                    ? "ℹ 升级自旧版本：监听范围已保留为「局域网」以免远程访问突然中断。建议在确认其它设备不再需要访问后改回「仅本机」。"
                    : "ℹ Upgraded from an older version: the bind scope stays on LAN so remote access does not break unexpectedly. Consider switching back to loopback once no other device needs access.");
            }

            // C. API Link
            //   ★ 旧的 "…/api/snapshot?pwd=123456" 链接已移除（契约 W13：口令不得进 URL）。
            //     网页版现在走「登录 → 主会话 Cookie → 一次性票据」的协议。
            group.AddLink(isChinese ? "网页版入口（需登录）" : "Web page (sign-in required)",
                isChinese ? "http://<本机地址>:<端口>/" : "http://<host>:<port>/",
                () => ShowFirewallHelp(isChinese));

            AddGroupToPage(group);
        }

        /// <summary>
        /// 构造"实际监听地址"的显示文本（契约 W11：必须显示真实监听地址与端口）。
        /// </summary>
        private string BuildListenAddressText(bool isChinese)
        {
            var server = LiteWebServer.Instance;
            bool running = server != null && server.IsRunning;
            int port = running ? server.CurrentRunningPort : Config.WebServerPort;
            string scope = Config.WebServerBindMode == WebServerBindMode.Lan ? "0.0.0.0" : "127.0.0.1";
            string state = running
                ? (isChinese ? "运行中" : "running")
                : (isChinese ? "未运行" : "not running");
            return $"{scope}:{port} ({state})";
        }

        private void ShowFirewallHelp(bool isChinese)
        {
            try
            {
                Process.Start(new ProcessStartInfo("firewall.cpl") { UseShellExecute = true });

                string msg;
                if (isChinese)
                {
                    msg = "如果您在首次启动时的【Windows 防火墙授权弹窗】中点击了“取消”，\n" +
                          "或者是直接关闭了弹窗，会导致手机无法内网访问网页。\n\n" +
                          "已为你打开防火墙设置，请按以下步骤手动“解封”：\n" +
                          "------------------------------\n" +
                          "1. 【关键】请点击新窗口左上侧的【允许应用或功能通过 Windows Defender 防火墙】。\n" +
                          "2. 点击右上角的【更改设置】按钮（如果是灰色的）。\n" +
                          "3. 在列表中找到【LiteMonitor++】。\n" +
                          "4. ★ 必须打三个勾（缺一不可）：\n" +
                          "   左侧名字【LiteMonitor++】\n" +
                          "   右侧【专用】\n" +
                          "   右侧【公用】\n" +
                          "5. 点击底部的【确定】保存。";
                }
                else
                {
                    msg = "If you clicked 'Cancel' or closed the firewall permission dialog on the first launch,\n" +
                          "your mobile device will not be able to connect to the web page.\n\n" +
                          "The firewall settings have been opened for you. Please follow these steps:\n" +
                          "------------------------------\n" +
                          "1. [Critical] Click 'Allow an app or feature through Windows Defender Firewall' on the top-left.\n" +
                          "2. Click the 'Change settings' button (if greyed out).\n" +
                          "3. Find 'LiteMonitor++' in the list.\n" +
                          "4. ★ You MUST check all three boxes:\n" +
                          "   Left Name [LiteMonitor++]\n" +
                          "   Right [Private]\n" +
                          "   Right [Public]\n" +
                          "5. Click [OK] at the bottom to save.";
                }
                MessageBox.Show(msg, isChinese ? "内网连接修复指引" : "Connection Fix Guide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
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
